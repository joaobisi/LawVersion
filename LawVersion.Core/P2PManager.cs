using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using Grpc.Net.Client;
using LawVersion.Network;
using LawVersion.Network.Services;

namespace LawVersion.Core;

public class P2PManager : IDisposable, ILockEventBus
{
    private readonly string _workingDirectory;
    private readonly string _lawyerName;
    private readonly int _port;
    private readonly IVersionControlService _versionService;
    private readonly IDiscoveryService _discoveryService;
    private readonly IP2PServer _p2PServer;
    private readonly DocumentWatcher _watcher;
    private readonly ILogger<P2PManager> _logger;
    private bool _disposed;


    private readonly ConcurrentDictionary<string, PeerInfo> _knownPeers = new();
    
    // Timer para renovação periódica dos locks locais (keep-alive).
    private Timer? _lockRenewalTimer;
    
    // Intervalo de renovação de locks.
    private static readonly TimeSpan LockRenewalInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, bool> _localLocks = new();

    public event Action? OnFolderChanged;
    public event Action<string, string>? OnPeerDetected;
    public event Action<string, string>? OnFileLocked;   
    public event Action<string>? OnFileUnlocked;

    public string WorkingDirectory => _workingDirectory;
    public string LawyerName => _lawyerName;

    public P2PManager(
        string workingDirectory, 
        string lawyerName, 
        int port,
        IVersionControlService versionService,
        IDiscoveryService discoveryService,
        IP2PServer p2PServer,
        ILoggerFactory loggerFactory)
    {
        _workingDirectory = workingDirectory;
        _lawyerName = lawyerName;
        _port = port;
        _versionService = versionService;
        _discoveryService = discoveryService;
        _p2PServer = p2PServer;
        _logger = loggerFactory.CreateLogger<P2PManager>();

        var watcherLogger = loggerFactory.CreateLogger<DocumentWatcher>();
        _watcher = new DocumentWatcher(_workingDirectory, _versionService, watcherLogger);
    }

    public void InitializeSystem()
    {
        if (!Directory.Exists(_workingDirectory))
            Directory.CreateDirectory(_workingDirectory);

        _versionService.InitializeRepository(_workingDirectory);
        _p2PServer.Start(_port);
        
        // Registra este manager como receptor de eventos do gRPC
        VersionSyncServiceImpl.RegisterEventBus(this);
        
        ConfigurarEventos();
        StartLockRenewalTimer();
        
        _logger.LogInformation("[SISTEMA] LawVersion ativo para {Name} na porta {Port}", _lawyerName, _port);
    }

    private void ConfigurarEventos()
    {
        // Eventos locais do Watcher
        _watcher.FileChanged += (_, _) => OnFolderChanged?.Invoke();

        _watcher.FileLockChanged += async (fileName, isLocked) => 
        {
            if (isLocked) await BroadcastLock(fileName);
            else await BroadcastUnlock(fileName);
        };
    }
    
    void ILockEventBus.OnRemoteLockReceived(string fileName, string owner) 
        => OnFileLocked?.Invoke(fileName, owner);
    
    void ILockEventBus.OnRemoteUnlockReceived(string fileName) 
        => OnFileUnlocked?.Invoke(fileName);

    public async Task StartNetworkAsync()
    {
        _discoveryService.StartListening((peerName, endpoint) => 
        {
            RegisterPeer(peerName, endpoint);
            OnPeerDetected?.Invoke(peerName, endpoint.ToString());
        }, _lawyerName);

        await _discoveryService.BroadcastPresence(_lawyerName, _port);
    }
    
    private void RegisterPeer(string peerName, IPEndPoint endpoint)
    {
        var isNew = !_knownPeers.ContainsKey(peerName);
        
        var peer = _knownPeers.AddOrUpdate(
            peerName,
            _ => new PeerInfo(peerName, endpoint.Address, endpoint.Port),
            (_, existing) =>
            {
                existing.LastSeen = DateTime.UtcNow;
                return existing;
            });
        
        if (isNew)
        {
            _logger.LogInformation("[P2P] Novo par registrado: {Name} em {Endpoint}", peerName, peer.GrpcEndpoint);
            _ = SyncLocksFromPeerAsync(peer);
        }
    }

    private async Task SyncLocksFromPeerAsync(PeerInfo peer)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(peer.GrpcEndpoint);
            var client = new VersionSync.VersionSyncClient(channel);
            
            var response = await client.GetActiveLocksAsync(new EmptyRequest());
            
            foreach (var lockEntry in response.Locks)
            {
                _logger.LogDebug("[P2P] Sync: {File} travado por {Owner}", lockEntry.FileName, lockEntry.Owner);
                OnFileLocked?.Invoke(lockEntry.FileName, lockEntry.Owner);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[P2P] Falha no sync inicial com {Peer}: {Error}", peer.Name, ex.Message);
        }
    }

    public List<string> GetFileHistory(string fileName) => 
        _disposed ? [] : _versionService.GetCommitHistory(fileName);


    // Notifica todos os pares conhecidos via gRPC de que um arquivo foi travado localmente.
    private async Task BroadcastLock(string fileName)
    {
        _logger.LogInformation("[P2P] Notificando rede: LOCK em {File}", fileName);
        _localLocks[fileName] = true;
        
        OnFileLocked?.Invoke(fileName, _lawyerName);
        
        // Envia para todos os pares via gRPC
        var request = new LockRequest 
        { 
            FileName = fileName, 
            LawyerName = _lawyerName 
        };

        var tasks = _knownPeers.Values.Select(peer => SendLockToPeerAsync(peer, request));
        await Task.WhenAll(tasks);
    }

    // Notifica todos os pares conhecidos via gRPC de que um arquivo foi liberado.
    private async Task BroadcastUnlock(string fileName)
    {
        _logger.LogInformation("[P2P] Notificando rede: UNLOCK em {File}", fileName);
        _localLocks.TryRemove(fileName, out _);
        
        OnFileUnlocked?.Invoke(fileName);
        
        var request = new LockRequest 
        { 
            FileName = fileName, 
            LawyerName = _lawyerName 
        };

        var tasks = _knownPeers.Values.Select(peer => SendUnlockToPeerAsync(peer, request));
        await Task.WhenAll(tasks);
    }

    private async Task SendLockToPeerAsync(PeerInfo peer, LockRequest request)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(peer.GrpcEndpoint);
            var client = new VersionSync.VersionSyncClient(channel);
            var response = await client.RequestLockAsync(request);
            
            if (!response.IsGranted)
            {
                _logger.LogWarning("[P2P] Lock NEGADO por {Peer}: {File} já travado por {Owner}", 
                    peer.Name, request.FileName, response.CurrentOwner);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[P2P] Falha ao enviar lock para {Peer}: {Error}", peer.Name, ex.Message);
        }
    }

    private async Task SendUnlockToPeerAsync(PeerInfo peer, LockRequest request)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(peer.GrpcEndpoint);
            var client = new VersionSync.VersionSyncClient(channel);
            await client.ReleaseLockAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[P2P] Falha ao enviar unlock para {Peer}: {Error}", peer.Name, ex.Message);
        }
    }

    // Inicia um timer para renovar periodicamente os locks locais nos pares remotos.
    private void StartLockRenewalTimer()
    {
        _lockRenewalTimer = new Timer(async _ =>
        {
            foreach (var fileName in _localLocks.Keys)
            {
                var request = new LockRequest 
                { 
                    FileName = fileName, 
                    LawyerName = _lawyerName 
                };

                foreach (var peer in _knownPeers.Values)
                {
                    try
                    {
                        using var channel = GrpcChannel.ForAddress(peer.GrpcEndpoint);
                        var client = new VersionSync.VersionSyncClient(channel);
                        await client.RenewLockAsync(request);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("[P2P] Falha ao renovar lock com {Peer}: {Error}", peer.Name, ex.Message);
                    }
                }
            }
        }, null, LockRenewalInterval, LockRenewalInterval);
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        // Remove o event bus para evitar memory leaks
        VersionSyncServiceImpl.UnregisterEventBus();
        
        _lockRenewalTimer?.Dispose();
        _discoveryService.Stop();
        
        try { _p2PServer.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning("Erro ao parar servidor: {Error}", ex.Message); }
        
        _watcher.Dispose();
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
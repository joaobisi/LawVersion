using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using Grpc.Net.Client;
using Grpc.Core;
using Google.Protobuf;
using LawVersion.Network;
using LawVersion.Network.Services;
using System.Text.Json;

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
    private readonly ConcurrentDictionary<string, List<string>> _sharedPeers = new();

    public event Action? OnFolderChanged;
    public event Action<string, string>? OnPeerDetected;
    public event Action<string, string>? OnFileLocked;   
    public event Action<string>? OnFileUnlocked;
    public event Action<string, string>? OnFileCompleted;

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
        
        LoadShares();
        ConfigurarEventos();
        StartLockRenewalTimer();
        
        _logger.LogInformation("[SISTEMA] LawVersion ativo para {Name} na porta {Port}", _lawyerName, _port);
    }

    private string GetSharesFilePath()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LawVersion"
        );
        
        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }

        var safeLawyer = string.Concat(_lawyerName.Split(Path.GetInvalidFileNameChars()));
        
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(_workingDirectory);
        var hashBytes = sha256.ComputeHash(bytes);
        var hashStr = Convert.ToHexString(hashBytes).Substring(0, 12).ToLowerInvariant();

        return Path.Combine(baseDir, $"shares_{safeLawyer}_{hashStr}.json");
    }

    private void LoadShares()
    {
        var newPath = GetSharesFilePath();
        var oldPath = Path.Combine(_workingDirectory, "shares.json");

        // Migração automática do arquivo antigo do workspace para o AppData
        if (!File.Exists(newPath) && File.Exists(oldPath))
        {
            try
            {
                File.Copy(oldPath, newPath, true);
                File.Delete(oldPath);
                _logger.LogInformation("[P2P] Migrado shares.json do workspace para a pasta de dados do sistema.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[P2P] Falha ao migrar shares.json antigo");
            }
        }

        var path = File.Exists(newPath) ? newPath : oldPath;
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (dict != null)
            {
                foreach (var kvp in dict)
                {
                    _sharedPeers[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P] Erro ao carregar compartilhamentos");
        }
    }

    private void SaveShares()
    {
        var path = GetSharesFilePath();
        try
        {
            var json = JsonSerializer.Serialize(_sharedPeers);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P] Erro ao salvar compartilhamentos");
        }
    }

    public List<string> GetActivePeerNames() => _knownPeers.Keys.ToList();

    public bool IsFileShared(string fileName) => _sharedPeers.TryGetValue(fileName, out var list) && list.Count > 0;
    
    public List<string> GetSharedPeersForFile(string fileName) => _sharedPeers.TryGetValue(fileName, out var list) ? list : new List<string>();

    public string GetFileOwner(string fileName)
    {
        if (_localLocks.ContainsKey(fileName))
            return _lawyerName;
            
        return VersionSyncServiceImpl.GetActiveLockOwner(fileName);
    }

    public async Task ShareFileWithAsync(string fileName, string peerName)
    {
        var list = _sharedPeers.GetOrAdd(fileName, _ => new List<string>());
        lock (list)
        {
            if (!list.Contains(peerName))
            {
                list.Add(peerName);
            }
        }
        SaveShares();
        _logger.LogInformation("[P2P] Arquivo {File} compartilhado com {Peer}", fileName, peerName);
        
        if (_knownPeers.TryGetValue(peerName, out var peer))
        {
            await SendFileToSinglePeerAsync(peer, fileName);
        }
    }

    private async Task SendFileToSinglePeerAsync(PeerInfo peer, string fileName)
    {
        var fullPath = Path.Combine(_workingDirectory, fileName);
        if (!File.Exists(fullPath)) return;
        try
        {
            var content = await File.ReadAllBytesAsync(fullPath);
            var frame = new FileFrame
            {
                FileName = fileName,
                Content = ByteString.CopyFrom(content),
                VersionHash = DateTime.UtcNow.Ticks.ToString()
            };
            using var channel = GrpcChannel.ForAddress(peer.GrpcEndpoint);
            var client = new VersionSync.VersionSyncClient(channel);
            var metadata = new Metadata { { "sender-name", _lawyerName } };
            await client.PushVersionAsync(frame, metadata);
            _logger.LogInformation("[P2P] Arquivo {File} enviado para {Peer}", fileName, peer.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[P2P] Falha ao enviar arquivo para {Peer}: {Error}", peer.Name, ex.Message);
        }
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
    {
        // Se o arquivo for compartilhado ou recebido do dono, atualiza a UI
        if (_sharedPeers.TryGetValue(fileName, out var list) && list.Contains(owner))
        {
            OnFileLocked?.Invoke(fileName, owner);
        }
    }
    
    void ILockEventBus.OnRemoteUnlockReceived(string fileName, string sender) 
    {
        if (string.IsNullOrEmpty(sender) || (_sharedPeers.TryGetValue(fileName, out var list) && list.Contains(sender)))
        {
            OnFileUnlocked?.Invoke(fileName);
        }
    }

    void ILockEventBus.OnRemoteFileReceived(string fileName, byte[] content, string sender)
    {
        try
        {
            var fullPath = Path.Combine(_workingDirectory, fileName);
            
            // Aceita o arquivo e registra o remetente automaticamente no círculo de compartilhamento
            var list = _sharedPeers.GetOrAdd(fileName, _ => new List<string>());
            lock (list)
            {
                if (!list.Contains(sender))
                {
                    list.Add(sender);
                    SaveShares();
                }
            }
            
            _watcher.BeginSync(fileName);
            
            File.WriteAllBytes(fullPath, content);
            _versionService.CommitFile(fileName, $"Recebido de {sender}");
            
            _logger.LogInformation("[P2P] Arquivo sincronizado: {File} de {Sender}", fileName, sender);
            OnFolderChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P] Erro ao sincronizar arquivo: {File}", fileName);
        }
        finally
        {
            _ = Task.Delay(2000).ContinueWith(_ => _watcher.EndSync(fileName));
        }
    }

    void ILockEventBus.OnRemoteFileCompleted(string fileName, string sender)
    {
        try
        {
            _logger.LogInformation("[P2P] Notificação de conclusão de arquivo recebida: {File} por {Sender}", fileName, sender);
            
            // Remove lock local se houver
            _localLocks.TryRemove(fileName, out _);
            OnFileUnlocked?.Invoke(fileName);

            // Apaga arquivo do workspace
            var fullPath = Path.Combine(_workingDirectory, fileName);
            if (File.Exists(fullPath))
            {
                _watcher.BeginSync(fileName);
                File.Delete(fullPath);
                _logger.LogInformation("[P2P] Arquivo local excluído após conclusão: {File}", fileName);
            }
            
            // Remove do shares
            _sharedPeers.TryRemove(fileName, out _);
            SaveShares();
            
            // Dispara o evento de conclusão para a UI
            OnFileCompleted?.Invoke(fileName, sender);
            OnFolderChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P] Erro ao tratar conclusão remota do arquivo: {File}", fileName);
        }
        finally
        {
            _ = Task.Delay(2000).ContinueWith(_ => _watcher.EndSync(fileName));
        }
    }

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
                if (_sharedPeers.TryGetValue(lockEntry.FileName, out var list) && list.Contains(peer.Name))
                {
                    _logger.LogDebug("[P2P] Sync: {File} travado por {Owner}", lockEntry.FileName, lockEntry.Owner);
                    OnFileLocked?.Invoke(lockEntry.FileName, lockEntry.Owner);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[P2P] Falha no sync inicial com {Peer}: {Error}", peer.Name, ex.Message);
        }
    }

    public List<string> GetFileHistory(string fileName) => 
        _disposed ? [] : _versionService.GetCommitHistory(fileName);

    public void RestoreFileToVersion(string fileName, string commitSha)
    {
        if (_disposed) return;

        var owner = GetFileOwner(fileName);
        if (!string.IsNullOrEmpty(owner) && owner != _lawyerName)
        {
            throw new InvalidOperationException($"Não é possível restaurar: o arquivo está bloqueado por {owner}.");
        }

        _versionService.RestoreFileVersion(fileName, commitSha);
    }

    public void ExtractFileToVersion(string fileName, string commitSha, string destinationPath)
    {
        if (_disposed) return;
        _versionService.ExtractFileVersion(fileName, commitSha, destinationPath);
    }

    public async Task CompleteFileAsync(string fileName, string targetPath)
    {
        if (_disposed) return;

        // Validar se o arquivo está bloqueado por outro advogado
        var owner = GetFileOwner(fileName);
        if (!string.IsNullOrEmpty(owner) && owner != _lawyerName)
        {
            throw new InvalidOperationException($"Não é possível concluir o arquivo. Ele está bloqueado por {owner}.");
        }

        var fullPath = Path.Combine(_workingDirectory, fileName);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Arquivo não encontrado no diretório de trabalho.", fullPath);
        }

        // Exportar o arquivo (copiar para targetPath)
        File.Copy(fullPath, targetPath, overwrite: true);
        _logger.LogInformation("[P2P] Arquivo {File} exportado com sucesso para {TargetPath}", fileName, targetPath);

        // Obter a lista de pares que compartilham o arquivo
        var sharedList = _sharedPeers.TryGetValue(fileName, out var list) ? list.ToList() : new List<string>();

        // Liberar locks locais do arquivo
        _localLocks.TryRemove(fileName, out _);
        
        // Apagar arquivo localmente
        _watcher.BeginSync(fileName);
        try
        {
            File.Delete(fullPath);
            _logger.LogInformation("[P2P] Arquivo {File} removido do workspace local.", fileName);
        }
        finally
        {
            _ = Task.Delay(2000).ContinueWith(_ => _watcher.EndSync(fileName));
        }

        // Remover do shares
        _sharedPeers.TryRemove(fileName, out _);
        SaveShares();

        // Disparar notificação gRPC para todos os participantes compartilhados
        if (sharedList.Count > 0)
        {
            var request = new FileCompletedRequest
            {
                FileName = fileName,
                LawyerName = _lawyerName
            };

            var peers = _knownPeers.Values.Where(p => sharedList.Contains(p.Name)).ToList();
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    using var channel = GrpcChannel.ForAddress(peer.GrpcEndpoint);
                    var client = new VersionSync.VersionSyncClient(channel);
                    await client.NotifyFileCompletedAsync(request);
                    _logger.LogInformation("[P2P] Notificação de conclusão enviada para {Peer}", peer.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[P2P] Falha ao enviar notificação de conclusão para {Peer}: {Error}", peer.Name, ex.Message);
                }
            });

            await Task.WhenAll(tasks);
        }

        // Disparar evento local de conclusão para a UI
        OnFileCompleted?.Invoke(fileName, _lawyerName);
        OnFolderChanged?.Invoke();
    }

    // Notifica todos os pares conhecidos via gRPC de que um arquivo foi travado localmente.
    private async Task BroadcastLock(string fileName)
    {
        _localLocks[fileName] = true;
        OnFileLocked?.Invoke(fileName, _lawyerName);
        
        if (!_sharedPeers.TryGetValue(fileName, out var sharedList) || sharedList.Count == 0) return;

        _logger.LogInformation("[P2P] Notificando rede (Lock): {File}", fileName);
        var request = new LockRequest 
        { 
            FileName = fileName, 
            LawyerName = _lawyerName 
        };

        var tasks = _knownPeers.Values
            .Where(peer => sharedList.Contains(peer.Name))
            .Select(peer => SendLockToPeerAsync(peer, request));
        await Task.WhenAll(tasks);
    }

    // Notifica todos os pares conhecidos via gRPC de que um arquivo foi liberado.
    private async Task BroadcastUnlock(string fileName)
    {
        _localLocks.TryRemove(fileName, out _);
        OnFileUnlocked?.Invoke(fileName);
        
        if (!_sharedPeers.TryGetValue(fileName, out var sharedList) || sharedList.Count == 0) return;

        _logger.LogInformation("[P2P] Notificando rede (Unlock): {File}", fileName);
        var request = new LockRequest 
        { 
            FileName = fileName, 
            LawyerName = _lawyerName 
        };

        var tasks = _knownPeers.Values
            .Where(peer => sharedList.Contains(peer.Name))
            .Select(peer => SendUnlockToPeerAsync(peer, request));
        await Task.WhenAll(tasks);
        
        // Envia o arquivo atualizado para todos os peers do círculo
        await SyncFileToPeers(fileName);
    }

    private async Task SyncFileToPeers(string fileName)
    {
        if (!_sharedPeers.TryGetValue(fileName, out var sharedList) || sharedList.Count == 0) return;

        var fullPath = Path.Combine(_workingDirectory, fileName);
        if (!File.Exists(fullPath)) return;
        
        try
        {
            var content = await File.ReadAllBytesAsync(fullPath);
            var frame = new FileFrame
            {
                FileName = fileName,
                Content = ByteString.CopyFrom(content),
                VersionHash = DateTime.UtcNow.Ticks.ToString()
            };
            
            var tasks = _knownPeers.Values
                .Where(peer => sharedList.Contains(peer.Name))
                .Select(async peer =>
                {
                    try
                     {
                         using var channel = GrpcChannel.ForAddress(peer.GrpcEndpoint);
                         var client = new VersionSync.VersionSyncClient(channel);
                         var metadata = new Metadata { { "sender-name", _lawyerName } };
                         await client.PushVersionAsync(frame, metadata);
                         _logger.LogInformation("[P2P] Arquivo {File} enviado para {Peer}", fileName, peer.Name);
                     }
                     catch (Exception ex)
                     {
                         _logger.LogWarning("[P2P] Falha ao enviar arquivo para {Peer}: {Error}", peer.Name, ex.Message);
                     }
                });
            
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P] Erro ao ler arquivo para sync: {File}", fileName);
        }
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
                if (!_sharedPeers.TryGetValue(fileName, out var sharedList) || sharedList.Count == 0) continue;

                var request = new LockRequest 
                { 
                    FileName = fileName, 
                    LawyerName = _lawyerName 
                };

                foreach (var peer in _knownPeers.Values.Where(p => sharedList.Contains(p.Name)))
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
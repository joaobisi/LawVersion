using Grpc.Core;
using System.Collections.Concurrent;

namespace LawVersion.Network.Services;

/// <summary>
/// Registro de um lock ativo com timestamps para suportar TTL/expiração.
/// </summary>
internal record LockRecord(string Owner, DateTime AcquiredAtUtc)
{
    public DateTime LastRenewedAtUtc { get; set; } = DateTime.UtcNow;
}

public class VersionSyncServiceImpl : VersionSync.VersionSyncBase
{
    /// <summary>
    /// Tiempo máximo sem renovação antes de considerar o lock expirado.
    /// </summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(2);
    
    /// <summary>
    /// Intervalo de verificação de locks expirados.
    /// </summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    // Dicionário thread-safe para rastrear quem está editando o quê na rede
    private static readonly ConcurrentDictionary<string, LockRecord> ActiveLocks = new();
    
    // Timer estático para limpeza de locks expirados
    private static Timer? _cleanupTimer;
    
    // Event bus para notificar o P2PManager sem acoplamento via static events.
    private static ILockEventBus? _eventBus;

    public static string GetActiveLockOwner(string fileName)
    {
        if (ActiveLocks.TryGetValue(fileName, out var record))
        {
            return record.Owner;
        }
        return string.Empty;
    }

    /// <summary>
    /// Registra o event bus para recebimento de eventos de lock.
    /// Deve ser chamado durante a inicialização do sistema.
    /// </summary>
    public static void RegisterEventBus(ILockEventBus eventBus)
    {
        _eventBus = eventBus;
        
        // Inicia o timer de limpeza de locks expirados
        _cleanupTimer?.Dispose();
        _cleanupTimer = new Timer(
            _ => CleanupExpiredLocks(), 
            null, 
            CleanupInterval, 
            CleanupInterval);
    }

    /// <summary>
    /// Remove o event bus e para o timer de limpeza. Chamado no Dispose do P2PManager.
    /// </summary>
    public static void UnregisterEventBus()
    {
        _eventBus = null;
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
    }
    
    /// <summary>
    /// Método usado para disparar o evento de Lock. 
    /// Chamado internamente pelo gRPC ou manualmente em Testes Unitários.
    /// </summary>
    public static void ReceiveRemoteLock(string fileName, string owner)
    {
        _eventBus?.OnRemoteLockReceived(fileName, owner);
    }

    /// <summary>
    /// Método usado para disparar o evento de Unlock.
    /// </summary>
    public static void ReceiveRemoteUnlock(string fileName, string sender)
    {
        _eventBus?.OnRemoteUnlockReceived(fileName, sender);
    }

    public override Task<LockResponse> RequestLock(LockRequest request, ServerCallContext context)
    {
        // Primeiro limpa locks expirados para não bloquear indevidamente
        CleanupExpiredLocks();
        
        // Tenta adicionar atomicamente — se já existe, verifica o dono
        var newRecord = new LockRecord(request.LawyerName, DateTime.UtcNow);
        
        var existing = ActiveLocks.GetOrAdd(request.FileName, newRecord);
        
        if (existing == newRecord)
        {
            // Lock adquirido com sucesso (era novo)
            ReceiveRemoteLock(request.FileName, request.LawyerName);
            return Task.FromResult(new LockResponse 
            { 
                IsGranted = true, 
                CurrentOwner = request.LawyerName 
            });
        }
        
        // Lock já existe — verifica se é do mesmo dono
        if (existing.Owner == request.LawyerName)
        {
            // Mesmo dono — renova o lock
            existing.LastRenewedAtUtc = DateTime.UtcNow;
            return Task.FromResult(new LockResponse 
            { 
                IsGranted = true, 
                CurrentOwner = request.LawyerName 
            });
        }
        
        // Lock pertence a outro advogado — nega acesso
        return Task.FromResult(new LockResponse 
        { 
            IsGranted = false, 
            CurrentOwner = existing.Owner 
        });
    }

    public override Task<LockResponse> ReleaseLock(LockRequest request, ServerCallContext context)
    {
        if (ActiveLocks.TryRemove(request.FileName, out _))
        {
            ReceiveRemoteUnlock(request.FileName, request.LawyerName);
        }
        
        return Task.FromResult(new LockResponse { IsGranted = true });
    }

    public override Task<LockResponse> RenewLock(LockRequest request, ServerCallContext context)
    {
        if (ActiveLocks.TryGetValue(request.FileName, out var record))
        {
            if (record.Owner == request.LawyerName)
            {
                record.LastRenewedAtUtc = DateTime.UtcNow;
                return Task.FromResult(new LockResponse 
                { 
                    IsGranted = true, 
                    CurrentOwner = request.LawyerName 
                });
            }
            
            // Lock pertence a outro advogado
            return Task.FromResult(new LockResponse 
            { 
                IsGranted = false, 
                CurrentOwner = record.Owner 
            });
        }
        
        // Lock não existe mais (expirou ou foi liberado)
        return Task.FromResult(new LockResponse 
        { 
            IsGranted = false, 
            CurrentOwner = string.Empty 
        });
    }

    public override Task<ActiveLocksResponse> GetActiveLocks(EmptyRequest request, ServerCallContext context)
    {
        var response = new ActiveLocksResponse();
        
        foreach (var kvp in ActiveLocks)
        {
            response.Locks.Add(new LockEntry
            {
                FileName = kvp.Key,
                Owner = kvp.Value.Owner,
                AcquiredAtUtcTicks = kvp.Value.AcquiredAtUtc.Ticks
            });
        }
        
        return Task.FromResult(response);
    }

    public override Task<SyncResponse> PushVersion(FileFrame request, ServerCallContext context)
    {
        var bytesCount = request.Content?.Length ?? 0;
        Console.WriteLine($"[gRPC] Arquivo recebido: {request.FileName} | Hash: {request.VersionHash} | Tamanho: {bytesCount} bytes");
        
        if (request.Content != null && request.Content.Length > 0)
        {
            var sender = context.RequestHeaders.FirstOrDefault(h => h.Key == "sender-name")?.Value ?? "Desconhecido";
            _eventBus?.OnRemoteFileReceived(request.FileName, request.Content.ToByteArray(), sender);
        }
        
        return Task.FromResult(new SyncResponse 
        { 
            Success = true, 
            Message = "Arquivo recebido e sincronizado." 
        });
    }

    public override Task<SyncResponse> NotifyFileCompleted(FileCompletedRequest request, ServerCallContext context)
    {
        Console.WriteLine($"[gRPC] Recebida notificação de conclusão do arquivo: {request.FileName} de {request.LawyerName}");
        _eventBus?.OnRemoteFileCompleted(request.FileName, request.LawyerName);
        return Task.FromResult(new SyncResponse
        {
            Success = true,
            Message = "Notificação de conclusão de arquivo recebida."
        });
    }

    /// <summary>
    /// Remove locks cujo LastRenewedAtUtc excedeu o TTL.
    /// Se um nó caiu, seus locks serão limpos automaticamente.
    /// </summary>
    private static void CleanupExpiredLocks()
    {
        var now = DateTime.UtcNow;
        
        foreach (var kvp in ActiveLocks)
        {
            if (now - kvp.Value.LastRenewedAtUtc > LockTtl)
            {
                if (ActiveLocks.TryRemove(kvp.Key, out _))
                {
                    Console.WriteLine($"[gRPC] Lock expirado removido: {kvp.Key} (dono: {kvp.Value.Owner})");
                    ReceiveRemoteUnlock(kvp.Key, kvp.Value.Owner);
                }
            }
        }
    }
    
    /// <summary>
    /// Limpa todos os locks ativos. Usado para reset em testes.
    /// </summary>
    internal static void ClearAllLocks()
    {
        ActiveLocks.Clear();
    }
}
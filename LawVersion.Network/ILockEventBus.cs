namespace LawVersion.Network;

/// <summary>
/// Interface para desacoplar eventos de lock/unlock da implementação estática.
/// O P2PManager implementa esta interface para receber notificações do gRPC server.
/// </summary>
public interface ILockEventBus
{
    /// <summary>
    /// Chamado quando um lock é recebido de um par remoto via gRPC.
    /// </summary>
    void OnRemoteLockReceived(string fileName, string owner);
    
    /// <summary>
    /// Chamado quando um unlock é recebido de um par remoto via gRPC.
    /// </summary>
    void OnRemoteUnlockReceived(string fileName, string sender);
    
    /// <summary>
    /// Chamado quando um arquivo é recebido de um par remoto via PushVersion.
    /// </summary>
    void OnRemoteFileReceived(string fileName, byte[] content, string sender);

    /// <summary>
    /// Chamado quando uma notificação de conclusão/finalização de arquivo é recebida via gRPC.
    /// </summary>
    void OnRemoteFileCompleted(string fileName, string sender);
}

using System.Net;

namespace LawVersion.Network;

/// <summary>
/// Representa um par (peer) descoberto na rede P2P.
/// </summary>
public record PeerInfo(string Name, IPAddress Address, int GrpcPort)
{
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    
    public string GrpcEndpoint => $"http://{Address}:{GrpcPort}";
}

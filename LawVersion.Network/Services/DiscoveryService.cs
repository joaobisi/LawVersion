using System.Net;
using System.Net.Sockets;
using System.Text;
namespace LawVersion.Network.Services;

public class DiscoveryService : IDiscoveryService
{
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Timer? _heartbeatTimer;
    
    // Porta padrão para o "Grito" UDP (Beacon)
    private const int DiscoveryPort = 9876; 
    
    /// <summary>
    /// Intervalo entre broadcasts de presença (heartbeat).
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private string _myLawyerName = string.Empty;
    private int _myGrpcPort;
        
    public void StartListening(Action<string, IPEndPoint> onPeerFound, string myName)
    {
        _myLawyerName = myName;
        _cts = new CancellationTokenSource();
        
        _udpClient = new UdpClient();
        // Permite que João e Maria rodem no mesmo Ubuntu sem erro de porta ocupada
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        
        // Escuta em todas as interfaces na porta de descoberta
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
                    string message = Encoding.UTF8.GetString(result.Buffer);
                    
                    // Formato esperado: "LawVersion|Nome|PortagRPC"
                    var parts = message.Split('|');
                    
                    if (parts.Length == 3 && parts[0] == "LawVersion")
                    {
                        string remoteName = parts[1];
                        
                        // Se o nome recebido for igual ao meu, eu ignoro (é o meu próprio anúncio)
                        if (remoteName.Equals(_myLawyerName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; 
                        }

                        if (int.TryParse(parts[2], out int remoteGrpcPort))
                        {
                            // Criamos o endpoint com o IP real de quem enviou + a porta gRPC dele
                            var peerEndPoint = new IPEndPoint(result.RemoteEndPoint.Address, remoteGrpcPort);
                            
                            // Notifica que um colega REAL (não eu) foi encontrado
                            onPeerFound(remoteName, peerEndPoint);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* Parada limpa */ }
            catch (ObjectDisposedException) { /* Socket fechado */ }
            catch (Exception ex) 
            { 
                Console.WriteLine($"[Discovery] Erro na escuta: {ex.Message}"); 
            }
        }, _cts.Token);
    }

    public async Task BroadcastPresence(string lawyerName, int grpcPort)
    {
        _myGrpcPort = grpcPort;
        
        // Envia o broadcast inicial
        await SendBroadcast(lawyerName, grpcPort);
        
        // Inicia heartbeat periódico para que novos pares nos descubram
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(async _ =>
        {
            try
            {
                await SendBroadcast(lawyerName, grpcPort);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discovery] Erro no heartbeat: {ex.Message}");
            }
        }, null, HeartbeatInterval, HeartbeatInterval);
    }

    private static async Task SendBroadcast(string lawyerName, int grpcPort)
    {
        try 
        {
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            
            // Enviamos nosso "cartão de visitas" P2P
            string message = $"LawVersion|{lawyerName}|{grpcPort}";
            byte[] data = Encoding.UTF8.GetBytes(message);
            
            var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            await client.SendAsync(data, data.Length, endpoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discovery] Erro ao enviar anúncio: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _udpClient?.Dispose();
        Console.WriteLine("[Discovery] Serviço parado.");
    }
}
namespace LawVersion.Network;

public interface IDiscoveryService
{ 
    void StartListening(Action<string, System.Net.IPEndPoint> onPeerFound, string myName);
    
    Task BroadcastPresence(string lawyerName, int port);
    void Stop();
}
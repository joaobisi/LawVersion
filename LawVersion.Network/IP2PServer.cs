namespace LawVersion.Network;

public interface IP2PServer
{
    void Start(int port);
    Task StopAsync();
}
using LawVersion.Network.Services;

namespace LawVersion.Network;

public class P2PServer : IP2PServer
{
    private IHost? _host;

    public void Start(int port)
    {
        var builder = WebApplication.CreateBuilder();
        
        // Configura o Kestrel (servidor web) para ouvir na porta gRPC
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });

        // Adiciona o gRPC ao sistema
        builder.Services.AddGrpc();

        var app = builder.Build();

        // Mapeia o seu serviço que criamos antes
        app.MapGrpcService<VersionSyncServiceImpl>();

        _host = app;
        Task.Run(() => app.Run());
        
        Console.WriteLine($"[P2P] Servidor gRPC rodando na porta {port}");
    }

    public async Task StopAsync()
    {
        if (_host != null) await _host.StopAsync();
    }
}
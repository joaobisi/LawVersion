using LawVersion.Core;
using LawVersion.Network;
using LawVersion.Network.Services;
using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.Logging;

namespace LawVersion.Tests;

public class P2PConcurrencyTests
{
    [Fact]
    public void Deve_Atualizar_Status_Remoto_Quando_Outro_Advogado_Trava_Arquivo()
    {
        var mockGit = Substitute.For<IVersionControlService>();
        var mockDiscovery = Substitute.For<IDiscoveryService>();
        var mockServer = Substitute.For<IP2PServer>();
        
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

        string uniqueId = Guid.NewGuid().ToString();
        string tempPath = Path.Combine(Path.GetTempPath(), $"LawVersion_Test_{uniqueId}");
        
        if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);

        try 
        {
            using var managerMaria = new P2PManager(
                tempPath, 
                "Dra. Maria", 
                5001, 
                mockGit, 
                mockDiscovery, 
                mockServer,
                loggerFactory
            );

            // Ativa os listeners de eventos e a infraestrutura
            managerMaria.InitializeSystem();

            string? arquivoRecebido = null;
            string? donoRecebido = null;

            managerMaria.OnFileLocked += (file, owner) => 
            {
                arquivoRecebido = file;
                donoRecebido = owner;
            };

            string arquivoAlvo = "Contrato_Social.docx";
            string advogadoRemoto = "Dr. João";
            
            // Simula o sinal vindo da rede gRPC
            VersionSyncServiceImpl.ReceiveRemoteLock(arquivoAlvo, advogadoRemoto);

            arquivoRecebido.Should().Be(arquivoAlvo);
            donoRecebido.Should().Be(advogadoRemoto);
        }
        finally 
        {
            if (Directory.Exists(tempPath))
            {
                try { Directory.Delete(tempPath, true); } catch { /* Cleanup */ }
            }
        }
    }
}
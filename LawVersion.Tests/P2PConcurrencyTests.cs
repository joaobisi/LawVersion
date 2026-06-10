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
            
            // Registra o compartilhamento para que o lock seja processado
            managerMaria.ShareFileWithAsync(arquivoAlvo, advogadoRemoto).GetAwaiter().GetResult();
            
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

    [Fact]
    public void Deve_Migrar_Shares_Json_Para_AppData_E_Limpar_Workspace()
    {
        var mockGit = Substitute.For<IVersionControlService>();
        var mockDiscovery = Substitute.For<IDiscoveryService>();
        var mockServer = Substitute.For<IP2PServer>();
        
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

        string uniqueId = Guid.NewGuid().ToString();
        string tempPath = Path.Combine(Path.GetTempPath(), $"LawVersion_Test_{uniqueId}");
        if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);

        // Escreve um shares.json antigo no diretório de trabalho
        var oldSharesPath = Path.Combine(tempPath, "shares.json");
        var testShares = new Dictionary<string, List<string>>
        {
            { "Contrato_Test.docx", new List<string> { "Dr. Carlos" } }
        };
        File.WriteAllText(oldSharesPath, System.Text.Json.JsonSerializer.Serialize(testShares));

        try 
        {
            using var manager = new P2PManager(
                tempPath, 
                $"Lawyer_{uniqueId}", // Nome único para evitar conflito com outros testes executando em paralelo
                5002, 
                mockGit, 
                mockDiscovery, 
                mockServer,
                loggerFactory
            );

            // Ao inicializar o sistema, ele deve detectar o shares.json antigo e migrar
            manager.InitializeSystem();

            // O arquivo antigo não deve mais existir no workspace
            File.Exists(oldSharesPath).Should().BeFalse();

            // O compartilhamento deve ter sido lido com sucesso
            manager.IsFileShared("Contrato_Test.docx").Should().BeTrue();
            manager.GetSharedPeersForFile("Contrato_Test.docx").Should().Contain("Dr. Carlos");

            // Limpa o arquivo recém-criado em LocalApplicationData para manter o ambiente do OS limpo
            // Precisamos descobrir o nome que foi gerado pelo método privado GetSharesFilePath
            var method = typeof(P2PManager).GetMethod("GetSharesFilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                var newPath = method.Invoke(manager, null) as string;
                if (newPath != null && File.Exists(newPath))
                {
                    File.Delete(newPath);
                }
            }
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
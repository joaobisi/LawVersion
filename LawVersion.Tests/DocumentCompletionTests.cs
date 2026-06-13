using LawVersion.Core;
using LawVersion.Network;
using LawVersion.Network.Services;
using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.Logging;
using System.IO;

namespace LawVersion.Tests;

public class DocumentCompletionTests
{
    [Fact]
    public async Task Deve_Exportar_E_Deletar_Arquivo_Ao_Concluir()
    {
        var mockGit = Substitute.For<IVersionControlService>();
        var mockDiscovery = Substitute.For<IDiscoveryService>();
        var mockServer = Substitute.For<IP2PServer>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

        string uniqueId = Guid.NewGuid().ToString();
        string tempPath = Path.Combine(Path.GetTempPath(), $"LawVersion_Test_{uniqueId}");
        string exportPath = Path.Combine(Path.GetTempPath(), $"LawVersion_Export_{uniqueId}.docx");
        
        if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);

        string fileName = "contrato_para_concluir.docx";
        string fullPath = Path.Combine(tempPath, fileName);
        File.WriteAllText(fullPath, "Contrato Finalizado");

        try
        {
            using var manager = new P2PManager(
                tempPath,
                "Dr. Joao",
                5003,
                mockGit,
                mockDiscovery,
                mockServer,
                loggerFactory
            );

            manager.InitializeSystem();

            await manager.CompleteFileAsync(fileName, exportPath);

            File.Exists(fullPath).Should().BeFalse();

            File.Exists(exportPath).Should().BeTrue();
            File.ReadAllText(exportPath).Should().Be("Contrato Finalizado");

            manager.IsFileShared(fileName).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
            if (File.Exists(exportPath)) File.Delete(exportPath);
        }
    }

    [Fact]
    public async Task Nao_Deve_Concluir_Arquivo_Bloqueado_Por_Outro_Advogado()
    {
        var mockGit = Substitute.For<IVersionControlService>();
        var mockDiscovery = Substitute.For<IDiscoveryService>();
        var mockServer = Substitute.For<IP2PServer>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

        string uniqueId = Guid.NewGuid().ToString();
        string tempPath = Path.Combine(Path.GetTempPath(), $"LawVersion_Test_{uniqueId}");
        string exportPath = Path.Combine(Path.GetTempPath(), $"LawVersion_Export_{uniqueId}.docx");
        
        if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);

        string fileName = "contrato_bloqueado.docx";
        string fullPath = Path.Combine(tempPath, fileName);
        File.WriteAllText(fullPath, "Contrato Bloqueado");

        try
        {
            using var manager = new P2PManager(
                tempPath,
                "Dr. Joao",
                5004,
                mockGit,
                mockDiscovery,
                mockServer,
                loggerFactory
            );

            manager.InitializeSystem();

            var activeLocksField = typeof(VersionSyncServiceImpl).GetField("ActiveLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var dict = activeLocksField?.GetValue(null) as System.Collections.IDictionary;
            dict?.Clear();

            await manager.ShareFileWithAsync(fileName, "Dra. Maria");
            
            var service = new VersionSyncServiceImpl();
            await service.RequestLock(new LockRequest
            {
                FileName = fileName,
                LawyerName = "Dra. Maria",
                MachineId = "remote-machine"
            }, null!);

            Func<Task> act = async () => await manager.CompleteFileAsync(fileName, exportPath);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*bloqueado por Dra. Maria*");

            File.Exists(fullPath).Should().BeTrue();
            File.Exists(exportPath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
            if (File.Exists(exportPath)) File.Delete(exportPath);
        }
    }
}

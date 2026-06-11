using LawVersion.Core;
using FluentAssertions;

namespace LawVersion.Tests;

public class VersionControlTests : IDisposable
{
    private readonly string _testPath;

    public VersionControlTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "LawVersion_Tests_" + Guid.NewGuid().ToString());
        if (!Directory.Exists(_testPath)) Directory.CreateDirectory(_testPath);
    }

    [Fact]
    public void Deve_Inicializar_Repositorio_Git()
    {
        var service = new VersionControlService(_testPath);
        
        service.InitializeRepository(_testPath);
        
        var gitPath = Path.Combine(_testPath, ".git");
        Directory.Exists(gitPath).Should().BeTrue();
    }

    [Fact]
    public void Deve_Gerar_Historico_De_Arquivos_Docx()
    {
        var service = new VersionControlService(_testPath);
        service.InitializeRepository(_testPath);
        
        string fileName = "contrato.docx";
        string fullPath = Path.Combine(_testPath, fileName);
        File.WriteAllText(fullPath, "Conteudo de Teste");

        service.CommitFile(fileName, "Versao Inicial");
        
        var history = service.GetCommitHistory(fileName);

        history.Should().NotBeEmpty();
        // Verificamos se algum item da lista contém a mensagem do commit
        history.Any(h => h.Contains("Versao Inicial")).Should().BeTrue();
    }

    [Fact]
    public void Deve_Restaurar_Versao_Anterior_Do_Arquivo()
    {
        var service = new VersionControlService(_testPath);
        service.InitializeRepository(_testPath);
        
        string fileName = "contrato.docx";
        string fullPath = Path.Combine(_testPath, fileName);
        
        // Escreve e comita versão 1
        File.WriteAllText(fullPath, "Versao Um - Conteudo Original");
        service.CommitFile(fileName, "Versao 1");
        
        // Escreve e comita versão 2
        File.WriteAllText(fullPath, "Versao Dois - Alterado");
        service.CommitFile(fileName, "Versao 2");
        
        // Recupera o histórico
        var history = service.GetCommitHistory(fileName);
        history.Count.Should().BeGreaterThanOrEqualTo(2);
        
        // O commit "Versao 1" deve ser o mais antigo (último da lista)
        var firstCommitLine = history.Last(h => h.Contains("Versao 1"));
        var firstCommitSha = firstCommitLine.Split('|')[0].Trim();
        
        // Executa restauração para a Versão 1
        service.RestoreFileVersion(fileName, firstCommitSha);
        
        // Valida se os bytes originais voltaram para o disco
        var contentOnDisk = File.ReadAllText(fullPath);
        contentOnDisk.Should().Be("Versao Um - Conteudo Original");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_testPath)) return;

        int tentativas = 3;
        while (tentativas > 0)
        {
            try
            {
                Directory.Delete(_testPath, true);
                break; 
            }
            catch (IOException)
            {
                tentativas--;
                Thread.Sleep(500);
            }
        }
    }
}
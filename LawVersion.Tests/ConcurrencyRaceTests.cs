using LawVersion.Network;
using LawVersion.Network.Services;
using FluentAssertions;
using Xunit.Abstractions;

namespace LawVersion.Tests;

public class ConcurrencyRaceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Deve_Garantir_Que_Apenas_Um_Advogado_Consiga_Lock_Em_Acesso_Simultaneo()
    {
        var service = new VersionSyncServiceImpl();
        var nomeArquivo = "Contrato_Sigiloso.docx";
        int numeroDeTentativasSimultaneas = 100;
        
        var pedidos = Enumerable.Range(1, numeroDeTentativasSimultaneas)
            .Select(i => new LockRequest 
            { 
                FileName = nomeArquivo, 
                LawyerName = $"Advogado_{i}" 
            })
            .ToList();

        output.WriteLine($"[TESTE] Disputa iniciada para {nomeArquivo} com {numeroDeTentativasSimultaneas} threads.");

        var tarefas = pedidos.Select(req => service.RequestLock(req, null!));
        var resultados = await Task.WhenAll(tarefas);

        var sucessos = resultados.Where(r => r.IsGranted).ToList();
        
        sucessos.Should().HaveCount(1, "em uma condição de corrida, o lock deve ser exclusivo.");

        var ganhador = sucessos.First();
        output.WriteLine($"[RESULTADO] Vencedor do Lock: {ganhador.CurrentOwner}");
    }
}
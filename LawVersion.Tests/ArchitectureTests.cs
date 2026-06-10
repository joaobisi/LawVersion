using NetArchTest.Rules;
using FluentAssertions;
using LawVersion.Core;
using LawVersion.Network;
using LawVersion.UI.ViewModels;

namespace LawVersion.Tests;

public class ArchitectureTests
{
    private const string NamespaceUi = "LawVersion.UI";
    private const string NamespaceCore = "LawVersion.Core";

    [Fact]
    public void Camada_Core_Nao_Deve_Depender_Da_Ui()
    {
        var result = Types.InAssembly(typeof(P2PManager).Assembly)
            .ShouldNot()
            .HaveDependencyOn(NamespaceUi)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("O Core nunca deve conhecer a Interface Gráfica.");
    }

    [Fact]
    public void Camada_Network_Nao_Deve_Depender_Do_Core_Nem_Da_Ui()
    {
        var result = Types.InAssembly(typeof(IDiscoveryService).Assembly)
            .ShouldNot()
            .HaveDependencyOn(NamespaceCore)
            .And()
            .HaveDependencyOn(NamespaceUi)
            .GetResult();

        result.IsSuccessful.Should().BeTrue("A camada de rede deve ser independente das regras de negócio e da UI.");
    }

    [Fact]
    public void ViewModels_Devem_Residir_No_Namespace_Correto()
    {
        var result = Types.InAssembly(typeof(MainViewModel).Assembly)
            .That()
            .HaveNameEndingWith("ViewModel")
            .Should()
            .ResideInNamespace("LawVersion.UI.ViewModels")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Todas as ViewModels devem estar na pasta ViewModels.");
    }
}
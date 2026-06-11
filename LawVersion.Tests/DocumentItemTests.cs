using LawVersion.UI.Models;
using LawVersion.UI.ViewModels;
using FluentAssertions;

namespace LawVersion.Tests;

public class DocumentItemTests
{
    [Fact]
    public void Deve_Retornar_Estado_Livre_Quando_Nao_Houver_Dono_Do_Lock()
    {
        var item = new DocumentItem
        {
            Name = "teste.docx",
            CurrentOwner = string.Empty,
            IsOwnerMe = false
        };

        item.IsLocked.Should().BeFalse();
        item.IsLockedByOther.Should().BeFalse();
        item.StatusColor.Should().Be("#66BB6A");
        item.StatusText.Should().Be("Livre para edição");
        item.StatusIcon.Should().Contain("M14,2H6");
    }

    [Fact]
    public void Deve_Retornar_Estado_Editando_Pelo_Proprio_Usuario()
    {
        var item = new DocumentItem
        {
            Name = "teste.docx",
            CurrentOwner = "Dr. Joao",
            IsOwnerMe = true
        };

        item.IsLocked.Should().BeTrue();
        item.IsLockedByOther.Should().BeFalse();
        item.StatusColor.Should().Be("#89B4FA");
        item.StatusText.Should().Be("Você está editando");
        item.StatusIcon.Should().Contain("M14.06,9");
    }

    [Fact]
    public void Deve_Retornar_Estado_Editando_Por_Outro_Usuario()
    {
        var item = new DocumentItem
        {
            Name = "teste.docx",
            CurrentOwner = "Dra. Maria",
            IsOwnerMe = false
        };

        item.IsLocked.Should().BeTrue();
        item.IsLockedByOther.Should().BeTrue();
        item.StatusColor.Should().Be("#EF5350");
        item.StatusText.Should().Be("Editando: Dra. Maria");
        item.StatusIcon.Should().Contain("M12,17");
    }

    [Fact]
    public void Deve_Retornar_Propriedades_De_Botao_Corretas_Para_Peer_Nao_Compartilhado()
    {
        var peerItem = new SharePeerItem("Dr. Joao", false);

        peerItem.CanShare.Should().BeTrue();
        peerItem.ButtonText.Should().Be("Compartilhar");
        peerItem.ButtonColor.Should().Be("#89B4FA");
        peerItem.ButtonTextColor.Should().Be("#1E1E2E");
    }

    [Fact]
    public void Deve_Retornar_Propriedades_De_Botao_Corretas_Para_Peer_Ja_Compartilhado()
    {
        var peerItem = new SharePeerItem("Dra. Maria", true);

        peerItem.CanShare.Should().BeFalse();
        peerItem.ButtonText.Should().Be("✓ Compartilhado");
        peerItem.ButtonColor.Should().Be("#45475A");
        peerItem.ButtonTextColor.Should().Be("#A6ADC8");
    }
}

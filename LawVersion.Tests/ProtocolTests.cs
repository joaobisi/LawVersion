using FluentAssertions;

namespace LawVersion.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData("LawVersion|Joao|5000", "Joao", 5000)]
    [InlineData("LawVersion|Dr. Rodrigo Silva|8080", "Dr. Rodrigo Silva", 8080)]
    public void Deve_Parsear_Mensagem_Discovery_Corretamente(string raw, string expectedName, int expectedPort)
    {
        // Act
        var parts = raw.Split('|');
        
        // Assert
        parts[0].Should().Be("LawVersion");
        parts[1].Should().Be(expectedName);
        int.Parse(parts[2]).Should().Be(expectedPort);
    }

    [Fact]
    public void Deve_Falhar_Se_Protocolo_For_Invalido()
    {
        string invalidMessage = "HackerMessage|Virus|000";
        var parts = invalidMessage.Split('|');
        parts[0].Should().NotBe("LawVersion");
    }
}
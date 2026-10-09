using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class PokemonGeneratorTests
{
    private const string First = "Pikachu\nLevel: 50\n- Thunder Shock";
    private const string Second = "Eevee\nLevel: 50\n- Tackle";

    [Fact]
    public async Task StandardTwoMemberImportGeneratesLegalNativePokemonInOrder()
    {
        var result = await new PokemonGenerator().GenerateAsync(First + "\n\n" + Second);
        Assert.Equal(2, result.Count);
        Assert.Equal((ushort)Species.Pikachu, result[0].Pokemon.Species);
        Assert.Equal((ushort)Species.Eevee, result[1].Pokemon.Species);
        Assert.All(result, item => Assert.True(new LegalityAnalysis(item.Pokemon).Valid));
    }

    [Fact]
    public async Task OldBotSeparatorAndCodeFencesImportBothMembers()
    {
        var result = await new PokemonGenerator().GenerateAsync("```showdown\n" + First + "\n---\n" + Second + "\n```");
        Assert.Equal(2, result.Count);
        Assert.Equal((ushort)Species.Pikachu, result[0].Pokemon.Species);
        Assert.Equal((ushort)Species.Eevee, result[1].Pokemon.Species);
    }

    [Theory]
    [InlineData("Pikachu\nEVs: 252 HP / 252 Atk / 252 Spe")]
    [InlineData("Pikachu\nEVs: 253 HP")]
    [InlineData("Pikachu\n- Definitely Not A Move")]
    [InlineData("DefinitelyNotAPokemon\n- Tackle")]
    public async Task InvalidLaterBlockRejectsWholeBatch(string invalid)
    {
        await Assert.ThrowsAsync<OrderException>(() => new PokemonGenerator().GenerateAsync(First + "\n\n" + invalid + "\n\n" + Second));
    }

    [Fact]
    public async Task RecognizedButImpossibleExplicitMoveIsNotSilentlyReplaced()
    {
        await Assert.ThrowsAsync<OrderException>(() => new PokemonGenerator().GenerateAsync("Pikachu\nLevel: 50\n- Spacial Rend"));
    }
}

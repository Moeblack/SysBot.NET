using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class SVAutoOTTests
{
    private static TradePartnerSV Partner(uint id = 1809803390)
    {
        var status = new TradeMyStatus();
        BinaryPrimitives.WriteUInt32LittleEndian(status.Data, id);
        status.Data[5] = 1;
        var name = new PK9 { OriginalTrainerName = "咖喱" };
        name.OriginalTrainerTrash[..24].CopyTo(status.Data.AsSpan(8,24));
        return new TradePartnerSV(status);
    }

    [Theory]
    [InlineData("Annihilape", true)]
    [InlineData("Volcarona", true)]
    [InlineData("Metagross", true)]
    [InlineData("Whimsicott", false)]
    [InlineData("Typhlosion", true)]
    public async Task ActualGenerationAdoptsRecipientWithoutChangingRequestedData(string species, bool shiny)
    {
        var generated = await new PokemonGenerator().GenerateAsync($"{species}\nShiny: {(shiny ? "Yes" : "No")}\nLanguage: ChineseS\nLevel: 100\n");
        var original = Assert.Single(generated).Pokemon;
        Assert.True(SVAutoOT.TryApply(original, Partner(), out var actual, out var reason), reason);
        Assert.Equal("咖喱", actual.OriginalTrainerName);
        Assert.Equal(1809803390u, actual.ID32);
        Assert.Equal(803390u, actual.TrainerTID7);
        Assert.Equal(1809u, actual.TrainerSID7);
        Assert.Equal(1, actual.OriginalTrainerGender);
        Assert.Equal(shiny, actual.IsShiny);
        Assert.Equal(original.Language, actual.Language);
        Assert.Equal(original.Version, actual.Version);
        Assert.Equal(original.MetDate, actual.MetDate);
        Assert.Equal(original.MetLocation, actual.MetLocation);
        Assert.Equal(original.Ball, actual.Ball);
        Assert.Equal(original.IV32, actual.IV32);
        Assert.Equal(original.EncryptionConstant, actual.EncryptionConstant);
        Assert.True(actual.ChecksumValid);
        Assert.True(new LegalityAnalysis(actual).Valid);
        Assert.Equal("SysBot", original.OriginalTrainerName); // clone, not shared mutation
    }

    [Fact]
    public async Task NonShinyDoesNotBecomeShinyWhenRecipientIdsCoincideWithPid()
    {
        var source = Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\nShiny: No\n")).Pokemon;
        Assert.False(source.IsShiny);
        Assert.True(SVAutoOT.TryApply(source, Partner(source.PID), out var actual, out var reason), reason);
        Assert.False(actual.IsShiny);
        Assert.True(new LegalityAnalysis(actual).Valid);
    }

    [Fact]
    public async Task TrackedIndividualRetainsOriginalIdentity()
    {
        var source = Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\n")).Pokemon;
        source.Tracker=123;source.RefreshChecksum();
        Assert.False(SVAutoOT.TryApply(source, Partner(), out var actual, out _));
        Assert.Same(source,actual);
    }
}

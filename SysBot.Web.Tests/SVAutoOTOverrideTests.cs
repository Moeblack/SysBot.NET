using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class SVAutoOTOverrideTests
{
    private const uint SourceId = 123456789;
    private const uint PartnerId = 1809803390;

    public static IEnumerable<object[]> OverrideCases()
    {
        var masks = new[]
        {
            TrainerOverrideFields.None,
            TrainerOverrideFields.All,
            TrainerOverrideFields.Name,
            TrainerOverrideFields.TID,
            TrainerOverrideFields.SID,
            TrainerOverrideFields.Gender,
        };
        foreach (var species in new[] { "Eevee", "Annihilape" })
        foreach (var mask in masks)
        foreach (var shiny in new[] { false, true })
            yield return new object[] { species, mask, shiny };
    }

    [Theory]
    [MemberData(nameof(OverrideCases))]
    public async Task ExplicitFieldsArePreservedAndMissingFieldsAdoptPartner(string species, TrainerOverrideFields mask, bool shiny)
    {
        var generated = await new PokemonGenerator().GenerateAsync($"{species}\nShiny: {(shiny ? "Yes" : "No")}\nLanguage: ChineseS\nLevel: 100\n");
        var source = Assert.Single(generated).Pokemon;
        Assert.Equal(shiny, source.IsShiny);
        var shinyXor = source.ShinyXor;
        source.OriginalTrainerTrash.Clear();
        source.OriginalTrainerName = "手填";
        source.OriginalTrainerGender = 0;
        source.ID32 = SourceId;
        // Changing IDs must not change the generated shiny class before exercising AutoOT.
        source.PID = ((uint)(source.TID16 ^ source.SID16 ^ (source.PID & 0xFFFF) ^ shinyXor) << 16) | (source.PID & 0xFFFF);
        source.RefreshChecksum();
        Assert.Equal(shiny, source.IsShiny);
        var sourceLegality = new LegalityAnalysis(source);
        Assert.True(sourceLegality.Valid, sourceLegality.Report());
        var snapshot = (PK9)source.Clone();

        var status = new TradeMyStatus();
        BinaryPrimitives.WriteUInt32LittleEndian(status.Data, PartnerId);
        status.Data[5] = 1;
        var partnerName = new PK9 { OriginalTrainerName = "咖喱" };
        partnerName.OriginalTrainerTrash[..24].CopyTo(status.Data.AsSpan(8, 24));
        var partner = new TradePartnerSV(status);

        Assert.True(SVAutoOT.TryApply(source, partner, out var actual, out var reason, mask), reason);
        Assert.NotSame(source, actual);
        Assert.Equal(mask.HasFlag(TrainerOverrideFields.Name) ? "手填" : "咖喱", actual.OriginalTrainerName);
        Assert.Equal(mask.HasFlag(TrainerOverrideFields.Gender) ? 0 : 1, actual.OriginalTrainerGender);
        var expectedTid = mask.HasFlag(TrainerOverrideFields.TID) ? SourceId % 1_000_000 : PartnerId % 1_000_000;
        var expectedSid = mask.HasFlag(TrainerOverrideFields.SID) ? SourceId / 1_000_000 : PartnerId / 1_000_000;
        Assert.Equal(expectedTid, actual.TrainerTID7);
        Assert.Equal(expectedSid, actual.TrainerSID7);
        Assert.Equal(expectedSid * 1_000_000 + expectedTid, actual.ID32);
        Assert.Equal(shiny, actual.IsShiny);
        if (shiny)
            Assert.Equal(snapshot.ShinyXor, actual.ShinyXor);
        Assert.Equal(snapshot.Language, actual.Language);
        Assert.Equal(snapshot.Version, actual.Version);
        Assert.Equal(snapshot.Species, actual.Species);
        Assert.Equal(snapshot.MetDate, actual.MetDate);
        Assert.Equal(snapshot.MetLocation, actual.MetLocation);
        Assert.Equal(snapshot.Ball, actual.Ball);
        Assert.Equal(snapshot.IV32, actual.IV32);
        Assert.Equal(snapshot.EncryptionConstant, actual.EncryptionConstant);
        Assert.True(actual.ChecksumValid);
        var actualLegality = new LegalityAnalysis(actual);
        Assert.True(actualLegality.Valid, actualLegality.Report());
        Assert.Equal(snapshot.Data, source.Data);
        Assert.True(source.ChecksumValid);
        if (mask == TrainerOverrideFields.All)
            Assert.Equal(snapshot.Data, actual.Data);
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class ShowdownPlusTests
{
    [Fact]
    public async Task ExplicitTrainerIdentityIsGeneratedAndTaggedForAutoOT()
    {
        var item = Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\nOT: 小明\nOTGender: female\nTID: 123456\nSID: 0123\n"));
        Assert.Equal(TrainerOverrideFields.All, item.TrainerOverrides);
        Assert.Equal("小明", item.Pokemon.OriginalTrainerName);
        Assert.Equal(1, item.Pokemon.OriginalTrainerGender);
        Assert.Equal(123456u, item.Pokemon.TrainerTID7);
        Assert.Equal(123u, item.Pokemon.TrainerSID7);
        Assert.True(new LegalityAnalysis(item.Pokemon).Valid);
    }

    [Fact]
    public async Task OptionalSizeDatesPartnerMarkAndEquippedTitleReallyGenerate()
    {
        var item = Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\nHeight: 128\nWeight: 128\nScale: 128\nMetDate: 2025-06-02\nEggMetDate: 2025-06-01\nMarks: Partner\nTitle: Partner\n"));
        var pk=item.Pokemon;
        Assert.Equal(128,pk.HeightScalar);Assert.Equal(128,pk.WeightScalar);Assert.Equal(128,pk.Scale);
        Assert.Equal(new DateOnly(2025,6,2),pk.MetDate);Assert.Equal(new DateOnly(2025,6,1),pk.EggMetDate);
        Assert.True(pk.RibbonMarkPartner);
        Assert.Equal((sbyte)RibbonIndex.MarkPartner,pk.AffixedRibbon);
        Assert.True(new LegalityAnalysis(pk).Valid);
    }

    [Fact]
    public async Task HistoricalMightiestRillaboomUsesActualMightiestEncounter()
    {
        var item=Assert.Single(await new PokemonGenerator().GenerateAsync("Rillaboom @ Adamant Mint\nLevel: 100\nShiny: No\nLanguage: ChineseS\nBall: Nest Ball\nAbility: Grassy Surge\nJolly Nature\nMarks: Mightiest, Partner\nTitle: Mightiest\nMetLocation: 30024\nMetLevel: 100\nMetDate: 2023-07-28\n- Grassy Glide\n- High Horsepower\n- Drum Beating\n- U-turn"));
        var pk=item.Pokemon;
        Assert.True(pk.RibbonMarkMightiest);Assert.True(pk.RibbonMarkPartner);
        Assert.Equal((sbyte)RibbonIndex.MarkMightiest,pk.AffixedRibbon);
        Assert.Equal(30024,pk.MetLocation);Assert.Equal(100,pk.MetLevel);
        var legality=new LegalityAnalysis(pk);Assert.True(legality.Valid);
        Assert.Equal("EncounterMight9",legality.EncounterOriginal.GetType().Name);
    }

    [Fact]
    public async Task NativeDotSyntaxAndEightDigitDateRemainCompatible()
    {
        var item=Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\n.RibbonMarkPartner=true\n.Scale=128\n.MetDate=20250602\n"));
        Assert.True(item.Pokemon.RibbonMarkPartner);
        Assert.Equal(128,item.Pokemon.Scale);
        Assert.Equal(new DateOnly(2025,6,2),item.Pokemon.MetDate);
    }

    [Fact]
    public async Task IndividualRibbonCanBeRequestedAndEquipped()
    {
        var item=Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\nRibbons: ChampionPaldea\nTitle: ChampionPaldea\n"));
        Assert.True(item.Pokemon.RibbonChampionPaldea);
        Assert.Equal((sbyte)RibbonIndex.ChampionPaldea,item.Pokemon.AffixedRibbon);
    }

    [Fact]
    public void LowTraditionalEvsAreNotGuessedAsChampionsPoints()
    {
        var request=Assert.Single(PokemonGenerator.ParseRequests("Eevee\nEVs: 32 HP / 32 Atk / 2 Spe"));
        Assert.Equal(32,request.Set.EVs[0]);Assert.Equal(32,request.Set.EVs[1]);Assert.Equal(2,request.Set.EVs[3]);
        Assert.Empty(request.Options.Notices);
    }

    [Theory]
    [InlineData("Format: Champions\nEevee\nEVs: 32 HP / 32 Atk / 2 Spe")]
    [InlineData("Eevee\nSPs: 32 HP / 32 Atk / 2 Spe")]
    [InlineData("=== [gen9champions] A team ===\nEevee\nEVs: 32 HP / 32 Atk / 2 Spe")]
    public void ChampionsInputConvertsAndDisclosesLoss(string text)
    {
        var request=Assert.Single(PokemonGenerator.ParseRequests(text));
        Assert.Equal(252,request.Set.EVs[0]);Assert.Equal(252,request.Set.EVs[1]);Assert.Equal(4,request.Set.EVs[3]);
        Assert.NotEmpty(request.Options.Notices);
        Assert.Contains(request.Options.Notices,n=>n.Contains("EVs: 252 HP / 252 Atk / 4 Spe"));
    }

    [Fact]
    public async Task ChampionsConversionSurvivesRealGeneration()
    {
        var item=Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\nSPs: 32 HP / 32 Atk / 2 Spe"));
        Assert.Equal(252,item.Pokemon.EV_HP);Assert.Equal(252,item.Pokemon.EV_ATK);Assert.Equal(4,item.Pokemon.EV_SPE);
        Assert.NotEmpty(item.Notices!);
    }

    [Fact]
    public void TeamDirectivesAndPerMemberOptionsDoNotLeak()
    {
        var sets=PokemonGenerator.ParseRequests("Format: Champions\n\nEevee\nOT: Alice\nEVs: 32 HP\n\nPikachu\nEVs: 32 Spe\n\nFormat: Showdown\n\nPichu\nEVs: 4 HP");
        Assert.Equal(3,sets.Count);
        Assert.Equal(TrainerOverrideFields.Name,sets[0].Options.TrainerOverrides);
        Assert.Equal(TrainerOverrideFields.None,sets[1].Options.TrainerOverrides);
        Assert.Equal(252,sets[0].Set.EVs[0]);Assert.Equal(252,sets[1].Set.EVs[3]);Assert.Equal(4,sets[2].Set.EVs[0]);
    }

    [Theory]
    [InlineData("Scale: 256")]
    [InlineData("MetDate: 2025-02-30")]
    [InlineData("OTGender: unknown")]
    [InlineData("TID: 1000000")]
    [InlineData("TID: 999999\nSID: 4294")]
    [InlineData("Marks: NotARealMark")]
    [InlineData("Marks: , ,")]
    [InlineData("Title: MAX_COUNT")]
    [InlineData("Title: 111")]
    [InlineData("Title: Mightiest\n.RibbonMarkMightiest=false")]
    [InlineData(".PID=1")]
    [InlineData("SPs: 32 HP / 32 Atk / 32 Spe")]
    [InlineData("EVs: 4 HP\nSPs: 2 HP")]
    public void InvalidOptionalFieldsFailBeforeGeneration(string field)
    {
        Assert.Throws<OrderException>(()=>PokemonGenerator.ParseRequests("Eevee\n"+field));
    }

    [Fact]
    public void OtherGenerationTargetIsExplicitlyUnsupportedNotSilentlyConverted()
    {
        var error=Assert.Throws<OrderException>(()=>PokemonGenerator.ParseRequests("Target: SWSH\nEevee"));
        Assert.Contains("适配器",error.Message);
    }
    [Fact]
    public void OrdinaryTeamNameChampionsDoesNotChangeEvUnits()
    {
        var set=Assert.Single(PokemonGenerator.ParseRequests("=== My Champions team ===\nEevee\nEVs: 32 HP"));
        Assert.Equal(32,set.Set.EVs[0]);
    }

    [Fact]
    public async Task EveryCopyableDocumentationExampleGeneratesSuccessfully()
    {
        var doc=System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"SHOWDOWN-PLUS.zh-CN.md"));
        var examples=System.Text.RegularExpressions.Regex.Matches(doc,"```text\r?\n([\\s\\S]*?)```");
        Assert.Equal(3,examples.Count);
        foreach(System.Text.RegularExpressions.Match example in examples)
            Assert.Single(await new PokemonGenerator().GenerateAsync(example.Groups[1].Value));
    }

    [Fact]
    public async Task IndependentScalarsAndEggEncounterDetailsAreNotIgnored()
    {
        var item = Assert.Single(await new PokemonGenerator().GenerateAsync("Pikachu\nHeight: 123\nWeight: 124\nScale: 125\nEggMetDate: 2024-05-05\nMetDate: 2024-05-06\nEggLocation: 30002\nMetLocation: 6\nMetLevel: 1"));
        Assert.Equal(123,item.Pokemon.HeightScalar);Assert.Equal(124,item.Pokemon.WeightScalar);Assert.Equal(125,item.Pokemon.Scale);
        Assert.Equal(30002,item.Pokemon.EggLocation);Assert.Equal(6,item.Pokemon.MetLocation);Assert.Equal(1,item.Pokemon.MetLevel);
        Assert.Equal(new DateOnly(2024,5,5),item.Pokemon.EggMetDate);
    }

    [Fact]
    public async Task TitleNoneKeepsRequestedMarkButDoesNotEquipIt()
    {
        var item=Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\nMarks: Partner\nTitle: None"));
        Assert.True(item.Pokemon.RibbonMarkPartner);
        Assert.Equal(-1,item.Pokemon.AffixedRibbon);
    }

    [Theory]
    [InlineData("#Hero (Eevee)", "#Hero")]
    [InlineData("//Hero (Eevee)", "//Hero")]
    [InlineData("OT: A (Eevee)", "OT: A")]
    [InlineData("Format: SP (Eevee)", "Format: SP")]
    public void ValidPsNicknamesTakePriorityOverExtensions(string header, string nickname)
    {
        var request=Assert.Single(PokemonGenerator.ParseRequests(header+"\nAbility: Run Away\n- Protect"));
        Assert.Equal((ushort)Species.Eevee,request.Set.Species);
        Assert.Equal(nickname,request.Set.Nickname);
        Assert.Equal(TrainerOverrideFields.None,request.Options.TrainerOverrides);
    }

    [Fact]
    public void FormatDirectiveNeverReinterpretsAnEarlierMember()
    {
        var requests=PokemonGenerator.ParseRequests("Format: Champions\nEevee\nEVs: 32 HP\nFormat: Showdown\nPikachu\nEVs: 4 HP");
        Assert.Equal(252,requests[0].Set.EVs[0]);Assert.NotEmpty(requests[0].Options.Notices);
        Assert.Equal(4,requests[1].Set.EVs[0]);Assert.Empty(requests[1].Options.Notices);
        var reversed=Assert.Single(PokemonGenerator.ParseRequests("Eevee\nEVs: 32 HP\nFormat: Champions"));
        Assert.Equal(32,reversed.Set.EVs[0]);Assert.Empty(reversed.Options.Notices);
    }

    [Fact]
    public async Task BallAndLanguageKeysAreNormalizedBeforeNativeAlm()
    {
        var item=Assert.Single(await new PokemonGenerator().GenerateAsync("Eevee\nball: Nest Ball\nlanguage: English"));
        Assert.Equal((byte)Ball.Nest,item.Pokemon.Ball);
        Assert.Equal((int)LanguageID.English,item.Pokemon.Language);
    }

    [Theory]
    [InlineData("Ball: Nest Ball\nball: Poke Ball")]
    [InlineData("Language: English\nlanguage: Japanese")]
    public void ConflictingBallOrLanguageDoesNotSilentlyUseLastValue(string fields) =>
        Assert.Throws<OrderException>(()=>PokemonGenerator.ParseRequests("Eevee\n"+fields));

}

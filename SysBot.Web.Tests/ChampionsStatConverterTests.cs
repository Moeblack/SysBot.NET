using System;
using System.Collections.Generic;
using System.Linq;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class ChampionsStatConverterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(2, 12)]
    [InlineData(16, 124)]
    [InlineData(31, 244)]
    [InlineData(32, 252)]
    public void UsesMinimumEvsForLevel50OddIv(int sp, int ev)
    {
        var result = ChampionsStatConverter.Convert([sp, 0, 0, 0, 0, 0]);
        Assert.Equal(ev, result.Evs[0]);
        Assert.True(result.IsLossless);
        Assert.Equal(0, result.LostStatPoints);
    }

    [Fact]
    public void MinimumEvsMatchActualPreNatureFormulaForEveryBaseAndSp()
    {
        for (var baseStat = 1; baseStat <= 255; baseStat++)
        for (var sp = 0; sp <= 32; sp++)
        {
            var ev = ChampionsStatConverter.Convert([sp, 0, 0, 0, 0, 0]).Evs[0];
            var champions = ((2 * baseStat + 31) / 2) + sp;
            var traditional = (2 * baseStat + 31 + (ev / 4)) / 2;
            Assert.Equal(champions, traditional);
            // Constants for HP/non-HP and identical nature modifiers preserve this equality.
            if (ev > 0)
                Assert.True((2 * baseStat + 31 + ((ev - 1) / 4)) / 2 < champions);
        }
    }

    [Fact]
    public void CommonTwoMaxAndTwoSpreadCannotBeLosslesslyRepresented()
    {
        var result = ChampionsStatConverter.Convert([2, 32, 0, 32, 0, 0]);
        Assert.Equal(new[] { 4, 252, 0, 252, 0, 0 }, result.Evs);
        Assert.Equal(new[] { 1, 32, 0, 32, 0, 0 }, result.ConvertedStatPoints);
        Assert.False(result.IsLossless);
        Assert.Equal(1, result.LostStatPoints);
        Assert.Equal(508, result.Evs.Sum());
        Assert.Contains(result.Messages, message => message.Contains("516 EV"));
        Assert.Contains(result.Messages, message => message.Contains("HP：2 SP → 1 SP"));
        Assert.Equal("EVs: 4 HP / 252 Atk / 252 Spe", result.ShowdownEVLine);
    }

    [Fact]
    public void SixPositiveStatsCanPreserveFull66PointBudget()
    {
        var result = ChampionsStatConverter.Convert([11, 11, 11, 11, 11, 11]);
        Assert.True(result.IsLossless);
        Assert.Equal(504, result.Evs.Sum());
        Assert.All(result.Evs, value => Assert.Equal(84, value));
    }

    [Fact]
    public void TiesUseShowdownOrderAndStorageUsesPkhexOrder()
    {
        var result = ChampionsStatConverter.Convert([0, 32, 0, 1, 32, 1]);
        Assert.Equal(new[] { 0, 252, 0, 4, 252, 0 }, result.Evs);
        Assert.False(result.IsLossless);
        Assert.Contains(result.Messages, message => message.Contains("SpD：1 SP → 0 SP"));
        Assert.Equal("EVs: 252 Atk / 252 SpA / 4 Spe", result.ShowdownEVLine);
    }

    [Fact]
    public void ZeroSpreadIsExplicitAndResultDoesNotAliasInput()
    {
        var input = new int[6];
        var result = ChampionsStatConverter.Convert(input);
        input[0] = 32;
        Assert.Equal("EVs: 0 HP", result.ShowdownEVLine);
        Assert.Equal(0, result.ConvertedStatPoints[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)result.Evs)[0] = 252);
        Assert.Contains(result.Messages, message => message.Contains("其他等级、IV"));
    }

    [Fact]
    public void ValidRandomSpreadsAreDeterministicLegalAndNeverOverTarget()
    {
        var random = new Random(104729);
        for (var sample = 0; sample < 5000; sample++)
        {
            var points = new int[6];
            for (var point = 0; point < 66; point++)
            {
                var index = random.Next(6);
                if (points[index] < 32 && random.Next(4) != 0)
                    points[index]++;
            }
            var result = ChampionsStatConverter.Convert(points);
            var repeated = ChampionsStatConverter.Convert(points);
            Assert.InRange(result.Evs.Sum(), 0, 510);
            Assert.All(result.Evs, value => Assert.InRange(value, 0, 252));
            Assert.Equal(result.Evs, repeated.Evs);
            Assert.Equal(result.Messages, repeated.Messages);
            Assert.Equal(points.Sum() - result.ConvertedStatPoints.Sum(), result.LostStatPoints);
            Assert.Equal(result.LostStatPoints == 0, result.IsLossless);
            for (var index = 0; index < 6; index++)
                Assert.InRange(result.ConvertedStatPoints[index], 0, points[index]);
        }
    }

    [Theory]
    [InlineData(new int[] { 0, 0, 0, 0, 0 })]
    [InlineData(new int[] { 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new int[] { -1, 0, 0, 0, 0, 0 })]
    [InlineData(new int[] { 33, 0, 0, 0, 0, 0 })]
    [InlineData(new int[] { 32, 32, 3, 0, 0, 0 })]
    [InlineData(new int[] { int.MaxValue, 0, 0, 0, 0, 0 })]
    public void RejectsInvalidSpInsteadOfClampingInput(int[] input) =>
        Assert.Throws<ArgumentException>(() => ChampionsStatConverter.Convert(input));

    [Fact]
    public void RejectsNullInput() =>
        Assert.Throws<ArgumentNullException>(() => ChampionsStatConverter.Convert(null!));
}

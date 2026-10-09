using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SysBot.Pokemon.Web;

/// <summary>
/// Converts explicitly identified Champions SPs to legal traditional EVs. Both arrays use
/// PKHeX order: HP, Attack, Defense, Speed, Special Attack, Special Defense.
/// This does not infer units from small EV values or parse Showdown input.
/// </summary>
public static class ChampionsStatConverter
{
    // Evidence: Pokemon-Champions-Lab's ncp stat_data.js CALC_HP_CHAMP / CALC_STAT_CHAMP
    // add SP directly at Lv.50 with IV 31. Traditional stats put floor(EV / 4) inside
    // the level multiplier. At Lv.50, odd IV 31 allows the first point at 4 EV and
    // each subsequent point at another 8 EV: minimum EV = 8 * SP - 4, not 8 * SP.
    // This is a level-specific stat conversion, not a transferable training equivalence.
    private static readonly int[] ShowdownOrder = [0, 1, 2, 4, 5, 3];
    private static readonly string[] StatNames = ["HP", "Atk", "Def", "Spe", "SpA", "SpD"];

    public static ChampionsStatConversion Convert(IReadOnlyList<int> statPoints)
    {
        ArgumentNullException.ThrowIfNull(statPoints);
        if (statPoints.Count != 6)
            throw new ArgumentException("Champions SPs require exactly six stats in HP/Atk/Def/Spe/SpA/SpD order.", nameof(statPoints));

        var original = statPoints.ToArray();
        if (original.Any(value => value is < 0 or > 32) || original.Sum() > 66)
            throw new ArgumentException("冠军SP单项必须为0～32，总和不超过66。", nameof(statPoints));

        var converted = (int[])original.Clone();
        var evs = converted.Select(ToEvs).ToArray();
        while (evs.Sum() > 510)
        {
            // Preserve the largest investments. Ties use Showdown order, not storage order.
            // Never spend leftover EVs on an unrequested stat or produce an over-target stat.
            var index = ShowdownOrder.Where(i => converted[i] > 0)
                .OrderBy(i => converted[i]).First();
            converted[index]--;
            evs[index] = ToEvs(converted[index]);
        }

        var lost = original.Sum() - converted.Sum();
        var messages = new List<string>
        {
            "冠军SP换算以50级、有效IV为31的性格修正前数值为基准；其他等级、IV或无传统EV的游戏不保证等价。",
        };
        if (lost == 0)
            messages.Add("在上述条件下无损换算，保留全部请求的SP数值增量。");
        else
        {
            messages.Add($"有损换算：原请求需要 {original.Sum(ToEvs)} EV，超过510；优先保留大项，减少 {lost} 点性格修正前增量。");
            foreach (var index in ShowdownOrder.Where(i => original[i] != converted[i]))
                messages.Add($"{StatNames[index]}：{original[index]} SP → {converted[index]} SP（{evs[index]} EV），减少 {original[index] - converted[index]} 点性格修正前增量。");
        }

        // Explicit zero keeps a zero-investment set distinguishable from an omitted EV line.
        var parts = ShowdownOrder.Where(i => evs[i] != 0)
            .Select(i => evs[i].ToString(CultureInfo.InvariantCulture) + " " + StatNames[i]);
        var line = "EVs: " + (evs.Any(value => value != 0) ? string.Join(" / ", parts) : "0 HP");
        return new ChampionsStatConversion(Array.AsReadOnly(evs), Array.AsReadOnly(converted), lost == 0,
            lost, Array.AsReadOnly(messages.ToArray()), line);
    }

    private static int ToEvs(int points) => points == 0 ? 0 : (8 * points) - 4;
}

/// <summary>
/// Lossless refers only to level-50, effective-IV-31 pre-nature stat increments, not to
/// general game equivalence. Nature rounding may hide a reported reduction, and fixed-HP
/// species ignore HP investment. Arrays are immutable snapshots in PKHeX stat order.
/// </summary>
public sealed record ChampionsStatConversion(
    IReadOnlyList<int> Evs,
    IReadOnlyList<int> ConvertedStatPoints,
    bool IsLossless,
    int LostStatPoints,
    IReadOnlyList<string> Messages,
    string ShowdownEVLine);

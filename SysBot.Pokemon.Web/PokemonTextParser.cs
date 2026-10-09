using System.Globalization;
using System.Text.RegularExpressions;
using PKHeX.Core;

namespace SysBot.Pokemon.Web;

public sealed record PokemonTextRequest(ShowdownSet Set, PokemonTextOptions Options);

/// <summary>Showdown+ v1: ordinary PS text plus optional typed metadata and explicit stat units.</summary>
public static class PokemonTextParser
{
    public static IReadOnlyList<PokemonTextRequest> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new OrderException("请粘贴1～12只宝可梦的PS配置。");
        if (text.Length > 60000) throw new OrderException("配置文本过长，请分批导入。");
        var requests = new List<PokemonTextRequest>();
        var lines = new List<string>();
        bool champions = false, blockChampions = false, fenced = false;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            // A valid PS nickname/species header wins over our optional directives/comments.
            if (lines.Count == 0 && line.Length != 0 && new ShowdownSet(line).Species != 0)
            {
                blockChampions = champions;
                lines.Add(line);
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal)) { Flush(); fenced = !fenced; continue; }
            if (line.Length == 0 || line == "---") { Flush(); continue; }
            if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("===", StringComparison.Ordinal) && line.EndsWith("===", StringComparison.Ordinal))
            {
                Flush();
                champions = Regex.IsMatch(line, @"^===\s*\[[^\]]*champions[^\]]*\]", RegexOptions.IgnoreCase);
                continue;
            }
            var directive = Regex.Match(line, @"^(Format|Target)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (directive.Success)
            {
                Flush(); // A format directive starts a new scope; never reinterpret earlier EVs.
                var value = directive.Groups[2].Value.Trim();
                if (directive.Groups[1].Value.Equals("Target", StringComparison.OrdinalIgnoreCase))
                {
                    if (!new[] {"SV", "PK9", "Gen9", "Scarlet", "Violet"}.Contains(value, StringComparer.OrdinalIgnoreCase))
                        throw new OrderException($"Target: {value} 尚无网页派送适配器；当前支持 SV / PK9。其他世代不会被静默当作朱紫派送。");
                }
                else
                {
                    champions = value.ToLowerInvariant() switch
                    {
                        "champions" or "champion" or "sp" => true,
                        "showdown" or "standard" or "ev" or "sv" or "gen3" or "gen4" or "gen5" or "gen6" or "gen7" or "gen8" or "gen9" => false,
                        _ => throw new OrderException($"未知 Format: {value}；使用 Champions 或 Showdown。"),
                    };
                }
                continue;
            }
            if (lines.Count == 0) blockChampions = champions;
            lines.Add(line);
        }
        if (fenced) throw new OrderException("代码围栏未闭合，请补上结尾的 ```。");
        Flush();
        if (requests.Count == 0) throw new OrderException("请粘贴1～12只宝可梦的PS配置。");
        return requests;

        void Flush()
        {
            if (lines.Count == 0) return;
            int index = requests.Count + 1;
            try
            {
                if (index > 12) throw new OrderException("一次最多导入12只宝可梦。");
                var options = new PokemonTextOptions(typeof(PK9));
                var normal = new List<string>();
                bool hasInvestment = false;
                bool firstLine = true;
                foreach (var line in lines)
                {
                    if (firstLine) { normal.Add(line); firstLine = false; continue; }
                    var stat = Regex.Match(line, @"^(EVs|SPs|SP)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
                    if (stat.Success)
                    {
                        if (hasInvestment) throw new OrderException("EVs/SPs重复或混用；每只只写一行努力值。");
                        hasInvestment = true;
                        if (blockChampions || !stat.Groups[1].Value.Equals("EVs", StringComparison.OrdinalIgnoreCase))
                        {
                            var converted = ChampionsStatConverter.Convert(ParseStatPoints(stat.Groups[2].Value));
                            normal.Add(converted.ShowdownEVLine);
                            options.Notices.AddRange(converted.Messages.Select(m => $"第 {index} 只：{m}"));
                            options.Notices.Add($"第 {index} 只实际使用 {converted.ShowdownEVLine}");
                            continue;
                        }
                    }
                    if (!options.TryRead(line)) normal.Add(line);
                }
                options.Complete();
                normal.AddRange(options.NativeLines());
                var sets = ShowdownParsing.GetShowdownSets((IEnumerable<string>)normal).ToArray();
                if (sets.Length != 1) throw new OrderException("每只宝可梦之间请留一个空行或使用 --- 分隔。");
                requests.Add(new PokemonTextRequest(sets[0], options));
            }
            catch (ArgumentException e) { throw new OrderException($"第 {index} 只：{e.Message}"); }
            catch (OrderException e) { throw new OrderException($"第 {index} 只：{e.Message}", e.Details); }
            finally { lines.Clear(); }
        }
    }

    private static int[] ParseStatPoints(string text)
    {
        var result = new int[6];
        var used = new HashSet<int>();
        foreach (var piece in text.Split('/', StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(piece, @"^(\d+)\s+(HP|Atk|Def|SpA|SpD|Spe)$", RegexOptions.IgnoreCase);
            if (!match.Success) throw new OrderException($"无法解析冠军SP：{piece}；例如 SPs: 32 HP / 32 Atk / 2 Spe。");
            int stat = match.Groups[2].Value.ToLowerInvariant() switch { "hp" => 0, "atk" => 1, "def" => 2, "spe" => 3, "spa" => 4, _ => 5 };
            if (!used.Add(stat)) throw new OrderException($"冠军SP中 {match.Groups[2].Value} 重复。");
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw new OrderException("冠军SP数值过大。");
            result[stat] = value;
        }
        return result;
    }
}

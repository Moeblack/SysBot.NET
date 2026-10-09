using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace SysBot.Pokemon.Web;

public sealed class PokemonGenerator : IPokemonGenerator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool initialized;
    private static bool blocked;

    public static LegalitySettings CreateSettings() => new()
    {
        GenerateLanguage = LanguageID.ChineseS,
        GameVersionPriority = GameVersionPriorityType.NativeOnly,
        EnableHOMETrackerCheck = true,
        EnableEasterEggs = false,
        AllowTrainerDataOverride = true,
        AllowBatchCommands = true,
        ForceLevel100for50 = false,
        SetMatchingBalls = true,
        ForceSpecifiedBall = true,
    };

    public static IReadOnlyList<ShowdownSet> ParseSets(string text) => ParseRequests(text).Select(r => r.Set).ToArray();

    public static IReadOnlyList<PokemonTextRequest> ParseRequests(string text)
    {
        var requests = PokemonTextParser.Parse(text);
        for (int i = 0; i < requests.Count; i++) ValidateInput(requests[i].Set, i + 1);
        return requests;
    }

    public async Task<IReadOnlyList<GeneratedPokemon>> GenerateAsync(string text)
    {
        var sets = ParseRequests(text);
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (blocked)
                throw new OrderException("生成器曾超时或初始化失败；为避免后台任务重叠，请重启本地服务。");
            if (!initialized)
            {
                try
                {
                    AutoLegalityWrapper.EnsureInitialized(CreateSettings());
                    initialized = true;
                }
                catch
                {
                    blocked = true;
                    throw new OrderException("合法性生成器初始化失败，请检查依赖和配置后重启服务。");
                }
            }
            // Run CPU work outside the HTTP request thread, but keep the process-wide gate.
            return await Task.Run(() => Generate(sets)).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IReadOnlyList<GeneratedPokemon> Generate(IReadOnlyList<PokemonTextRequest> sets)
    {
        var trainer = AutoLegalityWrapper.GetTrainerInfo<PK9>();
        var output = new List<GeneratedPokemon>(sets.Count);
        var chinese = GameInfo.GetStrings("zh-Hans");
        var english = GameInfo.GetStrings("en");
        for (int i = 0; i < sets.Count; i++)
        {
            var set = sets[i].Set;
            var options = sets[i].Options;
            int index = i + 1;
            // RegenTemplate shares Moves/IVs and sanitizes forms; capture before constructing it.
            var request = new RequestSnapshot(set);
            var template = (RegenTemplate)AutoLegalityWrapper.GetTemplate(set);
            var generated = trainer.GetLegal(template, out var status);
            if (status == "Timeout")
            {
                blocked = true; // ALM timeout does not cancel the underlying Task.Run.
                throw Error(index, "生成超时。本批未入队；请重启服务后再生成，避免后台任务重叠。");
            }
            if (status != "Regenerated")
                throw Error(index, status == "VersionMismatch"
                    ? "PKHeX 与自动合法化组件版本不匹配。"
                    : "无法按该配置生成合法的朱紫宝可梦。", status);
            var pk = EntityConverter.ConvertToType(generated, typeof(PK9), out _) as PK9;
            if (pk is null)
                throw Error(index, "生成结果无法转换为朱紫 PK9。");
            pk.RefreshChecksum();
            var legality = new LegalityAnalysis(pk);
            if (!legality.Valid)
                throw Error(index, "该字段组合与合法遭遇不兼容，请调整配置。", legality.Report());
            if (!pk.CanBeTraded(legality.EncounterOriginal))
                throw Error(index, "该宝可梦不可交换。");
            if (legality.EncounterOriginal.Context != pk.Context || pk.GO)
                throw Error(index, "配置需要跨作来源；首版只派送朱紫原生宝可梦。");
            if (pk is IHomeTrack { HasTracker: true })
                throw Error(index, "生成结果带有 HOME 追踪记录，不能作为原生配置派送。");
            var differences = request.Compare(pk, template);
            differences.AddRange(options.Compare(pk));
            if (differences.Count != 0)
                throw Error(index, "合法化改变了指定配置，请调整配置后重试。", differences.ToArray());
            pk.ResetPartyStats();
            // ResetPartyStats can change stored fields after ALM generation; keep the in-memory
            // PK9 checksum current before the batch preflight, not only when writing the work slot.
            pk.RefreshChecksum();
            output.Add(new GeneratedPokemon(chinese.Species[pk.Species], english.Species[pk.Species], pk, options.TrainerOverrides, options.Notices.ToArray()));
        }
        return output;
    }

    private static void ValidateInput(ShowdownSet set, int index)
    {
        if (set.Species == 0)
            throw Error(index, "无法识别宝可梦；请检查物种名以及每只之间的空行。");
        if (Regex.Matches(set.Text, @"(?im)^\s*Shiny\s*:").Count > 1)
            throw Error(index, "闪光标记重复，请只保留一行 Shiny: Yes 或 Shiny: No。");
        foreach (var error in set.InvalidLines)
        {
            // PKHeX exports no line for a non-shiny Pokemon and flags explicit No as invalid.
            // Accept this exact false value only; do not accept arbitrary invalid shiny tokens.
            if (!set.Shiny && Regex.IsMatch(error.Value ?? "", @"^Shiny\s*:\s*No\s*$", RegexOptions.IgnoreCase))
                continue;
            // Optional fields were typed/normalized by PokemonTextParser before reaching ALM.
            if (error.Type == BattleTemplateParseErrorType.TokenUnknown &&
                (Regex.IsMatch(error.Value ?? "", @"^(Ball|Language|OT|OTGender|TID|SID)\s*:\s*\S", RegexOptions.IgnoreCase) ||
                 (error.Value ?? "").StartsWith(".", StringComparison.Ordinal)))
                continue;
            throw Error(index, "存在无法解析或不支持的配置行，请查看 Showdown+ 语法文档。", "不支持的行：" + error.Value);
        }
        if (set.EVs.Any(v => v < 0 || v > 252) || set.EVs.Sum() > 510)
            throw Error(index, "传统 EV 单项必须为0～252、总计不超过510；冠军配置请使用 SPs: 或 Format: Champions。");
        if (set.IVs.Any(v => v < 0 || v > 31))
            throw Error(index, "IV 单项必须为 0–31。");
        if (set.Level is < 1 or > 100)
            throw Error(index, "等级必须为 1–100。");
    }

    private static OrderException Error(int index, string message, params string[] details) =>
        new($"第 {index} 只：{message}", details);

    private sealed class RequestSnapshot
    {
        private readonly ushort species;
        private readonly byte form;
        private readonly int ability;
        private readonly Nature nature;
        private readonly bool shiny;
        private readonly byte level;
        private readonly int item;
        private readonly byte? gender;
        private readonly MoveType tera;
        private readonly int[] evs;
        private readonly int[] ivs;
        private readonly ushort[] moves;
        private readonly bool explicitTera;
        private readonly bool explicitBall;
        private readonly bool explicitLanguage;

        public RequestSnapshot(ShowdownSet set)
        {
            species = set.Species;
            form = set.Form;
            ability = set.Ability;
            nature = set.Nature;
            shiny = set.Shiny;
            level = set.Level;
            item = set.HeldItem;
            gender = set.Gender;
            tera = set.TeraType;
            evs = (int[])set.EVs.Clone();
            ivs = (int[])set.IVs.Clone();
            moves = (ushort[])set.Moves.Clone();
            explicitTera = Regex.IsMatch(set.Text, @"(?im)^Tera Type\s*:");
            explicitBall = set.InvalidLines.Any(e => Regex.IsMatch(e.Value ?? "", @"^Ball\s*:", RegexOptions.IgnoreCase));
            explicitLanguage = set.InvalidLines.Any(e => Regex.IsMatch(e.Value ?? "", @"^Language\s*:", RegexOptions.IgnoreCase));
        }

        public List<string> Compare(PK9 pk, RegenTemplate template)
        {
            var result = new List<string>();
            if (pk.Species != species || pk.Form != form) result.Add("物种或形态不符");
            if (ability >= 0 && pk.Ability != ability) result.Add("特性不符");
            // StatAlignment includes mint effects; Nature alone incorrectly rejects minted PK9.
            if ((int)nature < 25 && pk.StatAlignment != nature) result.Add("有效性格不符（含薄荷效果）");
            if (pk.IsShiny != shiny) result.Add("异色要求不符");
            if (pk.CurrentLevel != level) result.Add("等级不符");
            if (pk.HeldItem != item) result.Add("携带物品不符");
            if (gender.HasValue && pk.Gender != gender.Value) result.Add("性别不符");
            if (explicitTera && pk.TeraType != tera) result.Add("太晶属性不符");
            var actualEVs = new int[6];
            pk.GetEVs(actualEVs);
            if (!actualEVs.SequenceEqual(evs)) result.Add("EV 被改变");
            var actualIVs = new int[6];
            pk.GetIVs(actualIVs);
            bool[] hyper = [pk.HT_HP, pk.HT_ATK, pk.HT_DEF, pk.HT_SPE, pk.HT_SPA, pk.HT_SPD];
            for (int i = 0; i < ivs.Length; i++)
            {
                int effective = hyper[i] ? 31 : actualIVs[i];
                if (effective != ivs[i]) result.Add($"第 {i + 1} 项有效 IV 不符（含极限训练效果）");
            }
            var actualMoves = new ushort[4];
            pk.GetMoves(actualMoves);
            for (int i = 0; i < moves.Length; i++)
                if (moves[i] != 0 && actualMoves[i] != moves[i]) result.Add($"第 {i + 1} 个招式被改变");
            if (explicitBall && (template.Regen.Extra.Ball == Ball.None || pk.Ball != (byte)template.Regen.Extra.Ball))
                result.Add("指定精灵球无法满足或无法识别");
            if (explicitLanguage && (!template.Regen.Extra.Language.HasValue || pk.Language != (int)template.Regen.Extra.Language.Value))
                result.Add("指定语言无法满足或无法识别");
            return result;
        }
    }
}

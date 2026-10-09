using PKHeX.Core;

namespace SysBot.Pokemon;

/// <summary>
/// Adapted from Secludedly/FusionBot ApplyAutoOT (31594ca8bf60afdb6a8ddfa32064569398df334e).
/// Only trainer identity is changed; language, origin game, encounter and requested stats stay intact.
/// </summary>
public static class SVAutoOT
{
    public static bool TryApply(PK9 source, TradePartnerSV partner, out PK9 result, out string reason, TrainerOverrideFields overrides = TrainerOverrideFields.None)
    {
        result = source;
        var before = new LegalityAnalysis(source);
        if (AutoLegalityWrapper.IsFixedOT(before.EncounterOriginal, source))
        {
            reason = "该来源具有固定初训家，保留原身份。";
            return false;
        }
        if (source is IHomeTrack { HasTracker: true } || source.Generation != source.Format)
        {
            reason = "跨作或带 HOME 记录的个体保留原身份。";
            return false;
        }
        var clone = (PK9)source.Clone();
        if (!overrides.HasFlag(TrainerOverrideFields.Name))
        {
            clone.OriginalTrainerTrash.Clear();
            clone.OriginalTrainerName = partner.TrainerName;
        }
        if (!overrides.HasFlag(TrainerOverrideFields.Gender))
            clone.OriginalTrainerGender = (byte)partner.Gender;
        var tid = overrides.HasFlag(TrainerOverrideFields.TID) ? source.TrainerTID7 : partner.ID32 % 1_000_000;
        var sid = overrides.HasFlag(TrainerOverrideFields.SID) ? source.TrainerSID7 : partner.ID32 / 1_000_000;
        var id = (ulong)sid * 1_000_000 + tid;
        if (id > uint.MaxValue)
        {
            reason = "手填ID与接收方ID组合超出32位范围，保留生成时的训练家身份。";
            return false;
        }
        clone.ID32 = (uint)id;
        // Trainer IDs participate in shiny XOR. Keep the requested shiny class; also
        // prevent a non-shiny individual becoming shiny by coincidence after the ID change.
        if (source.IsShiny || clone.IsShiny)
            clone.PID = ((uint)(clone.TID16 ^ clone.SID16 ^ (clone.PID & 0xFFFF) ^ source.ShinyXor) << 16) | (clone.PID & 0xFFFF);
        clone.RefreshChecksum();
        var after = new LegalityAnalysis(clone);
        if (!after.Valid)
        {
            reason = "该个体的来源关联不允许直接替换初训家，保留原身份。";
            return false;
        }
        result = clone;
        reason = "已使用接收方初训家身份。";
        return true;
    }
}

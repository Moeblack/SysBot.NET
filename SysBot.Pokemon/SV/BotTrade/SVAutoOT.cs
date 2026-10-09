using PKHeX.Core;

namespace SysBot.Pokemon;

/// <summary>
/// Adapted from Secludedly/FusionBot ApplyAutoOT (31594ca8bf60afdb6a8ddfa32064569398df334e).
/// Only trainer identity is changed; language, origin game, encounter and requested stats stay intact.
/// </summary>
public static class SVAutoOT
{
    public static bool TryApply(PK9 source, TradePartnerSV partner, out PK9 result, out string reason)
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
        clone.OriginalTrainerTrash.Clear();
        clone.OriginalTrainerName = partner.TrainerName;
        clone.OriginalTrainerGender = (byte)partner.Gender;
        clone.ID32 = partner.ID32;
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

using System;

namespace SysBot.Pokemon;

/// <summary>Explicit trainer fields survive recipient AutoOT; omitted fields still follow the recipient.</summary>
[Flags]
public enum TrainerOverrideFields
{
    None = 0,
    Name = 1,
    Gender = 2,
    TID = 4,
    SID = 8,
    All = Name | Gender | TID | SID,
}

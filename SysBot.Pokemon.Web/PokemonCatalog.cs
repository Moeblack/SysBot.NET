using PKHeX.Core;

namespace SysBot.Pokemon.Web;

public sealed record PokemonChoice(string Id, string Name, string Template);

public static class PokemonCatalog
{
    public static readonly PokemonChoice[] Choices = Build();
    private static PokemonChoice[] Build()
    {
        var choices = new List<PokemonChoice>
        {
            new("Metagross", "巨金怪", """
Metagross
Shiny: Yes
Language: ChineseS
Ball: Heavy Ball
Ability: Clear Body
Level: 100
EVs: 100 HP / 228 Atk / 180 Spe
Adamant Nature
- Iron Head
- Protect
- Psychic Fangs
- Body Press
"""),
            new("Indeedee", "爱管侍♂（内敛）", """
Indeedee (M)
Shiny: Yes
Language: ChineseS
Ball: Dive Ball
Ability: Psychic Surge
Level: 100
EVs: 4 HP / 252 SpA / 252 Spe
Modest Nature
- Expanding Force
- Hyper Voice
- Imprison
- Trick
"""),
            new("Annihilape", "弃世猴", """
Annihilape
Shiny: No
Language: ChineseS
Ball: Heavy Ball
Ability: Defiant
Level: 100
EVs: 244 HP / 116 Atk / 52 Def / 84 SpD / 12 Spe
Adamant Nature
- Rage Fist
- Protect
- Bulk Up
- Drain Punch
"""),
            new("Volcarona", "火神蛾", """
Volcarona
Shiny: Yes
Language: ChineseS
Ball: Ultra Ball
Ability: Flame Body
Level: 100
EVs: 252 HP / 156 Def / 92 SpD / 4 Spe
Bold Nature
- Tailwind
- Overheat
- Rage Powder
- Struggle Bug
"""),
            new("Whimsicott", "风妖精", """
Whimsicott
Shiny: Yes
Language: ChineseS
Ball: Moon Ball
Ability: Prankster
Level: 100
EVs: 252 HP / 4 SpD / 252 Spe
Timid Nature
- Tailwind
- Sunny Day
- Endeavor
- Moonblast
"""),
            new("Typhlosion-Hisui", "火暴兽（洗翠）", """
Typhlosion-Hisui
Shiny: No
Language: ChineseS
Ball: Moon Ball
Ability: Blaze
Level: 100
EVs: 4 HP / 252 SpA / 252 Spe
Modest Nature
- Eruption
- Heat Wave
- Shadow Ball
- Infernal Parade
"""),
            new("Kleavor", "劈斧螳螂", """
Kleavor
Shiny: No
Language: ChineseS
Ball: Level Ball
Ability: Sharpness
Level: 100
EVs: 4 HP / 252 Atk / 252 Spe
Adamant Nature
- Stone Axe
- X-Scissor
- Close Combat
- Protect
"""),
            new("Indeedee-Timid", "爱管侍♂（胆小）", """
Indeedee (M)
Shiny: Yes
Language: ChineseS
Ball: Dive Ball
Ability: Psychic Surge
Level: 100
EVs: 4 HP / 252 SpA / 252 Spe
Timid Nature
- After You
- Expanding Force
- Hyper Voice
- Trick
"""),
        };
        var en = GameInfo.GetStrings("en").Species;
        var zh = GameInfo.GetStrings("zh-Hans").Species;
        for (ushort i = 1; i < en.Count; i++)
        {
            if (!PersonalTable.SV.IsSpeciesInGame(i) || choices.Any(x => x.Id == en[i])) continue;
            choices.Add(new PokemonChoice(en[i], zh[i], $"{en[i]}\nShiny: No\nLanguage: ChineseS\nLevel: 100"));
        }
        return choices.ToArray();
    }
}

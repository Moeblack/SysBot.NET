using System.Globalization;
using System.Reflection;
using PKHeX.Core;

namespace SysBot.Pokemon.Web;

/// <summary>Optional typed fields around native Showdown/ALM. No arbitrary batch filters or scripts.</summary>
public sealed class PokemonTextOptions(Type entityType)
{
    private readonly Dictionary<string, (PropertyInfo Property, object Value)> fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> extra = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> trainer = new(StringComparer.OrdinalIgnoreCase);
    public TrainerOverrideFields TrainerOverrides { get; private set; }
    public List<string> Notices { get; } = [];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Height"] = "HeightScalar", ["Weight"] = "WeightScalar", ["Size"] = "Scale",
        ["Met Date"] = "MetDate", ["Egg Date"] = "EggMetDate", ["EggDate"] = "EggMetDate",
        ["Met Location"] = "MetLocation", ["Egg Location"] = "EggLocation",
        ["Met Level"] = "MetLevel", ["Title"] = "AffixedRibbon",
    };
    private static readonly HashSet<string> ScalarFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "HeightScalar", "WeightScalar", "Scale", "AffixedRibbon", "MetDate", "EggMetDate",
        "MetLocation", "EggLocation", "MetLevel",
    };

    /// <returns>True when this line is an optional extension; false for ordinary Showdown lines.</returns>
    public bool TryRead(string line)
    {
        var dotted = line.StartsWith('.');
        int separator = line.IndexOf(dotted ? '=' : ':');
        if (separator < 0)
        {
            if (dotted) throw new OrderException("扩展字段使用 .Field=value，例如 .Scale=128。");
            return false;
        }
        var key = line[(dotted ? 1 : 0)..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        if (!dotted && (key.Equals("Ball", StringComparison.OrdinalIgnoreCase) || key.Equals("Language", StringComparison.OrdinalIgnoreCase)))
        {
            key = key.Equals("Ball", StringComparison.OrdinalIgnoreCase) ? "Ball" : "Language";
            if (value.Length == 0) throw new OrderException($"{key} 不能为空；不指定时删除这一行。");
            if (extra.TryGetValue(key, out var previous) && !previous.Equals(value, StringComparison.OrdinalIgnoreCase))
                throw new OrderException($"{key} 的多个值互相冲突。");
            extra[key] = value;
            return true;
        }
        if (!dotted && TryReadTrainer(key, value)) return true;
        if (!dotted && (key.Equals("Marks", StringComparison.OrdinalIgnoreCase) || key.Equals("Ribbons", StringComparison.OrdinalIgnoreCase)))
        {
            var prefix = key.Equals("Marks", StringComparison.OrdinalIgnoreCase) ? "RibbonMark" : "Ribbon";
            if (value.Length == 0) throw new OrderException($"{key} 不能为空；不需要时删除这一行。");
            var names = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0) throw new OrderException($"{key} 至少写一个名称；不需要时删除这一行。");
            foreach (var name in names)
                AddField(name.StartsWith("Ribbon", StringComparison.OrdinalIgnoreCase) ? name : prefix + name, "true");
            return true;
        }
        if (Aliases.TryGetValue(key, out var canonical)) key = canonical;
        if (!ScalarFields.Contains(key) && !key.StartsWith("Ribbon", StringComparison.OrdinalIgnoreCase))
        {
            if (dotted) throw new OrderException($"不支持扩展字段 .{key}；请查看配置语法文档中的字段列表。");
            return false;
        }
        AddField(key, value);
        return true;
    }

    private bool TryReadTrainer(string key, string value)
    {
        TrainerOverrideFields flag;
        switch (key.ToLowerInvariant())
        {
            case "ot": key = "OT"; flag = TrainerOverrideFields.Name;
                if (value.Length is < 1 or > 12) throw new OrderException("OT 名字长度必须为1～12个字符。");
                break;
            case "otgender": key = "OTGender"; flag = TrainerOverrideFields.Gender;
                value = value.ToLowerInvariant() switch
                {
                    "male" or "m" or "0" or "男" => "Male",
                    "female" or "f" or "1" or "女" => "Female",
                    _ => throw new OrderException("OTGender 使用 Male 或 Female。"),
                };
                break;
            case "tid": key = "TID"; flag = TrainerOverrideFields.TID;
                if (!uint.TryParse(value, out var tid) || tid > 999999) throw new OrderException("TID 使用0～999999的公开ID。");
                value = tid.ToString(CultureInfo.InvariantCulture); break;
            case "sid": key = "SID"; flag = TrainerOverrideFields.SID;
                if (!uint.TryParse(value, out var sid) || sid > 4294) throw new OrderException("SID 使用0～4294的隐藏ID；TID/SID组合须在32位范围内。");
                value = sid.ToString(CultureInfo.InvariantCulture); break;
            default: return false;
        }
        if (!trainer.TryAdd(key, value)) throw new OrderException($"{key} 重复；同一只宝可梦每个字段只写一次。");
        TrainerOverrides |= flag;
        return true;
    }

    private void AddField(string name, string text)
    {
        var property = entityType.GetProperties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null || !property.CanWrite || !property.CanRead || property.GetIndexParameters().Length != 0)
            throw new OrderException($"当前目标格式 {entityType.Name} 不支持字段 {name}。");
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object value;
        if (property.Name == "AffixedRibbon")
        {
            if (text.Equals("None", StringComparison.OrdinalIgnoreCase) || text == "-1") value = (sbyte)-1;
            else if (sbyte.TryParse(text, out var numeric) && numeric >= 0 && Enum.IsDefined(typeof(RibbonIndex), (byte)numeric) && numeric < (int)RibbonIndex.MAX_COUNT) value = numeric;
            else
            {
                var ribbon = text.StartsWith("Ribbon", StringComparison.OrdinalIgnoreCase) ? text[6..] : text;
                var qualified = text.StartsWith("Ribbon", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Mark", StringComparison.OrdinalIgnoreCase);
                RibbonIndex index;
                if (!(qualified ? Enum.TryParse(ribbon, true, out index) :
                    Enum.TryParse("Mark" + ribbon, true, out index) || Enum.TryParse(ribbon, true, out index)))
                    throw new OrderException($"未知称号 {text}；例如 Title: Mightiest、Title: ChampionPaldea 或 Title: None。");
                if (!Enum.IsDefined(index) || (int)index >= (int)RibbonIndex.MAX_COUNT) throw new OrderException($"称号 {text} 不适用于当前格式。");
                value = (sbyte)index;
            }
        }
        else if (type == typeof(DateOnly))
        {
            if (!DateOnly.TryParseExact(text, new[] { "yyyy-MM-dd", "yyyyMMdd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 2000 or > 2255)
                throw new OrderException($"{property.Name} 使用真实日期 YYYY-MM-DD，年份2000～2255。");
            value = date;
        }
        else if (type == typeof(bool))
        {
            value = text.ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => true, "false" or "no" or "0" => false,
                _ => throw new OrderException($"{property.Name} 使用 true 或 false。"),
            };
        }
        else
        {
            try { value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
            { throw new OrderException($"{property.Name} 数值格式或范围不正确（类型 {type.Name}）。"); }
            if (property.Name == "MetLevel" && Convert.ToInt32(value) is < 1 or > 100)
                throw new OrderException("MetLevel 使用1～100。");
        }
        if (fields.TryGetValue(property.Name, out var prior))
        {
            if (!Equals(prior.Value, value)) throw new OrderException($"{property.Name} 的多个写法互相冲突。");
            return; // Same value via friendly alias and native syntax is harmless.
        }
        fields.Add(property.Name, (property, value));
    }

    public void Complete()
    {
        if (trainer.TryGetValue("TID", out var tid) && trainer.TryGetValue("SID", out var sid) &&
            ulong.Parse(sid, CultureInfo.InvariantCulture) * 1_000_000 + ulong.Parse(tid, CultureInfo.InvariantCulture) > uint.MaxValue)
            throw new OrderException("TID/SID组合超出32位范围，请调整其中一项。");
        if (fields.TryGetValue("AffixedRibbon", out var title) && (sbyte)title.Value >= 0)
        {
            var name = Enum.GetName(typeof(RibbonIndex), (byte)(sbyte)title.Value);
            if (name is not null) AddField("Ribbon" + name, "true");
        }
    }

    public IEnumerable<string> NativeLines()
    {
        foreach (var (key, value) in extra) yield return $"{key}: {value}";
        foreach (var (key, value) in trainer) yield return $"{key}: {value}";
        foreach (var (_, field) in fields)
        {
            // Native PKHeX batch dates use yyyyMMdd, not ISO's hyphenated form.
            if (field.Value is DateOnly date)
            {
                yield return $".{field.Property.Name}={date:yyyyMMdd}";
                continue;
            }
            yield return $".{field.Property.Name}={Convert.ToString(field.Value, CultureInfo.InvariantCulture)}";
        }
    }

    public IEnumerable<string> Compare(PKM pk)
    {
        foreach (var field in fields.Values)
            if (!Equals(field.Property.GetValue(pk), field.Value))
                yield return $"{field.Property.Name} 要求 {field.Value}，实际 {field.Property.GetValue(pk)}";
        if (trainer.TryGetValue("OT", out var name) && pk.OriginalTrainerName != name) yield return "手填OT未满足";
        if (trainer.TryGetValue("OTGender", out var gender) && pk.OriginalTrainerGender != (gender == "Female" ? 1 : 0)) yield return "手填OTGender未满足";
        if (trainer.TryGetValue("TID", out var tid) && pk.TrainerTID7 != uint.Parse(tid, CultureInfo.InvariantCulture)) yield return "手填TID未满足";
        if (trainer.TryGetValue("SID", out var sid) && pk.TrainerSID7 != uint.Parse(sid, CultureInfo.InvariantCulture)) yield return "手填SID未满足";
    }
}

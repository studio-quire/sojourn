using System.Globalization;
using System.Text;
using LibreMetaverse;

namespace Sojourn.Core.Appearance;

/// <summary>
/// A wearable asset ("LLWearable version 22" text): our own reader and writer. LibreMetaverse's AssetWearable drops the
/// description when encoding and fails on unknown keys; this one round-trips what the viewer writes.
/// </summary>
public sealed class Wearable
{
    public string Name = "New Wearable";
    public string Description = "";
    public WearableType Type;
    public uint BaseMask = 0x7fffffff, OwnerMask = 0x7fffffff, GroupMask, EveryoneMask, NextOwnerMask = 0x00082000;
    public UUID Creator, Owner, LastOwner, Group;
    public string SaleType = "not";
    public int SalePrice = 10;
    public SortedDictionary<int, float> Params = new();
    public SortedDictionary<int, UUID> Textures = new();

    public bool IsBodyPart => Type is WearableType.Shape or WearableType.Skin or WearableType.Hair or WearableType.Eyes;
    public AssetType AssetType => IsBodyPart ? AssetType.Bodypart : AssetType.Clothing;

    public static Wearable Parse(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data).TrimEnd('\0');
        var lines = text.Replace("\r", "").Split('\n');
        var w = new Wearable();
        var i = 0;
        if (lines.Length < 3 || !lines[0].StartsWith("LLWearable version", StringComparison.Ordinal))
            throw new FormatException("not a wearable asset");
        w.Name = lines[1];
        // the description line may be missing in old assets; it's whatever sits before the permissions block
        i = 2;
        if (!lines[2].TrimStart().StartsWith("permissions", StringComparison.Ordinal)) { w.Description = lines[2]; i = 3; }
        for (; i < lines.Length; i++)
        {
            var parts = lines[i].Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var key = parts[0];
            var val = parts.Length > 1 ? parts[1].Trim() : "";
            switch (key)
            {
                case "base_mask": w.BaseMask = Hex(val); break;
                case "owner_mask": w.OwnerMask = Hex(val); break;
                case "group_mask": w.GroupMask = Hex(val); break;
                case "everyone_mask": w.EveryoneMask = Hex(val); break;
                case "next_owner_mask": w.NextOwnerMask = Hex(val); break;
                case "creator_id": UUID.TryParse(val, out w.Creator); break;
                case "owner_id": UUID.TryParse(val, out w.Owner); break;
                case "last_owner_id": UUID.TryParse(val, out w.LastOwner); break;
                case "group_id": UUID.TryParse(val, out w.Group); break;
                case "sale_type": w.SaleType = val; break;
                case "sale_price": int.TryParse(val, out w.SalePrice); break;
                case "type": w.Type = (WearableType)int.Parse(val, CultureInfo.InvariantCulture); break;
                case "parameters":
                    var np = int.Parse(val, CultureInfo.InvariantCulture);
                    for (var k = 0; k < np && i + 1 < lines.Length; k++)
                    {
                        var pv = lines[++i].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (pv.Length >= 2 && int.TryParse(pv[0], out var id))
                            w.Params[id] = float.Parse(pv[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                    }
                    break;
                case "textures":
                    var nt = int.Parse(val, CultureInfo.InvariantCulture);
                    for (var k = 0; k < nt && i + 1 < lines.Length; k++)
                    {
                        var tv = lines[++i].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tv.Length >= 2 && int.TryParse(tv[0], out var te) && UUID.TryParse(tv[1], out var tid)) w.Textures[te] = tid;
                    }
                    break;
            }
        }
        return w;
    }

    static uint Hex(string s) => uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>The text the viewer writes (LLWearable::exportStream).</summary>
    public byte[] Encode()
    {
        var sb = new StringBuilder();
        sb.Append("LLWearable version 22\n");
        sb.Append(Name.Replace("\n", " ")).Append('\n');
        sb.Append(Description.Replace("\n", " ")).Append('\n');
        sb.Append("\tpermissions 0\n\t{\n");
        sb.Append($"\t\tbase_mask\t{BaseMask:x8}\n\t\towner_mask\t{OwnerMask:x8}\n\t\tgroup_mask\t{GroupMask:x8}\n");
        sb.Append($"\t\teveryone_mask\t{EveryoneMask:x8}\n\t\tnext_owner_mask\t{NextOwnerMask:x8}\n");
        sb.Append($"\t\tcreator_id\t{Creator}\n\t\towner_id\t{Owner}\n\t\tlast_owner_id\t{LastOwner}\n\t\tgroup_id\t{Group}\n");
        sb.Append("\t}\n");
        sb.Append($"\tsale_info\t0\n\t{{\n\t\tsale_type\t{SaleType}\n\t\tsale_price\t{SalePrice}\n\t}}\n");
        sb.Append($"type {(int)Type}\n");
        sb.Append($"parameters {Params.Count}\n");
        foreach (var (id, v) in Params) sb.Append(id).Append(' ').Append(Num(v)).Append('\n');
        sb.Append($"textures {Textures.Count}\n");
        foreach (var (te, id) in Textures) sb.Append(te).Append(' ').Append(id).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The viewer's terse_F32_to_string: %.2f, trailing zeros and dot stripped, no leading zero (.59, -.59, 0).</summary>
    public static string Num(float v)
    {
        var s = v.ToString("F2", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        if (s is "" or "-" or "-0") return "0";
        if (s.StartsWith("0.", StringComparison.Ordinal)) s = s[1..];
        else if (s.StartsWith("-0.", StringComparison.Ordinal)) s = "-" + s[2..];
        return s;
    }

    /// <summary>A fresh wearable of a type, every tweakable parameter of that type at its default (as the viewer creates them).</summary>
    public static Wearable New(WearableType type, string name, UUID me)
    {
        var w = new Wearable { Type = type, Name = name, Creator = me, Owner = me };
        foreach (var p in VisualParamsFor(type)) w.Params[p.ParamID] = p.DefaultValue;
        foreach (var te in Slots.For(type)) w.Textures[(int)te.Index] = te.Default;
        return w;
    }

    public static IEnumerable<VisualParam> VisualParamsFor(WearableType type)
    {
        var name = Slots.TypeName(type);
        return VisualParams.Params.Values.Where(p => p.Group == 0 && string.Equals(p.Wearable, name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.ParamID).Select(g => g.First());
    }
}

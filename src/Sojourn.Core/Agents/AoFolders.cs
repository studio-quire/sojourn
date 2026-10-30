using System.Globalization;
using LibreMetaverse;

namespace Sojourn.Core.Agents;

/// <summary>
/// Firestorm's AO folder format, read and written (aoengine.cpp update(), reloadStateAnimations(), readAnimationLinks()
/// and the get*FolderName()s), over a plain tree so it can be checked without a grid:
/// - the AO folder (Firestorm's is #Firestorm/#AO) holds a folder per set: "name[:SO][:SM][:DM][:**]" (sit override,
///   smart sit, no stands in mouselook, the default set);
/// - a set holds a folder per state, by Firestorm's or ZHAO's name: "Standing[:CT&lt;seconds&gt;][:CY][:RN]" (cycle time,
///   cycle, random);
/// - a state holds animations (links, usually), each with its order as its description; subfolders "name:OR&lt;n&gt;" are
///   groups played together as one step, "name:TR[:CT..][:CY][:RN]" tracks played alongside.
/// Also a ZHAO-II notecard read in place (a scripted AO's folder: "[ Standing ]anim1|anim2,anim3"), which Firestorm
/// only imports by copying into its own folders. Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class AoFolders
{
    public sealed record Item(UUID Id, string Name, string Desc, UUID AssetId, bool IsAnimation, bool IsNotecard = false);

    public sealed class Folder(UUID id, string name)
    {
        public UUID Id { get; } = id;
        public string Name { get; } = name;
        public List<Folder> Folders { get; } = new();
        public List<Item> Items { get; } = new();
    }

    /// <summary>What a picked folder is: a folder of sets (Firestorm's #AO), one set (its subfolders are states), or a
    /// scripted AO's folder with a ZHAO-II notecard.</summary>
    public enum Kind { Sets, Set, Notecard, Nothing }

    public static Kind KindOf(Folder f)
    {
        if (f.Folders.Any(IsStateFolder)) return Kind.Set;
        if (f.Folders.Any(s => s.Folders.Any(IsStateFolder))) return Kind.Sets;
        if (f.Items.Any(i => i.IsNotecard) && f.Items.Any(i => i.IsAnimation)) return Kind.Notecard;
        return f.Folders.Count > 0 ? Kind.Sets : Kind.Nothing;
    }

    static bool IsStateFolder(Folder f) => AoMotions.KindByName(Tokens(f.Name).FirstOrDefault() ?? "") is not null;

    static List<string> Tokens(string name) =>
        name.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The sets under an AO folder (Kind.Sets), with warnings for what wasn't understood.</summary>
    public static List<AoSet> ReadSets(Folder ao, List<string> warnings)
    {
        var sets = new List<AoSet>();
        foreach (var f in ao.Folders)
        {
            var set = ReadSet(f, warnings);
            if (set is null) continue;
            // two folders for one set name: Firestorm reads the second's states into the first; here the one with
            // animations wins (an empty twin, as an interrupted "new set" leaves, mustn't hide the real one)
            int at = sets.FindIndex(s => s.Name == set.Name);
            if (at >= 0)
            {
                warnings.Add($"two folders for the set {set.Name}; the fuller one is played");
                if (Count(set) > Count(sets[at])) { set.IsDefault |= sets[at].IsDefault; sets[at] = set; }
                else sets[at].IsDefault |= set.IsDefault;
                continue;
            }
            sets.Add(set);
        }
        return sets;
    }

    static int Count(AoSet s) => s.States.Sum(st => st.Steps.Count + st.Tracks.Count);

    public static AoSet? ReadSet(Folder f, List<string> warnings)
    {
        var p = Tokens(f.Name);
        if (p.Count == 0) { warnings.Add($"a set folder with no name ({f.Id})"); return null; }
        var set = new AoSet { Name = p[0], FolderId = f.Id };
        foreach (var flag in p.Skip(1))
        {
            switch (flag)
            {
                case "SO": set.SitOverride = true; break;
                case "SM": set.Smart = true; break;
                case "DM": set.MouselookStandDisable = true; break;
                case "**": set.IsDefault = true; break;
                default: warnings.Add($"{set.Name}: unknown set option {flag}"); break;
            }
        }
        foreach (var sf in f.Folders)
        {
            var sp = Tokens(sf.Name);
            if (sp.Count == 0) continue;
            if (AoMotions.KindByName(sp[0]) is not { } kind) { warnings.Add($"{set.Name}: unknown state {sp[0]}, skipped"); continue; }
            var st = set.State(kind);
            st.FolderId = sf.Id;
            foreach (var flag in sp.Skip(1))
            {
                if (flag == "CY") st.Cycle = true;
                else if (flag == "RN") st.Random = true;
                else if (flag.StartsWith("CT") && float.TryParse(flag[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ct)) st.CycleTime = ct;
                else warnings.Add($"{set.Name}/{sp[0]}: unknown state option {flag}");
            }
            ReadState(st, sf, warnings);
        }
        return set;
    }

    static void ReadState(AoState st, Folder f, List<string> warnings)
    {
        st.Steps.Clear();
        st.Tracks.Clear();
        foreach (var a in Animations(f))
        {
            var step = new AoStep { Name = a.Name, Id = a.ItemId, Sort = a.Sort };
            step.Members.Add(a);
            InsertStep(st.Steps, step);
        }
        foreach (var sub in f.Folders)
        {
            var p = Tokens(sub.Name);
            if (p.Count == 0) continue;
            if (p.Skip(1).Contains("TR"))
            {
                var t = new AoTrack { Name = p[0], FolderId = sub.Id };
                foreach (var flag in p.Skip(1))
                {
                    if (flag == "TR") continue;
                    if (flag == "CY") t.Cycle = true;
                    else if (flag == "RN") t.Random = true;
                    else if (flag.StartsWith("CT") && float.TryParse(flag[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ct)) t.CycleTime = ct;
                    else warnings.Add($"{st.Name}/{p[0]}: unknown track option {flag}");
                }
                t.Animations.AddRange(Animations(sub));
                st.Tracks.Add(t);
            }
            else
            {
                var step = new AoStep { Name = p[0], Id = sub.Id, IsGroup = true };
                foreach (var flag in p.Skip(1))
                    if (flag.StartsWith("OR") && int.TryParse(flag[2..], out var or)) step.Sort = or;
                step.Members.AddRange(Animations(sub));
                if (step.Members.Count == 0) continue;
                InsertStep(st.Steps, step);
            }
        }
    }

    /// <summary>A folder's animations in their order (the description holds it; unordered ones go last).</summary>
    static List<AoAnimation> Animations(Folder f)
    {
        var list = new List<AoAnimation>();
        foreach (var it in f.Items)
        {
            if (!it.IsAnimation) continue;
            var a = new AoAnimation { Name = it.Name, ItemId = it.Id, AssetId = it.AssetId, Sort = int.TryParse(it.Desc.Trim(), out var s) ? s : -1 };
            if (a.Sort < 0) { list.Add(a); continue; }
            int at = list.FindIndex(x => x.Sort > a.Sort);
            if (at < 0) list.Add(a); else list.Insert(at, a);
        }
        return list;
    }

    static void InsertStep(List<AoStep> steps, AoStep step)
    {
        if (step.Sort < 0) { steps.Add(step); return; }
        int at = steps.FindIndex(x => x.Sort > step.Sort);
        if (at < 0) steps.Add(step); else steps.Insert(at, step);
    }

    // ---------- writing names back (the person's changes) ----------

    static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static string SetFolderName(AoSet s) =>
        s.Name + (s.SitOverride ? ":SO" : "") + (s.Smart ? ":SM" : "") + (s.MouselookStandDisable ? ":DM" : "") + (s.IsDefault ? ":**" : "");

    public static string StateFolderName(AoState s) =>
        s.Name + (s.CycleTime > 0 ? ":CT" + F(s.CycleTime) : "") + (s.Cycle ? ":CY" : "") + (s.Random ? ":RN" : "");

    /// <summary>Firestorm's sanitising of a name typed for a set or group (no ':' or '|', printable ASCII).</summary>
    public static string CleanName(string name)
    {
        var clean = new string(name.Where(ch => ch != ':' && ch != '|' && ch >= 0x20 && ch <= 0x7E).ToArray()).Trim();
        return clean;
    }

    // ---------- ZHAO-II notecards ----------

    /// <summary>A set from a ZHAO-II notecard's text ("[ State ]anim|anim,anim": '|' between steps, ',' within a
    /// group; '#' comments), naming animations in the same folder. Null when nothing in it plays.</summary>
    public static AoSet? FromZhao(string setName, UUID folder, string text, IEnumerable<Item> items, List<string> warnings)
    {
        var byName = new Dictionary<string, Item>();
        foreach (var it in items) if (it.IsAnimation) byName.TryAdd(it.Name, it);
        var set = new AoSet { Name = setName, FolderId = folder, FromNotecard = true };
        bool any = false;
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] != '[') { warnings.Add($"line {lineNo}: no [ state ] at its start"); continue; }
            int end = line.IndexOf(']');
            if (end < 0) { warnings.Add($"line {lineNo}: no ]"); continue; }
            var stateName = line[1..end].Trim();
            if (AoMotions.KindByName(stateName) is not { } kind) { warnings.Add($"line {lineNo}: unknown state {stateName}"); continue; }
            var st = set.State(kind);
            foreach (var stepText in line[(end + 1)..].Split('|'))
            {
                var step = new AoStep { Sort = st.Steps.Count };
                foreach (var nm in stepText.Split(','))
                {
                    var n = nm.Trim();
                    if (n.Length == 0) continue;
                    if (!byName.TryGetValue(n, out var it)) { warnings.Add($"{stateName}: no animation named {n} in the folder"); continue; }
                    step.Members.Add(new AoAnimation { Name = n, ItemId = it.Id, AssetId = it.AssetId, Sort = step.Members.Count });
                }
                if (step.Members.Count == 0) continue;
                step.IsGroup = step.Members.Count > 1;
                step.Name = step.IsGroup ? $"Group {st.Steps.Count(s => s.IsGroup) + 1}" : step.Members[0].Name;
                st.Steps.Add(step);
                any = true;
            }
        }
        return any ? set : null;
    }
}

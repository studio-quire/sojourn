using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Hud;

/// <summary>
/// Buttons read off a HUD's picture (docs/access-app.md > HUDs out of sight): most HUDs are one textured prim with
/// buttons painted on it, which a script tells apart by where it's touched, so they have no parts to name; the assistant
/// names what it sees and where (SojournHud save_buttons), and the person presses them by name or number from then on.
/// Kept per look, not per object (a HUD is a new object each time it's worn): its parts' shapes, places and textures,
/// with their offsets and repeats, so a HUD showing another page (a texture or its offset changed) is a look of its
/// own, read when it's asked about; colours and glow are left out, since buttons light up as they're used. Each button
/// is its middle and size across the HUD's box as placed (0-1 from the left and from the top), which turning the HUD
/// changes, and so does its look. In the session's folder, a few hundred looks at most. Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed class HudButtons
{
    /// <summary>A button: its name as read, its middle and size across the HUD's box (0-1 from the left and the top).</summary>
    public sealed record Button(string Name, float X, float Y, float W, float H);

    /// <summary>A look's buttons: what the HUD is called, who read them and when.</summary>
    public sealed record Entry(string Key, string Hud, string By, DateTime At, List<Button> Buttons);

    readonly string _path;
    readonly Action<string>? _log;
    readonly Dictionary<string, Entry> _looks = new();
    const int MaxLooks = 400;

    /// <summary>A look's buttons were saved (its key).</summary>
    public event Action<string>? Saved;

    public HudButtons(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) is { } list)
                foreach (var e in list) _looks[e.Key] = e;
        }
        catch (Exception e) { _log?.Invoke($"HUD buttons: {path} unreadable ({e.Message}); starting afresh"); }
    }

    public Entry? For(string key)
    {
        lock (_looks) return _looks.TryGetValue(key, out var e) ? e : null;
    }

    public void Save(Entry e)
    {
        lock (_looks)
        {
            _looks[e.Key] = e;
            foreach (var old in _looks.Values.OrderByDescending(x => x.At).Skip(MaxLooks).ToList()) _looks.Remove(old.Key);
        }
        Write();
        Saved?.Invoke(e.Key);
    }

    /// <summary>A look's buttons forgotten (to read them again).</summary>
    public void Forget(string key)
    {
        lock (_looks) if (!_looks.Remove(key)) return;
        Write();
        Saved?.Invoke(key);
    }

    void Write()
    {
        List<Entry> all;
        lock (_looks) all = _looks.Values.OrderBy(x => x.At).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception x) { _log?.Invoke($"HUD buttons: couldn't keep them ({x.Message})"); }
    }

    /// <summary>A HUD's look, from the world model: each part's shape, size, place and rotation in the root's frame,
    /// and its faces' textures with their offsets, repeats and rotation; the root's own rotation. Order-free (local ids
    /// change with every attach).</summary>
    public static string LookKey(AgentSession s, Primitive root)
    {
        var w = s.Region ?? throw new InvalidOperationException("not in a region");
        static string F(float v, float step) => ((int)MathF.Round(v / step)).ToString(CultureInfo.InvariantCulture);
        static string V(Vector3 v, float step) => $"{F(v.X, step)},{F(v.Y, step)},{F(v.Z, step)}";
        static string Q(Quaternion q, float step)
        {
            if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);   // (the same turn)
            return $"{F(q.X, step)},{F(q.Y, step)},{F(q.Z, step)},{F(q.W, step)}";
        }
        string Part(Primitive p, bool isRoot)
        {
            var sb = new StringBuilder();
            sb.Append((int)p.PrimData.PCode).Append('|').Append(p.Sculpt?.SculptTexture.ToString() ?? "").Append('|');
            sb.Append(V(p.Scale, 0.001f)).Append('|');
            if (!isRoot) sb.Append(V(p.Position, 0.001f)).Append('|').Append(Q(p.Rotation, 0.01f)).Append('|');
            for (uint i = 0; i < 8; i++)
            {
                if (p.Textures?.GetFace(i) is not { } f) continue;
                sb.Append(f.TextureID).Append(':').Append(F(f.OffsetU, 0.001f)).Append(',').Append(F(f.OffsetV, 0.001f))
                  .Append(',').Append(F(f.RepeatU, 0.001f)).Append(',').Append(F(f.RepeatV, 0.001f)).Append(',').Append(F(f.Rotation, 0.01f)).Append(';');
            }
            return sb.ToString();
        }
        var rest = s.Objects.Linkset(w, root).Where(p => p.LocalID != root.LocalID).Select(p => Part(p, false)).OrderBy(x => x, StringComparer.Ordinal);
        var all = Part(root, true) + "#" + Q(root.Rotation, 0.05f) + "#" + string.Join("#", rest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all)))[..20].ToLowerInvariant();
    }
}

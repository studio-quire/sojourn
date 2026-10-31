using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Leap;

namespace Sojourn.Client.Showing;

/// <summary>Where an element sits: with the person (in view, following lazily; at the left wrist; at a place in the
/// room, which travels with them), or out in the world (on an object or avatar, at a point).</summary>
public enum AnchorKind { View, Wrist, Room, Thing, Point }

/// <summary>A place in the world: something by id (an object or an avatar, followed as it moves), or a fixed point in
/// grid-global metres (so it survives region crossings).</summary>
public readonly record struct Target(UUID Thing, Vector3d Global)
{
    public bool IsThing => Thing != UUID.Zero;
}

public sealed record Anchor(AnchorKind Kind, Target Where = default)
{
    public bool InWorld => Kind is AnchorKind.Thing or AnchorKind.Point;
    public string Describe() => Kind switch
    {
        AnchorKind.Thing => $"on {Where.Thing}",
        AnchorKind.Point => "at a point",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

public enum TextStyle { Body, Title, Big, Dim }

public abstract record Block
{
    /// <summary>Its kind as the API names it.</summary>
    public abstract string Kind { get; }
    /// <summary>Changes on its own (a timer, a pointer toward something, a map): redrawn as it does.</summary>
    public virtual bool Live => false;
}

public sealed record TextBlock(string Text, TextStyle Style) : Block { public override string Kind => "text"; }
public sealed record ListItem(string Text, string? Detail, bool? Done);
public sealed record ListBlock(IReadOnlyList<ListItem> Items, bool Numbered) : Block
{
    public override string Kind => Items.Any(i => i.Done != null) ? "checklist" : "list";
}
public sealed record TableBlock(IReadOnlyList<string[]> Rows, bool Header) : Block { public override string Kind => "table"; }
/// <summary>A countdown (Seconds from when it was sent: Started, on the store's clock) or a stopwatch (Up).</summary>
public sealed record TimerBlock(double Seconds, bool Up, double Started) : Block
{
    public override string Kind => Up ? "stopwatch" : "timer";
    public override bool Live => true;
}
public sealed record ProgressBlock(double Value, double Max, string? Label) : Block { public override string Kind => "progress"; }
public sealed record ChartBlock(IReadOnlyList<(string? Label, double Value)> Points, bool Line, string? Unit) : Block { public override string Kind => "chart"; }
/// <summary>"view" (what the person sees, taken when it's sent), a texture's id, or a picture Sojourn saved (Source its
/// full path in Pictures\Sojourn, Width and Height its own size: <see cref="Pictures"/>), drawn big.</summary>
public sealed record ImageBlock(string Source, int Width = 0, int Height = 0) : Block
{
    public override string Kind => "image";
    public bool IsPicture => Width > 0 && Height > 0;
}
public sealed record TowardBlock(Target To, string? Label) : Block
{
    public override string Kind => "toward";
    public override bool Live => true;
}
public sealed record MapBlock(IReadOnlyList<(Target At, string? Label)> Points) : Block
{
    public override string Kind => "map";
    public override bool Live => true;
}

/// <summary>What the assistant asked to show, checked against the caps (<see cref="Caps"/>).</summary>
public sealed record ElementSpec(string Id, Anchor At, string? Title, IReadOnlyList<Block> Blocks, double? ForSeconds)
{
    /// <summary>One line of short text and nothing else: drawn as a small capsule, not a card.</summary>
    public bool IsLabel => Title == null && Blocks is [TextBlock { Style: TextStyle.Body or TextStyle.Title } t] && t.Text.Length <= Caps.LabelChars && !t.Text.Contains('\n');
}

/// <summary>What one assistant can put on screen at once, and how fast it can change it: so a runaway model can't
/// fill the view or the frame. Limits on content keep every element readable at a glance; past them the op is refused
/// with the reason, never quietly cut.</summary>
public static class Caps
{
    // No caps on what the assistant shows: how much, how many or how often is the person's to judge (the user,
    // 2026-10-28). What's left is layout: one line this short is a label, and the widths cards are laid out to.
    public const int LabelChars = 40;
    /// <summary>Logical pixels (the panel's): cards with the person, and out in the world.</summary>
    public const float CardWidth = 300, WideCardWidth = 360, WorldWidth = 260;
    /// <summary>A card holding a saved picture: big enough to judge it (about 39 degrees wide in view, 31 in the
    /// room).</summary>
    public const float PictureCardWidth = 560;
}

public static class ElementParse
{
    static readonly string[] Kinds = { "text", "list", "table", "timer", "stopwatch", "progress", "chart", "image", "toward", "map" };

    static LeapError Err(string m) => new(m);

    public static string Id(JsonObject a)
    {
        var id = a.Str("id")?.Trim();
        if (string.IsNullOrEmpty(id)) throw Err("id: a short name for it, like \"tea-timer\"");
        if (id.Length > 40 || !id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' '))
            throw Err("id: up to 40 letters, digits, spaces, - _ .");
        return id;
    }

    /// <summary>A new spec, or the given fields over an old one (an update). here: the region the person is in
    /// (positions given as x,y,z are in it); now: the store's clock; picture: an image block's source that isn't the
    /// view or a texture, as a saved picture (null: none are shown).</summary>
    public static ElementSpec Parse(JsonObject a, ElementSpec? old, ulong here, double now, Func<string, ImageBlock>? picture = null)
    {
        var id = Id(a);
        var at = a.ContainsKey("at") ? ParseAnchor(a["at"], here) : old?.At ?? throw Err("at: view, wrist, room, an object's or avatar's id, or [x,y,z] in this region");
        string? title = old?.Title;
        if (a.ContainsKey("title"))
        {
            title = a.Str("title")?.Trim();
            if (title is { Length: 0 }) title = null;
        }
        IReadOnlyList<Block> blocks = old?.Blocks ?? Array.Empty<Block>();
        if (a["blocks"] is JsonArray arr)
        {
            var list = new List<Block>();
            foreach (var n in arr)
                list.Add(n is JsonObject o ? ParseBlock(o, here, now, picture) : n is JsonValue v && v.TryGetValue<string>(out var s)
                    ? Text(s, TextStyle.Body) : throw Err("blocks: each an object like {\"text\": \"...\"}"));
            // a timer sent again as it was (the rest of the element changing) keeps running rather than starting over
            for (int i = 0; i < list.Count && old != null; i++)
                if (list[i] is TimerBlock t && old.Blocks.OfType<TimerBlock>().FirstOrDefault(o => o.Seconds == t.Seconds && o.Up == t.Up) is { } same)
                    list[i] = same;
                else if (list[i] is ImageBlock { Source: not "view" } im && old.Blocks.OfType<ImageBlock>().FirstOrDefault(o => o.Source == im.Source) is { } had)
                    list[i] = had;   // the texture or picture already fetched
            blocks = list;
        }
        else if (a.ContainsKey("blocks")) throw Err("blocks: a list, like [{\"text\": \"...\"}]");
        else if (a.ContainsKey("text")) blocks = new[] { Text(a.Str("text") ?? "", TextStyle.Body) };   // a label's shorthand
        if (blocks.Count == 0 && title == null) throw Err("nothing to show: give blocks (or text)");

        double? forS = old?.ForSeconds;
        if (a.ContainsKey("for"))
        {
            forS = a.Num("for");
            if (forS is { } f && (double.IsNaN(f) || f < 3 || f > 86400)) throw Err("for: seconds, 3 to 86400 (or null to stay)");
        }
        if (at.InWorld && blocks.Any(b => b is ImageBlock or MapBlock or TableBlock))
            throw Err("images, maps and tables go in view, at the wrist or in the room, not out in the world (too small to read there)");
        return new ElementSpec(id, at, title, blocks, forS);
    }

    public static Anchor ParseAnchor(JsonNode? n, ulong here)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            s = s.Trim().ToLowerInvariant();
            if (s is "view" or "here" or "screen") return new Anchor(AnchorKind.View);
            if (s is "wrist" or "watch") return new Anchor(AnchorKind.Wrist);
            if (s is "room") return new Anchor(AnchorKind.Room);
            if (UUID.TryParse(s, out var id) && id != UUID.Zero) return new Anchor(AnchorKind.Thing, new Target(id, default));
            throw Err("at: view, wrist, room, an object's or avatar's id, or [x,y,z] in this region");
        }
        if (n is JsonObject o)
        {
            foreach (var k in new[] { "object", "avatar", "id", "on" })
                if (o.Str(k) is { } ids && UUID.TryParse(ids, out var id) && id != UUID.Zero) return new Anchor(AnchorKind.Thing, new Target(id, default));
            if (o["pos"] is JsonArray pa) return new Anchor(AnchorKind.Point, new Target(UUID.Zero, Point(pa, here)));
        }
        if (n is JsonArray a) return new Anchor(AnchorKind.Point, new Target(UUID.Zero, Point(a, here)));
        throw Err("at: view, wrist, room, an object's or avatar's id, or [x,y,z] in this region");
    }

    static Vector3d Point(JsonArray a, ulong here)
    {
        var xyz = a.Select(Json.AsDouble).ToArray();
        if (xyz.Length is < 2 or > 3 || xyz.Any(d => d is not { } f || !double.IsFinite(f) || Math.Abs(f) > 4096)) throw Err("a position is [x, y, z] in metres in this region");
        if (here == 0) throw Err("not in a region yet");
        Utils.LongToUInts(here, out uint rx, out uint ry);
        return new Vector3d(rx + xyz[0]!.Value, ry + xyz[1]!.Value, xyz.Length > 2 ? xyz[2]!.Value : double.NaN);
    }

    static Target ParseTarget(JsonNode? n, ulong here, string what)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s) && UUID.TryParse(s.Trim(), out var id) && id != UUID.Zero) return new Target(id, default);
        if (n is JsonArray a) return new Target(UUID.Zero, Point(a, here));
        throw Err($"{what}: an object's or avatar's id, or [x,y,z] in this region");
    }

    static string Clip(string? s) => (s ?? "").Trim();

    static TextBlock Text(string s, TextStyle style)
    {
        var t = Clip(s);
        if (t.Length == 0) throw Err("text: empty");
        return new TextBlock(t, style);
    }

    static Block ParseBlock(JsonObject o, ulong here, double now, Func<string, ImageBlock>? picture)
    {
        if (o.ContainsKey("text"))
        {
            var style = (o.Str("style") ?? "body").ToLowerInvariant() switch
            {
                "body" or "" => TextStyle.Body, "title" or "heading" => TextStyle.Title, "big" or "figure" => TextStyle.Big,
                "dim" or "small" or "note" => TextStyle.Dim, _ => throw Err("style: body, title, big or dim"),
            };
            return Text(o.Str("text") ?? "", style);
        }
        if (o["list"] is JsonArray items)
        {
            if (items.Count == 0) throw Err("list: empty");
            var list = items.Select(i => i switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => new ListItem(Clip(s), null, null),
                JsonObject io => new ListItem(Clip(io.Str("text") ?? io.Str("label") ?? io.Str("name")),
                    io.Str("detail") is { } d ? Clip(d) : null, io.Bool("done")),
                _ => throw Err("list: items are text or {text, detail?, done?}"),
            }).ToList();
            if (list.Any(i => i.Text.Length == 0)) throw Err("list: an item has no text");
            return new ListBlock(list, o.Bool("numbered", false));
        }
        if (o["table"] is JsonArray rows)
        {
            if (rows.Count == 0) throw Err("table: empty");
            var cells = rows.Select(r => r is JsonArray ra
                ? ra.Select(c => Clip(c is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : c?.ToJsonString() ?? "")).ToArray()
                : throw Err("table: rows are lists of cells")).ToList();
            int cols = cells.Max(r => r.Length);
            if (cols == 0) throw Err("table: no cells");
            return new TableBlock(cells.Select(r => r.Concat(Enumerable.Repeat("", cols - r.Length)).ToArray()).ToList(), o.Bool("header", false));
        }
        if (o.ContainsKey("timer"))
        {
            var sec = o.Num("timer");
            if (sec is not { } s || double.IsNaN(s) || s < 1 || s > 86400) throw Err("timer: seconds to count down, 1 to 86400");
            return new TimerBlock(s, false, now);
        }
        if (o.ContainsKey("stopwatch")) return new TimerBlock(0, true, now - Math.Clamp(o.Num("from") ?? 0, 0, 86400));
        if (o.ContainsKey("progress"))
        {
            double v = o.Num("progress") ?? double.NaN, max = o.Num("of") ?? 1;
            if (!double.IsFinite(v) || !double.IsFinite(max) || max <= 0 || v < 0) throw Err("progress: a value, and of: the whole (default 1)");
            return new ProgressBlock(Math.Min(v, max), max, o.Str("label") is { } l ? Clip(l) : null);
        }
        if (o["chart"] is JsonArray pts)
        {
            bool line = (o.Str("kind") ?? "bars").ToLowerInvariant() == "line";
            var points = pts.Select(p => p switch
            {
                JsonArray pa when pa.Count == 2 && Json.AsDouble(pa[1]) is { } y => ((string?)Clip(pa[0]?.ToString()), y),
                JsonObject po when po.Num("value") is { } y => (po.Str("label") is { } l ? Clip(l) : null, y),
                _ when Json.AsDouble(p) is { } y => ((string?)null, y),
                _ => throw Err("chart: values, or [label, value] pairs"),
            }).ToList();
            if (points.Count == 0) throw Err("chart: empty");
            if (points.Any(p => !double.IsFinite(p.Item2))) throw Err("chart: values are numbers");
            if (line && points.Count < 2) throw Err("chart: a line needs two values or more");
            if (!line && points.Any(p => p.Item2 < 0)) throw Err("chart: bars show amounts of zero or more");
            return new ChartBlock(points, line, o.Str("unit") is { } u ? Clip(u) : null);
        }
        if (o.ContainsKey("image"))
        {
            var src = o.Str("image")?.Trim() ?? "";
            if (src.ToLowerInvariant() is "view" or "the view") return new ImageBlock("view");
            if (UUID.TryParse(src, out var tid) && tid != UUID.Zero) return new ImageBlock(tid.ToString());
            if (src.Length > 0 && picture != null) return picture(src);
            throw Err("image: \"view\" (what they see now), \"last\" (the last picture saved), a picture's file name in Pictures\\Sojourn, or a texture's id");
        }
        if (o.ContainsKey("toward"))
            return new TowardBlock(ParseTarget(o["toward"], here, "toward"), o.Str("label") is { } l ? Clip(l) : null);
        if (o["map"] is JsonArray mp)
        {
            if (mp.Count == 0) throw Err("map: give the places to mark");
            var points = mp.Select(p => p is JsonObject po
                ? (ParseTarget(po["at"] ?? po["id"], here, "map place"), po.Str("label") is { } l ? (string?)Clip(l) : null)
                : (ParseTarget(p, here, "map place"), (string?)null)).ToList();
            return new MapBlock(points);
        }
        throw Err($"a block is one of: {string.Join(", ", Kinds)} (e.g. {{\"text\": \"...\"}}, {{\"timer\": 600}})");
    }

    public static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}

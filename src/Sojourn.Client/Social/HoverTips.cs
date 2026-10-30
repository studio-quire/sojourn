using LibreMetaverse;
using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Core.Agents;
using Sojourn.Render.World;
using static Sojourn.Session.Assistant.Panel;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Social;

/// <summary>
/// The viewer's hover tips (LLToolPie's tooltip after ToolTipDelay, 0.7 s): the pointer (the mouse, or the right hand's
/// ray while its pointer is on) resting on a thing shows what it is in a small capsule over the point: its name, and
/// its price when it's for sale. Little text, by the studio's rule: no owner, description or "click to" hints (the
/// viewer's tip has an inspect button and those); nothing for things named only "Object", nor for people while their
/// name tags show (the tag says it), nor for the ground (the viewer's land tips are off by default). Off with the
/// hover_tips setting. Claude Opus 5.5, 2026-10-28 (batch B4).
/// </summary>
public sealed class HoverTips
{
    /// <summary>A tip's picture: its line, a second dim one (the price), and the look.</summary>
    public sealed record Tip(string Text, string? Sub, PanelLook Look);

    /// <summary>One to draw this frame: over At (the eye's region frame), Distance away.</summary>
    public readonly record struct Want(Tip Tip, Vector3 At, float Distance, float Alpha);

    readonly AgentSession _s;
    UUID _on;
    double _since;
    float _alpha;

    public HoverTips(AgentSession session) => _s = session;

    static readonly HashSet<string> Unnamed = new(StringComparer.OrdinalIgnoreCase) { "", "Object", "Primitive", "an object", "a HUD" };

    /// <summary>This frame's tip, if the pointer has rested on something long enough. region: the eye's.</summary>
    public Want? Frame(PointerTarget? t, ulong region, Vector3 eye, PanelLook look, SocialSettings opt, bool namesShown, float dt, double now)
    {
        UUID key = t is null || t.Hud || t.Region != region ? UUID.Zero : t.Kind switch
        {
            PickKind.Object => t.RootId,
            PickKind.Avatar when !namesShown => t.Id,
            _ => UUID.Zero,
        };
        if (!opt.HoverTips || key == UUID.Zero) { _on = UUID.Zero; _alpha = 0; return null; }
        if (key != _on) { _on = key; _since = now; _alpha = 0; return null; }
        if (now - _since < opt.HoverTipDelay) return null;
        var (text, sub) = Words(t!);
        if (text == null) return null;
        _alpha = Math.Min(1, _alpha + dt * 8);
        return new Want(new Tip(text, sub, look with { TintOpacity = Math.Max(look.TintOpacity, 0.88f) }), t!.Point, Vector3.Distance(t.Point, eye), _alpha);
    }

    (string? Text, string? Sub) Words(PointerTarget t)
    {
        if (t.Kind == PickKind.Avatar)
            return (_s.People.Pseudonym(t.Id) ?? _s.Names.DisplayName(t.Id) ?? _s.Names.Legacy(t.Id), null);
        Primitive? root = null;
        foreach (var rw in _s.VisibleRegions()) if ((root = rw.PrimById(t.RootId)) != null) break;
        var name = root?.Properties?.Name is { Length: > 0 } n ? n : t.Name;
        // the pointer's own name says whose an attachment is; a tip names the thing
        int worn = name.IndexOf(" (worn by ", StringComparison.Ordinal);
        if (worn > 0) name = name[..worn];
        name = name.Trim();
        if (Unnamed.Contains(name)) return (null, null);
        string? price = root?.Properties is { } p && p.SaleType != SaleType.Not ? $"L${p.SalePrice}" : null;
        return (name, price);
    }

    const float Size = 13.5f, SubSize = 11.5f, Pad = 9;

    internal static NameTags.Pic Draw(Tip t, float scale)
    {
        var ink = Ink.For(t.Look);
        var f = Font(ink.Semi, Size);
        var sf = Font(ink.Regular, SubSize);
        var text = Ellipsize(t.Text, f, 240);
        float w = MathF.Ceiling(2 * Pad + Math.Max(Measure(f, text), t.Sub != null ? Measure(sf, t.Sub) : 0));
        float h = t.Sub != null ? 39 : 25;
        var (pixels, pw, ph, _) = Rasterise(scale, w, new List<PanelKey>(), (c, height) =>
        {
            var draw = new Painter(c, ink, w);
            draw.Body(h);
            draw.Text(text, (w - Measure(f, text)) / 2, 17, f, ink.Text);
            if (t.Sub != null) draw.Text(t.Sub, (w - Measure(sf, t.Sub)) / 2, 31.5f, sf, ink.Dim);
            return h;
        });
        return new NameTags.Pic(pixels, pw, ph, 0, 0);
    }
}

using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using static Sojourn.Session.Assistant.Panel;

namespace Sojourn.Client.Login;

public sealed partial class LoginSheet
{
    readonly record struct SheetKey(int Id, SKRect Rect);

    /// <summary>Everything a drawing needs, taken on the main thread (drawing runs on a worker).</summary>
    sealed record Snap(
        Stage Stage, long Version, int Hover, bool Help, string? Note, int Phase,
        IReadOnlyList<(string Name, string Grid)> Accounts, int Selected, bool SelectedRemembered, bool ForgetArmed,
        string User, string Pass, string Uri, string Region, string Code, int Focus,
        bool Remember, int Grid, int Start, bool Agree, bool RememberComputer, int TermsPage,
        string? TermsKind, string? TermsText, int MfaAsks, string? Plain, string? Detail, bool WrongPassword, string Who, bool Screen, string About);

    Snap Take(int phase)
    {
        var s = _session;
        var stage = Current ?? Stage.Connecting;
        int focus = _focus == _user ? Ids.UserField : _focus == _pass ? Ids.PassField : _focus == _uri ? Ids.UriField
            : _focus == _region ? Ids.RegionField : _focus == _code ? Ids.CodeField : -1;
        (string? plain, string? detail) = s?.LoginFailure is { } f ? LoginWords.For(f.Key, f.Message) : (null, null);
        string who = Chosen?.Account.Name ?? (s != null ? (s.Config.Last.Equals("Resident", StringComparison.OrdinalIgnoreCase) ? s.Config.First : s.Config.LoginName) : "");
        return new Snap(stage, Interlocked.Read(ref _version), _hover, _help, _note, phase,
            _accounts.Select(a => (a.Name, a.Grid == Grids.Beta ? Lang.T("Beta") : Grids.Label(a.Grid, a.GridName))).ToList(), _selected,
            _selected >= 0 && _selected < _accounts.Count && _accounts[_selected].Remembered, _forgetArmed,
            _user.Shown, _pass.Shown, _uri.Shown, _region.Shown, _code.Shown, focus,
            _remember, _grid, _start, _agree, _rememberComputer, _termsPage,
            s?.PendingTerms?.Kind, s?.PendingTerms?.Message, s?.MfaAsks ?? 0, plain, detail,
            FromScreen && s?.LoginFailure?.Key == "key", who, FromScreen, About);
    }

    sealed record Drawn(OverlayImage Image, float Scale, float LogicalW, float LogicalH, IReadOnlyList<SheetKey> Keys, long Version);
    volatile Drawn? _latest;
    Drawn? _drawn;
    volatile bool _drawing;
    long _drawnKey = -1;
    float _drawnScale;
    bool _drawnLayer;
    PanelLook? _drawnLook;
    long _imageVersion;

    /// <summary>The sheet's picture at a scale (raster px a logical px), redrawn on a worker when anything in it
    /// changed; the last finished one meanwhile (null until the first is).</summary>
    (OverlayImage Image, float Scale, float W, float H)? Picture(float scale, bool layer)
    {
        Tick();
        var stage = Current;
        int phase = stage == Stage.Connecting ? (int)(Environment.TickCount64 / 125) % 16 : 0;
        long key = Interlocked.Read(ref _version) * 32 + phase;
        var look = _look();
        if (!_drawing && (key != _drawnKey || scale != _drawnScale || layer != _drawnLayer || !ReferenceEquals(look, _drawnLook)))
        {
            _drawing = true;
            (_drawnKey, _drawnScale, _drawnLayer, _drawnLook) = (key, scale, layer, look);
            var snap = Take(phase);
            Task.Run(() =>
            {
                try { _latest = Render(snap, look, scale, layer); }
                catch (Exception e) { _log($"login sheet: drawing failed ({e.Message})"); }
                finally { _drawing = false; }
            });
        }
        _drawn = _latest;
        return _drawn is { } d ? (d.Image, d.Scale, d.LogicalW, d.LogicalH) : null;
    }

    Drawn Render(Snap s, PanelLook look, float scale, bool layer)
    {
        var ink = Ink.For(look);
        var keys = new List<SheetKey>();
        var (pixels, w, h, used) = Rasterise(scale, Width, new List<PanelKey>(), (c, height) =>
        {
            keys.Clear();
            var draw = new Painter(c, ink, Width);
            return s.Stage switch
            {
                Stage.Form => LayoutForm(draw, s, keys, height),
                Stage.Terms => LayoutTerms(draw, s, keys, height),
                Stage.Mfa => LayoutMfa(draw, s, keys, height),
                Stage.Failed => LayoutFailed(draw, s, keys, height),
                _ => LayoutConnecting(draw, s, keys, height),
            };
        }, MaxRasterWidth);
        GlassLight.Finish(pixels, look, layer);
        return new Drawn(new OverlayImage(pixels, w, h, Interlocked.Increment(ref _imageVersion), look.Frost, look.TintOpacity), used, w / used, h / used, keys, s.Version);
    }

    const float Pad = 18;

    // ---- the parts ----

    static float Header(Painter draw, Snap s, List<SheetKey> keys, string title, bool close)
    {
        var k = draw.K;
        float y = 14, W = draw.W;
        var closeKey = new SKRect(W - 10 - 38, y, W - 10, y + 38);
        var helpKey = close ? new SKRect(closeKey.Left - 4 - 38, y, closeKey.Left - 4, y + 38) : closeKey;
        draw.Text(Ellipsize(title, Font(k.Semi, 17), helpKey.Left - Pad - 10), Pad + 2, y + 25, Font(k.Semi, 17), k.Text);
        draw.GlyphKey(helpKey, Painter.Glyph.Help, s.Hover == Ids.Help, on: s.Help);
        keys.Add(new SheetKey(Ids.Help, helpKey));
        if (close)
        {
            draw.GlyphKey(closeKey, Painter.Glyph.Close, s.Hover == Ids.Close);
            keys.Add(new SheetKey(Ids.Close, closeKey));
        }
        return y + 38 + 12;
    }

    static float Help(Painter draw, Snap s, float y, IEnumerable<string> lines)
    {
        if (!s.Help) return y;
        var k = draw.K;
        var hf = Font(k.Regular, 14);
        var wrapped = lines.SelectMany(l => Wrap(Lang.T(l), hf, draw.W - 2 * Pad - 24, 4)).ToList();
        var box = new SKRect(Pad, y, draw.W - Pad, y + 14 + wrapped.Count * 20);
        draw.Recess(box, 14);
        float hy = y + 7;
        foreach (var l in wrapped) { draw.Text(l, Pad + 12, hy + 14, hf, k.Dim); hy += 20; }
        return box.Bottom + 12;
    }

    /// <summary>A text field: its words (or a faint placeholder), a caret and a ring while it has the keys.</summary>
    static float FieldBox(Painter draw, Snap s, List<SheetKey> keys, int id, string text, string placeholder, float y, bool focused)
    {
        var k = draw.K;
        var r = new SKRect(Pad, y, draw.W - Pad, y + 42);
        draw.Recess(r, 14);
        var f = Font(k.Regular, 16.5f);
        float room = r.Width - 28;
        if (text.Length > 0)
        {
            // the end of a long entry shows, as a field scrolls
            var shown = text;
            while (shown.Length > 1 && Measure(f, shown) > room) shown = shown[1..];
            draw.Text(shown, r.Left + 14, r.MidY + 6, f, k.Text);
            if (focused && draw.Canvas is { } c)
            {
                float x = r.Left + 14 + Measure(f, shown) + 2;
                using var p = new SKPaint { Color = k.Text, StrokeWidth = 1.5f, IsAntialias = true };
                c.DrawLine(x, r.MidY - 10, x, r.MidY + 10, p);
            }
        }
        else
        {
            draw.Text(placeholder, r.Left + 14, r.MidY + 6, f, k.Faint);
            if (focused && draw.Canvas is { } c)
            {
                using var p = new SKPaint { Color = k.Text, StrokeWidth = 1.5f, IsAntialias = true };
                c.DrawLine(r.Left + 13, r.MidY - 10, r.Left + 13, r.MidY + 10, p);
            }
        }
        if ((focused || s.Hover == id) && draw.Canvas is { } cc)
        {
            using var ring = new SKPaint { Color = A(focused ? k.Accent : k.Text, focused ? 0.7f : 0.35f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            cc.DrawRoundRect(new SKRect(r.Left + 0.75f, r.Top + 0.75f, r.Right - 0.75f, r.Bottom - 0.75f), 14, 14, ring);
        }
        keys.Add(new SheetKey(id, r));
        return r.Bottom + 10;
    }

    /// <summary>A row with a label and a switch; the whole row is the key.</summary>
    static float SwitchRow(Painter draw, Snap s, List<SheetKey> keys, int id, string label, bool on, float y)
    {
        var k = draw.K;
        var row = new SKRect(Pad - 6, y, draw.W - Pad + 6, y + 44);
        bool lit = s.Hover == id;
        if (lit) draw.Fill(new SKRect(row.Left, row.Top + 2, row.Right, row.Bottom - 2), 14, k.Look.KeyOpacity * 0.9f);
        var sw = new SKRect(row.Right - 8 - 46, row.MidY - 13, row.Right - 8, row.MidY + 13);
        draw.Switch(sw, on, lit, true);
        draw.Text(Ellipsize(label, Font(k.Regular, 16), sw.Left - row.Left - 20), row.Left + 10, row.MidY + 6, Font(k.Regular, 16), k.Text);
        keys.Add(new SheetKey(id, row));
        return row.Bottom + 4;
    }

    /// <summary>A row with a label and a segmented control.</summary>
    static float SegmentRow(Painter draw, Snap s, List<SheetKey> keys, int firstId, string label, string[] options, int selected, float y)
    {
        var k = draw.K;
        var f = Font(k.Regular, 14.5f);
        float right = draw.W - Pad, cy = y + 22;
        var widths = options.Select(o => Measure(f, o) + 26).ToArray();
        var well = new SKRect(right - widths.Sum() - 6, cy - 17, right, cy + 17);
        draw.Recess(well, 17);
        float x = well.Left + 3;
        for (int o = 0; o < options.Length; o++)
        {
            var seg = new SKRect(x, well.Top + 3, x + widths[o], well.Bottom - 3);
            draw.Segment(seg, options[o], o == selected, s.Hover == firstId + o, true);
            keys.Add(new SheetKey(firstId + o, seg));
            x = seg.Right;
        }
        if (label.Length > 0) draw.Text(Ellipsize(label, Font(k.Regular, 16), well.Left - Pad - 8), Pad + 4, cy + 6, Font(k.Regular, 16), k.Text);
        return y + 44 + 6;
    }

    /// <summary>One or two keys across the foot: a plain key (left) and the accent key (right, armed or not).</summary>
    static float Keys(Painter draw, Snap s, List<SheetKey> keys, float y, (int Id, string Label)? plain, (int Id, string Label, bool Armed)? accent, float plainWidth = 0)
    {
        float W = draw.W;
        if (plain is { } p && accent is { } a)
        {
            float mid = plainWidth > 0 ? Pad + plainWidth + 5 : W / 2;
            var left = new SKRect(Pad, y, mid - 5, y + 46);
            var right = new SKRect(mid + 5, y, W - Pad, y + 46);
            draw.Key(left, p.Label, s.Hover == p.Id);
            keys.Add(new SheetKey(p.Id, left));
            draw.AccentKey(right, a.Label, s.Hover == a.Id, a.Armed);
            keys.Add(new SheetKey(a.Id, right));
        }
        else if (accent is { } only)
        {
            var r = new SKRect(Pad, y, W - Pad, y + 46);
            draw.AccentKey(r, only.Label, s.Hover == only.Id, only.Armed);
            keys.Add(new SheetKey(only.Id, r));
        }
        else if (plain is { } lone)
        {
            var r = new SKRect(Pad, y, W - Pad, y + 46);
            draw.Key(r, lone.Label, s.Hover == lone.Id);
            keys.Add(new SheetKey(lone.Id, r));
        }
        return y + 46 + 14;
    }

    static float Lines(Painter draw, string text, float size, SKColor color, float y, int max)
    {
        var f = Font(draw.K.Regular, size);
        foreach (var l in Wrap(text, f, draw.W - 2 * Pad - 8, max)) { draw.Text(l, Pad + 4, y + size, f, color); y += size * 1.4f; }
        return y + 6;
    }

    // ---- the stages ----

    static readonly string[] FormHelp =
    {
        "your username as on the Second Life website: firstname lastname, or the one name",
        "the password goes only to the grid; it's kept only with remember on, as the other viewers keep it",
        "other: an OpenSim grid's login URI · start: a region's name, or a SLurl",
        "Tab moves between fields, Enter logs in, Esc quits · in the headset, press a field for the keyboard",
    };

    static float LayoutForm(Painter draw, Snap s, List<SheetKey> keys, float height)
    {
        var k = draw.K;
        draw.Body(height);
        float y = Header(draw, s, keys, "Sojourn", close: true);
        y = Help(draw, s, y, FormHelp);

        // the saved accounts, the chosen one lit, and a new one
        if (s.Accounts.Count > 0)
        {
            var nf = Font(k.Regular, 16.5f);
            var gf = Font(k.Regular, 13.5f);
            for (int i = 0; i < s.Accounts.Count && i < 6; i++)
            {
                var row = new SKRect(Pad - 6, y, draw.W - Pad + 6, y + 42);
                int id = Ids.Account + i;
                bool sel = i == s.Selected;
                if (sel) draw.KeyFace(row, 14, s.Hover == id);
                else if (s.Hover == id) draw.Fill(row, 14, k.Look.KeyOpacity * 0.9f);
                var (name, grid) = s.Accounts[i];
                float gw = Measure(gf, grid);
                draw.Text(grid, row.Right - 12 - gw, row.MidY + 5, gf, k.Dim);
                draw.Text(Ellipsize(name, nf, row.Width - gw - 40), row.Left + 12, row.MidY + 6, nf, sel ? k.Text : k.Dim);
                keys.Add(new SheetKey(id, row));
                y = row.Bottom + 4;
            }
            var add = new SKRect(Pad - 6, y, draw.W - Pad + 6, y + 38);
            if (s.Selected < 0) draw.KeyFace(add, 14, s.Hover == Ids.New);
            else if (s.Hover == Ids.New) draw.Fill(add, 14, k.Look.KeyOpacity * 0.9f);
            var pf = Font(k.Regular, 20);
            // + and its words, as the launcher's "new account"
            var af = Font(k.Regular, 16.5f);
            var adding = Lang.T("new account");
            float aw = Measure(pf, "+") + 10 + Measure(af, adding), ax = add.MidX - aw / 2;
            draw.Text("+", ax, add.MidY + 7, pf, s.Selected < 0 ? k.Text : k.Dim);
            draw.Text(adding, ax + Measure(pf, "+") + 10, add.MidY + 6, af, s.Selected < 0 ? k.Text : k.Dim);
            keys.Add(new SheetKey(Ids.New, add));
            y = add.Bottom + 8;
            draw.Hairline(Pad, draw.W - Pad, y);
            y += 12;
        }

        if (s.Selected < 0) y = FieldBox(draw, s, keys, Ids.UserField, s.User, Lang.T("username"), y, s.Focus == Ids.UserField);
        bool kept = s.Selected >= 0 && s.SelectedRemembered && s.Pass.Length == 0;
        y = FieldBox(draw, s, keys, Ids.PassField, kept ? "••••••••" : s.Pass, Lang.T("password"), y, s.Focus == Ids.PassField);
        y = SwitchRow(draw, s, keys, Ids.Remember, Lang.T("remember password"), s.Remember, y);
        if (s.Selected < 0)
        {
            y = SegmentRow(draw, s, keys, Ids.Grid, Lang.T("grid"), new[] { "Second Life", Lang.T("Beta"), Lang.T("other") }, s.Grid, y);
            if (s.Grid == 2) y = FieldBox(draw, s, keys, Ids.UriField, s.Uri, Lang.T("login URI"), y, s.Focus == Ids.UriField);
        }
        y = SegmentRow(draw, s, keys, Ids.Start, Lang.T("start", "login"), new[] { Lang.T("last"), Lang.T("home", "start"), Lang.T("a place") }, s.Start, y);
        if (s.Start == 2) y = FieldBox(draw, s, keys, Ids.RegionField, s.Region, Lang.T("region or SLurl"), y, s.Focus == Ids.RegionField);
        if (s.Note is { } note) y = Lines(draw, Lang.T(note), 15, k.Accent, y, 1);
        y += 4;
        y = Keys(draw, s, keys, y, s.Selected >= 0 ? (Ids.Forget, Lang.T(s.ForgetArmed ? "sure?" : "forget account")) : null, (Ids.LogIn, Lang.T("Log in"), true), plainWidth: 150);
        // the version, as the TPV policy asks of a login screen (1.g)
        var vf = Font(k.Regular, 12.5f);
        var version = s.About;
        draw.Text(version, draw.W / 2 - Measure(vf, version) / 2, y + 4, vf, k.Dim);
        return y + 18;
    }

    static float LayoutConnecting(Painter draw, Snap s, List<SheetKey> keys, float height)
    {
        var k = draw.K;
        draw.Body(height);
        float y = 16;
        draw.Orb(36, y + 24, s.Phase);
        draw.Text(Ellipsize(s.Who.Length > 0 ? s.Who : "Sojourn", Font(k.Semi, 17), draw.W - 90), 70, y + 30, Font(k.Semi, 17), k.Text);
        y += 62;
        return s.Screen ? Keys(draw, s, keys, y, (Ids.Cancel, Lang.T("Cancel")), null) : y;
    }

    const int TermsLines = 12;

    static float LayoutTerms(Painter draw, Snap s, List<SheetKey> keys, float height)
    {
        var k = draw.K;
        draw.Body(height);
        bool tos = s.TermsKind == "tos";
        float y = Header(draw, s, keys, Lang.T(tos ? "Terms of Service" : "From the grid"), close: false);
        y = Help(draw, s, y, tos
            ? new[] { "the grid asks you to agree before you go on; the terms are also at lindenlab.com/tos", "no one can agree for you: only your own press counts" }
            : new[] { "the grid asks you to read this before you go on", "only your own press counts" });
        var f = Font(k.Regular, 15);
        var lines = Wrap(Clean(s.TermsText ?? ""), f, draw.W - 2 * Pad - 8, 400);
        int pages = Math.Max(1, (lines.Count + TermsLines - 1) / TermsLines);
        int page = Math.Clamp(s.TermsPage, 0, pages - 1);
        float top = y;
        foreach (var l in lines.Skip(page * TermsLines).Take(TermsLines)) { draw.Text(l, Pad + 4, y + 15, f, k.Text); y += 21; }
        if (pages > 1)
        {
            y = top + TermsLines * 21 + 6;
            var prev = new SKRect(Pad, y, Pad + 44, y + 34);
            var next = new SKRect(draw.W - Pad - 44, y, draw.W - Pad, y + 34);
            if (page > 0) { draw.Key(prev, "‹", s.Hover == Ids.PagePrev, 18); keys.Add(new SheetKey(Ids.PagePrev, prev)); }
            if (page < pages - 1) { draw.Key(next, "›", s.Hover == Ids.PageNext, 18); keys.Add(new SheetKey(Ids.PageNext, next)); }
            draw.PageDots(draw.W / 2, y + 17, pages, page);
            y += 42;
        }
        y += 6;
        if (tos) y = SwitchRow(draw, s, keys, Ids.Agree, Lang.T("I agree to the Terms of Service"), s.Agree, y);
        y += 6;
        return Keys(draw, s, keys, y, (Ids.Decline, Lang.T("Decline")), (Ids.Continue, Lang.T("Continue"), !tos || s.Agree));
    }

    static string Clean(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, "<br\\s*/?>|</p>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(t).Trim();
    }

    static float LayoutMfa(Painter draw, Snap s, List<SheetKey> keys, float height)
    {
        var k = draw.K;
        draw.Body(height);
        float y = Header(draw, s, keys, Lang.T("Authenticator code"), close: false);
        y = Help(draw, s, y, new[] { "the code your authenticator app shows for this account", "remember: this computer won't be asked again for 30 days" });
        if (s.MfaAsks > 1) y = Lines(draw, Lang.T("That code didn't work."), 15, k.Accent, y, 1);
        y = FieldBox(draw, s, keys, Ids.CodeField, s.Code, "000000", y, s.Focus == Ids.CodeField);
        y = SwitchRow(draw, s, keys, Ids.RememberComputer, Lang.T("remember this computer"), s.RememberComputer, y);
        y += 6;
        return Keys(draw, s, keys, y, s.Screen ? (Ids.Cancel, Lang.T("Cancel")) : null, (Ids.Continue, Lang.T("Continue"), s.Code.Length >= 6));
    }

    static float LayoutFailed(Painter draw, Snap s, List<SheetKey> keys, float height)
    {
        var k = draw.K;
        draw.Body(height);
        float y = Header(draw, s, keys, s.Who.Length > 0 ? s.Who : "Sojourn", close: false);
        y = Help(draw, s, y, new[] { "Back: the login screen, to change the account, grid or start", "Try again: the same login once more" });
        y = Lines(draw, s.Plain ?? Lang.T("Couldn't log in."), 16.5f, k.Text, y, 3);
        if (s.Detail is { } d) y = Lines(draw, d, 14, k.Dim, y, 4);
        if (s.WrongPassword) y = FieldBox(draw, s, keys, Ids.PassField, s.Pass, Lang.T("password"), y + 4, s.Focus == Ids.PassField);
        y += 6;
        return Keys(draw, s, keys, y, s.Screen ? (Ids.Back, Lang.T("Back")) : null, (Ids.TryAgain, Lang.T("Try again"), true));
    }
}

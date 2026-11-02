using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sojourn.Session.Login;

namespace Sojourn.Launcher;

/// <summary>--shots DIR: the window's states drawn offscreen at 2x into PNGs (no window, no focus), over made-up accounts
/// and settings in a scratch folder that's deleted after; never the person's own. In English, or --lang CODE's language;
/// --pseudo long (every string 40% longer, as German or Russian run, drawn as de) or cjk (CJK filler without spaces,
/// drawn as ja) fills a made-up translation into the catalogs in memory only, for checking the layout: anything still in
/// English in those pictures isn't going through Lang.</summary>
static class Shots
{
    public static void Write(string dir, Places real, string? lang = null, string? pseudo = null)
    {
        if (pseudo != null) lang = Pseudo(pseudo);
        Directory.CreateDirectory(dir);
        var scratch = Path.Combine(Path.GetTempPath(), "sojourn-launcher-shots-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("SOJOURN_LOGINS", Path.Combine(scratch, "logins"));
        Environment.SetEnvironmentVariable("SOJOURN_KEYS", Path.Combine(scratch, "keys"));
        try
        {
            var places = new Places(Path.Combine(scratch, "base"));
            new SettingsFile(places.SettingsPath).Graphics = "balanced";

            // a language other than English: the views made in English first and changed to it as the globe changes
            // it, to be the same as made in it (changed-*.png beside first.png and settings.png)
            if (lang is { } l && Lang.Find(l)?.Code != "en")
            {
                Lang.Use("en");
                var first = Make(places);
                var settings = Make(places, v => v.ShowPage("settings"));
                Draw(first, null);
                Draw(settings, null);
                Lang.Use(l);
                first.LanguageChanged();
                settings.LanguageChanged();
                Draw(first, Path.Combine(dir, "changed-first.png"));
                Draw(settings, Path.Combine(dir, "changed-settings.png"));
            }
            Lang.Use(lang ?? "en");

            // no accounts yet: the new-account form
            Draw(Make(places), Path.Combine(dir, "first.png"));

            var store = new LoginStore(places.BaseDir);
            store.Logged(new SavedAccount { First = "Sojourn", Last = "Pilot", Grid = "http://127.0.0.1:9700/", GridName = "Atelier", Start = "uri:Atelier&128&118&26" }, null, false);
            store.Logged(new SavedAccount { First = "juniper", Last = "Resident", Grid = Grids.Main, Start = "last" }, "$1$" + new string('0', 32), true);

            Draw(Make(places), Path.Combine(dir, "account.png"));
            Draw(Make(places, dev: true), Path.Combine(dir, "dev.png"));
            Draw(Make(places, v => v.ShowPage("accounts")), Path.Combine(dir, "accounts.png"));
            Draw(Make(places, v => v.ShowPage("new")), Path.Combine(dir, "new.png"));
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings.png"));
            Draw(Make(places, v => v.ShowUpdateFor("0.1.0.812")), Path.Combine(dir, "update.png"));
            // OpenRouter with a decoy key kept (never a real one: the keys folder is the scratch one)
            var file = new SettingsFile(places.SettingsPath) { Provider = "openrouter" };
            Sojourn.Session.Assistant.ProviderKeys.Keep("sk-or-v1-" + new string('0', 40));
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings-openrouter-claude.png"));
            file.OpenRouterModel = "google/gemini-4-argon";
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings-openrouter.png"));
            // the list under ▾, drawn on its own over the window's light (a popup isn't part of the view)
            var list = Make(places, v => v.ShowPage("settings")).ModelListShot();
            var back = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xF1)), Padding = new Thickness(12), Child = list };
            back.Width = list.Width + 24; back.Measure(new Size(back.Width, double.PositiveInfinity)); back.Height = back.DesiredSize.Height;
            Draw(back, Path.Combine(dir, "settings-openrouter-list.png"));
            // the language list above the globe, as the model list
            var languages = Make(places).LanguageListShot();
            var behind = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xF1)), Padding = new Thickness(12), Child = languages };
            behind.Width = languages.Width + 24; behind.Measure(new Size(behind.Width, double.PositiveInfinity)); behind.Height = behind.DesiredSize.Height;
            Draw(behind, Path.Combine(dir, "language-list.png"));
            file.Provider = "anthropic";
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings-anthropic.png"));
            // Studio Quire's relay: its choice shows once a token is kept (a decoy)
            Sojourn.Session.Assistant.ProviderKeys.Keep("sqr-" + new string('0', 43));
            file.Provider = "relay";
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings-relay.png"));
            Draw(Make(places, v => v.ShowStatus(new RunStatus(Phase.Code, Headset: true))), Path.Combine(dir, "code-headset.png"));
            Draw(Make(places, v => v.ShowStatus(new RunStatus(Phase.Code, Headset: true, Question: new() { ["asks"] = 1 }, Answers: true))),
                Path.Combine(dir, "code.png"));
            Draw(Make(places, v => v.ShowStatus(new RunStatus(Phase.Code, Headset: true, Question: new() { ["asks"] = 2 }, Answers: true))),
                Path.Combine(dir, "code-again.png"));
            var terms = "Linden Lab Terms of Service" + Environment.NewLine + Environment.NewLine
                + "These terms changed on 2026-10-01. Made-up text for the picture: "
                + string.Concat(Enumerable.Repeat("By using Second Life you agree to these terms and to the policies they name. ", 12));
            Draw(Make(places, v => v.ShowStatus(new RunStatus(Phase.Terms, Headset: true, Question: new() { ["kind"] = "tos", ["text"] = terms }, Answers: true))),
                Path.Combine(dir, "terms.png"));
            Draw(Make(places, v => v.ShowStatus(new RunStatus(Phase.World, Headset: true))), Path.Combine(dir, "headset.png"));
            Draw(Make(places, v => v.ShowStatus(new RunStatus(Phase.Crashed, Log: Path.Combine(places.BaseDir, "logs", "session.log")))),
                Path.Combine(dir, "crashed.png"));
            // after a crash, as the crash_reports setting's "ask" shows it: with a Studio Quire token (the decoy above),
            // then without one (sent without a token)
            var crash = new RunStatus(Phase.Crashed) { ExitCode = unchecked((int)0xC0000005) };
            Draw(new CrashReportView(places, crash), Path.Combine(dir, "crash-report.png"));
            Sojourn.Session.Assistant.ProviderKeys.Forget("relay");
            Draw(new CrashReportView(places, crash), Path.Combine(dir, "crash-report-no-token.png"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOJOURN_LOGINS", null);
            Environment.SetEnvironmentVariable("SOJOURN_KEYS", null);
            try { Directory.Delete(scratch, recursive: true); } catch (Exception) { }
        }
    }

    static LauncherView Make(Places places, Action<LauncherView>? then = null, bool dev = false)
    {
        var v = new LauncherView(places, new LoginStore(places.BaseDir), desktop: false, Array.Empty<string>()) { Dev = dev };
        then?.Invoke(v);
        return v;
    }

    /// <summary>A made-up translation of every catalog key into a language drawn for it (de for long, ja for cjk), in
    /// memory only; its code.</summary>
    static string Pseudo(string kind)
    {
        var code = kind == "cjk" ? "ja" : "de";
        foreach (var (key, tr) in Lang.Catalog())
        {
            int hash = key.LastIndexOf('#');
            var text = hash > 0 && !key[hash..].Contains('}') ? key[..hash] : key;
            tr[code] = kind == "cjk" ? Cjk(text) : Longer(text);
        }
        return code;
    }

    static string Longer(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s) sb.Append(c switch { 'a' => 'á', 'e' => 'é', 'i' => 'í', 'o' => 'ö', 'u' => 'ü', _ => c });
        int pad = Math.Max(2, (int)Math.Ceiling(s.Length * 0.4));
        for (int i = 0; i < pad; i++) sb.Append(i % 7 == 0 ? ' ' : 'x');
        return sb.ToString();
    }

    static string Cjk(string s)
    {
        const string Filler = "設定表示言語接続開始終了確認送信保存取消画面頭部装着記憶";
        var sb = new System.Text.StringBuilder();
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{' && i + 2 < s.Length && char.IsAsciiDigit(s[i + 1]) && s[i + 2] == '}') { sb.Append(s, i, 3); i += 2; continue; }
            if (char.IsLetter(s[i]))
            {
                int j = i;
                while (j < s.Length && char.IsLetter(s[j])) j++;
                for (int k = 0; k < (j - i + 1) / 2; k++) sb.Append(Filler[n++ % Filler.Length]);
                i = j - 1;
            }
            else if (s[i] != ' ') sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Laid out and drawn at 2x into path (null: only laid out).</summary>
    static void Draw(FrameworkElement view, string? path)
    {
        var height = view.Height;
        if (double.IsNaN(height))
        {
            // as a window sized to its content: at least its MinHeight, taller when another language needs it
            view.Measure(new Size(view.Width, double.PositiveInfinity));
            height = Math.Max(view.MinHeight, view.DesiredSize.Height);
        }
        var size = new Size(view.Width, height);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        if (path == null) return;
        var bmp = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(view);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }
}

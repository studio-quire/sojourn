using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sojourn.Session.Login;

namespace Sojourn.Launcher;

/// <summary>--shots DIR: the window's states drawn offscreen at 2x into PNGs (no window, no focus), over made-up accounts
/// and settings in a scratch folder that's deleted after; never the person's own.</summary>
static class Shots
{
    public static void Write(string dir, Places real)
    {
        Directory.CreateDirectory(dir);
        var scratch = Path.Combine(Path.GetTempPath(), "sojourn-launcher-shots-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("SOJOURN_LOGINS", Path.Combine(scratch, "logins"));
        Environment.SetEnvironmentVariable("SOJOURN_KEYS", Path.Combine(scratch, "keys"));
        try
        {
            var places = new Places(Path.Combine(scratch, "base"));
            new SettingsFile(places.SettingsPath).Graphics = "balanced";

            // no accounts yet: the new-account form
            Draw(Make(places), Path.Combine(dir, "first.png"));

            var store = new LoginStore(places.BaseDir);
            store.Logged(new SavedAccount { First = "Sojourn", Last = "Pilot", Grid = "http://127.0.0.1:9700/", GridName = "Atelier", Start = "uri:Atelier&128&118&26" }, null, false);
            store.Logged(new SavedAccount { First = "juniper", Last = "Resident", Grid = Grids.Main, Start = "last" }, "$1$" + new string('0', 32), true);

            Draw(Make(places), Path.Combine(dir, "account.png"));
            Draw(Make(places, v => v.ShowPage("accounts")), Path.Combine(dir, "accounts.png"));
            Draw(Make(places, v => v.ShowPage("new")), Path.Combine(dir, "new.png"));
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings.png"));
            // OpenRouter with a decoy key kept (never a real one: the keys folder is the scratch one)
            var file = new SettingsFile(places.SettingsPath) { Provider = "openrouter" };
            file.OpenRouterModel = "~google/gemini-pro-latest";
            Sojourn.Session.Assistant.ProviderKeys.Keep("sk-or-v1-" + new string('0', 40));
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings-openrouter.png"));
            file.Provider = "anthropic";
            Draw(Make(places, v => v.ShowPage("settings")), Path.Combine(dir, "settings-anthropic.png"));
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
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOJOURN_LOGINS", null);
            Environment.SetEnvironmentVariable("SOJOURN_KEYS", null);
            try { Directory.Delete(scratch, recursive: true); } catch (Exception) { }
        }
    }

    static LauncherView Make(Places places, Action<LauncherView>? then = null)
    {
        var v = new LauncherView(places, new LoginStore(places.BaseDir), desktop: false, Array.Empty<string>());
        then?.Invoke(v);
        return v;
    }

    static void Draw(FrameworkElement view, string path)
    {
        var size = new Size(view.Width, view.Height);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(view);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }
}

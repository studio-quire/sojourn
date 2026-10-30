using SkiaSharp;
using Sojourn.Client.Assistant;

namespace Sojourn.Client.Login;

public sealed partial class LoginSheet
{
    /// <summary>--login-shots DIR: each stage of the sheet in each preset look, over the void's slate (or a bright
    /// backdrop), as PNGs, to look at without a GPU or a grid.</summary>
    public static void Shots(string dir)
    {
        Directory.CreateDirectory(dir);
        var sheet = new LoginSheet(null, () => PanelLook.InUse, _ => { });
        var accounts = new List<(string, string)> { ("Sojourn Pilot", "127.0.0.1:9700"), ("juniperberry", "Second Life") };
        Snap Base(Stage st) => new(st, 1, -1, false, null, 3, accounts, 1, true, false, "", "", "", "", "", -1, true, 0, 0, false, false, 0,
            null, null, 0, null, null, false, "juniperberry", true, "Sojourn 0.1.0.0");
        const string tos = "Linden Lab has updated its Terms of Service, effective today. Please read them before you go on: they cover your " +
            "use of Second Life, the content you make and share, and how disputes are resolved. You can read them in full at https://lindenlab.com/tos.";
        var (plain, detail) = LoginWords.For("key", "Sorry! We couldn't log you in. Please check to make sure you entered the right * Username (like bobsmith12 or steller.sunshine) * Password");
        var shots = new (string Name, Snap Snap)[]
        {
            ("form", Base(Stage.Form)),
            ("form-new", Base(Stage.Form) with { Selected = -1, User = "Ada Lovelace", Pass = "•••••••••", Focus = 11, Remember = false, Grid = 2, Uri = "http://127.0.0.1:9700/", Start = 2, Region = "Atelier/128/118/26" }),
            ("form-help", Base(Stage.Form) with { Help = true, Note = "password" }),
            ("connecting", Base(Stage.Connecting)),
            ("tos", Base(Stage.Terms) with { TermsKind = "tos", TermsText = tos }),
            ("tos-agreed", Base(Stage.Terms) with { TermsKind = "tos", TermsText = tos, Agree = true, Hover = 51 }),
            ("critical", Base(Stage.Terms) with { TermsKind = "critical", TermsText = "The region you last visited is closed for maintenance until 18:00 SLT." }),
            ("mfa", Base(Stage.Mfa) with { Code = "1234", Focus = 14 }),
            ("mfa-again", Base(Stage.Mfa) with { Code = "123456", MfaAsks = 2, RememberComputer = true }),
            ("failed", Base(Stage.Failed) with { Plain = plain, Detail = detail, WrongPassword = true, Focus = 11 }),
        };
        foreach (var look in PanelLook.Presets)
            foreach (var (name, snap) in shots)
            {
                var d = sheet.Render(snap, look, 2, layer: false);
                int w = d.Image.Width + 120, h = d.Image.Height + 120;
                using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(new SKColor(22, 27, 34));
                using var img = SKImage.FromPixels(new SKImageInfo(d.Image.Width, d.Image.Height, SKColorType.Rgba8888, SKAlphaType.Premul), SKData.CreateCopy(d.Image.Rgba), d.Image.Width * 4);
                canvas.DrawImage(img, 60, 60);
                using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(dir, $"{look.Preset}-{name}.png"), png.ToArray());
            }
    }
}

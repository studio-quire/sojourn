using SkiaSharp;
using Sojourn.Client.Assistant;

namespace Sojourn.Client.Hud;

/// <summary>`--panel-shots`: the headset's HUD board (its glass and × key) over the world screenshot in each look, and, with
/// `hud.png` in the folder (a HUD screen with transparency), that laid in its screen.</summary>
public static class HudShots
{
    public static void Write(string dir, SKBitmap? bg, float scale)
    {
        using var hud = File.Exists(Path.Combine(dir, "hud.png")) ? SKBitmap.Decode(Path.Combine(dir, "hud.png")) : null;
        foreach (var look in PanelLook.Presets)
            foreach (bool lit in new[] { false, true })
            {
                if (lit && look.Preset != new PanelLook().Preset) continue;
                var img = HudBoard.DrawChrome(look, lit, 1, scale);
                using var surface = SKSurface.Create(new SKImageInfo(img.Width + 80, img.Height + 80, SKColorType.Rgba8888, SKAlphaType.Premul));
                var c = surface.Canvas;
                if (bg != null) c.DrawBitmap(bg, new SKRect(0, 0, img.Width + 80, img.Height + 80));
                else c.Clear(new SKColor(90, 100, 110));
                using var chrome = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, chrome.GetPixels(), img.Rgba.Length);
                c.DrawBitmap(chrome, 40, 40);
                if (hud != null)
                {
                    var r = HudBoard.ScreenRect;
                    c.DrawBitmap(hud, new SKRect(40 + r.Left * scale, 40 + r.Top * scale, 40 + r.Right * scale, 40 + r.Bottom * scale));
                }
                PanelShots.Save(surface, Path.Combine(dir, $"hud-board-{look.Preset}{(lit ? "-close-lit" : "")}.png"));
            }
    }
}

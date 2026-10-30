using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace Sojourn.Client.Window;

/// <summary>
/// Test runs stay out of the way (the user, 2026-10-26: agents' tests kept switching to their windows and grabbing the
/// cursor while they worked at the PC). A client started from a Claude Code session (CLAUDECODE=1, inherited by every
/// tool it runs) is in the background unless SOJOURN_FOREGROUND=1: its window shows without taking focus and the mouse
/// is never captured. Runs meant for the person set SOJOURN_FOREGROUND=1 (dev.py start, client.py vr);
/// SOJOURN_BACKGROUND=1 forces it from anywhere. Posted input (tools/winmouse.ps1, winkey.ps1) still reaches it.
/// </summary>
static class Background
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SOJOURN_BACKGROUND") == "1"
        || Environment.GetEnvironmentVariable("CLAUDECODE") == "1" && Environment.GetEnvironmentVariable("SOJOURN_FOREGROUND") != "1";

    /// <summary>Show a window created invisible without activating it.</summary>
    public static void ShowWithoutFocus(IWindow window)
    {
        if (window.Native?.Win32 is { } w32)
        {
            ShowWindow(w32.Hwnd, 4);   // SW_SHOWNOACTIVATE
            // and under every other window: a test run shown on top while the user played a fullscreen game still
            // pulled them out of it, without activation (2026-10-26)
            SetWindowPos(w32.Hwnd, 1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_BOTTOM; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        }
        else window.IsVisible = true;
    }

    /// <summary>Whether the window is the one in the foreground (the person's keys and mouse are on it). GLFW reads the
    /// cursor's position from the system wherever the cursor is, so mouse look reads it only then: a test run in the
    /// background turned with the person's own mouse anywhere on the screen.</summary>
    public static bool Foreground(IWindow window) => window.Native?.Win32 is not { } w32 || GetForegroundWindow() == w32.Hwnd;

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}

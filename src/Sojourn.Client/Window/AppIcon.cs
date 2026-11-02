using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace Sojourn.Client.Window;

/// <summary>
/// The window's icon: Sojourn's (art/icon/sojourn.ico, built into SojournVR.exe as its ApplicationIcon). GLFW only
/// looks for a resource named GLFW_ICON and otherwise shows Windows' generic one, so the exe's own is set on the window
/// here, the big one (Alt+Tab, the taskbar) and the small one (the title bar) each at the size the window's DPI wants.
/// Run as `dotnet SojournVR.dll` there's no such resource and the window keeps the generic icon.
/// </summary>
static class AppIcon
{
    const int GroupId = 32512;   // the id the compiler gives ApplicationIcon's icon group

    public static void Apply(IWindow window)
    {
        if (window.Native?.Win32 is not { } w32) return;
        var module = GetModuleHandleW(null);
        uint dpi = GetDpiForWindow(w32.Hwnd);
        if (dpi == 0) dpi = 96;
        Set(w32.Hwnd, module, 1, GetSystemMetricsForDpi(11, dpi));   // ICON_BIG, SM_CXICON
        Set(w32.Hwnd, module, 0, GetSystemMetricsForDpi(49, dpi));   // ICON_SMALL, SM_CXSMICON
    }

    static void Set(nint hwnd, nint module, int which, int size)
    {
        // never destroyed: the window keeps it for the life of the process
        var icon = LoadImageW(module, GroupId, 1, size, size, 0);   // IMAGE_ICON
        if (icon != 0) SendMessageW(hwnd, 0x0080, which, icon);     // WM_SETICON
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] static extern nint LoadImageW(nint module, nint name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
}

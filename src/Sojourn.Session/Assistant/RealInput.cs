using System.Runtime.InteropServices;

namespace Sojourn.Session.Assistant;

/// <summary>
/// Whether the mouse press being handled came from a hand on the mouse. The assistant can run commands on this
/// computer, and a program can click in the window two ways: posting button messages (as tools/winmouse.ps1 does),
/// which never touch the button's state, and injecting input (SendInput), which Windows marks as injected. A
/// confirmation takes neither. Called while the window handles the button message (GLFW raises the press inside it).
/// SOJOURN_TEST_INPUT=1 at start lets tests confirm with posted clicks.
/// </summary>
static class RealInput
{
    static readonly bool TestInput = Environment.GetEnvironmentVariable("SOJOURN_TEST_INPUT") == "1";

    public static bool LeftButton()
    {
        if (TestInput) return true;
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            // the button as this thread's input says (updated by real or injected input, never by a posted message),
            // or as the system has it right now (a quick click whose release is already queued)
            bool down = (GetKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (!down) return false;
            // only what Windows calls hardware: not injected, not posted, not made by the system
            return GetCurrentInputMessageSource(out var src) && src.OriginId == IMO_HARDWARE;
        }
        catch (EntryPointNotFoundException) { return true; }
        catch (DllNotFoundException) { return true; }
    }

    const int VK_LBUTTON = 0x01;
    const int IMO_HARDWARE = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    struct InputMessageSource { public int DeviceType; public int OriginId; }

    [DllImport("user32.dll")] static extern short GetKeyState(int vKey);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
}

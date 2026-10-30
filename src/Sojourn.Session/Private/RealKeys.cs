using System.Runtime.InteropServices;

namespace Sojourn.Session.Private;

/// <summary>
/// Whether the key being handled is a hand on the keyboard, as Assistant/RealInput.cs does for the mouse: a key message
/// a program posts never touches the key's state, and injected input is marked so: only a key Windows says came from
/// hardware counts (not injected, posted, or one the system made). Turning private mode off and
/// sending a dictated draft take only real keys (the assistant can run programs on this computer).
/// SOJOURN_TEST_INPUT=1 lets tests use posted keys.
/// </summary>
static class RealKeys
{
    static readonly bool TestInput = Environment.GetEnvironmentVariable("SOJOURN_TEST_INPUT") == "1";

    public const int Return = 0x0D, Escape = 0x1B, P = 0x50;
    const int IMO_HARDWARE = 0x01;

    public static bool Down(int vk)
    {
        if (TestInput || !OperatingSystem.IsWindows()) return true;
        try
        {
            bool down = (GetKeyState(vk) & 0x8000) != 0 || (GetAsyncKeyState(vk) & 0x8000) != 0;
            // only what Windows calls hardware: not injected, not posted, not made by the system
            return down && GetCurrentInputMessageSource(out var src) && src.OriginId == IMO_HARDWARE;
        }
        catch (EntryPointNotFoundException) { return true; }
        catch (DllNotFoundException) { return true; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InputMessageSource { public int DeviceType; public int OriginId; }

    [DllImport("user32.dll")] static extern short GetKeyState(int vKey);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
}

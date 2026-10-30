using System.Runtime.InteropServices;

namespace Sojourn.Launcher;

/// <summary>
/// Whether the input being handled now came from a hand on the mouse or the keyboard, as the client's
/// Assistant/RealInput.cs judges its confirmations: what Windows calls hardware, not injected (SendInput), posted
/// (PostMessage) or made by UI Automation (an Invoke runs outside any input message). WPF raises a button's Click
/// inside the window's handling of the mouse-up or key message, so the question is asked there. The grid's terms,
/// the MFA code and a key kept here go only from such a press: the assistant runs commands on this computer and must
/// never stand in for the person. SOJOURN_TEST_INPUT=1 at start lets tests press with posted input (as the client's).
/// </summary>
static class RealPress
{
    static readonly bool TestInput = Environment.GetEnvironmentVariable("SOJOURN_TEST_INPUT") == "1";

    public static bool Now()
    {
        if (TestInput) return true;
        try { return GetCurrentInputMessageSource(out var src) && src.OriginId == IMO_HARDWARE; }
        catch (EntryPointNotFoundException) { return true; }   // (Windows before 8: no way to tell)
    }

    const int IMO_HARDWARE = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    struct InputMessageSource { public int DeviceType; public int OriginId; }

    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
}

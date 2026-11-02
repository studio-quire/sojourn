using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sojourn.Core.Access;

/// <summary>Where announcements go: the person's own screen reader, through its API (never a voice of ours).</summary>
public interface IReaderVoice : IDisposable
{
    string Name { get; }
    /// <summary>Speak after what it's saying (queued in order), or cutting in. urgent: cut in but carry on with what was
    /// interrupted afterwards, where the reader can (NVDA 2024.1+); otherwise the same as interrupt.</summary>
    bool Speak(string text, bool interrupt, bool urgent = false);
    void Cancel();
    /// <summary>A flash message on a braille display, if the reader has one.</summary>
    void Braille(string text);
}

/// <summary>
/// NVDA through its controller client (nvdaControllerClient.dll, NV Access, LGPL-2.1; fetched into local/nvda by
/// tools/screenreader.py setup, or beside the program). Speech goes into NVDA's own queue in order; cutting in cancels
/// its speech first. Urgent things (a confirmation) use NVDA's "now" priority where it has one, which resumes what it
/// was saying afterwards.
/// </summary>
public sealed class NvdaVoice : IReaderVoice
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)] delegate int Text([MarshalAs(UnmanagedType.LPWStr)] string text);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate int Ssml([MarshalAs(UnmanagedType.LPWStr)] string ssml, int symbolLevel, int priority, byte asynchronous);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int IsSpeakingFn(out byte speaking);

    readonly NoArgs _test, _cancel;
    readonly Text _speak, _braille;
    readonly Ssml? _ssml;   // 2.0: NVDA 2024.1 and later
    readonly IsSpeakingFn? _isSpeaking;   // 3.0: NVDA 2026.3 and later
    volatile bool _noSsml;

    public string Name => "NVDA";
    public string Path { get; }

    NvdaVoice(nint lib, string path)
    {
        Path = path;
        T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        _test = F<NoArgs>("nvdaController_testIfRunning");
        _cancel = F<NoArgs>("nvdaController_cancelSpeech");
        _speak = F<Text>("nvdaController_speakText");
        _braille = F<Text>("nvdaController_brailleMessage");
        _ssml = NativeLibrary.TryGetExport(lib, "nvdaController_speakSsml", out var s) ? Marshal.GetDelegateForFunctionPointer<Ssml>(s) : null;
        _isSpeaking = NativeLibrary.TryGetExport(lib, "nvdaController_isSpeaking", out var sp) ? Marshal.GetDelegateForFunctionPointer<IsSpeakingFn>(sp) : null;
    }

    /// <summary>The controller client from the first of these folders that has it (x64 build), or null.</summary>
    public static NvdaVoice? Load(IEnumerable<string> folders, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        // tests on a hidden desktop (Sojourn Access's tools/access.py): there the controller client answers "running"
        // with no NVDA at all, so speech would go nowhere instead of to the UI Automation listener
        if (Environment.GetEnvironmentVariable("SOJOURN_TEST_NO_NVDA") == "1") { log?.Invoke("screen reader: NVDA left out (SOJOURN_TEST_NO_NVDA)"); return null; }
        foreach (var dir in folders)
        {
            var path = System.IO.Path.Combine(dir, "nvdaControllerClient.dll");
            if (!File.Exists(path)) continue;
            if (NativeLibrary.TryLoad(path, out var lib))
            {
                try { return new NvdaVoice(lib, path); }
                catch (EntryPointNotFoundException e) { log?.Invoke($"screen reader: {path} isn't NVDA's controller client ({e.Message})"); }
            }
            else log?.Invoke($"screen reader: couldn't load {path}");
        }
        return null;
    }

    public bool Running => _test() == 0;

    /// <summary>The controller's last answer to speaking (0: spoken; else a Windows RPC error).</summary>
    public int LastError { get; private set; }

    public bool Speak(string text, bool interrupt, bool urgent = false)
    {
        if (urgent && _ssml != null && !_noSsml)
        {
            int r = _ssml("<speak>" + System.Security.SecurityElement.Escape(text) + "</speak>", -1, 2, 1);   // symbols unchanged, priority now, asynchronous
            if (r == 0) return true;
            if (r == 1717) _noSsml = true;   // RPC_S_UNKNOWN_IF: an NVDA before 2024.1
        }
        if (interrupt || urgent) _cancel();
        int e = _speak(text);
        LastError = e;
        return e == 0;
    }

    /// <summary>Whether NVDA is speaking now; null when it can't say (an NVDA before 2026.3 answers 1717, as does a
    /// controller client without the call).</summary>
    public bool? Speaking() => _isSpeaking != null && _isSpeaking(out byte b) == 0 ? b != 0 : null;

    public void Cancel() => _cancel();
    public void Braille(string text) => _braille(text);
    public void Dispose() { }   // the library stays loaded for the process
}

/// <summary>
/// Narrator, JAWS and other readers through UI Automation notifications (UiaRaiseNotificationEvent, Windows 10 1709+),
/// raised by a provider for the app's window. The window's procedure is subclassed so UIA finds that provider for the
/// window itself (WM_GETOBJECT), named after the app rather than its changing title.
/// </summary>
public sealed class UiaVoice : IReaderVoice
{
    readonly nint _hwnd;
    readonly Provider _provider;
    readonly WndProc _proc;
    nint _oldProc;

    public string Name => "UI Automation";

    UiaVoice(nint hwnd, string appName)
    {
        _hwnd = hwnd;
        _provider = new Provider(hwnd, appName);
        _proc = Proc;
        _oldProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
    }

    /// <summary>For this window (call on its thread); null off Windows or without a window.</summary>
    public static UiaVoice? For(nint hwnd, string appName, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return null;
        try { return new UiaVoice(hwnd, appName); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            log?.Invoke($"screen reader: no UI Automation ({e.Message})");
            return null;
        }
    }

    nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // an exception escaping a window procedure would end the process: UIA's answer is only ever extra
        if (msg == WM_GETOBJECT && (int)(long)lParam == UiaRootObjectId)
            try { return UiaReturnRawElementProvider(hwnd, wParam, lParam, _provider); }
            catch (Exception) { }
        return CallWindowProcW(_oldProc, hwnd, msg, wParam, lParam);
    }

    public bool Speak(string text, bool interrupt, bool urgent = false)
    {
        try
        {
            if (!UiaClientsAreListening()) return false;
            // queued things all spoken in order; cutting in, only the latest of its kind
            bool now = interrupt || urgent;
            return UiaRaiseNotificationEvent(_provider, NotificationKind_Other,
                now ? NotificationProcessing_ImportantMostRecent : NotificationProcessing_All, text, now ? "Sojourn.now" : "Sojourn.said") == 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException) { return false; }
    }

    public void Cancel() { }   // a UIA client has no way to be told; the reader's own key (Ctrl) stops it
    public void Braille(string text) { }   // the reader brailles notifications itself

    public void Dispose()
    {
        // put the window's own procedure back, if nobody subclassed it after us
        if (_oldProc != 0 && GetWindowLongPtrW(_hwnd, GWLP_WNDPROC) == Marshal.GetFunctionPointerForDelegate(_proc))
            SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _oldProc);
        try { UiaDisconnectProvider(_provider); } catch (Exception) { }
        GC.KeepAlive(_proc);
    }

    // --- the provider: just enough to be the window's element and the source of its notifications ---

    [ComVisible(true)]
    public sealed class Provider(nint hwnd, string name) : IRawElementProviderSimple
    {
        public int ProviderOptions => ProviderOptions_ServerSideProvider | ProviderOptions_UseComThreading;
        public object? GetPatternProvider(int patternId) => null;
        public object? GetPropertyValue(int propertyId) => propertyId switch
        {
            UIA_NamePropertyId => name,
            UIA_ControlTypePropertyId => UIA_WindowControlTypeId,
            _ => null,
        };
        public IRawElementProviderSimple? HostRawElementProvider =>
            UiaHostProviderFromHwnd(hwnd, out var host) == 0 ? host : null;
    }

    [ComImport, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRawElementProviderSimple
    {
        int ProviderOptions { get; }
        [return: MarshalAs(UnmanagedType.IUnknown)] object? GetPatternProvider(int patternId);
        [return: MarshalAs(UnmanagedType.Struct)] object? GetPropertyValue(int propertyId);
        IRawElementProviderSimple? HostRawElementProvider { get; }
    }

    const int ProviderOptions_ServerSideProvider = 0x2, ProviderOptions_UseComThreading = 0x20;
    const int UIA_NamePropertyId = 30005, UIA_ControlTypePropertyId = 30003, UIA_WindowControlTypeId = 50032;
    const int NotificationKind_Other = 4;
    const int NotificationProcessing_ImportantMostRecent = 1, NotificationProcessing_All = 2;
    const uint WM_GETOBJECT = 0x003D;
    const int UiaRootObjectId = -25, GWLP_WNDPROC = -4;

    delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("UIAutomationCore.dll")] static extern int UiaHostProviderFromHwnd(nint hwnd, out IRawElementProviderSimple provider);
    [DllImport("UIAutomationCore.dll")] static extern nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam, IRawElementProviderSimple el);
    [DllImport("UIAutomationCore.dll")] static extern bool UiaClientsAreListening();
    [DllImport("UIAutomationCore.dll")] static extern int UiaDisconnectProvider(IRawElementProviderSimple provider);
    [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
    static extern int UiaRaiseNotificationEvent(IRawElementProviderSimple provider, int kind, int processing,
        [MarshalAs(UnmanagedType.BStr)] string displayString, [MarshalAs(UnmanagedType.BStr)] string activityId);
    [DllImport("user32.dll")] static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] static extern nint CallWindowProcW(nint prev, nint hwnd, uint msg, nint wParam, nint lParam);
}

/// <summary>Which screen reader runs, if any, and whether it's safe to speak at all.</summary>
public static class ScreenReaders
{
    /// <summary>The reader running now by name (NVDA, JAWS, Narrator, or "a screen reader" for any other that sets the
    /// system's flag), or null. Cheap enough for every couple of seconds.</summary>
    public static string? Running(NvdaVoice? nvda)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (nvda?.Running == true || Process("nvda")) return "NVDA";
        if (Process("jfw")) return "JAWS";
        if (Process("Narrator")) return "Narrator";
        if (Process("zt")) return "ZoomText";
        return SystemParametersInfoW(SPI_GETSCREENREADER, 0, out int on, 0) && on != 0 ? "a screen reader" : null;
    }

    static bool Process(string name)
    {
        var all = System.Diagnostics.Process.GetProcessesByName(name);
        foreach (var p in all) p.Dispose();
        return all.Length > 0;
    }

    /// <summary>The input desktop is the person's own (not locked, not a secure screen such as a UAC prompt): NVDA runs
    /// there too, and its docs ask apps not to speak then.</summary>
    public static bool DesktopOpen()
    {
        if (!OperatingSystem.IsWindows()) return true;
        nint d = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (d == 0) return false;   // access denied: the lock screen or a secure desktop
        try
        {
            var buf = new char[64];
            return GetUserObjectInformationW(d, UOI_NAME, buf, buf.Length * 2, out _)
                   && new string(buf).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(d); }
    }

    const uint SPI_GETSCREENREADER = 0x0046, DESKTOP_READOBJECTS = 0x0001;
    const int UOI_NAME = 2;
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);
    [DllImport("user32.dll")] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(nint obj, int index, [Out] char[] info, int length, out int needed);
}

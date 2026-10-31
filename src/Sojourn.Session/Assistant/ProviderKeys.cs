using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Sojourn.Session.Assistant;

/// <summary>
/// The person's own keys for the assistant's provider (an Anthropic API key, an OpenRouter key, or a personal token for
/// Studio Quire's relay): docs/assistant.md > Switching it on. Each is kept in a file of its own in the keys folder,
/// encrypted for this Windows account (DPAPI),
/// and read only by the sidecar (assistant/provider.py; the relay's token also by <see cref="RelayToken"/>, for sending
/// a log), which never hands it to Claude Code: its sessions talk to a
/// loopback proxy that adds the key. The folder is the repo's local/secrets/assistant, or a release's
/// %LOCALAPPDATA%\Sojourn\secrets\assistant (SOJOURN_KEYS for tests), both under the assistant's deny rules and shell
/// guard (assistant/harness.py). No settings file, log or agent API op holds or returns a key: keys come only from the
/// person's own hands (the launcher's key box, Sojourn Access's key box), and settings carry only which provider. The
/// launcher (src/Sojourn.Launcher) links this file, so it depends on nothing of the session's.
/// Soniox's key (cloud hearing, assistant/cloud.py) is kept the same way, as soniox.key here; cloud.py moves a plain
/// local/secrets/soniox.key in once and deletes it.
/// Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class ProviderKeys
{
    public static readonly string[] Kinds = { "anthropic", "openrouter", "relay" };

    /// <summary>The Claude account (the subscription Claude Code is signed in to) is offered only in runs from the repo:
    /// packaged and public builds offer keys and the studio's token alone (Anthropic's Agent SDK terms; CLAUDE.md's
    /// decision of 2026-10-30). There a setting of "claude" means none chosen yet.</summary>
    public static bool ClaudeOffered { get; } = false;

    /// <summary>A packaged build's setting that names no provider it offers ("claude", from the old default or a run that
    /// had it): the provider of a key or token already kept, or null (none chosen yet).</summary>
    public static string? FallbackFor(string kind) => ClaudeOffered || Kinds.Contains(kind) ? null : Kinds.FirstOrDefault(Has);

    // what a key looks like (provider.py's SHAPES): anything else pasted is refused, never kept
    static readonly Dictionary<string, Regex> Shapes = new()
    {
        ["anthropic"] = new(@"^sk-ant-[A-Za-z0-9_\-]{16,300}$"),
        ["openrouter"] = new(@"^sk-or-[A-Za-z0-9_\-]{16,300}$"),
        // a personal token for Studio Quire's relay (tools/relay.py makes them)
        ["relay"] = new(@"^sqr-[A-Za-z0-9_\-]{40,64}$"),
    };

    public static string Dir
    {
        get
        {
            if (Environment.GetEnvironmentVariable("SOJOURN_KEYS") is { Length: > 0 } env) return Path.GetFullPath(env);
            if (RepoRoot() is { } root) return Path.Combine(root, "local", "secrets", "assistant");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sojourn", "secrets", "assistant");
        }
    }

    /// <summary>The repo this runs from (src/Sojourn.Client above it), or null: a release.</summary>
    static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "Sojourn.Client")) && Directory.Exists(Path.Combine(d.FullName, "assistant")))
                return d.FullName;
        return null;
    }

    static string FileOf(string kind) => Path.Combine(Dir, kind + ".key");

    /// <summary>Soniox's key is kept (encrypted), or still in its old plain file, which the sidecar moves in.</summary>
    public static bool HasSoniox() => File.Exists(FileOf("soniox"))
        || RepoRoot() is { } root && File.Exists(Path.Combine(root, "local", "secrets", "soniox.key"));

    public static string Name(string kind) => kind switch { "openrouter" => "OpenRouter", "relay" => "Studio Quire", _ => "Anthropic" };

    /// <summary>What's kept for a provider, as said: "Anthropic key", "Studio Quire token".</summary>
    public static string Thing(string kind) => $"{Name(kind)} {(kind == "relay" ? "token" : "key")}";

    /// <summary>Which provider a key is for, by its shape (trimmed of spaces and quotes), and the key; null if it's no key.</summary>
    public static (string Kind, string Key)? Recognise(string? text)
    {
        var t = (text ?? "").Trim().Trim('"', '\'').Trim();
        foreach (var (kind, re) in Shapes)
            if (re.IsMatch(t)) return (kind, t);
        return null;
    }

    public static bool Has(string kind) => Kinds.Contains(kind) && File.Exists(FileOf(kind));

    /// <summary>When the key was last kept (0: none): sent with the assistant's prefs so a new key restarts its sessions.</summary>
    public static long Stamp(string kind) => Has(kind) ? File.GetLastWriteTimeUtc(FileOf(kind)).Ticks : 0;

    /// <summary>Keep a key (encrypted). Returns its provider, or null if the text isn't a key.</summary>
    public static string? Keep(string? text)
    {
        if (Recognise(text) is not { } k) return null;
        Directory.CreateDirectory(Dir);
        var tmp = FileOf(k.Kind) + ".tmp";
        var data = Encoding.UTF8.GetBytes(k.Key);
        try { File.WriteAllText(tmp, Convert.ToBase64String(Protect(data)), Encoding.ASCII); }
        finally { Array.Clear(data); }
        File.Move(tmp, FileOf(k.Kind), overwrite: true);
        return k.Kind;
    }

    /// <summary>The Studio Quire token itself, for the one request the app makes with it: a log sent to the relay
    /// (Sojourn Access's SendLog.cs), straight to the relay over HTTPS, never logged or shown. Null if none is kept or it
    /// can't be read. The provider keys stay the sidecar's alone.</summary>
    public static string? RelayToken()
    {
        if (!Has("relay")) return null;
        try
        {
            var data = Unprotect(Convert.FromBase64String(File.ReadAllText(FileOf("relay"), Encoding.ASCII).Trim()));
            try { return Recognise(Encoding.UTF8.GetString(data)) is ("relay", var t) ? t : null; }
            finally { Array.Clear(data); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException) { return null; }
    }

    public static void Forget(string kind)
    {
        if (Kinds.Contains(kind)) File.Delete(FileOf(kind));
    }

    /// <summary>
    /// The person pressed "paste key": a key on the clipboard is kept, and taken off the clipboard (if it's still what's
    /// there) so it isn't pasted somewhere by mistake later. Returns its provider, or null with why (nothing there,
    /// not a key). Never says what the clipboard held.
    /// </summary>
    public static (string? Kind, string Said) FromClipboard()
    {
        string? text;
        try { text = ClipboardText(); }
        catch (Exception) { return (null, "couldn't read the clipboard"); }
        if (string.IsNullOrWhiteSpace(text)) return (null, "copy the key first");
        string? kind;
        try { kind = Keep(text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return (null, "couldn't keep it"); }
        if (kind == null) return (null, "that isn't an Anthropic or OpenRouter key or a Studio Quire token");
        try { ClearClipboardIf(text); } catch (Exception) { }
        return (kind, $"{Thing(kind)} kept");
    }

    // ---- the clipboard (Win32: no window or UI framework needed) ----

    const uint CF_UNICODETEXT = 13;

    static string? ClipboardText()
    {
        if (!OpenClipboardRetry()) throw new InvalidOperationException("clipboard busy");
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == 0) return null;
            var p = GlobalLock(h);
            if (p == 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    static void ClearClipboardIf(string text)
    {
        if (ClipboardText() != text || !OpenClipboardRetry()) return;
        try { EmptyClipboard(); } finally { CloseClipboard(); }
    }

    static bool OpenClipboardRetry()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint mem);

    // ---- DPAPI (CryptProtectData for the current user): provider.py decrypts, same entropy; here only the relay's token ----

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sojourn assistant key");

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Only for the relay's token (<see cref="RelayToken"/>).</summary>
    static byte[] Unprotect(byte[] input)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("keys are kept with Windows' data protection");
        var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = hIn.AddrOfPinnedObject() };
            var entBlob = new DataBlob { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, 1 /* UI_FORBIDDEN */, out var outBlob))
                throw new InvalidOperationException($"data protection failed ({Marshal.GetLastWin32Error()})");
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
        finally { hIn.Free(); hEnt.Free(); }
    }

    static byte[] Protect(byte[] input)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("keys are kept with Windows' data protection");
        var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = hIn.AddrOfPinnedObject() };
            var entBlob = new DataBlob { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
            if (!CryptProtectData(ref inBlob, "Sojourn", ref entBlob, IntPtr.Zero, IntPtr.Zero, 1 /* UI_FORBIDDEN */, out var outBlob))
                throw new InvalidOperationException($"data protection failed ({Marshal.GetLastWin32Error()})");
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
        finally { hIn.Free(); hEnt.Free(); }
    }
}

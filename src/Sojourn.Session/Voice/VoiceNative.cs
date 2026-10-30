using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sojourn.Session.Voice;

/// <summary>An event from the native shim (src/Sojourn.Voice.Native/sojourn_voice.h), copied off its thread.</summary>
public readonly record struct NativeEvent(long Pc, NativeEventKind Kind, string? A, string? B, int N);

public enum NativeEventKind
{
    Offer = 1, Answer = 2, Candidate = 3, Gathered = 4, State = 5, DataOpen = 6, DataClosed = 7, Data = 8,
    Renegotiate = 9, Error = 10, Device = 20,
}

/// <summary>
/// The C ABI of sojourn_voice.dll. Its callbacks run on libwebrtc's threads; they only copy and enqueue, and
/// VoiceClient's own thread takes them from <see cref="Events"/>. The assistant's tap is pulled
/// (<see cref="sv_tap_read"/>), so no managed code ever runs on the audio threads.
/// </summary>
public static unsafe partial class VoiceNative
{
    const string Lib = "sojourn_voice";

    public static readonly ConcurrentQueue<NativeEvent> Events = new();
    public static readonly AutoResetEvent Wake = new(false);
    public static Action<int, string>? Log;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnLog(int level, byte* msg) => Log?.Invoke(level, Marshal.PtrToStringUTF8((nint)msg) ?? "");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnEvent(long pc, int kind, byte* a, byte* b, int n)
    {
        Events.Enqueue(new NativeEvent(pc, (NativeEventKind)kind, a == null ? null : Marshal.PtrToStringUTF8((nint)a),
            b == null ? null : Marshal.PtrToStringUTF8((nint)b), n));
        Wake.Set();
    }

    /// <summary>Starts the engine; false (and why) if the DLL isn't there or won't start.</summary>
    public static bool Init(out string? error)
    {
        try
        {
            int r = sv_init(&OnLog, &OnEvent);
            error = r == 0 ? null : $"sv_init returned {r}";
            return r == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"no voice engine ({e.Message})";
            return false;
        }
    }

    public static string Devices(bool capture)
    {
        int n = sv_devices(capture ? 1 : 0, null, 0);
        var buf = new byte[n];
        fixed (byte* p = buf) sv_devices(capture ? 1 : 0, p, n);
        return System.Text.Encoding.UTF8.GetString(buf, 0, Math.Max(0, n - 1));
    }

    public static string Stats()
    {
        var buf = new byte[2048];
        int n;
        fixed (byte* p = buf) n = sv_stats(p, buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf, 0, Math.Clamp(n - 1, 0, buf.Length - 1));
    }

    [LibraryImport(Lib)] private static partial int sv_init(delegate* unmanaged[Cdecl]<int, byte*, void> log, delegate* unmanaged[Cdecl]<long, int, byte*, byte*, int, void> events);
    [LibraryImport(Lib)] public static partial void sv_shutdown();
    [LibraryImport(Lib)] private static partial int sv_devices(int capture, byte* buf, int cap);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int sv_audio_start(string capture, string render);
    [LibraryImport(Lib)] public static partial void sv_audio_stop();
    [LibraryImport(Lib)] public static partial void sv_set_mute(int muted, int fadeMs);
    [LibraryImport(Lib)] public static partial void sv_set_gate(int open, int tailMs);
    [LibraryImport(Lib)] public static partial void sv_set_tap(int enabled);
    [LibraryImport(Lib)] public static partial int sv_tap_read(short* output, int maxSamples);
    [LibraryImport(Lib)] public static partial void sv_set_voice_volume(float v);
    [LibraryImport(Lib)] public static partial void sv_set_output_volume(float v);
    [LibraryImport(Lib)] public static partial float sv_mic_level();
    [LibraryImport(Lib)] public static partial void sv_set_processing(int aec, int ns, int agc, int hpf);
    [LibraryImport(Lib)] public static partial void sv_play(int stream, short* pcm, int frames, int rate, int channels);
    [LibraryImport(Lib)] public static partial void sv_play_clear(int stream);
    [LibraryImport(Lib)] public static partial void sv_test_echo(int delayMs, float gain);
    [LibraryImport(Lib)] public static partial int sv_play_queued_ms(int stream);
    [LibraryImport(Lib)] private static partial int sv_stats(byte* buf, int cap);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial long sv_pc_new(string ice);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void sv_pc_answer(long pc, string sdp);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void sv_pc_send(long pc, string text);
    [LibraryImport(Lib)] public static partial void sv_pc_mute_send(long pc, int muted);
    [LibraryImport(Lib)] public static partial void sv_pc_receive_volume(long pc, float v);
    [LibraryImport(Lib)] public static partial void sv_pc_close(long pc);
}

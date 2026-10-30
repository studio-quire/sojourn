using System.Runtime.InteropServices;

namespace Sojourn.Session.Audio;

/// <summary>World audio's half of sojourn_voice.dll's C ABI (src/Sojourn.Voice.Native/world.cpp, declared in
/// sojourn_voice.h): sound buffers, 32 channels mixed spatially by the render thread, the music stream's ring, and
/// Media Foundation's MP3 and AAC decoders.</summary>
public static unsafe partial class AudioNative
{
    const string Lib = "sojourn_voice";
    public const int Channels = 32;
    public const int FlagLoop = 1, FlagSpatial = 2;

    [LibraryImport(Lib)] public static partial int sv_buf_load(float* pcm, int frames, int rate, int channels);
    [LibraryImport(Lib)] public static partial void sv_buf_free(int id);
    [LibraryImport(Lib)] public static partial void sv_listener(double x, double y, double z, float fx, float fy, float fz, float ux, float uy, float uz);
    [LibraryImport(Lib)] public static partial void sv_world_set(float busGain, float rolloff);
    [LibraryImport(Lib)] public static partial int sv_ch_play(int ch, int buf, int flags, float gain, double x, double y, double z, int master);
    [LibraryImport(Lib)] public static partial void sv_ch_queue(int ch, int buf);
    [LibraryImport(Lib)] public static partial void sv_ch_set(int ch, float gain, double x, double y, double z, int loop);
    [LibraryImport(Lib)] public static partial void sv_ch_stop(int ch);
    [LibraryImport(Lib)] public static partial void sv_ch_states(int* output, int n);
    [LibraryImport(Lib)] public static partial void sv_music_push(float* pcm, int frames, int rate, int channels);
    [LibraryImport(Lib)] public static partial void sv_music_gain(float gain);
    [LibraryImport(Lib)] public static partial void sv_music_clear();
    [LibraryImport(Lib)] public static partial void sv_music_preroll(int ms);
    [LibraryImport(Lib)] public static partial int sv_music_queued_ms();
    [LibraryImport(Lib)] public static partial long sv_dec_open(int kind, int rate, int channels);
    [LibraryImport(Lib)] public static partial int sv_dec_feed(long h, byte* data, int n);
    [LibraryImport(Lib)] public static partial int sv_dec_read(long h, float* output, int max, int* rate, int* channels);
    [LibraryImport(Lib)] public static partial void sv_dec_close(long h);
    [LibraryImport(Lib)] private static partial int sv_world_stats(byte* buf, int cap);

    public static string Stats()
    {
        var buf = new byte[1024];
        int n;
        fixed (byte* p = buf) n = sv_world_stats(p, buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf, 0, Math.Clamp(n - 1, 0, buf.Length - 1));
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sojourn.Render.World;

/// <summary>
/// J2C decoding through OpenJPEG (2.5.3, BSD-2-Clause; built by tools/openjpeg.py into src/Sojourn.Render/native/openjp2.dll), the
/// viewer's own open decoder: several times faster than the managed CoreJ2K and nearly allocation-free, which
/// matters when a busy region streams in thousands of textures (CoreJ2K allocated ~10 MB a decode, and the garbage
/// collector's pauses landed on the render thread). It reads a whole codestream or a prefix (non-strict mode decodes
/// what's there) at a reduced resolution. When the library isn't there or a stream defeats it, the caller falls back.
/// </summary>
public static unsafe class OpenJpeg
{
    const string Lib = "openjp2";

    [DllImport(Lib)] static extern nint opj_create_decompress(int format);
    [DllImport(Lib)] static extern void opj_set_default_decoder_parameters(void* parameters);
    [DllImport(Lib)] static extern int opj_setup_decoder(nint codec, void* parameters);
    [DllImport(Lib)] static extern int opj_decoder_set_strict_mode(nint codec, int strict);
    [DllImport(Lib)] static extern int opj_set_decoded_resolution_factor(nint codec, uint factor);
    [DllImport(Lib)] static extern int opj_codec_set_threads(nint codec, int threads);
    [DllImport(Lib)] static extern nint opj_stream_create(nuint bufferSize, int isInput);
    [DllImport(Lib)] static extern void opj_stream_set_read_function(nint stream, delegate* unmanaged<void*, nuint, void*, nuint> fn);
    [DllImport(Lib)] static extern void opj_stream_set_skip_function(nint stream, delegate* unmanaged<long, void*, long> fn);
    [DllImport(Lib)] static extern void opj_stream_set_seek_function(nint stream, delegate* unmanaged<long, void*, int> fn);
    [DllImport(Lib)] static extern void opj_stream_set_user_data(nint stream, void* data, void* freeFn);
    [DllImport(Lib)] static extern void opj_stream_set_user_data_length(nint stream, ulong length);
    [DllImport(Lib)] static extern int opj_read_header(nint stream, nint codec, Image** image);
    [DllImport(Lib)] static extern int opj_decode(nint codec, nint stream, Image* image);
    [DllImport(Lib)] static extern int opj_end_decompress(nint codec, nint stream);
    [DllImport(Lib)] static extern void opj_stream_destroy(nint stream);
    [DllImport(Lib)] static extern void opj_destroy_codec(nint codec);
    [DllImport(Lib)] static extern void opj_image_destroy(Image* image);

    [StructLayout(LayoutKind.Sequential)]
    struct Image
    {
        public uint X0, Y0, X1, Y1, NumComps;
        public int ColorSpace;
        public Comp* Comps;
        public byte* Icc;
        public uint IccLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Comp
    {
        public uint Dx, Dy, W, H, X0, Y0, Prec, Bpp, Sgnd, ResnoDecoded, Factor;
        public int* Data;
        public ushort Alpha;
    }

    struct Source
    {
        public byte* Data;
        public long Length, Position;
    }

    static readonly Lazy<bool> Loaded = new(() =>
    {
        try
        {
            var codec = opj_create_decompress(0);
            if (codec == 0) return false;
            opj_destroy_codec(codec);
            return true;
        }
        catch (Exception) { return false; }
    });

    /// <summary>Whether the native library loaded.</summary>
    public static bool Available => Loaded.Value;

    [UnmanagedCallersOnly]
    static nuint Read(void* buffer, nuint n, void* user)
    {
        var s = (Source*)user;
        long left = s->Length - s->Position;
        if (left <= 0) return unchecked((nuint)(-1));
        long count = Math.Min((long)n, left);
        Buffer.MemoryCopy(s->Data + s->Position, buffer, (long)n, count);
        s->Position += count;
        return (nuint)count;
    }

    [UnmanagedCallersOnly]
    static long Skip(long n, void* user)
    {
        var s = (Source*)user;
        long to = Math.Clamp(s->Position + n, 0, s->Length);
        long moved = to - s->Position;
        s->Position = to;
        return moved == 0 && n != 0 ? -1 : moved;
    }

    [UnmanagedCallersOnly]
    static int Seek(long pos, void* user)
    {
        var s = (Source*)user;
        if (pos < 0 || pos > s->Length) return 0;
        s->Position = pos;
        return 1;
    }

    /// <summary>Decode at <paramref name="reduce"/> halvings of the full size. Components as 8-bit planes (rows top
    /// down, as the codestream stores them), or null if the stream couldn't be decoded.</summary>
    /// <param name="pooled">planes rented from the shared array pool (they may be longer than width x height): the
    /// caller gives them back with <see cref="Return"/> (a texture's planes are megabytes each, on the large object
    /// heap, and arrived tens a second while a place loaded)</param>
    public static (byte[][] Planes, int Width, int Height)? Decode(byte[] data, int reduce, bool pooled = false)
    {
        if (!Available || data.Length < 2) return null;
        nint codec = 0, stream = 0;
        Image* image = null;
        // the decoder's parameters struct (~8.3 KB in 2.5); room to spare, defaults set by the library itself
        var parameters = NativeMemory.AllocZeroed(16384);
        try
        {
            fixed (byte* p = data)
            {
                var src = new Source { Data = p, Length = data.Length };
                codec = opj_create_decompress(0);   // OPJ_CODEC_J2K: a raw codestream, as SL stores textures
                if (codec == 0) return null;
                opj_set_default_decoder_parameters(parameters);
                if (opj_setup_decoder(codec, parameters) == 0) return null;
                opj_decoder_set_strict_mode(codec, 0);   // a prefix decodes as far as it goes
                opj_codec_set_threads(codec, 1);
                stream = opj_stream_create(1 << 16, 1);
                if (stream == 0) return null;
                opj_stream_set_read_function(stream, &Read);
                opj_stream_set_skip_function(stream, &Skip);
                opj_stream_set_seek_function(stream, &Seek);
                opj_stream_set_user_data(stream, &src, null);
                opj_stream_set_user_data_length(stream, (ulong)data.Length);
                if (opj_read_header(stream, codec, &image) == 0 || image == null) return null;
                if (reduce > 0 && opj_set_decoded_resolution_factor(codec, (uint)reduce) == 0) return null;
                if (opj_decode(codec, stream, image) == 0) return null;
                opj_end_decompress(codec, stream);
                int n = (int)image->NumComps;
                if (n <= 0) return null;
                var c0 = image->Comps[0];
                int w = (int)c0.W, h = (int)c0.H;
                var planes = new byte[n][];
                for (int c = 0; c < n; c++)
                {
                    var comp = image->Comps[c];
                    // subsampled components (never seen in SL textures): let the managed decoder have it
                    if (comp.W != w || comp.H != h || comp.Data == null) return null;
                    var plane = planes[c] = pooled ? System.Buffers.ArrayPool<byte>.Shared.Rent(w * h) : new byte[w * h];
                    int shift = (int)comp.Prec - 8, bias = comp.Sgnd != 0 ? 1 << ((int)comp.Prec - 1) : 0;
                    int* d = comp.Data;
                    for (int i = 0, end = w * h; i < end; i++)
                    {
                        int v = d[i] + bias;
                        if (shift > 0) v >>= shift; else if (shift < 0) v <<= -shift;
                        plane[i] = (byte)Math.Clamp(v, 0, 255);
                    }
                }
                return (planes, w, h);
            }
        }
        catch (Exception) { return null; }
        finally
        {
            if (image != null) opj_image_destroy(image);
            if (stream != 0) opj_stream_destroy(stream);
            if (codec != 0) opj_destroy_codec(codec);
            NativeMemory.Free(parameters);
        }
    }

    /// <summary>Planes from a pooled <see cref="Decode"/>, back to the pool.</summary>
    public static void Return(byte[][] planes)
    {
        foreach (var p in planes) System.Buffers.ArrayPool<byte>.Shared.Return(p);
    }
}

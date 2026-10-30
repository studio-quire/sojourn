using System.Runtime.InteropServices;

namespace Sojourn.Render.Media;

/// <summary>
/// One media surface's shared section (src/Sojourn.Media.Native/common/protocol.h; keep the two in step): made here,
/// written by a helper process (frames as a triple buffer of BGRA, sound as a float stereo ring), read by the
/// renderer's upload worker (frames) and the native mixer (sound, straight from <see cref="Base"/>). Labelled so a
/// low-integrity helper may write it, and readable and writable by this user only.
/// </summary>
public sealed unsafe class MediaSection : IDisposable
{
    public const uint Magic = 0x444D4A53, Version = 1;
    const int HeaderBytes = 4096, Buffers = 3;
    // Header field offsets (protocol.h's Header, pack 8)
    const int OffMaxW = 8, OffMaxH = 12, OffFrame = 16, OffAudio = 40, OffAudioCap = 48, OffAudioRate = 52, OffSeq = 56, OffLatest = 64,
        OffInfo = 72, InfoSize = 40, OffAudioWrite = 192, OffReading = 200, OffConsumed = 208, OffAudioRead = 216, OffAlive = 224;

    public string Name { get; }
    public uint MaxWidth { get; }
    public uint MaxHeight { get; }
    public byte* Base { get; private set; }
    IntPtr _map;
    readonly long _size;
    long _lastSeq;

    public MediaSection(string name, uint maxWidth, uint maxHeight, uint audioFrames = 48000)
    {
        Name = name;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        long at = HeaderBytes;
        var offs = new long[Buffers];
        for (int i = 0; i < Buffers; i++)
        {
            offs[i] = at;
            at = Align(at + (long)maxWidth * maxHeight * 4, 65536);
        }
        long audioAt = at;
        _size = at + audioFrames * 8L;
        var sa = Security.LowIntegrityForUser(out var sd);
        try
        {
            _map = CreateFileMappingW(new IntPtr(-1), ref sa, 0x04 /* PAGE_READWRITE */, (uint)(_size >> 32), (uint)_size, name);
        }
        finally { LocalFree(sd); }
        if (_map == IntPtr.Zero) throw new InvalidOperationException($"media section {name}: CreateFileMapping failed ({Marshal.GetLastWin32Error()})");
        Base = (byte*)MapViewOfFile(_map, 0x0002 | 0x0004, 0, 0, (UIntPtr)_size);
        if (Base == null) { CloseHandle(_map); throw new InvalidOperationException($"media section {name}: MapViewOfFile failed"); }
        new Span<byte>(Base, HeaderBytes).Clear();
        W32(0, Magic);
        W32(4, Version);
        W32(OffMaxW, maxWidth);
        W32(OffMaxH, maxHeight);
        for (int i = 0; i < Buffers; i++) *(long*)(Base + OffFrame + 8 * i) = offs[i];
        *(long*)(Base + OffAudio) = audioAt;
        W32(OffAudioCap, audioFrames);
        *(int*)(Base + OffLatest) = -1;
        *(int*)(Base + OffReading) = -1;
    }

    static long Align(long v, long a) => (v + a - 1) / a * a;
    void W32(int off, uint v) => *(uint*)(Base + off) = v;

    /// <summary>The newest frame, if there's one this reader hasn't taken: held until <see cref="Release"/>.</summary>
    public bool TryTake(out MediaFrame frame)
    {
        frame = default;
        if (Base == null) return false;
        long seq = Volatile.Read(ref *(long*)(Base + OffSeq));
        if (seq == _lastSeq) return false;
        int latest;
        for (int tries = 0; ; tries++)
        {
            latest = Volatile.Read(ref *(int*)(Base + OffLatest));
            if (latest < 0 || latest >= Buffers) return false;
            Interlocked.Exchange(ref *(int*)(Base + OffReading), latest);
            if (Volatile.Read(ref *(int*)(Base + OffLatest)) == latest) break;
            if (tries > 8) return false;
        }
        byte* info = Base + OffInfo + latest * InfoSize;
        long fseq = *(long*)info;
        int w = *(int*)(info + 8), h = *(int*)(info + 12);
        int dx = *(int*)(info + 16), dy = *(int*)(info + 20), dw = *(int*)(info + 24), dh = *(int*)(info + 28);
        if (w <= 0 || h <= 0 || w > MaxWidth || h > MaxHeight) { Release(fseq); return false; }
        dx = Math.Clamp(dx, 0, w);
        dy = Math.Clamp(dy, 0, h);
        dw = Math.Clamp(dw, 0, w - dx);
        dh = Math.Clamp(dh, 0, h - dy);
        long off = *(long*)(Base + OffFrame + 8 * latest);
        frame = new MediaFrame(Base + off, w, h, (int)MaxWidth * 4, dx, dy, dw, dh, fseq);
        return true;
    }

    /// <summary>The newest frame whether or not it was taken before (a whole copy of what's showing, for a reader that
    /// starts late), held until <see cref="EndPeek"/>; the next frame's dirty rect still counts from the last taken.</summary>
    public bool TryPeek(out MediaFrame frame)
    {
        frame = default;
        if (Base == null) return false;
        int latest;
        for (int tries = 0; ; tries++)
        {
            latest = Volatile.Read(ref *(int*)(Base + OffLatest));
            if (latest < 0 || latest >= Buffers) return false;
            Interlocked.Exchange(ref *(int*)(Base + OffReading), latest);
            if (Volatile.Read(ref *(int*)(Base + OffLatest)) == latest) break;
            if (tries > 8) { EndPeek(); return false; }
        }
        byte* info = Base + OffInfo + latest * InfoSize;
        int w = *(int*)(info + 8), h = *(int*)(info + 12);
        if (w <= 0 || h <= 0 || w > MaxWidth || h > MaxHeight) { EndPeek(); return false; }
        long off = *(long*)(Base + OffFrame + 8 * latest);
        frame = new MediaFrame(Base + off, w, h, (int)MaxWidth * 4, 0, 0, w, h, *(long*)info);
        return true;
    }

    public void EndPeek()
    {
        if (Base != null) Volatile.Write(ref *(int*)(Base + OffReading), -1);
    }

    /// <summary>Done with the frame <see cref="TryTake"/> gave: the helper may write that buffer again, and the next
    /// dirty rect counts from it.</summary>
    public void Release(long seq)
    {
        if (Base == null) return;
        _lastSeq = Math.Max(_lastSeq, seq);
        Volatile.Write(ref *(long*)(Base + OffConsumed), seq);
        Volatile.Write(ref *(int*)(Base + OffReading), -1);
    }

    /// <summary>Let the next frame be taken whole (a new texture, after a resize).</summary>
    public void Rewind()
    {
        if (Base == null) return;
        Volatile.Write(ref *(long*)(Base + OffConsumed), 0);
        _lastSeq = 0;
    }

    /// <summary>The helper's heartbeat (ms of its uptime clock; 0 before its first frame or sound).</summary>
    public long AliveMs => Base == null ? 0 : Volatile.Read(ref *(long*)(Base + OffAlive));
    public long AudioWritten => Base == null ? 0 : Volatile.Read(ref *(long*)(Base + OffAudioWrite));
    public long AudioRead => Base == null ? 0 : Volatile.Read(ref *(long*)(Base + OffAudioRead));
    public long FramesPublished => Base == null ? 0 : Volatile.Read(ref *(long*)(Base + OffSeq));

    public void Dispose()
    {
        if (Base != null) UnmapViewOfFile((IntPtr)Base);
        Base = null;
        if (_map != IntPtr.Zero) CloseHandle(_map);
        _map = IntPtr.Zero;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMappingW(IntPtr file, ref Security.SECURITY_ATTRIBUTES sa, uint protect, uint sizeHigh, uint sizeLow, string name);
    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [DllImport("kernel32")] static extern bool UnmapViewOfFile(IntPtr at);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr h);
}

/// <summary>A frame in a section: <paramref name="Pixels"/> rows of <paramref name="Stride"/> bytes (BGRA, top row first),
/// and the part that changed since the frame this reader last released.</summary>
public readonly unsafe struct MediaFrame(byte* pixels, int width, int height, int stride, int dirtyX, int dirtyY, int dirtyW, int dirtyH, long seq)
{
    public readonly byte* Pixels = pixels;
    public readonly int Width = width, Height = height, Stride = stride, DirtyX = dirtyX, DirtyY = dirtyY, DirtyW = dirtyW, DirtyH = dirtyH;
    public readonly long Seq = seq;
}

/// <summary>Security descriptors for objects shared with the helpers.</summary>
public static class Security
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    /// <summary>Full access for this user and SYSTEM only, with a low mandatory label so a low-integrity helper may write
    /// it too. Free the descriptor with LocalFree.</summary>
    public static SECURITY_ATTRIBUTES LowIntegrityForUser(out IntPtr descriptor)
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})(A;;GA;;;SY)S:(ML;;NW;;;LW)", 1, out descriptor, IntPtr.Zero))
            throw new InvalidOperationException($"security descriptor: {Marshal.GetLastWin32Error()}");
        return new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), Descriptor = descriptor };
    }

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
}

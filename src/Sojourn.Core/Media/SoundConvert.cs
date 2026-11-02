using System.Buffers.Binary;
using NVorbis;
using OggVorbisEncoder;

namespace Sojourn.Core.Media;

/// <param name="SourceFormat">e.g. "PCM 16-bit", "IEEE float 32-bit".</param>
/// <param name="Duration">Seconds.</param>
/// <param name="SampleRate">Of the encoded sound: always 44100.</param>
/// <param name="Peak">Absolute peak of the encoded mono signal, 0-1.</param>
/// <param name="RmsDbfs">RMS level of the encoded mono signal in dBFS (-inf for silence).</param>
public sealed record SoundInfo(
    string SourceFormat,
    int SourceChannels,
    int SourceSampleRate,
    double Duration,
    int SampleRate,
    bool Resampled,
    bool Downmixed,
    float Peak,
    double RmsDbfs,
    int OggBytes);

/// <summary>Decoded audio, downmixed to mono.</summary>
public sealed record DecodedSound(float[] Samples, int SampleRate, int SourceChannels)
{
    public double Duration => SampleRate > 0 ? (double)Samples.Length / SampleRate : 0;
}

/// <summary>
/// Sound uploads: WAV -> Ogg Vorbis as the official viewer makes them (llvorbisencode.cpp):
/// mono, 44.1 kHz, Vorbis VBR quality 0.05 (oggenc -q0.5), at most 30 s.
/// The viewer accepts only 8/16-bit PCM, mono or stereo, at exactly 44.1 kHz; this also takes
/// 24/32-bit PCM, 32/64-bit float, WAVE_FORMAT_EXTENSIBLE, more channels (averaged, as the viewer
/// averages stereo) and other sample rates (resampled to 44.1 kHz). The 30 s limit stays: SL rejects longer sounds.
/// </summary>
public static class SoundConvert
{
    public const int SampleRate = 44100;          // LLVORBIS_CLIP_SAMPLE_RATE
    public const double MaxSeconds = 30.0;        // LLVORBIS_CLIP_MAX_TIME
    const float Quality = 0.05f;                  // "SL-52913 & SL-53779 ... equivalent to oggenc -q0.5"

    public static (byte[] Ogg, SoundInfo Info) WavToVorbis(byte[] wav)
    {
        var src = Wav.Read(wav);
        double duration = (double)src.Frames / src.SampleRate;
        if (duration > MaxSeconds)
            throw new MediaFormatException(
                $"The sound is {duration:0.##} s long; Second Life's limit is {MaxSeconds:0} s. Trim it to {MaxSeconds:0} s or less.");
        if (src.Frames == 0)
            throw new MediaFormatException("The WAV has no audio data.");

        float[] mono = src.Mono;
        bool resampled = src.SampleRate != SampleRate;
        if (resampled) mono = Resampler.Resample(mono, src.SampleRate, SampleRate);
        for (int i = 0; i < mono.Length; i++) mono[i] = Math.Clamp(mono[i], -1f, 1f);

        byte[] ogg = Encode(mono);
        var (peak, rms) = Levels(mono);
        return (ogg, new SoundInfo(src.Format, src.Channels, src.SampleRate, (double)mono.Length / SampleRate, SampleRate,
            resampled, src.Channels > 1, peak, rms, ogg.Length));
    }

    /// <summary>A WAV or Ogg Vorbis file as Second Life's mono at 44.1 kHz (any length), and what it was.</summary>
    public static (float[] Mono, string Format, int Channels, int Rate) ReadMono(byte[] data, bool wav)
    {
        float[] mono;
        string format;
        int channels, rate;
        if (wav)
        {
            var w = Wav.Read(data);
            (mono, format, channels, rate) = (w.Mono, $"WAV, {w.Format}", w.Channels, w.SampleRate);
        }
        else
        {
            var d = DecodeVorbis(data);
            (mono, format, channels, rate) = (d.Samples, "Ogg Vorbis", d.SourceChannels, d.SampleRate);
        }
        if (rate != SampleRate) mono = Resampler.Resample(mono, rate, SampleRate);
        return (mono, format, channels, rate);
    }

    /// <summary>Mono samples at 44.1 kHz encoded as the viewer encodes an upload (no length check: the caller's).</summary>
    public static byte[] EncodeMono(float[] mono)
    {
        var m = new float[mono.Length];
        for (int i = 0; i < m.Length; i++) m[i] = Math.Clamp(mono[i], -1f, 1f);
        return Encode(m);
    }

    /// <summary>Decodes Ogg Vorbis (any channel count, averaged to mono).</summary>
    public static DecodedSound DecodeVorbis(byte[] ogg)
    {
        try
        {
            using var reader = new VorbisReader(new MemoryStream(ogg), closeOnDispose: true);
            int channels = reader.Channels;
            var mono = new List<float>(reader.TotalSamples > 0 && reader.TotalSamples < int.MaxValue ? (int)reader.TotalSamples : 1 << 16);
            var buf = new float[4096 * channels];
            int read;
            while ((read = reader.ReadSamples(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i + channels <= read; i += channels)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += buf[i + c];
                    mono.Add(sum / channels);
                }
            }
            return new DecodedSound([.. mono], reader.SampleRate, channels);
        }
        catch (Exception e) when (e is not MediaFormatException)
        {
            throw new MediaFormatException($"Not a readable Ogg Vorbis stream: {e.Message}");
        }
    }

    /// <summary>Peak (0-1) and RMS in dBFS.</summary>
    public static (float Peak, double RmsDbfs) Levels(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        double sq = 0;
        foreach (float s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
            sq += (double)s * s;
        }
        double rms = samples.Length > 0 ? Math.Sqrt(sq / samples.Length) : 0;
        return (peak, rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity);
    }

    static byte[] Encode(float[] mono)
    {
        var info = VorbisInfo.InitVariableBitRate(1, SampleRate, Quality);
        var stream = new OggStream(Random.Shared.Next());
        var output = new MemoryStream();

        stream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        stream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        stream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        Flush(stream, output, force: true); // audio starts on a fresh page, as in the viewer

        // OggVorbisEncoder 1.2.2 loses the first half long block (1024 samples here): decoders start
        // 1024 samples into the input and the stream ends 1024 short. Lead in with the opening
        // mirrored (no step for the encoder to smear), so decoding gives exactly the input, aligned.
        // Pinned by SoundConvertTests.Decoded_IsAlignedAndExactLength.
        int lead = info.CodecSetup.BlockSizes[1] / 2;
        var padded = new float[lead + mono.Length];
        for (int i = 0; i < lead; i++) padded[i] = mono[Math.Clamp(lead - i, 0, mono.Length - 1)];
        mono.CopyTo(padded, lead);

        var state = ProcessingState.Create(info);
        const int chunk = 1024;
        mono = padded;
        var data = new[] { mono };
        for (int at = 0, n; ; at += n)
        {
            n = Math.Min(chunk, mono.Length - at);
            if (n == 0) state.WriteEndOfStream();
            else state.WriteData(data, n, at);
            while (!stream.Finished && state.PacketOut(out var packet))
            {
                stream.PacketIn(packet);
                Flush(stream, output, force: false);
            }
            if (n == 0) break;
        }
        Flush(stream, output, force: true);
        return output.ToArray();
    }

    static void Flush(OggStream stream, Stream output, bool force)
    {
        while (stream.PageOut(out var page, force))
        {
            output.Write(page.Header);
            output.Write(page.Body);
        }
    }
}

/// <summary>RIFF WAVE reader: PCM 8/16/24/32-bit, IEEE float 32/64-bit, plain or WAVE_FORMAT_EXTENSIBLE.</summary>
internal sealed class Wav
{
    public required string Format;
    public required int Channels;
    public required int SampleRate;
    public required int Frames;
    public required float[] Mono;

    const ushort TagPcm = 1, TagFloat = 3, TagExtensible = 0xFFFE;

    public static Wav Read(byte[] b)
    {
        if (b.Length < 12) throw new MediaFormatException("Not a WAV file: too short.");
        string riff = Id(b, 0);
        if (riff is "RIFX" or "RF64")
            throw new MediaFormatException($"{riff} WAV files aren't supported; export a standard (RIFF) WAV.");
        if (riff != "RIFF" || Id(b, 8) != "WAVE")
            throw new MediaFormatException("Not a WAV file (no RIFF/WAVE header). Sounds must be uploaded as WAV.");

        ushort tag = 0, channels = 0, blockAlign = 0, bits = 0;
        int rate = 0;
        int dataStart = -1, dataLength = 0;
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = Id(b, pos);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 4));
            int body = pos + 8;
            long remaining = b.Length - body;
            if (id == "fmt ")
            {
                if (size < 16 || size > remaining) throw new MediaFormatException("The WAV's fmt chunk is damaged.");
                var f = b.AsSpan(body);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
                rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(f[4..]), int.MaxValue);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
                if (tag == TagExtensible && size >= 40)
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(f[24..]); // first two bytes of the SubFormat GUID
            }
            else if (id == "data")
            {
                dataStart = body;
                // Streamed writers leave 0 or 0xFFFFFFFF here; take what's in the file.
                dataLength = (int)(size == 0 || size > remaining ? remaining : size);
                if (size == 0 || size > remaining) break;
            }
            if (size > remaining) break;
            pos = body + (int)size + (int)(size & 1); // chunks are padded to even sizes
        }

        if (channels == 0 && tag == 0) throw new MediaFormatException("The WAV has no fmt chunk.");
        if (dataStart < 0) throw new MediaFormatException("The WAV has no data chunk.");
        if (tag != TagPcm && tag != TagFloat)
            throw new MediaFormatException(
                $"The WAV is compressed (format 0x{tag:X4}); save it as uncompressed PCM (16-bit is the usual choice).");
        if (channels < 1) throw new MediaFormatException("The WAV says it has 0 channels.");
        if (rate < 1000 || rate > 768000)
            throw new MediaFormatException($"The WAV's sample rate ({rate} Hz) isn't usable; 44100 Hz is what SL uses.");

        int container = blockAlign > 0 && blockAlign % channels == 0 ? blockAlign / channels : (bits + 7) / 8;
        string format;
        Func<int, float> sample;
        if (tag == TagFloat)
        {
            if (container == 4) sample = o => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
            else if (container == 8) sample = o => (float)BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o));
            else throw new MediaFormatException($"{bits}-bit float WAVs aren't supported; use 16-bit PCM or 32-bit float.");
            format = $"IEEE float {container * 8}-bit";
        }
        else
        {
            sample = container switch
            {
                1 => o => (b[o] - 128) / 128f,
                2 => o => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o)) / 32768f,
                3 => o => ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f,
                4 => o => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)) / 2147483648f,
                _ => throw new MediaFormatException($"{bits}-bit PCM WAVs aren't supported; use 8, 16, 24 or 32-bit."),
            };
            format = $"PCM {(bits > 0 ? bits : container * 8)}-bit";
        }

        int frameBytes = container * channels;
        int frames = dataLength / frameBytes;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int o = dataStart + i * frameBytes;
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += sample(o + c * container);
            float v = sum / channels;
            mono[i] = float.IsFinite(v) ? v : 0f;
        }
        return new Wav { Format = format, Channels = channels, SampleRate = rate, Frames = frames, Mono = mono };
    }

    static string Id(byte[] b, int at) => System.Text.Encoding.ASCII.GetString(b, at, 4);
}

/// <summary>Band-limited resampling with a Blackman-windowed sinc, tabulated.</summary>
internal static class Resampler
{
    const int ZeroCrossings = 32;
    const int Oversample = 512;
    static readonly float[] Kernel = BuildKernel();

    static float[] BuildKernel()
    {
        var k = new float[ZeroCrossings * Oversample + 2];
        for (int i = 0; i < k.Length; i++)
        {
            double x = (double)i / Oversample;
            double sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double w = x >= ZeroCrossings ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * x / ZeroCrossings) + 0.08 * Math.Cos(2 * Math.PI * x / ZeroCrossings);
            k[i] = (float)(sinc * w);
        }
        return k;
    }

    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return (float[])input.Clone();
        double step = (double)fromRate / toRate;          // input samples per output sample
        double cutoff = Math.Min(1.0, 1.0 / step) * 0.97; // fraction of the input Nyquist kept
        double halfWidth = ZeroCrossings / cutoff;        // in input samples
        int outLength = (int)Math.Floor(input.Length / step);
        var output = new float[outLength];
        for (int n = 0; n < outLength; n++)
        {
            double t = n * step;
            int first = Math.Max(0, (int)Math.Ceiling(t - halfWidth));
            int last = Math.Min(input.Length - 1, (int)Math.Floor(t + halfWidth));
            double acc = 0;
            for (int k = first; k <= last; k++)
            {
                double x = Math.Abs(t - k) * cutoff * Oversample;
                int i = (int)x;
                if (i >= Kernel.Length - 1) continue;
                double frac = x - i;
                acc += input[k] * (Kernel[i] + (Kernel[i + 1] - Kernel[i]) * frac);
            }
            output[n] = (float)(acc * cutoff);
        }
        return output;
    }
}

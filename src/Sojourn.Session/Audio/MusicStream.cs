using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Sojourn.Session.Audio;

/// <summary>
/// The parcel's music stream: internet radio (Shoutcast and Icecast; MP3, AAC in ADTS, Ogg Vorbis) played into the
/// native mixer's music ring at the viewer's music level (0.3) times the person's. As the viewer (LLViewerAudio):
/// a new URL fades the old stream out over 2 s, then the new one in over 3 s once it's playing; no URL fades out and
/// stops. The connection is our own small HTTP/1.0 client because Shoutcast v1 answers "ICY 200 OK", which .NET's
/// HttpClient refuses; it asks for the ICY metadata to show the song playing, follows redirects and .pls/.m3u
/// playlists, and reconnects (2 s growing to 30 s) while the stream's still wanted. Decoding runs on the stream's
/// own below-normal thread, paced by the network; about 1.5 s is buffered before playing.
/// </summary>
public sealed partial class MusicStream : IDisposable
{
    const float ViewerLevel = 0.3f;   // AudioLevelMusic
    const float FadeOutS = 2, FadeInS = 3;
    readonly Action<string> _log;
    readonly Thread _thread;
    readonly object _lock = new();
    volatile bool _stop;
    string _want = "";          // what the parcel says (and the person allows)
    string _playing = "";       // what the connection is for
    Connection? _conn;
    float _fade;                // 0..1
    volatile float _volume = 1;
    volatile bool _on = true;
    volatile bool _ducked;      // a teleport

    public string State { get; private set; } = "off";
    public string? Title { get; private set; }
    public string? Url => _playing.Length > 0 ? _playing : null;
    public string? Format { get; private set; }
    /// <summary>The status changed (a title, playing, an error): for the menu.</summary>
    public event Action? Changed;

    public MusicStream(Action<string> log)
    {
        _log = log;
        AudioNative.sv_music_preroll(1500);
        AudioNative.sv_music_gain(0);
        _thread = new Thread(Run) { Name = "audio-music", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The person's: music on or off, and its volume as a share of the viewer's level.</summary>
    public bool On { get => _on; set => _on = value; }
    public float Volume { get => _volume; set => _volume = Math.Max(0, value); }
    public bool Ducked { set => _ducked = value; }

    /// <summary>The parcel's URL as it stands ("" none). The viewer plays one longer than 12 characters starting with
    /// http:// or https://.</summary>
    public void Want(string? url)
    {
        url = url?.Trim() ?? "";
        if (url.Length <= 12 || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            url = "";
        lock (_lock) _want = url;
    }

    public string ParcelUrl { get { lock (_lock) return _want; } }

    void SetState(string s, string? title = null, bool keepTitle = true)
    {
        bool changed = s != State || (!keepTitle && title != Title);
        State = s;
        if (!keepTitle) Title = title;
        if (changed) Changed?.Invoke();
    }

    void Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        while (!_stop)
        {
            Thread.Sleep(20);
            double now = clock.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            string want;
            lock (_lock) want = _on ? _want : "";
            if (want != _playing)
            {
                // fade out what's playing, then switch
                if (_conn != null && _fade > 0) _fade = Math.Max(0, _fade - (float)(dt / FadeOutS));
                if (_conn == null || _fade <= 0)
                {
                    _conn?.Dispose();
                    _conn = null;
                    AudioNative.sv_music_clear();
                    _playing = want;
                    _fade = 0;
                    Title = null;
                    Format = null;
                    if (want.Length > 0)
                    {
                        _log($"music: {want}");
                        _conn = new Connection(this, want);
                    }
                    else SetState("off", null, keepTitle: false);
                }
            }
            else if (_conn != null)
            {
                bool sounding = AudioNative.sv_music_queued_ms() >= 0;
                if (sounding && _fade < 1 && !_ducked) _fade = Math.Min(1, _fade + (float)(dt / FadeInS));
                if (_ducked && _fade > 0) _fade = Math.Max(0, _fade - (float)(dt / FadeOutS));
            }
            AudioNative.sv_music_gain(_fade * ViewerLevel * _volume);
        }
        _conn?.Dispose();
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(2000);
        AudioNative.sv_music_clear();
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>One stream's connection and decoder, on its own thread, reconnecting until disposed.</summary>
    sealed class Connection : IDisposable
    {
        readonly MusicStream _m;
        readonly string _url;
        readonly Thread _thread;
        readonly CancellationTokenSource _cts = new();
        Stream? _net;

        public Connection(MusicStream m, string url)
        {
            _m = m;
            _url = url;
            _thread = new Thread(Run) { Name = "audio-stream", IsBackground = true, Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        void Run()
        {
            int backoff = 2;
            while (!_cts.IsCancellationRequested)
            {
                bool played = false;
                try
                {
                    _m.SetState("connecting");
                    played = Play(_url, 0);
                    if (_cts.IsCancellationRequested) break;
                    _m.SetState("reconnecting");
                }
                catch (Exception e) when (!_cts.IsCancellationRequested)
                {
                    _m.SetState("unreachable: " + Short(e.Message));
                    _m._log($"music: {_url}: {e.Message}");
                }
                catch { break; }
                if (played) backoff = 2;
                for (int i = 0; i < backoff * 10 && !_cts.IsCancellationRequested; i++) Thread.Sleep(100);
                backoff = Math.Min(30, backoff * 2);
            }
        }

        static string Short(string s) => s.Length > 60 ? s[..60] : s;

        /// <summary>Connects and plays until the stream ends or fails; true if any audio came.</summary>
        bool Play(string url, int depth)
        {
            if (depth > 4) throw new IOException("too many redirects or playlists");
            var (net, status, headers) = Open(new Uri(url));
            _net = net;
            using var _ = net;
            if (status is 301 or 302 or 303 or 307 or 308 && headers.TryGetValue("location", out var loc))
                return Play(new Uri(new Uri(url), loc).ToString(), depth + 1);
            if (status != 200) throw new IOException($"the server said {status}");
            headers.TryGetValue("content-type", out var type);
            type = (type ?? "").Split(';')[0].Trim().ToLowerInvariant();
            int metaint = headers.TryGetValue("icy-metaint", out var mi) && int.TryParse(mi, out var n) ? n : 0;
            if (type.Contains("scpls") || type.Contains("mpegurl") || url.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                return Play(PlaylistEntry(net, url), depth + 1);
            if (type.StartsWith("text/html")) throw new IOException("a web page, not a stream");
            return Decode(new IcyReader(net, metaint, t => { _m.Title = t; _m.Changed?.Invoke(); }), type);
        }

        string PlaylistEntry(Stream net, string url)
        {
            var buf = new byte[65536];
            int len = 0, r;
            while (len < buf.Length && (r = net.Read(buf, len, buf.Length - len)) > 0) len += r;
            var text = Encoding.UTF8.GetString(buf, 0, len);
            if (text.Contains("#EXT-X-TARGETDURATION")) throw new IOException("an HLS playlist (not supported)");
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && eq > 0) line = line[(eq + 1)..].Trim();
                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return line;
            }
            throw new IOException("an empty playlist");
        }

        (Stream Net, int Status, Dictionary<string, string> Headers) Open(Uri uri)
        {
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 15000, SendTimeout = 10000 };
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                cts.CancelAfter(10000);
                tcp.ConnectAsync(uri.Host, uri.Port, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            Stream s = tcp.GetStream();
            if (uri.Scheme == "https")
            {
                // Any certificate, as the viewers' stream players take: SL streams often sit behind a shared host's
                // certificate (live.lightmanstreams.com's names another host). Nothing is sent but the GET, and the
                // reply is only played, so an unchecked peer can do no more than an http:// stream.
                var ssl = new SslStream(s, false, (_, _, _, _) => true);
                ssl.AuthenticateAsClient(uri.Host);
                s = ssl;
            }
            s.ReadTimeout = 15000;
            var req = $"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}\r\n" +
                      "User-Agent: Sojourn/0.1 (a Second Life viewer)\r\nAccept: */*\r\nIcy-MetaData: 1\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(req);
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
            // the head, byte by byte so none of the body is taken with it; Shoutcast v1 starts "ICY 200 OK"
            var head = new StringBuilder();
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                head.Append((char)b);
                if (head.Length > 16384) throw new IOException("no end to the response's head");
                int L = head.Length;
                if (L >= 4 && head[L - 1] == '\n' && head[L - 2] == '\r' && head[L - 3] == '\n' && head[L - 4] == '\r') break;
                if (L >= 2 && head[L - 1] == '\n' && head[L - 2] == '\n') break;
            }
            var lines = head.ToString().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) throw new IOException("no response");
            var parts = lines[0].Split(' ', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var status)) throw new IOException("not an HTTP or ICY response");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
            }
            return (s, status, headers);
        }

        // ---- decoding ----

        enum Kind { Unknown, Mp3, Aac, Ogg }

        bool Decode(IcyReader icy, string type)
        {
            var kind = type switch
            {
                "audio/mpeg" or "audio/mp3" or "audio/x-mpeg" or "audio/mpeg3" or "audio/x-mp3" => Kind.Mp3,
                "audio/aac" or "audio/aacp" or "audio/x-aac" or "audio/x-aacp" => Kind.Aac,
                "application/ogg" or "audio/ogg" or "audio/vorbis" or "audio/x-ogg" or "audio/x-vorbis" => Kind.Ogg,
                _ => Kind.Unknown,
            };
            var buf = new byte[16384];
            var pending = new List<byte>();
            long decoder = 0;
            bool any = false;
            var pcm = new float[16384];
            try
            {
                if (kind == Kind.Ogg) return OggVorbis.Play(icy, this, _cts.Token, ref any);
                while (!_cts.IsCancellationRequested)
                {
                    int n = icy.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    if (decoder == 0)
                    {
                        // the first frame header: what the stream is, if the server didn't say, and its rate
                        pending.AddRange(buf.AsSpan(0, n).ToArray());
                        if (kind == Kind.Unknown && pending.Count >= 4 && Encoding.ASCII.GetString(pending.GetRange(0, 4).ToArray()) == "OggS")
                        {
                            var rest = new PrefixedReader(pending.ToArray(), icy);
                            return OggVorbis.Play(rest, this, _cts.Token, ref any);
                        }
                        var arr = pending.ToArray();
                        int at = FindFrame(arr, ref kind, out int rate, out int ch);
                        if (at < 0)
                        {
                            if (pending.Count > 65536) throw new IOException("no MP3 or AAC frames in the stream");
                            continue;
                        }
                        decoder = AudioNative.sv_dec_open(kind == Kind.Mp3 ? 1 : 2, rate, ch);
                        if (decoder == 0) throw new IOException($"no {kind} decoder");
                        _m.Format = $"{(kind == Kind.Mp3 ? "MP3" : "AAC")} {rate / 1000.0:0.#} kHz {(ch == 1 ? "mono" : "stereo")}";
                        _m.SetState("buffering");
                        Feed(decoder, arr.AsSpan(at), pcm, ref any);
                        pending.Clear();
                        continue;
                    }
                    Feed(decoder, buf.AsSpan(0, n), pcm, ref any);
                }
            }
            finally
            {
                if (decoder != 0) AudioNative.sv_dec_close(decoder);
            }
            return any;
        }

        unsafe void Feed(long decoder, ReadOnlySpan<byte> data, float[] pcm, ref bool any)
        {
            fixed (byte* p = data) if (AudioNative.sv_dec_feed(decoder, p, data.Length) < 0) throw new IOException("the decoder refused the stream");
            for (;;)
            {
                int rate, ch, got;
                fixed (float* o = pcm) got = AudioNative.sv_dec_read(decoder, o, pcm.Length, &rate, &ch);
                if (got <= 0) break;
                Push(pcm.AsSpan(0, got), rate, ch, ref any);
            }
        }

        public void SetFormat(string format)
        {
            _m.Format = format;
            _m.SetState("buffering");
        }

        public unsafe void Push(ReadOnlySpan<float> pcm, int rate, int ch, ref bool any)
        {
            if (ch < 1 || rate <= 0) return;
            if (ch > 2)
            {
                // more than stereo: the front pair
                var two = new float[pcm.Length / ch * 2];
                for (int f = 0; f < pcm.Length / ch; f++) { two[2 * f] = pcm[f * ch]; two[2 * f + 1] = pcm[f * ch + 1]; }
                pcm = two;
                ch = 2;
            }
            fixed (float* p = pcm) AudioNative.sv_music_push(p, pcm.Length / ch, rate, ch);
            if (!any) { any = true; }
            int q = AudioNative.sv_music_queued_ms();
            if (q >= 0 && _m.State != "playing") _m.SetState("playing");
            // servers send a burst on connecting; past 8 s ahead, wait for the ring to drain
            while (!_cts.IsCancellationRequested && Math.Abs(AudioNative.sv_music_queued_ms()) > 8000) Thread.Sleep(100);
        }

        /// <summary>Finds the first MP3 or ADTS frame whose successor is where its length says (so a stray sync word
        /// doesn't count). kind narrows the search if the server named the format.</summary>
        static int FindFrame(byte[] b, ref Kind kind, out int rate, out int ch)
        {
            rate = ch = 0;
            int start = 0;
            // an ID3v2 tag at the start
            if (b.Length >= 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
                start = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
            for (int i = start; i + 8 < b.Length; i++)
            {
                if (b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) continue;
                if (kind != Kind.Mp3 && Adts(b, i, out int len, out rate, out ch))
                {
                    if (i + len + 2 <= b.Length && Adts(b, i + len, out _, out _, out _)) { kind = Kind.Aac; return i; }
                }
                else if (kind != Kind.Aac && Mpeg(b, i, out len, out rate, out ch))
                {
                    if (i + len + 4 <= b.Length && Mpeg(b, i + len, out _, out _, out _)) { kind = Kind.Mp3; return i; }
                }
            }
            return -1;
        }

        static readonly int[] AdtsRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        static bool Adts(byte[] b, int i, out int len, out int rate, out int ch)
        {
            len = rate = ch = 0;
            if (i + 7 > b.Length || b[i] != 0xFF || (b[i + 1] & 0xF6) != 0xF0) return false;
            int sf = (b[i + 2] >> 2) & 0xF;
            if (sf >= AdtsRates.Length) return false;
            rate = AdtsRates[sf];
            ch = ((b[i + 2] & 1) << 2) | (b[i + 3] >> 6);
            if (ch == 0) ch = 2;
            len = ((b[i + 3] & 3) << 11) | (b[i + 4] << 3) | (b[i + 5] >> 5);
            return len >= 7;
        }

        static readonly int[,] Mp3Kbps =
        {
            { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 },   // MPEG-1 layer III
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 },        // MPEG-2/2.5 layer III
        };

        static bool Mpeg(byte[] b, int i, out int len, out int rate, out int ch)
        {
            len = rate = ch = 0;
            if (i + 4 > b.Length || b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) return false;
            int version = (b[i + 1] >> 3) & 3, layer = (b[i + 1] >> 1) & 3;
            if (version == 1 || layer != 1) return false;   // layer III only (SL's streams; layer II is rare)
            int br = b[i + 2] >> 4, sr = (b[i + 2] >> 2) & 3, pad = (b[i + 2] >> 1) & 1;
            if (br == 0 || br == 15 || sr == 3) return false;
            int[] rates1 = { 44100, 48000, 32000 };
            rate = rates1[sr] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            int kbps = Mp3Kbps[version == 3 ? 0 : 1, br];
            len = (version == 3 ? 144 : 72) * kbps * 1000 / rate + pad;
            ch = (b[i + 3] >> 6) == 3 ? 1 : 2;
            return len > 4;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _net?.Dispose(); } catch { }
            if (_thread.IsAlive) _thread.Join(2000);
        }
    }

    /// <summary>The body of an ICY stream with its metadata blocks taken out (every metaint bytes: a length byte times
    /// 16, then "StreamTitle='...';").</summary>
    sealed class IcyReader : Stream
    {
        readonly Stream _s;
        readonly int _metaint;
        readonly Action<string> _title;
        int _left;

        public IcyReader(Stream s, int metaint, Action<string> title)
        {
            _s = s;
            _metaint = metaint;
            _left = metaint;
            _title = title;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_metaint <= 0) return _s.Read(buffer, offset, count);
            if (_left == 0)
            {
                int lenByte = _s.ReadByte();
                if (lenByte < 0) return 0;
                int len = lenByte * 16;
                if (len > 0)
                {
                    var meta = new byte[len];
                    int got = 0;
                    while (got < len)
                    {
                        int r = _s.Read(meta, got, len - got);
                        if (r <= 0) return 0;
                        got += r;
                    }
                    if (Title(meta) is { } t) _title(t);
                }
                _left = _metaint;
            }
            int n = _s.Read(buffer, offset, Math.Min(count, _left));
            if (n > 0) _left -= n;
            return n;
        }

        static string? Title(byte[] meta)
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(meta); }
            catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(meta); }
            text = text.TrimEnd('\0');
            const string key = "StreamTitle='";
            int a = text.IndexOf(key, StringComparison.Ordinal);
            if (a < 0) return null;
            a += key.Length;
            int e = text.IndexOf("';", a, StringComparison.Ordinal);
            var t = (e < 0 ? text[a..] : text[a..e]).Trim();
            return t.Length > 0 ? t : null;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Bytes already read, then the rest of a stream.</summary>
    sealed class PrefixedReader : Stream
    {
        readonly byte[] _prefix;
        int _at;
        readonly Stream _rest;
        public PrefixedReader(byte[] prefix, Stream rest) { _prefix = prefix; _rest = rest; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at < _prefix.Length)
            {
                int n = Math.Min(count, _prefix.Length - _at);
                Array.Copy(_prefix, _at, buffer, offset, n);
                _at += n;
                return n;
            }
            return _rest.Read(buffer, offset, count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

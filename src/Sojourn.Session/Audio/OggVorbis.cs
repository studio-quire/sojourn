using NVorbis;
using NVorbis.Contracts;

namespace Sojourn.Session.Audio;

public sealed partial class MusicStream
{
    /// <summary>
    /// Ogg Vorbis internet radio. NVorbis's own Ogg reader wants a seekable stream, so the pages are read here, forward
    /// only, and NVorbis's StreamDecoder gets the packets. Icecast chains a new logical stream per track (a new serial
    /// and its headers): each gets a fresh decoder. Opus in Ogg isn't decoded.
    /// </summary>
    static class OggVorbis
    {
        public static bool Play(Stream s, Connection conn, CancellationToken ct, ref bool any)
        {
            var pages = new PageReader(s);
            var buf = new float[8192];
            while (!ct.IsCancellationRequested)
            {
                var provider = new Provider(pages);
                if (!provider.Start()) break;
                if (provider.IsOpus) throw new IOException("an Opus stream (not supported)");
                using var dec = new StreamDecoder(provider);
                int ch = dec.Channels, rate = dec.SampleRate;
                conn.SetFormat($"Ogg Vorbis {rate / 1000.0:0.#} kHz {(ch == 1 ? "mono" : "stereo")}");
                for (;;)
                {
                    if (ct.IsCancellationRequested) return any;
                    int n = dec.Read(buf.AsSpan(), 0, buf.Length - buf.Length % ch);
                    if (n <= 0) break;
                    conn.Push(buf.AsSpan(0, n), rate, ch, ref any);
                }
                if (pages.Ended) break;
            }
            return any;
        }

        /// <summary>Ogg packets, page by page: (serial, the packet, whether it begins a logical stream).</summary>
        sealed class PageReader
        {
            readonly Stream _s;
            readonly Queue<(int Serial, byte[] Packet, bool Bos)> _ready = new();
            readonly Dictionary<int, List<byte>> _partial = new();
            public bool Ended { get; private set; }
            public PageReader(Stream s) => _s = s;

            public (int Serial, byte[] Packet, bool Bos)? Peek()
            {
                while (_ready.Count == 0 && !Ended) ReadPage();
                return _ready.Count > 0 ? _ready.Peek() : null;
            }

            public (int Serial, byte[] Packet, bool Bos)? Next()
            {
                var p = Peek();
                if (p != null) _ready.Dequeue();
                return p;
            }

            void ReadPage()
            {
                // sync on "OggS" (a stream joined mid-page starts with junk)
                int matched = 0;
                while (matched < 4)
                {
                    int b = _s.ReadByte();
                    if (b < 0) { Ended = true; return; }
                    matched = b == "OggS"[matched] ? matched + 1 : b == 'O' ? 1 : 0;
                }
                var head = new byte[23];
                if (!Fill(head)) return;
                int type = head[1];
                int serial = BitConverter.ToInt32(head, 10);
                int nseg = head[22];
                var lacing = new byte[nseg];
                if (!Fill(lacing)) return;
                int total = 0;
                foreach (var l in lacing) total += l;
                var body = new byte[total];
                if (!Fill(body)) return;
                bool bos = (type & 2) != 0;
                if (bos) _partial.Remove(serial);
                if ((type & 1) == 0) _partial.Remove(serial);   // not a continuation: drop a dangling partial
                _partial.TryGetValue(serial, out var cur);
                int at = 0;
                bool first = true;
                foreach (var l in lacing)
                {
                    cur ??= new List<byte>();
                    cur.AddRange(new ArraySegment<byte>(body, at, l));
                    at += l;
                    if (l < 255)
                    {
                        _ready.Enqueue((serial, cur.ToArray(), bos && first));
                        first = false;
                        cur = null;
                    }
                }
                if (cur != null) _partial[serial] = cur;
                else _partial.Remove(serial);
            }

            bool Fill(byte[] b)
            {
                int got = 0;
                while (got < b.Length)
                {
                    int r = _s.Read(b, got, b.Length - got);
                    if (r <= 0) { Ended = true; return false; }
                    got += r;
                }
                return true;
            }
        }

        /// <summary>One logical stream's packets for NVorbis; ends where the next stream begins.</summary>
        sealed class Provider : NVorbis.Contracts.IPacketProvider
        {
            readonly PageReader _pages;
            int _serial;
            bool _started;
            public bool IsOpus { get; private set; }
            public Provider(PageReader pages) => _pages = pages;

            /// <summary>Skips to the next stream's first packet; false at the end.</summary>
            public bool Start()
            {
                for (;;)
                {
                    var p = _pages.Peek();
                    if (p == null) return false;
                    if (p.Value.Bos || IsVorbisIdentification(p.Value.Packet))
                    {
                        _serial = p.Value.Serial;
                        _started = true;
                        var pk = p.Value.Packet;
                        IsOpus = pk.Length >= 8 && System.Text.Encoding.ASCII.GetString(pk, 0, 8) == "OpusHead";
                        return true;
                    }
                    _pages.Next();   // the middle of a stream we joined late: wait for a track to start
                }
            }

            static bool IsVorbisIdentification(byte[] p) =>
                p.Length >= 7 && p[0] == 1 && p[1] == 'v' && p[2] == 'o' && p[3] == 'r' && p[4] == 'b' && p[5] == 'i' && p[6] == 's';

            bool Mine((int Serial, byte[] Packet, bool Bos)? p, bool first) =>
                p != null && p.Value.Serial == _serial && (!p.Value.Bos || first);

            bool _first = true;

            public IPacket? GetNextPacket()
            {
                if (!_started) return null;
                var p = _pages.Peek();
                if (!Mine(p, _first)) return null;
                _first = false;
                _pages.Next();
                return new Packet(p!.Value.Packet);
            }

            public IPacket? PeekNextPacket()
            {
                var p = _pages.Peek();
                return Mine(p, _first) ? new Packet(p!.Value.Packet) : null;
            }

            public bool CanSeek => false;
            public int StreamSerial => _serial;
            public long GetGranuleCount() => throw new NotSupportedException();
            public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount) => throw new NotSupportedException();
        }

        sealed class Packet : DataPacket
        {
            readonly byte[] _data;
            int _at;
            public Packet(byte[] data) => _data = data;
            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _at < _data.Length ? _data[_at++] : -1;
            public override void Reset()
            {
                _at = 0;
                base.Reset();
            }
        }
    }
}

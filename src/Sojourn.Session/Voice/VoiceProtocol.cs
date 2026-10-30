using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace Sojourn.Session.Voice;

/// <summary>
/// SL's WebRTC voice protocol, the viewer's side, kept in this one file so it can be diffed against the viewer
/// (indra/newview/llvoicewebrtc.cpp, develop 2026-10-10) when LL changes it. The caps bodies, the data channel's
/// JSON both ways, and the numbers the viewer uses.
/// </summary>
public static class VoiceProtocol
{
    public const string ServerType = "webrtc";
    /// <summary>The voice tick (UPDATE_THROTTLE_SECONDS).</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    /// <summary>The listener is tethered to within this of the avatar (MAX_AUDIO_DIST); neighbours within twice it.</summary>
    public const float MaxAudioDist = 50f;
    public const float MaxRetrySeconds = 10f;
    /// <summary>A per-peer volume (0..1+) goes out times this (PEER_GAIN_CONVERSION_FACTOR in setUserVolume; the viewer
    /// re-sends saved volumes times 200 on joins, an inconsistency of its own: we use 220 both times).</summary>
    public const int PeerGainFactor = 220;
    /// <summary>Teleports mute everything, then restore this long after arrival (SET_HIDDEN_RESTORE_DELAY_MS).</summary>
    public const int HiddenRestoreMs = 200;
    public const string EstateChannel = "Estate";

    /// <summary>STUN servers: the viewer builds them from the grid's name (agni gets three).</summary>
    public static string IceServers(string loginUri)
    {
        string? grid = null;
        if (loginUri.Contains("login.agni.lindenlab.com", StringComparison.OrdinalIgnoreCase)) grid = "agni";
        else if (loginUri.Contains("login.aditi.lindenlab.com", StringComparison.OrdinalIgnoreCase)) grid = "aditi";
        if (grid == null) return "";   // OpenSim: the mixer advertises an address we reach directly
        int n = grid == "agni" ? 3 : 2;
        return string.Join("\n", Enumerable.Range(1, n).Select(i => $"stun:stun{i}.{grid}.secondlife.io:3478"));
    }

    // ---- caps ----

    public static OSDMap ProvisionSpatial(string sdp, int? parcelLocalId)
    {
        var body = new OSDMap
        {
            ["jsep"] = new OSDMap { ["type"] = "offer", ["sdp"] = sdp },
            ["channel_type"] = "local",
            ["voice_server_type"] = ServerType,
        };
        if (parcelLocalId is { } id) body["parcel_local_id"] = id;
        return body;
    }

    public static OSDMap ProvisionMultiAgent(string sdp, string channel, string credentials) => new()
    {
        ["jsep"] = new OSDMap { ["type"] = "offer", ["sdp"] = sdp },
        ["credentials"] = credentials,
        ["channel"] = channel,
        ["channel_type"] = "multiagent",
        ["voice_server_type"] = ServerType,
    };

    public static OSDMap Logout(OSD viewerSession) => new()
    {
        ["logout"] = true, ["viewer_session"] = viewerSession, ["voice_server_type"] = ServerType,
    };

    public static OSDMap Candidates(OSD viewerSession, IEnumerable<(string Candidate, string Mid, int MLine)> cands) => new()
    {
        ["candidates"] = new OSDArray(cands.Select(c => (OSD)new OSDMap
        {
            ["sdpMid"] = c.Mid, ["sdpMLineIndex"] = c.MLine, ["candidate"] = c.Candidate,
        }).ToList()),
        ["viewer_session"] = viewerSession,
        ["voice_server_type"] = ServerType,
    };

    public static OSDMap CandidatesComplete(OSD viewerSession) => new()
    {
        ["candidate"] = new OSDMap { ["completed"] = true },
        ["viewer_session"] = viewerSession,
        ["voice_server_type"] = ServerType,
    };

    /// <summary>The provision answer: (viewer_session, answer SDP), or null if it isn't one.</summary>
    public static (OSD Session, string Sdp)? ParseProvisionReply(OSD? reply)
    {
        if (reply is not OSDMap m || !m.ContainsKey("viewer_session") || m["jsep"] is not OSDMap j) return null;
        if (j["type"].AsString() != "answer" || !j.ContainsKey("sdp")) return null;
        return (m["viewer_session"], j["sdp"].AsString());
    }

    public static async Task<(HttpStatusCode Status, OSD? Body)> PostAsync(HttpClient http, Uri cap, OSD body, CancellationToken ct)
    {
        var content = new ByteArrayContent(OSDParser.SerializeLLSDXmlBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/llsd+xml");
        using var resp = await http.PostAsync(cap, content, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        OSD? osd = null;
        if (resp.IsSuccessStatusCode && bytes.Length > 0)
        {
            try { osd = OSDParser.Deserialize(bytes); } catch { }
        }
        return (resp.StatusCode, osd);
    }

    // ---- the data channel, to the server ----

    /// <summary>{"j":{"p":true}} on the primary region's connection, {"j":{}} on the others.</summary>
    public static string Join(bool primary) => primary ? "{\"j\":{\"p\":true}}" : "{\"j\":{}}";

    /// <summary>The avatar (speaker) and listener poses: global metres and quaternions, both times 100 as ints, in
    /// SL's frame (Z up, X forward).</summary>
    public static string Pose(Vector3d speaker, Quaternion speakerRot, Vector3d listener, Quaternion listenerRot)
    {
        static string V(Vector3d v) => $"{{\"x\":{(int)(v.X * 100)},\"y\":{(int)(v.Y * 100)},\"z\":{(int)(v.Z * 100)}}}";
        static string Q(Quaternion q) => $"{{\"x\":{(int)(q.X * 100)},\"y\":{(int)(q.Y * 100)},\"z\":{(int)(q.Z * 100)},\"w\":{(int)(q.W * 100)}}}";
        return $"{{\"sp\":{V(speaker)},\"sh\":{Q(speakerRot)},\"lp\":{V(listener)},\"lh\":{Q(listenerRot)}}}";
    }

    /// <summary>Mute flags and per-peer gains for my mix, in one message (either may be empty).</summary>
    public static string? MuteAndGain(IReadOnlyDictionary<UUID, bool> mutes, IReadOnlyDictionary<UUID, float> gains)
    {
        var root = new JsonObject();
        if (mutes.Count > 0)
        {
            var m = new JsonObject();
            foreach (var (id, on) in mutes) m[id.ToString()] = on;
            root["m"] = m;
        }
        if (gains.Count > 0)
        {
            var g = new JsonObject();
            foreach (var (id, v) in gains) g[id.ToString()] = (uint)Math.Max(0, v * PeerGainFactor);
            root["ug"] = g;
        }
        return root.Count > 0 ? root.ToJsonString() : null;
    }

    // ---- the data channel, from the server ----

    /// <summary>One agent's entry in a server message.</summary>
    public readonly record struct Update(UUID Id, bool Joined, bool JoinPrimary, bool Left, float? Power, bool? Speaking,
        bool? ModeratorMuted, string? Version);

    /// <summary>An object keyed by agent id. A null id is a test client: skipped, as the viewer does.</summary>
    public static List<Update> Parse(string json)
    {
        var list = new List<Update>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch { return list; }
        if (root is not JsonObject obj) return list;
        foreach (var (key, value) in obj)
        {
            if (!UUID.TryParse(key, out var id) || id == UUID.Zero || value is not JsonObject p) continue;
            bool joined = p["j"] is JsonObject, joinPrimary = p["j"] is JsonObject j && j["p"] is JsonValue jp && jp.TryGetValue<bool>(out var b) && b;
            bool left = p["l"] is JsonValue l && l.TryGetValue<bool>(out var lb) && lb;
            float? power = p["p"] is JsonValue pv && pv.TryGetValue<double>(out var pd) ? (float)(pd / 128.0) : null;
            bool? speaking = p["v"] is JsonValue vv && vv.TryGetValue<bool>(out var vb) ? vb : null;
            bool? muted = p["m"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) ? mb : null;
            string? version = p["V"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
            list.Add(new Update(id, joined, joinPrimary, left, power, speaking, muted, version));
        }
        return list;
    }

    /// <summary>Region handle to the region's global origin (metres).</summary>
    public static Vector3d RegionOrigin(ulong handle)
    {
        Utils.LongToUInts(handle, out uint x, out uint y);
        return new Vector3d(x, y, 0);
    }

    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

// Lip sync: speakers' mouths move with their voice, as the viewer's "Move avatar lips when speaking" does
// (newview/llvoavatar.cpp idleUpdateLipSync and idleUpdateVoiceVisualizer, newview/llvoicevisualizer.cpp
// lipSyncOohAah, the LipSync* settings' defaults in app_settings/settings.xml). LGPL 2.1, Linden Research, Inc.
using LibreMetaverse;
using Sojourn.Render.Bodies;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;

namespace Sojourn.Render.World;

/// <summary>Who's speaking and how loud, for lip sync (the client sets it from voice: Client/Control/AvatarsWiring.cs).
/// Any thread in, the render thread out.</summary>
public sealed class VoiceLevels
{
    public static VoiceLevels Shared { get; } = new();

    /// <summary>LLVoiceClient's getIsSpeaking and getCurrentPower together: the speaking amplitude (0-1) of someone
    /// speaking, null if they aren't (or are blocked).</summary>
    public volatile Func<UUID, float?>? Level;

    /// <summary>LipSyncEnabled (on by default, as the viewer's).</summary>
    public volatile bool LipSync = true;
}

public sealed partial class Avatars
{
    /// <summary>One avatar's lip sync: when it began speaking, its smoothed amplitude, and the head's vertices made in
    /// turn (three: a frame in flight may still be reading the last).</summary>
    sealed class LipState
    {
        public double Start = double.NaN, At = double.NaN;
        public float Amp;
        public readonly MeshVertex[][] Buffers = new MeshVertex[3][];
        public Vector3[]? Normals;
        public int Next;
    }

    // the viewer's defaults: LipSyncOoh, LipSyncAah (babble loops), LipSyncOohPowerTransfer, LipSyncAahPowerTransfer,
    // LipSyncOohAahRate; each digit d is 0.11 d (lipStringToF32s)
    static readonly float[] OohLoop = Digits("1247898743223344444443200000"), AahLoop = Digits("257998776531013446642343");
    static readonly float[] OohTransfer = Digits("0012345566778899"), AahTransfer = Digits("0000123456789");
    const float OohAahRate = 24f;
    /// <summary>How quickly the amplitude follows voice's reports (s): they come a few times a second, and the viewer's
    /// steps in them would show as jumps of the mouth at 90 Hz.</summary>
    const float AmpSmoothing = 0.06f;

    static float[] Digits(string s) => s.Select(c => 0.11f * Math.Min(c % 16, 9)).ToArray();

    MeshDef? _headOwner;
    ParamDef? _oohMorph, _aahMorph;
    bool _lipLooked;

    /// <summary>The head's LOD0 section and its Lipsync_Ooh and Lipsync_Aah morphs (the viewer falls back to
    /// Express_Kiss and Express_Open_Mouth when they're missing; avatar_lad.xml has both).</summary>
    bool LipMorphs()
    {
        if (_lipLooked) return _headOwner != null;
        _lipLooked = true;
        if (!_cd.Meshes.TryGetValue("headMesh", out var lods) || lods.Count == 0) return false;
        var owner = lods[0].Owner;
        ParamDef? Find(string name, string fallback) =>
            owner.Morphs.FirstOrDefault(p => p.Morph != null && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? owner.Morphs.FirstOrDefault(p => p.Morph != null && p.Name.Equals(fallback, StringComparison.OrdinalIgnoreCase));
        _oohMorph = Find("Lipsync_Ooh", "Express_Kiss");
        _aahMorph = Find("Lipsync_Aah", "Express_Open_Mouth");
        if (_oohMorph == null && _aahMorph == null) return false;
        _headOwner = owner;
        return true;
    }

    /// <summary>LLVoiceVisualizer::lipSyncOohAah, smooth: the power transfers and babble loops read between their
    /// entries (the viewer takes whole entries, stepping the mouth 24 times a second and at each new report of the
    /// voice's power), the amplitude eased toward each report, and easing to closed when speaking stops (the viewer
    /// snaps the morphs to rest). Returns false once the mouth is at rest.</summary>
    static bool OohAah(LipState st, float? level, double now, out float ooh, out float aah)
    {
        ooh = aah = 0;
        float dt = double.IsNaN(st.At) ? 0 : (float)Math.Clamp(now - st.At, 0, 0.1);
        st.At = now;
        if (level is { } l)
        {
            if (double.IsNaN(st.Start)) st.Start = now;   // setStartSpeaking
            st.Amp += (Math.Clamp(l, 0, 1) - st.Amp) * (1 - MathF.Exp(-dt / AmpSmoothing));
        }
        else
        {
            st.Amp += (0 - st.Amp) * (1 - MathF.Exp(-dt / AmpSmoothing));
            if (st.Amp < 0.004f) { st.Amp = 0; st.Start = double.NaN; return false; }
        }
        if (double.IsNaN(st.Start)) return false;
        double frames = (now - st.Start) * OohAahRate;
        long f = (long)Math.Floor(frames);
        float frac = (float)(frames - f);
        ooh = Between(OohTransfer, st.Amp) * Loop(OohLoop, f, frac);
        aah = Between(AahTransfer, st.Amp) * Loop(AahLoop, f, frac);
        return true;

        static float Between(float[] t, float amp)
        {
            float x = Math.Clamp(amp * t.Length, 0, t.Length - 1);
            int i = (int)x;
            return i + 1 < t.Length ? t[i] + (t[i + 1] - t[i]) * (x - i) : t[i];
        }
        static float Loop(float[] t, long f, float frac) => t[f % t.Length] + (t[(f + 1) % t.Length] - t[f % t.Length]) * frac;
    }

    /// <summary>The head's vertices with its lips where they are this frame (the shape's morphed head, the two lip
    /// morphs added on top: everything else in the shape is linear in its weights, so this is the shape built with
    /// them), or null while the mouth is at rest. Only the head is made again, so the body shape's own rebuilds (at most
    /// ten a second an avatar) don't make the mouth choppy.</summary>
    MeshVertex[]? LipVertices(Avatar av, Body body, double now)
    {
        var lv = VoiceLevels.Shared;
        float? level = lv.LipSync && lv.Level is { } f ? f(av.ID) : null;
        if (level == null && body.Lip == null) return null;
        var st = body.Lip ??= new LipState();
        if (!OohAah(st, level, now, out float ooh, out float aah)) { body.Lip = null; return null; }
        if (!LipMorphs() || body.Shape is not { } shape || !shape.Morphed.TryGetValue(_headOwner!, out var mm)) return null;
        int n = mm.Coords.Length;
        var buf = st.Buffers[st.Next];
        if (buf == null || buf.Length < n) st.Buffers[st.Next] = buf = new MeshVertex[n];
        st.Next = (st.Next + 1) % st.Buffers.Length;
        var normals = st.Normals is { } nn && nn.Length >= n ? nn : st.Normals = new Vector3[n];
        var scaled = mm.ScaledNormals ?? mm.Normals;
        for (int i = 0; i < n; i++)
        {
            buf[i] = new MeshVertex { Position = mm.Coords[i], Normal = mm.Normals[i], Tangent = new Vector4(1, 0, 0, 1), Uv = mm.TexCoords[i] };
            normals[i] = scaled[i];
        }
        int sex = shape.Male ? 2 : 1;
        void Apply(ParamDef? p, float amount)
        {
            if (p?.Morph is not { } mo || (p.Sex & sex) == 0) return;
            float w = p.Min + amount * (p.Max - p.Min);
            if (w == 0) return;
            for (int k = 0; k < mo.Indices.Length; k++)
            {
                int v = mo.Indices[k];
                if (v >= n) continue;
                buf[v].Position += mo.Coords[k] * w;
                buf[v].Uv += mo.TexCoords[k] * w;
                normals[v] += mo.Normals[k] * (w * 0.65f);   // NORMAL_SOFTEN_FACTOR, as BodyShape
            }
        }
        void Renormal(ParamDef? p)
        {
            if (p?.Morph is not { } mo) return;
            foreach (int v in mo.Indices)
                if (v < n) buf[v].Normal = normals[v].LengthSquared() > 0 ? Vector3.Normalize(normals[v]) : Vector3.UnitZ;
        }
        Apply(_oohMorph, ooh);
        Apply(_aahMorph, aah);
        Renormal(_oohMorph);
        Renormal(_aahMorph);
        return buf;
    }
}

using System.Numerics;
using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Core.Rlv;
using Sojourn.Render.Realtime;
using SphereFx = Sojourn.Render.Frame.Renderer.SphereFx;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Rlv;

/// <summary>
/// What RLV changes in the view, each frame (docs/rlv.md > Seeing): the vision spheres (@setsphere) handed to the
/// renderer with their origin in the frame's region (the avatar, or with origin 1 the camera: in the headset the middle
/// of the head, so both eyes see one sphere), eased over their tween as RLVa does; and the desktop camera's field of
/// view held within @setcam_fovmin/max.
/// </summary>
public sealed class RlvView
{
    readonly AgentSession _s;
    readonly FrameRenderer _r;
    readonly Dictionary<UUID, (RlvEngine.Sphere From, RlvEngine.Sphere To, double At)> _tween = new();
    int _rev = -1;
    List<RlvEngine.Sphere> _spheres = new();
    bool _drawn;

    public RlvView(AgentSession s, FrameRenderer r) => (_s, _r) = (s, r);

    /// <summary>The frame's region and camera (region coordinates; in the headset the middle of the head).</summary>
    public void Frame(ulong region, Vector3 camera, double now)
    {
        if (!_s.RlvActive || region == 0 || _s.Client is not { } c)
        {
            if (_drawn) { _r.VisionSpheres = null; _drawn = false; }
            return;
        }
        if (_s.Rlv.Revision != _rev)
        {
            _rev = _s.Rlv.Revision;
            var next = _s.Rlv.Spheres();
            foreach (var n in next)
            {
                var was = _spheres.FirstOrDefault(x => x.Object == n.Object);
                if (was != null && n.Tween > 0 && was != n) _tween[n.Object] = (Current(was, now), n, now);
                else _tween.Remove(n.Object);
            }
            _spheres = next;
        }
        if (_spheres.Count == 0)
        {
            if (_drawn) { _r.VisionSpheres = null; _drawn = false; }
            return;
        }
        // the avatar in the frame's region
        var avatar = new Vector3(c.Self.SimPosition.X, c.Self.SimPosition.Y, c.Self.SimPosition.Z);
        if (c.Network.CurrentSim is { } sim && sim.Handle != region)
        {
            Utils.LongToUInts(sim.Handle, out var sx, out var sy);
            Utils.LongToUInts(region, out var rx, out var ry);
            avatar += new Vector3((float)((double)sx - rx), (float)((double)sy - ry), 0);
        }
        var list = new List<SphereFx>(_spheres.Count);
        foreach (var sp in _spheres)
        {
            var s = Current(sp, now);
            var p = s.Params;
            list.Add(new SphereFx(s.Origin == 1 ? camera : avatar, s.Mode, s.DistExtend, s.ValueMin, s.DistMin, s.ValueMax, s.DistMax,
                new Vector4(p.X, p.Y, p.Z, p.W)));
        }
        _r.VisionSpheres = list;
        _drawn = true;
    }

    /// <summary>A sphere as it stands now, part way through its tween (distances, values and params eased linearly).</summary>
    RlvEngine.Sphere Current(RlvEngine.Sphere s, double now)
    {
        if (!_tween.TryGetValue(s.Object, out var t)) return s;
        float k = t.To.Tween <= 0 ? 1 : (float)Math.Clamp((now - t.At) / t.To.Tween, 0, 1);
        if (k >= 1) { _tween.Remove(s.Object); return t.To; }
        float L(float a, float b) => a + (b - a) * k;
        return t.To with
        {
            DistMin = L(t.From.DistMin, t.To.DistMin), DistMax = L(t.From.DistMax, t.To.DistMax),
            ValueMin = L(t.From.ValueMin, t.To.ValueMin), ValueMax = L(t.From.ValueMax, t.To.ValueMax),
            Params = new LibreMetaverse.Vector4(L(t.From.Params.X, t.To.Params.X), L(t.From.Params.Y, t.To.Params.Y),
                L(t.From.Params.Z, t.To.Params.Z), L(t.From.Params.W, t.To.Params.W)),
        };
    }

    /// <summary>The desktop camera's vertical field of view (radians) within @setcam_fovmin/max, or as a script set it
    /// by @setcam_fov.</summary>
    public float Fov(float fov)
    {
        if (!_s.RlvActive) return fov;
        if (_s.Rlv.CameraFov is { } asked) fov = asked;
        var cam = _s.Rlv.Camera();
        if (cam.FovMin is { } min) fov = MathF.Max(fov, min);
        if (cam.FovMax is { } max) fov = MathF.Min(fov, max);
        return fov;
    }
}

using LibreMetaverse;
using Sojourn.Render.Geometry;
using Sojourn.Render.Particles;

namespace Sojourn.Render.World;

/// <summary>
/// The region's part of the beacons (World/Beacons.cs), after the frame's draws are built: each object in view of a kind
/// turned on gets its beacon and its highlight (pipeline.cpp renderScriptedBeacons and the rest: scripted ones only at
/// a linkset's root, which isn't something worn; physical, particle, sound and media ones at any prim, worn ones too),
/// and with Highlight Transparent the faces drawn of nothing, fully transparent (red) or invisiprims (green, not worn),
/// which the frame never draws. Highlights use the shape the frame drew; rigged, flexible and animated meshes are left
/// unhighlighted (the viewer's renderSelected skips rigged faces too). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed partial class RegionScene
{
    internal void Beacons(BeaconScan scan, Vector3 offset, bool objects)
    {
        var kinds = objects ? scan.Kinds : BeaconKinds.None;
        bool touchOnly = (kinds & BeaconKinds.Touch) != 0;
        var flagsOf = scan.B.FlagsOf;
        long frame = _world.FrameNumber;
        foreach (var obj in _all)
        {
            if (obj.Skip || obj.Kind is ShapeKind.Tree or ShapeKind.Grass) continue;
            var prim = obj.Prim;
            BeaconKinds hit = BeaconKinds.None;
            Vector4 color = default;
            if (kinds != BeaconKinds.None)
            {
                var flags = flagsOf?.Invoke(prim) ?? prim.Flags;
                bool root = prim.ParentID == 0;   // (a worn object's root hangs from its wearer)
                // the floater's order: touch-only or scripted, physical, media, particles, sounds (the last colour wins
                // for an object of several kinds, as later beacons draw over earlier ones)
                if (root && (flags & PrimFlags.Scripted) != 0 && (touchOnly ? (flags & PrimFlags.Touch) != 0 : (kinds & BeaconKinds.Scripted) != 0))
                    (hit, color) = (hit | BeaconKinds.Scripted, new Vector4(1, 0, 0, 0.5f));
                if ((kinds & BeaconKinds.Physical) != 0 && (flags & PrimFlags.Physics) != 0)
                    (hit, color) = (hit | BeaconKinds.Physical, new Vector4(0, 1, 0, 0.5f));
                if ((kinds & BeaconKinds.Media) != 0 && HasMedia(prim))
                    (hit, color) = (hit | BeaconKinds.Media, new Vector4(1, 1, 1, 0.5f));
                if ((kinds & BeaconKinds.Particles) != 0 && IsParticleSource(prim))
                    (hit, color) = (hit | BeaconKinds.Particles, new Vector4(0.5f, 0.5f, 1, 0.5f));
                // sound sources' beacons come from world audio (Beacons.SoundSources); here only their highlights
                if ((kinds & BeaconKinds.Sound) != 0 && prim.Sound != UUID.Zero) hit |= BeaconKinds.Sound;
            }
            bool transparent = scan.Transparent && obj.Faces is { } fc && fc.ValidFrame == frame;
            if (hit == BeaconKinds.None && !transparent) continue;
            if (!Transform(obj, out var pos, out var rot)) continue;   // (a HUD, or a wearer not drawn)
            if ((int)prim.PrimData.AttachmentPoint is >= 31 and <= 38) continue;
            var scale = V(prim.Scale);
            var at = pos + offset;
            if (!scan.InView(at, 0.5f * scale.Length())) continue;
            if (scan.Lines && (hit & ~BeaconKinds.Sound) != 0) scan.Beacon(at, color);
            var shape = obj.Faces?.Shape;
            bool drawable = shape is { Ready: true, Evicted: false, Skin: null } && obj.Flexi == null && obj.Animesh == 0;
            if (!drawable) continue;
            if (scan.Highlights && hit != BeaconKinds.None) scan.Faces(shape!, at, rot, scale, prim.LocalID, FaceTint.Beacon);
            if (transparent) Undrawn(scan, obj, shape!, at, rot, scale);
        }
    }

    /// <summary>Highlight Transparent's faces the frame left out: fully transparent ones (the viewer's
    /// PASS_ALPHA_INVISIBLE) and invisiprims (PASS_INVISIBLE, never on what's worn).</summary>
    void Undrawn(BeaconScan scan, SceneObject obj, ShapeGeometry shape, Vector3 at, Quaternion rot, Vector3 scale)
    {
        var drawn = obj.Faces!.Faces;
        var prim = obj.Prim;
        bool Invisi(FaceGeometry f) => prim.Textures?.GetFace((uint)f.TeIndex) is { } te && Invisiprim(te);
        // (a face the frame doesn't draw that isn't an invisiprim is one FaceMaterials found fully transparent)
        if (drawn.Length < shape.Faces.Length)
            scan.Faces(shape, at, rot, scale, prim.LocalID, FaceTint.Blended, f =>
            {
                foreach (var d in drawn) if (ReferenceEquals(d.Face, f)) return false;
                return !Invisi(f);
            });
        if (obj.Owner == 0) scan.Faces(shape, at, rot, scale, prim.LocalID, FaceTint.Invisible, Invisi);
    }

    static bool HasMedia(Primitive prim)
    {
        var t = prim.Textures;
        if (t == null) return false;
        if (t.DefaultTexture?.MediaFlags == true) return true;
        foreach (var f in t.FaceTextures) if (f?.MediaFlags == true) return true;
        return false;
    }

    bool IsParticleSource(Primitive prim)
    {
        var ps = ParticleSystemTracker.Shared.TryGet(Source.Handle, prim.LocalID, out var tracked) ? tracked ?? default : prim.ParticleSys;
        return ps.BurstPartCount > 0 && ps.PartMaxAge > 0;
    }
}

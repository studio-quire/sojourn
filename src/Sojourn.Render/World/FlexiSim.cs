// Ported from the Second Life viewer: indra/newview/llflexibleobject.cpp (LLVolumeImplFlexible: remapSections,
// setAttributesOfAllSections, updateRenderRes, doFlexibleUpdate) and the LLQuaternion helpers it uses
// (llquaternion.cpp shortestArc, getAngleAxis). Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the
// viewer's licence header.
//
// The viewer's quaternion conventions in System.Numerics: `v * q` is Vector3.Transform(v, q), `a * b` (rotate by a,
// then by b) is Quaternion.Concatenate(a, b), `~q` is Quaternion.Conjugate(q).
using System.Numerics;
using Sojourn.Render.Geometry;

namespace Sojourn.Render.World;

/// <summary>A flexible prim's attributes (LLFlexibleObjectData): softness is the simulated sections' power of two.</summary>
public readonly record struct FlexiParams(int Softness, float Gravity, float Drag, float Wind, float Tension, Vector3 Force)
{
    public static FlexiParams From(LibreMetaverse.Primitive.FlexibleData f) =>
        new(Math.Clamp(f.Softness, 0, FlexiSim.MaxSections), f.Gravity, f.Drag, f.Wind, f.Tension, new Vector3(f.Force.X, f.Force.Y, f.Force.Z));
}

/// <summary>
/// One flexible prim's spring chain, as the viewer simulates it: up to 2^3 sections from the prim's anchor (the
/// middle of its -Z face) along its Z, pulled by gravity, wind and a constant force, held by tension, slowed by drag,
/// each section's bend limited; then the path the volume is swept along, in the prim's frame without its scale. Not
/// thread-safe: one simulation step at a time (RegionScene.Flexi.cs runs them one at a time per prim).
/// </summary>
public sealed class FlexiSim
{
    public const int MaxSections = 3;   // FLEXIBLE_OBJECT_MAX_SECTIONS
    const float MaxInternalTension = 0.99f;   // FLEXIBLE_OBJECT_MAX_INTERNAL_TENSION_FORCE
    const float MagThreshold = 0.0000001f;   // FP_MAG_THRESHOLD

    struct Section
    {
        public Vector3 Position, Direction, DPosition, Velocity;
        public Vector2 Scale;
        public Quaternion Rotation, AxisRotation;
    }

    readonly Section[] _s = new Section[(1 << MaxSections) + 1];
    readonly Section[] _remap = new Section[(1 << MaxSections) + 1];
    int _initializedRes = -1, _simRes;
    bool _initialized;
    /// <summary>The path's points as last written (doFlexibleUpdate keeps a point that moved too little to see), and
    /// whether there are any (mUpdated: a rebuild has used them).</summary>
    readonly Vector3[] _pathPos = new Vector3[(1 << MaxSections) + 1];
    int _pathCount;
    double _lastStep = double.NaN;
    Vector3 _scale;
    FlexiParams _attrs;

    public FlexiSim()
    {
        for (int i = 0; i < _s.Length; i++) _s[i].Rotation = _s[i].AxisRotation = Quaternion.Identity;
    }

    /// <summary>updateRenderRes: render sections' power of two by the prim's length on screen (its Z over its distance),
    /// at least one below its softness; simulated sections are no more than rendered.</summary>
    public static (int Render, int Simulate) Resolution(int softness, float scaleZ, float distance)
    {
        float appAngle = scaleZ / MathF.Max(distance, 0.001f);
        int render = (int)MathF.Min(12f * appAngle, 64f);
        render = Math.Clamp(render, softness - 1, MaxSections);
        return (render, Math.Min(softness, render));
    }

    /// <summary>One step at <paramref name="now"/> (seconds): the sections simulated from the prim's frame this moment
    /// (its render position and rotation in the region, its scale), and the path's points for
    /// <paramref name="renderRes"/> into <paramref name="path"/> (2^renderRes + 1 of them). <paramref name="wind"/>:
    /// the region's wind at a point; <paramref name="distance"/>: the camera's, for which point moves are too small to
    /// show.</summary>
    public int Step(double now, Vector3 framePos, Quaternion frameRot, Vector3 scale, VolumeParams vp, FlexiParams attrs,
        int simRes, int renderRes, float distance, Func<Vector3, Vector3>? wind, Span<FlexiPathPoint> path)
    {
        renderRes = Math.Clamp(renderRes, 0, MaxSections);
        simRes = Math.Clamp(simRes, 0, MaxSections);
        // a new section count, scale or attributes (onSetScale, onParameterChanged): the sections set again, kept
        // where they are as far as they can be
        if (!_initialized || simRes != _simRes || scale != _scale || attrs != _attrs)
        {
            _simRes = simRes;
            _scale = scale;
            _attrs = attrs;
            SetAttributesOfAllSections(framePos, frameRot, scale, vp);
            _initialized = true;
        }
        float dt = double.IsNaN(_lastStep) ? 0.2f : (float)Math.Min(now - _lastStep, 0.2);
        if (dt < 0) dt = 0;
        _lastStep = now;
        Simulate(dt, framePos, frameRot, scale, attrs, wind);
        return MakePath(framePos, frameRot, scale, renderRes, distance, path);
    }

    /// <summary>Moved by a long way at once (a teleport, a region crossing, a seat): the chain starts again straight.</summary>
    public void Reset() => _initialized = false;

    public Vector3 AnchorOf(Vector3 framePos, Quaternion frameRot, Vector3 scale) =>
        framePos - scale.Z / 2 * Vector3.Transform(Vector3.UnitZ, frameRot);

    // remapSections: from one section count to another (all from section 0 when there were none: straight along its
    // direction), in place where source and destination are the same array, in the viewer's order
    static void Remap(Section[] source, int sourceSections, Section[] dest, int destSections, float scaleZ)
    {
        int numOutput = 1 << destSections;
        float sectionLength = scaleZ / numOutput;
        if (sourceSections == -1)
        {
            dest[0] = source[0];
            for (int s = 0; s < numOutput; s++)
            {
                dest[s + 1] = dest[s];
                dest[s + 1].Position += dest[s].Direction * sectionLength;
                dest[s + 1].Velocity = Vector3.Zero;
            }
        }
        else if (sourceSections > destSections)
        {
            int numSteps = 1 << (sourceSections - destSections);
            for (int s = 0; s < numOutput; s++) dest[s + 1] = source[(s + 1) * numSteps];
            dest[0] = source[0];
        }
        else if (sourceSections < destSections)
        {
            int stepShift = destSections - sourceSections;
            int numSteps = 1 << stepShift;
            float sourceSectionLength = scaleZ / (1 << sourceSections);
            for (int s = numOutput - numSteps; s >= 0; s -= numSteps)
            {
                var last = source[s >> stepShift];
                var src = source[(s >> stepShift) + 1];
                // cubic interpolation of position: At^3 + Bt^2 + Ct + D
                Vector3 d = last.Position;
                Vector3 c = last.DPosition * sourceSectionLength;
                Vector3 y = src.DPosition * sourceSectionLength - c;
                Vector3 x = src.Position - d - c;
                Vector3 a = y - 2 * x;
                Vector3 b = x - a;
                float tInc = 1f / numSteps;
                float t = tInc;
                for (int step = 1; step < numSteps; step++)
                {
                    ref var o = ref dest[s + step];
                    o.Scale = Vector2.Lerp(last.Scale, src.Scale, t);
                    o.AxisRotation = Slerp(t, last.AxisRotation, src.AxisRotation);
                    float tSq = t * t;
                    o.Position = tSq * (t * a + b) + t * c + d;
                    o.Rotation = Slerp(t, last.Rotation, src.Rotation);
                    o.Velocity = Vector3.Lerp(last.Velocity, src.Velocity, t);
                    o.Direction = Vector3.Lerp(last.Direction, src.Direction, t);
                    o.DPosition = Vector3.Lerp(last.DPosition, src.DPosition, t);
                    dest[s + numSteps] = src;
                    t += tInc;
                }
            }
            dest[0] = source[0];
        }
        else
        {
            for (int s = 0; s <= numOutput; s++) dest[s] = source[s];
        }
    }

    void SetAttributesOfAllSections(Vector3 framePos, Quaternion frameRot, Vector3 scale, VolumeParams vp)
    {
        var (bx, by) = vp.BeginScale;
        var (ex, ey) = vp.EndScale;
        float beginRot = LLMath.Pi * vp.TwistBegin, endRot = LLMath.Pi * vp.TwistEnd;
        int numSections = 1 << _simRes;
        ref var s0 = ref _s[0];
        s0.Position = AnchorOf(framePos, frameRot, scale);
        s0.Direction = Vector3.Transform(Vector3.UnitZ, frameRot);
        s0.DPosition = s0.Direction;
        s0.Scale = new Vector2(scale.X * bx, scale.Y * by);
        s0.Velocity = Vector3.Zero;
        s0.AxisRotation = LLMath.AxisAngle(beginRot, 0, 0, 1);
        Remap(_s, _initializedRes, _s, _simRes, scale.Z);
        _initializedRes = _simRes;
        float tInc = 1f / numSections, t = tInc;
        for (int i = 1; i <= numSections; i++)
        {
            _s[i].AxisRotation = LLMath.AxisAngle(LLMath.Lerp(beginRot, endRot, t), 0, 0, 1);
            _s[i].Scale = new Vector2(scale.X * LLMath.Lerp(bx, ex, t), scale.Y * LLMath.Lerp(by, ey, t));
            t += tInc;
        }
    }

    // doFlexibleUpdate's simulation
    void Simulate(float dt, Vector3 basePosition, Quaternion baseRotation, Vector3 anchorScale, FlexiParams attrs, Func<Vector3, Vector3>? wind)
    {
        int numSections = 1 << _simRes;
        Quaternion parentSegmentRotation = baseRotation;
        Vector3 anchorDirectionRotated = Vector3.Transform(Vector3.UnitZ, parentSegmentRotation);
        float sectionLength = anchorScale.Z / numSections;
        float invSectionLength = 1f / sectionLength;

        // the anchor is offset from the base position (centroid) by half the length
        _s[0].Position = basePosition - anchorScale.Z / 2 * anchorDirectionRotated;
        _s[0].Direction = anchorDirectionRotated;
        _s[0].Rotation = baseRotation;

        // coefficients constant across sections
        float tFactor = attrs.Tension * 0.1f;
        tFactor = tFactor * (1 - MathF.Pow(0.85f, dt * 30));
        if (tFactor > MaxInternalTension) tFactor = MaxInternalTension;
        float friction = attrs.Drag * 2 + 1;
        friction = MathF.Pow(10f, friction * dt);
        friction = friction > 1 ? friction : 1;
        float momentum = 1.0f / friction;
        float windFactor = attrs.Wind * 0.1f * sectionLength * dt;
        float maxAngle = MathF.Atan(sectionLength * 2f);
        float forceFactor = sectionLength * dt;

        for (int i = 1; i <= numSections; i++)
        {
            ref var sec = ref _s[i];
            Vector3 lastPosition = sec.Position;
            // gravity
            sec.Position.Z -= attrs.Gravity * forceFactor;
            // wind
            if (attrs.Wind > 0.001f && wind != null) sec.Position += wind(sec.Position) * windFactor;
            // user-defined force
            sec.Position += attrs.Force * forceFactor;
            // tension (rigidity, stiffness)
            Vector3 parentSectionPosition = _s[i - 1].Position;
            Vector3 parentDirection = _s[i - 1].Direction;
            Vector3 parentSectionVector = i == 1 ? _s[0].Direction : _s[i - 2].Direction;
            Vector3 currentVector = sec.Position - parentSectionPosition;
            Vector3 difference = parentSectionVector * sectionLength - currentVector;
            sec.Position += difference * tFactor;
            // inertia
            sec.Position += sec.Velocity * momentum;
            // clamp length and rotation
            sec.Direction = NormVec(sec.Position - parentSectionPosition);
            Quaternion deltaRotation = ShortestArc(parentDirection, sec.Direction);
            GetAngleAxis(deltaRotation, out float angle, out Vector3 axis);
            if (angle > LLMath.Pi) angle -= 2f * LLMath.Pi;
            if (angle < -LLMath.Pi) angle += 2f * LLMath.Pi;
            if (angle > maxAngle) deltaRotation = SetQuat(maxAngle, axis);
            else if (angle < -maxAngle) deltaRotation = SetQuat(-maxAngle, axis);
            Quaternion segmentRotation = Quaternion.Concatenate(parentSegmentRotation, deltaRotation);
            parentSegmentRotation = segmentRotation;
            sec.Direction = Vector3.Transform(parentDirection, deltaRotation);
            sec.Position = parentSectionPosition + sec.Direction * sectionLength;
            sec.Rotation = segmentRotation;
            // half the rotation propagated up to the parent
            if (i > 1) _s[i - 1].Rotation = Quaternion.Concatenate(_s[i - 1].Rotation, SetQuat(angle / 2, axis));
            // velocity
            sec.Velocity = sec.Position - lastPosition;
            if (sec.Velocity.LengthSquared() > 1f) sec.Velocity = NormVec(sec.Velocity);
        }

        // derivatives
        _s[0].DPosition = (_s[1].Position - _s[0].Position) * invSectionLength;
        int k;
        for (k = 1; k < numSections; k++)
        {
            // quadratic numerical derivative of position
            Vector3 a = (_s[k - 1].Position - _s[k].Position + _s[k + 1].Position - _s[k].Position) * 0.5f * invSectionLength * invSectionLength;
            Vector3 b = _s[k + 1].Position - _s[k].Position - a * (sectionLength * sectionLength);
            b *= invSectionLength;
            _s[k].DPosition = b;
        }
        _s[k].DPosition = (_s[k].Position - _s[k - 1].Position) * invSectionLength;
    }

    // doFlexibleUpdate's points: the render sections (interpolated from the simulated ones) into the prim's frame
    int MakePath(Vector3 framePos, Quaternion frameRot, Vector3 scale, int renderRes, float distance, Span<FlexiPathPoint> path)
    {
        int numRender = 1 << renderRes;
        Remap(_s, _simRes, _remap, renderRes, scale.Z);
        Quaternion deltaRot = Quaternion.Conjugate(frameRot);
        bool updated = _pathCount == numRender + 1;
        float dist = MathF.Max(distance, 0.001f);
        for (int i = 0; i <= numRender; i++)
        {
            Vector3 pos = Vector3.Transform(_remap[i].Position - framePos, deltaRot);
            // a point that moved less than a thousandth of the camera's distance stays as it was (once one moves, the
            // rest are written)
            if (!updated || (_pathPos[i] - pos).Length() / dist > 0.001f)
            {
                _pathPos[i] = pos;
                updated = false;
            }
            // (the axis rotation by the render index of the simulated sections, as the viewer has it)
            Quaternion rot = Quaternion.Concatenate(Quaternion.Concatenate(_s[i].AxisRotation, _remap[i].Rotation), deltaRot);
            path[i] = new FlexiPathPoint
            {
                Pos = _pathPos[i], Rot = rot,
                ScaleX = _remap[i].Scale.X, ScaleY = _remap[i].Scale.Y, TexT = (float)i / numRender,
            };
        }
        _pathCount = numRender + 1;
        return numRender + 1;
    }

    // ---- LLQuaternion and LLVector3 as the viewer has them ----

    static Vector3 NormVec(Vector3 v)
    {
        float mag = v.Length();
        return mag > MagThreshold ? v / mag : Vector3.Zero;
    }

    static Quaternion SetQuat(float angle, Vector3 axis)
    {
        float mag = axis.Length();
        if (mag <= MagThreshold) return Quaternion.Identity;
        angle *= 0.5f;
        float c = MathF.Cos(angle), s = MathF.Sin(angle) / mag;
        return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    static Quaternion ShortestArc(Vector3 a, Vector3 b)
    {
        float ab = Vector3.Dot(a, b);
        Vector3 c = Vector3.Cross(a, b);
        float cc = Vector3.Dot(c, c);
        if (ab * ab + cc != 0)
        {
            if (cc > 0.0f)
            {
                float s = MathF.Sqrt(ab * ab + cc) + ab;
                float m = 1.0f / MathF.Sqrt(cc + s * s);
                return new Quaternion(c.X * m, c.Y * m, c.Z * m, s * m);
            }
            if (ab < 0.0f)
            {
                c = a - b;
                float m = MathF.Sqrt(c.X * c.X + c.Y * c.Y);
                if (m > MagThreshold) return new Quaternion(-c.Y / m, c.X / m, 0, 0);
                return new Quaternion(1, 0, 0, 0);
            }
        }
        return Quaternion.Identity;
    }

    static void GetAngleAxis(Quaternion q, out float angle, out Vector3 axis)
    {
        float v = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        if (v > MagThreshold)
        {
            float oomag = 1.0f / v;
            float w = q.W;
            if (q.W < 0.0f) { w = -w; oomag = -oomag; }
            axis = new Vector3(q.X * oomag, q.Y * oomag, q.Z * oomag);
            angle = 2.0f * MathF.Atan2(v, w);
        }
        else
        {
            angle = 0;
            axis = Vector3.UnitZ;
        }
    }

    // slerp(u, a, b) (llquaternion.cpp)
    static Quaternion Slerp(float u, Quaternion a, Quaternion b)
    {
        float cosT = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        bool flip = cosT < 0;
        if (flip) cosT = -cosT;
        float alpha, beta;
        if (1f - cosT < 0.00001f)   // F_APPROXIMATELY_ZERO
        {
            beta = 1f - u;
            alpha = u;
        }
        else
        {
            float theta = MathF.Acos(cosT);
            float sinT = MathF.Sin(theta);
            beta = MathF.Sin(theta - u * theta) / sinT;
            alpha = MathF.Sin(u * theta) / sinT;
        }
        if (flip) beta = -beta;
        return new Quaternion(beta * a.X + alpha * b.X, beta * a.Y + alpha * b.Y, beta * a.Z + alpha * b.Z, beta * a.W + alpha * b.W);
    }
}

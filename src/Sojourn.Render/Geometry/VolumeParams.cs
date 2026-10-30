// Ported from the Second Life viewer: indra/llmath/llvolume.cpp/.h (LLProfileParams, LLPathParams,
// LLVolumeParams checked setters) and indra/llprimitive/llvolumemessage.cpp (unpack + constrainVolumeParams).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using Primitive = LibreMetaverse.Primitive;

namespace Sojourn.Render.Geometry;

// Volume shape parameters exactly as the viewer holds them after unpacking an object update
// (LLVolumeMessage::unpackVolumeParams, which also runs constrainVolumeParams).
public sealed class VolumeParams
{
    // Raw curve bytes. Profile: low nibble = shape, high nibble = hole type. Path: 0x10 line, 0x20 circle,
    // 0x30 circle2, 0x40 test, 0x80 flexible. Several viewer tests compare the whole byte, so keep both nibbles.
    public byte ProfileCurve = VolumeConsts.ProfileSquare;
    public float ProfileBegin;
    public float ProfileEnd = 1f;
    public float ProfileHollow;

    public byte PathCurve = VolumeConsts.PathLine;
    public float PathBegin;
    public float PathEnd = 1f;
    public float ScaleX = 1f, ScaleY = 1f;
    public float ShearX, ShearY;
    public float TwistBegin, TwistEnd;
    public float RadiusOffset;
    public float TaperX, TaperY;
    public float Revolutions = 1f;
    public float Skew;

    // Sculpt type byte incl. invert (64) / mirror (128) flags; 0 for plain prims.
    public byte SculptType;

    public VolumeParams Clone() => (VolumeParams)MemberwiseClone();

    // LLPathParams::getBeginScale / getEndScale
    public (float X, float Y) BeginScale => (ScaleX > 1 ? 2 - ScaleX : 1f, ScaleY > 1 ? 2 - ScaleY : 1f);
    public (float X, float Y) EndScale => (ScaleX < 1 ? ScaleX : 1f, ScaleY < 1 ? ScaleY : 1f);

    // Build from LibreMetaverse's already-dequantized floats: re-quantize to the wire integers (lossless for
    // values that came off the wire) and then dequantize and constrain the way the viewer does.
    public static VolumeParams FromConstructionData(Primitive.ConstructionData c, byte sculptType = 0)
    {
        byte profileCurve = (byte)((byte)c.ProfileCurve | (byte)c.ProfileHole);
        return FromWire(
            (byte)c.PathCurve,
            Primitive.PackBeginCut(c.PathBegin), Primitive.PackEndCut(c.PathEnd),
            Primitive.PackPathScale(c.PathScaleX), Primitive.PackPathScale(c.PathScaleY),
            Primitive.PackPathShear(c.PathShearX), Primitive.PackPathShear(c.PathShearY),
            Primitive.PackPathTwist(c.PathTwist), Primitive.PackPathTwist(c.PathTwistBegin),
            Primitive.PackPathTwist(c.PathRadiusOffset),
            Primitive.PackPathTaper(c.PathTaperX), Primitive.PackPathTaper(c.PathTaperY),
            Primitive.PackPathRevolutions(c.PathRevolutions),
            Primitive.PackPathTwist(c.PathSkew),
            profileCurve,
            Primitive.PackBeginCut(c.ProfileBegin), Primitive.PackEndCut(c.ProfileEnd),
            Primitive.PackProfileHollow(c.ProfileHollow),
            sculptType);
    }

    // Wire values as in the ObjectUpdate ObjectData block.
    public static VolumeParams FromWire(byte pathCurve, ushort pathBegin, ushort pathEnd, byte pathScaleX, byte pathScaleY,
        sbyte pathShearX, sbyte pathShearY, sbyte pathTwist, sbyte pathTwistBegin, sbyte pathRadiusOffset,
        sbyte pathTaperX, sbyte pathTaperY, byte pathRevolutions, sbyte pathSkew,
        byte profileCurve, ushort profileBegin, ushort profileEnd, ushort profileHollow, byte sculptType = 0)
    {
        const float CUT_QUANTA = 0.00002f, SCALE_QUANTA = 0.01f, SHEAR_QUANTA = 0.01f, TAPER_QUANTA = 0.01f,
            REV_QUANTA = 0.015f, HOLLOW_QUANTA = 0.00002f;

        var p = new VolumeParams { SculptType = sculptType };

        // unpackPathParams
        p.PathCurve = pathCurve;
        p.PathBegin = pathBegin * CUT_QUANTA;
        p.PathEnd = (50000 - pathEnd) * CUT_QUANTA;
        p.ScaleX = (200 - pathScaleX) * SCALE_QUANTA;
        p.ScaleY = (200 - pathScaleY) * SCALE_QUANTA;
        p.ShearX = pathShearX * SHEAR_QUANTA;
        p.ShearY = pathShearY * SHEAR_QUANTA;
        p.TwistEnd = pathTwist * SCALE_QUANTA;
        p.TwistBegin = pathTwistBegin * SCALE_QUANTA;
        p.RadiusOffset = pathRadiusOffset * SCALE_QUANTA;
        p.TaperX = pathTaperX * TAPER_QUANTA;
        p.TaperY = pathTaperY * TAPER_QUANTA;
        p.Revolutions = pathRevolutions * REV_QUANTA + 1.0f;
        p.Skew = pathSkew * SCALE_QUANTA;

        // unpackProfileParams
        p.ProfileCurve = profileCurve;
        float f = profileBegin * CUT_QUANTA;
        if (f > 1f) f = 0f;
        p.SetProfileBegin(f);
        f = profileEnd * CUT_QUANTA;
        if (f > 1f) f = 1f;
        p.SetProfileEnd(1f - f);
        f = profileHollow * HOLLOW_QUANTA;
        if (f > 1f) f = 0f;
        p.SetProfileHollow(f);

        p.Constrain();
        return p;
    }

    // LLProfileParams setters truncate to 1e-5. The viewer is built with /fp:fast, which turns "/ 100000.0f" into a
    // multiply by the reciprocal; matching that keeps cut/hollow values bit-identical to a release viewer build.
    private void SetProfileBegin(float begin) => ProfileBegin = begin >= 1.0f ? 0.0f : ((int)(begin * 100000)) * (1 / 100000.0f);
    private void SetProfileEnd(float end) => ProfileEnd = end <= 0.0f ? 1.0f : ((int)(end * 100000)) * (1 / 100000.0f);
    private void SetProfileHollow(float hollow) => ProfileHollow = ((int)(hollow * 100000)) * (1 / 100000.0f);

    // LLVolumeMessage::constrainVolumeParams: feed everything through LLVolumeParams' checked setters.
    // Returns false if anything had to be forced (the viewer logs a warning and carries on).
    public bool Constrain()
    {
        bool ok = true;
        ok &= SetType(ProfileCurve, PathCurve);
        ok &= SetBeginAndEndS(ProfileBegin, ProfileEnd);
        ok &= SetBeginAndEndT(PathBegin, PathEnd);
        ok &= SetHollow(ProfileHollow);
        ok &= LimitRange(ref TwistBegin, -1f, 1f);
        ok &= LimitRange(ref TwistEnd, -1f, 1f);
        ok &= SetRatio(ScaleX, ScaleY);
        ok &= LimitRange(ref ShearX, -0.5f, 0.5f) & LimitRange(ref ShearY, -0.5f, 0.5f);
        ok &= LimitRange(ref TaperX, -1f, 1f) & LimitRange(ref TaperY, -1f, 1f);
        ok &= LimitRange(ref Revolutions, 1f, 4f);
        ok &= SetRadiusOffset(RadiusOffset);
        ok &= SetSkew(Skew);
        return ok;
    }

    private const float APPROXIMATELY_ZERO = 0.001f;
    private const float MIN_CUT_DELTA = 0.02f;

    private static bool ApproxZero(float f, float tolerance = APPROXIMATELY_ZERO) => f >= -tolerance && f <= tolerance;

    private static bool LimitRange(ref float v, float min, float max, float tolerance = APPROXIMATELY_ZERO)
    {
        float minDelta = v - min;
        if (minDelta < 0f)
        {
            v = min;
            if (!ApproxZero(minDelta, tolerance)) return false;
        }
        float maxDelta = max - v;
        if (maxDelta < 0f)
        {
            v = max;
            if (!ApproxZero(maxDelta, tolerance)) return false;
        }
        return true;
    }

    private bool SetType(byte profile, byte path)
    {
        bool result = true;
        int profileType = profile & VolumeConsts.ProfileMask;
        int holeType = (profile & VolumeConsts.HoleMask) >> 4;
        int pathType = path >> 4;
        if (profileType > VolumeConsts.ProfileMax)
        {
            profile = VolumeConsts.ProfileSquare;
            result = false;
        }
        else if (holeType > 0x03)
        {
            profile = (byte)profileType;
            result = false;
        }
        if (pathType < 0x01 || pathType > 0x08)
        {
            result = false;
            path = VolumeConsts.PathLine;
        }
        ProfileCurve = profile;
        PathCurve = path;
        return result;
    }

    private bool SetBeginAndEndS(float b, float e)
    {
        bool valid = true;
        float begin = b;
        valid &= LimitRange(ref begin, 0f, 1f - MIN_CUT_DELTA);
        float end = e;
        if (end >= .0149f && end < MIN_CUT_DELTA) end = MIN_CUT_DELTA;
        valid &= LimitRange(ref end, MIN_CUT_DELTA, 1f);
        valid &= LimitRange(ref begin, 0f, end - MIN_CUT_DELTA, .01f);
        SetProfileBegin(begin);
        SetProfileEnd(end);
        return valid;
    }

    private bool SetBeginAndEndT(float b, float e)
    {
        bool valid = true;
        float begin = b;
        valid &= LimitRange(ref begin, 0f, 1f - MIN_CUT_DELTA);
        float end = e;
        valid &= LimitRange(ref end, MIN_CUT_DELTA, 1f);
        valid &= LimitRange(ref begin, 0f, end - MIN_CUT_DELTA, .01f);
        PathBegin = begin;
        PathEnd = end;
        return valid;
    }

    private bool SetHollow(float h)
    {
        int profile = ProfileCurve & VolumeConsts.ProfileMask;
        int holeType = ProfileCurve & VolumeConsts.HoleMask;
        float maxHollow = 0.95f;
        if (holeType == VolumeConsts.HoleSquare &&
            (profile == VolumeConsts.ProfileCircle || profile == VolumeConsts.ProfileCircleHalf || profile == VolumeConsts.ProfileEqualTri))
            maxHollow = 0.7f;
        float hollow = h;
        bool valid = LimitRange(ref hollow, 0f, maxHollow);
        SetProfileHollow(hollow);
        return valid;
    }

    private bool SetRatio(float x, float y)
    {
        float minX = 0f, maxX = 2f, minY = 0f, maxY = 2f;
        int profileType = ProfileCurve & VolumeConsts.ProfileMask;
        if (PathCurve == VolumeConsts.PathCircle && profileType != VolumeConsts.ProfileCircleHalf)
        {
            minX = 0.05f; maxX = 1.0f; minY = 0.05f; maxY = 0.5f; // hole size
        }
        float rx = x, ry = y;
        bool valid = LimitRange(ref rx, minX, maxX);
        valid &= LimitRange(ref ry, minY, maxY);
        ScaleX = rx;
        ScaleY = ry;
        return valid;
    }

    private bool SetRadiusOffset(float offset)
    {
        int profileType = ProfileCurve & VolumeConsts.ProfileMask;
        if (profileType == VolumeConsts.ProfileCircleHalf || PathCurve != VolumeConsts.PathCircle)
        {
            RadiusOffset = 0f;
            return true;
        }
        bool valid = true;
        float radiusOffset = offset;
        float taperY = TaperY;
        float radiusMag = MathF.Abs(radiusOffset);
        float holeYMag = MathF.Abs(ScaleY);
        float taperYMag = MathF.Abs(taperY);
        if ((radiusOffset > 0f && taperY < 0f) || (radiusOffset < 0f && taperY > 0f))
            taperYMag = 0f;
        float maxRadiusMag = 1f - holeYMag * (1f - taperYMag) / (1f - holeYMag);
        float delta = maxRadiusMag - radiusMag;
        if (delta < 0f)
        {
            radiusOffset = radiusOffset < 0f ? -maxRadiusMag : maxRadiusMag;
            valid = ApproxZero(delta, .1f);
        }
        RadiusOffset = radiusOffset;
        return valid;
    }

    private bool SetSkew(float skewValue)
    {
        bool valid = true;
        float skew = Math.Clamp(skewValue, -0.95f, 0.95f);
        float skewMag = MathF.Abs(skew);
        float minSkewMag = 1.0f - 1.0f / (Revolutions * ScaleX + 1.0f);
        if (MathF.Abs(Revolutions - 1.0f) < 0.001f)
            minSkewMag = 0.0f;
        float delta = skewMag - minSkewMag;
        if (delta < 0f)
        {
            skew = skew < 0.0f ? -minSkewMag : minSkewMag;
            valid = ApproxZero(delta, .01f);
        }
        Skew = skew;
        return valid;
    }
}

public static class VolumeConsts
{
    public const byte ProfileMask = 0x0f;
    public const byte ProfileCircle = 0x00;
    public const byte ProfileSquare = 0x01;
    public const byte ProfileIsoTri = 0x02;
    public const byte ProfileEqualTri = 0x03;
    public const byte ProfileRightTri = 0x04;
    public const byte ProfileCircleHalf = 0x05;
    public const byte ProfileMax = 0x05;

    public const byte HoleMask = 0xf0;
    public const byte HoleSame = 0x00;
    public const byte HoleCircle = 0x10;
    public const byte HoleSquare = 0x20;
    public const byte HoleTriangle = 0x30;

    public const byte PathLine = 0x10;
    public const byte PathCircle = 0x20;
    public const byte PathCircle2 = 0x30;
    public const byte PathTest = 0x40;
    public const byte PathFlexible = 0x80;

    // LLFaceID bits (VolumeFace.FaceId)
    public const ushort FacePathBegin = 1 << 0;   // top cap
    public const ushort FacePathEnd = 1 << 1;     // bottom cap
    public const ushort FaceInnerSide = 1 << 2;   // hollow
    public const ushort FaceProfileBegin = 1 << 3;
    public const ushort FaceProfileEnd = 1 << 4;
    public const ushort FaceOuterSide0 = 1 << 5;
    public const ushort FaceOuterSide1 = 1 << 6;
    public const ushort FaceOuterSide2 = 1 << 7;
    public const ushort FaceOuterSide3 = 1 << 8;

    public const byte SculptTypeNone = 0;
    public const byte SculptTypeSphere = 1;
    public const byte SculptTypeTorus = 2;
    public const byte SculptTypePlane = 3;
    public const byte SculptTypeCylinder = 4;
    public const byte SculptTypeMesh = 5;
    public const byte SculptTypeMask = 7;
    public const byte SculptFlagInvert = 64;
    public const byte SculptFlagMirror = 128;

    public const int MinDetailFaces = 6;
}

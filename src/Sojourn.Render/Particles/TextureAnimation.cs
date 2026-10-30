using LibreMetaverse;

namespace Sojourn.Render.Particles;

/// <summary>llSetTextureAnim as the viewer plays it (llviewertextureanim.cpp LLViewerTextureAnim::animateTextures and
/// LLVOVolume::animateTextures; LGPL 2.1, Linden Research, Inc.): the frame at a time since the animation's timer
/// started, as the offset/scale/rotation that replace the face's texture transform (components the animation
/// doesn't drive keep the face's own).</summary>
public static class TextureAnimation
{
    public static void Apply(in Primitive.TextureAnimation a, double elapsed, ref float offS, ref float offT,
        ref float scaleS, ref float scaleT, ref float rot)
    {
        var mode = a.Flags;
        if ((mode & Primitive.TextureAnimMode.ANIM_ON) == 0) return;
        bool smooth = (mode & Primitive.TextureAnimMode.SMOOTH) != 0, loop = (mode & Primitive.TextureAnimMode.LOOP) != 0;
        float numFrames = a.Length != 0 ? a.Length : Math.Max(1f, a.SizeX * a.SizeY);
        float fullLength;
        if ((mode & Primitive.TextureAnimMode.PING_PONG) != 0)
        {
            if (smooth) fullLength = 2f * numFrames;
            else if (loop) fullLength = Math.Max(1f, 2f * numFrames - 2f);
            else fullLength = Math.Max(1f, 2f * numFrames - 1f);
        }
        else fullLength = numFrames;

        float frame = (float)(elapsed * a.Rate);
        if (loop) frame %= fullLength;   // fmod
        else frame = Math.Min(fullLength - 1f, frame);
        if (!smooth)
        {
            frame = MathF.Floor(frame + 0.01f);
            frame = Math.Min(fullLength - 1f, frame);
        }
        if ((mode & Primitive.TextureAnimMode.PING_PONG) != 0 && frame >= numFrames)
            frame = smooth ? numFrames - (frame - numFrames) : (numFrames - 1.99f) - (frame - numFrames);
        if ((mode & Primitive.TextureAnimMode.REVERSE) != 0)
            frame = smooth ? numFrames - frame : (numFrames - 0.99f) - frame;
        frame += a.Start;
        if (!smooth) frame = MathF.Floor(frame + 0.5f);   // ll_round

        if ((mode & Primitive.TextureAnimMode.ROTATE) != 0) rot = frame;
        else if ((mode & Primitive.TextureAnimMode.SCALE) != 0) { scaleS = frame; scaleT = frame; }
        else if (a.SizeX != 0 && a.SizeY != 0)
        {
            scaleS = 1f / a.SizeX;
            scaleT = 1f / a.SizeY;
            float xFrame = frame % a.SizeX;   // fmodf
            int yFrame = (int)(frame / a.SizeX);
            offS = (-0.5f + 0.5f * scaleS) + xFrame * scaleS;
            offT = (0.5f - 0.5f * scaleT) - yFrame * scaleT;
        }
        else
        {
            // translate only: the face keeps its own repeats (the viewer doesn't report SCALE here)
            offS = frame;
            offT = 0f;
        }
    }

    /// <summary>Whether an animation covers a face (its face number, or every face for -1 or a number past the
    /// object's faces).</summary>
    public static bool Covers(in Primitive.TextureAnimation a, int face, int faceCount) =>
        a.Face == 255 || a.Face >= faceCount || a.Face == face;
}

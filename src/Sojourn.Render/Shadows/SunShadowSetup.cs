namespace Sojourn.Render.Shadows;

/// <summary>One sun/moon shadow cascade (mRT->shadow[j]).</summary>
public sealed class ShadowCascade
{
    /// <summary>
    /// False: the viewer found no receivers in this split and only clears the map (depth 1.0).
    /// ShadowMatrix is then set to sample a lit result whatever the map holds.
    /// </summary>
    public bool Active { get; internal set; }

    /// <summary>True if the warped perspective projection was used, false for plain ortho.</summary>
    public bool Perspective { get; internal set; }

    /// <summary>Region -> light view (GL-style, looking down -z along the light). view[j].</summary>
    public Matrix4x4 LightView { get; internal set; }

    /// <summary>Light view -> GL clip (z in [-w, w]). proj[j].</summary>
    public Matrix4x4 LightProjection { get; internal set; }

    /// <summary>Region -> Vulkan clip (depth 0..1) for rendering casters. Needs depth clamp.</summary>
    public Matrix4x4 LightViewProj { get; internal set; }

    /// <summary>GL eye space -> shadow texture coords (xy in [0,1], z = compare depth). shadow_matrix[j].</summary>
    public Matrix4x4 ShadowMatrix { get; internal set; }

    /// <summary>
    /// Caster cull volume (the shadow camera): the projection's side and far planes, plus the
    /// shared near plane 2*FarClip toward the light. Cull caster AABBs with IntersectsAabb.
    /// Null when inactive.
    /// </summary>
    public ShadowFrustum? CullFrustum { get; internal set; }

    /// <summary>The slice of the view frustum this cascade must cover: what lies dist[j]*0.75 .. dist[j+1]*1.25 from the
    /// eye (a depth slice holding that shell; the far cascade's from the fourth's end to its own).</summary>
    public ShadowFrustum? SplitFrustum { get; internal set; }

    /// <summary>The receiver point cloud the fit used (region space). For debugging.</summary>
    public Vector3[] ReceiverPoints { get; internal set; } = [];

    /// <summary>mShadowError[j] (-1 when the FOV test fell back to ortho).</summary>
    public float Error { get; internal set; }

    /// <summary>mShadowFOV[j] (-1 when the error test chose ortho).</summary>
    public float Fov { get; internal set; }
}

/// <summary>
/// The CPU side of the viewer's cascaded sun shadows: LLPipeline::generateSunShadow (with
/// getVisiblePointCloud, look(), the LLCamera frustum code) and the shadow uniforms that
/// bindDeferredShader / allocateShadowBuffer produce.
/// </summary>
public sealed class SunShadowSetup
{
    /// <summary>
    /// False when the main view has no visible geometry: the viewer returns early and keeps last
    /// frame's shadows. With no previous frame, treat as no shadows (clear all maps to 1.0).
    /// </summary>
    public bool Valid { get; private set; }

    /// <summary>Always 4; with CubeSnapshot only the first 2 are generated.</summary>
    public ShadowCascade[] Cascades { get; } = [new(), new(), new(), new()];

    /// <summary>The coarse far cascade (ShadowSettings.FarMapSize; inactive without it): fitted like the others to the
    /// shell from where the fourth ends (<see cref="ShadowClip"/>.W) out to <see cref="FarClip"/>.Z.</summary>
    public ShadowCascade Far { get; } = new();

    /// <summary>The far cascade's distances for the shader (shadow_far): where it takes over from the fourth (x), where
    /// it starts fading out (y), where it ends (z), and w 1 when it's on.</summary>
    public Vector4 FarClip { get; private set; }

    public int FarMapSize { get; private set; }

    /// <summary>Sun shadow map size (all four maps). Bind as shadow_res.</summary>
    public int MapWidth { get; private set; }
    public int MapHeight { get; private set; }
    public Vector2 Resolution => new(MapWidth, MapHeight);

    /// <summary>shadow_clip: the split distances (mSunClipPlanes), radii about the eye here (the viewer's are eye-space
    /// depths).</summary>
    public Vector4 ShadowClip { get; private set; }

    /// <summary>Where the last cascade would end without <see cref="ShadowSettings.FarReach"/> (ShadowClip.W with it).</summary>
    public float FullFar { get; private set; }

    /// <summary>dist[0]: the near end of the first split.</summary>
    public float NearClip { get; private set; }

    /// <summary>shadow_bias as bound: RenderShadowBias + RenderShadowBiasError * |camera z| / 3000.</summary>
    public float Bias { get; private set; }

    /// <summary>shadow_offset as bound (RenderShadowOffset).</summary>
    public float Offset { get; private set; }

    public float ShadowNoise { get; private set; }
    public float BlurSize { get; private set; }

    public Matrix4x4[] LightViewProj => Array.ConvertAll(Cascades, c => c.LightViewProj);
    public Matrix4x4[] ShadowMatrix => Array.ConvertAll(Cascades, c => c.ShadowMatrix);

    /// <summary>
    /// The shadow_matrix[6] uniform: four cascades, then the two projector shadows (identity if absent).
    /// </summary>
    public Matrix4x4[] ShadowMatrixUniform(SpotShadow? spot0 = null, SpotShadow? spot1 = null) =>
    [
        Cascades[0].ShadowMatrix, Cascades[1].ShadowMatrix, Cascades[2].ShadowMatrix, Cascades[3].ShadowMatrix,
        spot0?.ShadowMatrix ?? Matrix4x4.Identity, spot1?.ShadowMatrix ?? Matrix4x4.Identity,
    ];

    /// <summary>The viewer's main projection far plane (MAX_FAR_CLIP * 2); it feeds the split widening.</summary>
    const float ProjectionFar = 1024f;

    // Samples (0.5, 0.5) at depth 0: always passes LEQUAL, i.e. lit, like a cleared map.
    static readonly Matrix4x4 AlwaysLit = new(
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 0, 0,
        0.5f, 0.5f, 0, 1);

    /// <summary>
    /// Compute the sun (or moon) shadow setup for one frame.
    /// </summary>
    /// <param name="camera">Main camera: region -> eye view, vertical fov, aspect, far = draw distance.</param>
    /// <param name="lightDirection">Region-space direction toward the caster: the sun, or the moon
    /// when the sun is down (LLEnvironment::getIsSunUp). Normalized here.</param>
    /// <param name="scene">Visible-extents source (see AabbSceneExtents).</param>
    /// <param name="screenWidth">Main render target width (mRT->width).</param>
    /// <param name="screenHeight">Main render target height.</param>
    public static SunShadowSetup Compute(in ShadowCamera camera, Vector3 lightDirection, ISceneExtents scene,
        int screenWidth, int screenHeight, ShadowSettings? settings = null)
    {
        var s = settings ?? ShadowSettings.Default;
        var r = new SunShadowSetup();

        float scale = s.CubeSnapshot ? 1.0f : MathF.Max(0f, s.ResolutionScale);
        r.MapWidth = ShadowMath.BlurHappySize(screenWidth, scale);
        r.MapHeight = ShadowMath.BlurHappySize(screenHeight, scale);

        Matrix4x4 view = camera.View;
        Matrix4x4.Invert(view, out var invView);
        Vector3 eye = invView.Translation;
        Vector3 camRight = new(view.M11, view.M21, view.M31);
        Vector3 camUp = new(view.M12, view.M22, view.M32);
        Vector3 camAt = -new Vector3(view.M13, view.M23, view.M33);
        Vector3 camLeft = -camRight;

        r.Bias = s.Bias + s.BiasError * MathF.Abs(eye.Z) / 3000f;
        r.Offset = s.Offset;
        r.ShadowNoise = s.ShadowNoise;
        r.BlurSize = s.BlurSize;

        foreach (var c in r.Cascades)
            c.ShadowMatrix = AlwaysLit;

        Vector3 casterDir = ShadowMath.Norm(lightDirection);
        float farClipSetting = s.FarClip ?? camera.Far;

        // universal near clip plane for the shadow cull volumes
        Vector4 shadowNearClip;
        {
            Vector3 p = eye + casterDir * (farClipSetting * 2f);
            shadowNearClip = new Vector4(casterDir, -Vector3.Dot(p, casterDir));
        }

        Vector3 lightDir = ShadowMath.Norm(-casterDir);

        Vector3 up = camAt;
        if (MathF.Abs(Vector3.Dot(up, lightDir)) > 0.75f)
            up = camUp;
        up = ShadowMath.Norm(up);

        // main camera frustum as LLViewerCamera::updateFrustumPlanes builds it in setPerspective (near corners on the
        // near plane), but reaching to depth Far, so it holds everything in view within Far of the eye (the viewer's far
        // corners sit at distance Far along the corner rays)
        float tanHalf = MathF.Tan(camera.FovY * 0.5f);
        float nh = camera.Near * tanHalf, nw = nh * camera.Aspect;
        Span<Vector3> mainFrust = stackalloc Vector3[8];
        mainFrust[0] = Vector3.Transform(new Vector3(-nw, -nh, -camera.Near), invView);
        mainFrust[1] = Vector3.Transform(new Vector3(nw, -nh, -camera.Near), invView);
        mainFrust[2] = Vector3.Transform(new Vector3(nw, nh, -camera.Near), invView);
        mainFrust[3] = Vector3.Transform(new Vector3(-nw, nh, -camera.Near), invView);
        for (int i = 0; i < 4; i++)
        {
            var ray = ShadowMath.Norm(mainFrust[i] - eye);
            mainFrust[i + 4] = eye + ShadowMath.Div(ray * camera.Far, Vector3.Dot(ray, camAt));
        }
        var mainCam = ShadowFrustum.FromCorners(mainFrust);

        float nearClip;
        Vector4 clip = default;
        {
            var fp = new List<Vector3>();
            GetVisiblePointCloud(mainCam, scene, fp);
            if (fp.Count == 0)
                return r;

            // Departure (docs/rendering.md > Distances from the head): the splits are radii about the eye, from the
            // nearest and farthest receivers within Far in every direction, not the visible ones' eye-space depths, so
            // they hold still as the head turns; the shader picks cascades by distance (shadows.glsl)
            float nearR, farR;
            if (!scene.TryGetRadialRange(eye, camera.Far, out nearR, out farR))
            {
                Vector3 min = Vector3.Transform(fp[0], view), max = min;
                for (int i = 1; i < fp.Count; i++)
                    ShadowMath.UpdateMinMax(ref min, ref max, Vector3.Transform(fp[i], view));
                (nearR, farR) = (-max.Z, -min.Z);
            }
            nearClip = Math.Clamp(nearR, MathF.Max(camera.Near, 0.01f), 4.0f);
            float farClip = Math.Clamp(farR * 2f, 16.0f, 512.0f);
            farClip = MathF.Min(farClip, camera.Far);
            float range = farClip - nearClip;

            Vector3 splitExp = s.SplitExponent;
            float da = 1f - MathF.Max(MathF.Abs(Vector3.Dot(lightDir, up)), MathF.Abs(Vector3.Dot(lightDir, camLeft)));
            da = MathF.Pow(da, splitExp.Z);
            float sxp = splitExp.Y + (splitExp.X - splitExp.Y) * da;

            for (int i = 0; i < 4; i++)
            {
                float x = (i + 1) / 4f;
                x = MathF.Pow(x, sxp);
                clip[i] = nearClip + range * x;
            }
            clip.X *= 1.25f; // bump back first split for transition padding
        }
        r.FullFar = clip.W;
        if (s.FarReach < 1f && !s.CubeSnapshot)
            clip.W = MathF.Min(clip.W, MathF.Max(clip.Z * 1.25f, clip.W * s.FarReach));

        if (s.CubeSnapshot)
        {
            clip.Y = clip.Z;
            clip.Z = clip.W;
            clip.W *= 1.5f;
        }

        r.Valid = true;
        r.NearClip = nearClip;
        r.ShadowClip = clip;
        Span<float> dist = [nearClip, clip.X, clip.Y, clip.Z, clip.W];

        // The main projection's far-plane corners (updateFrustumPlanes no_hacks through the GL
        // matrices; setup3DRender's projection far is MAX_FAR_CLIP*2).
        Span<Vector3> projFar = stackalloc Vector3[4];
        {
            float tw = tanHalf * camera.Aspect;
            for (int i = 0; i < 4; i++)
            {
                float sx = i is 1 or 2 ? 1f : -1f;
                float sy = i >= 2 ? 1f : -1f;
                projFar[i] = Vector3.Transform(
                    new Vector3(sx * tw * ProjectionFar, sy * tanHalf * ProjectionFar, -ProjectionFar), invView);
            }
        }

        int cascadeCount = s.CubeSnapshot ? 2 : 4;
        // the coarse far cascade (ours): the shell from where the fourth ends (trimmed or not) out past its full reach
        bool far = s.FarMapSize > 0 && !s.CubeSnapshot;
        float farEnd = r.FullFar * MathF.Max(s.FarEnd, 1.05f);
        Span<Vector3> frust = stackalloc Vector3[8];
        Span<Vector3> deltas = stackalloc Vector3[4];
        Span<float> dps = stackalloc float[4];
        for (int j = 0; j < cascadeCount + (far ? 1 : 0); j++)
        {
            bool isFar = j == 4;
            var cas = isFar ? r.Far : r.Cascades[j];
            // the shell's radii: the viewer's transition bands either side of a split; the far one from the fourth's end
            float shellNear = isFar ? clip.W * 0.98f : dist[j] * 0.75f, shellFar = isFar ? farEnd : dist[j + 1] * 1.25f;
            float margin = isFar ? s.FarCascadeMargin : j >= 2 && !s.CubeSnapshot ? s.FarMargin : 0;

            // Split frustum corners. Done in place as in the viewer: corners 2 and 3 take their 5%
            // widening from corners 4 and 5 after those were already moved to the split, so the
            // split frustums are widened asymmetrically (mostly at the bottom).
            projFar.CopyTo(frust[4..]);
            float minDp = 1f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 delta = frust[i + 4] - eye;
                delta += (frust[i + 4] - frust[(i + 2) % 4 + 4]) * 0.05f;
                delta = ShadowMath.Norm(delta);
                float dp = Vector3.Dot(delta, camAt);
                (deltas[i], dps[i]) = (delta, dp);
                minDp = MathF.Min(minDp, dp);
                frust[i + 4] = eye + ShadowMath.Div(delta * shellFar, dp);
            }
            // The split is a shell of radii dist[j] * 0.75 .. dist[j + 1] * 1.25 (the shader's transitions included):
            // its far end lies within depth dist[j + 1] * 1.25 as in the viewer; its near end comes as close as that
            // radius along the most slanted corner ray (the viewer's slice starts at depth dist[j] * 0.75)
            for (int i = 0; i < 4; i++)
                frust[i] = eye + ShadowMath.Div(deltas[i] * shellNear * MathF.Max(minDp, 0.05f), dps[i]);
            var splitCam = ShadowFrustum.FromCorners(frust);
            cas.SplitFrustum = splitCam;

            var fp = new List<Vector3>();
            if (!GetVisiblePointCloud(splitCam, scene, fp) || (!isFar && j > s.Splits))
            {
                cas.Active = false;
                cas.Error = 0f;
                cas.Fov = 0f;
                continue;
            }
            cas.ReceiverPoints = fp.ToArray();

            Matrix4x4 lview = ShadowMath.Look(eye, lightDir, -up);

            var wpf = new Vector3[fp.Count];
            for (int i = 0; i < fp.Count; i++)
                wpf[i] = ShadowMath.MulPoint(lview, fp[i]);

            Vector3 min = wpf[0], max = wpf[0];
            for (int i = 0; i < wpf.Length; i++)
                ShadowMath.UpdateMinMax(ref min, ref max, wpf[i]);

            Vector3 center = (min + max) * 0.5f;
            if (margin > 0)
            {
                // grown about the middle: across the light (x, and y: the camera's forward, the perspective warp's
                // axis) and along it (z, the depth range, so receivers a little higher or lower next frame still fit)
                float k = 1f + margin;
                for (int i = 0; i < wpf.Length; i++)
                    wpf[i] = center + (wpf[i] - center) * k;
                (min, max) = (center + (min - center) * k, center + (max - center) * k);
            }
            Vector3 size = (max - min) * 0.5f;

            // all points in quadrant 0 relative to the centre, then a least squares line
            float bfm = 0f, bfb = 0f;
            for (int i = 0; i < wpf.Length; i++)
            {
                wpf[i] -= center;
                wpf[i].X = MathF.Abs(wpf[i].X);
                wpf[i].Z = MathF.Abs(wpf[i].Z);
            }

            float n = wpf.Length;
            {
                float sx = 0f, sx2 = 0f, sy = 0f, sxy = 0f;
                for (int i = 0; i < wpf.Length; i++)
                {
                    sx += wpf[i].X;
                    sx2 += wpf[i].X * wpf[i].X;
                    sy += wpf[i].Y;
                    sxy += wpf[i].X * wpf[i].Y;
                }
                bfm = (sy * sx - n * sxy) / (sx * sx - n * sx2);
                bfb = (sx * sxy - sy * sx2) / (sx * sx - bfm * sx2);
            }

            Matrix4x4 proj;
            {
                // point furthest to the right of the line y = bfm*x + bfb
                float offX = -1f;
                Vector3 lp = Vector3.Zero;
                for (int i = 0; i < wpf.Length; i++)
                {
                    float lx = (wpf[i].Y - bfb) / bfm;
                    lx = wpf[i].X - lx;
                    if (offX < lx)
                    {
                        offX = lx;
                        lp = wpf[i];
                    }
                }

                bfb = lp.Y - bfm * lp.X;

                float error = 0f;
                for (int i = 0; i < wpf.Length; i++)
                {
                    float lx = (wpf[i].Y - bfb) / bfm;
                    error += MathF.Abs(wpf[i].X - lx);
                }
                error /= n;
                error /= size.X;
                cas.Error = error;

                if (error > s.ErrorCutoff)
                {
                    cas.Fov = -1f;
                    proj = ShadowMath.GlOrtho(min.X, max.X, min.Y, max.Y, -max.Z, -min.Z);
                }
                else
                {
                    Vector3 origin = new(0f, bfb, 0f);

                    float fovz = 1f, fovx = 1f;
                    Vector3 zp = Vector3.Zero;
                    for (int i = 0; i < wpf.Length; i++)
                    {
                        Vector3 atz = wpf[i] - origin;
                        atz.X = 0f;
                        atz = ShadowMath.Norm(atz);
                        if (fovz > -atz.Y)
                        {
                            zp = wpf[i];
                            fovz = -atz.Y;
                        }

                        Vector3 atx = wpf[i] - origin;
                        atx.Z = 0f;
                        atx = ShadowMath.Norm(atx);
                        if (fovx > -atx.Y)
                            fovx = -atx.Y;
                    }

                    fovx = MathF.Acos(fovx);
                    fovz = MathF.Acos(fovz);

                    float cutoff = MathF.Min(s.FovCutoff, 1.4f);
                    cas.Fov = fovx;

                    if (fovx < cutoff && fovz > cutoff)
                    {
                        // x fits, z too wide: back off from zp until fovz matches the cutoff
                        float d = zp.Z / MathF.Tan(cutoff);
                        float ny = zp.Y + MathF.Abs(d);
                        origin.Y = ny;

                        fovz = 1f;
                        fovx = 1f;
                        for (int i = 0; i < wpf.Length; i++)
                        {
                            Vector3 atz = wpf[i] - origin;
                            atz.X = 0f;
                            atz = ShadowMath.Norm(atz);
                            fovz = MathF.Min(fovz, -atz.Y);

                            Vector3 atx = wpf[i] - origin;
                            atx.Z = 0f;
                            atx = ShadowMath.Norm(atx);
                            fovx = MathF.Min(fovx, -atx.Y);
                        }

                        fovx = MathF.Acos(fovx);
                        fovz = MathF.Acos(fovz);
                        cas.Fov = cutoff;
                    }

                    origin += center;

                    float ynear = -(max.Y - origin.Y);
                    float yfar = -(min.Y - origin.Y);
                    if (ynear < 0.1f)
                    {
                        float diff = 0.1f - ynear;
                        origin.Y += diff;
                        ynear += diff;
                        yfar += diff;
                    }

                    if (fovx > cutoff)
                    {
                        cas.Error = -1f;
                        proj = ShadowMath.GlOrtho(min.X, max.X, min.Y, max.Y, -max.Z, -min.Z);
                    }
                    else
                    {
                        Matrix4x4.Invert(lview, out var invL);
                        Vector3 originAgent = ShadowMath.MulPoint(invL, origin);
                        lview = ShadowMath.Look(originAgent, lightDir, -up);

                        float fx = 1f / MathF.Tan(fovx);
                        float fz = 1f / MathF.Tan(fovz);
                        proj = new Matrix4x4(
                            -fx, 0, 0, 0,
                            0, (yfar + ynear) / (ynear - yfar), 0, -1f,
                            0, 0, -fz, 0,
                            0, (2f * yfar * ynear) / (ynear - yfar), 0, 0);
                        cas.Perspective = true;
                    }
                }
            }

            // Deviation: a cloud whose |x| values are all equal makes the least-squares slope 0/0 and
            // the viewer goes on with NaN matrices for this cascade. Fall back to its ortho fit instead.
            if (!IsFinite(proj) || !IsFinite(lview))
            {
                lview = ShadowMath.Look(eye, lightDir, -up);
                proj = ShadowMath.GlOrtho(min.X, max.X, min.Y, max.Y, -max.Z, -min.Z);
                cas.Perspective = false;
                cas.Error = float.NaN;
            }

            Matrix4x4 viewProj = lview * proj;
            var cull = ShadowFrustum.FromCorners(ShadowMath.UnprojectCube(viewProj));
            cull.SetPlaneKeepMask(ShadowFrustum.Near, shadowNearClip);

            cas.Active = true;
            cas.LightView = lview;
            cas.LightProjection = proj;
            cas.LightViewProj = viewProj * ShadowMath.VulkanDepthRemap;
            cas.ShadowMatrix = invView * (lview * (proj * ShadowMath.TexBias));
            cas.CullFrustum = cull;
        }
        if (far && r.Far.Active)
        {
            r.FarClip = new Vector4(clip.W, r.FullFar, farEnd, 1);
            r.FarMapSize = s.FarMapSize;
        }

        return r;
    }

    static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    /// <summary>
    /// LLPipeline::getVisiblePointCloud: the corners of (visible extents AABB) ∩ (frustum),
    /// approximated by box corners, frustum corners and edge/plane crossings, filtered with 5 cm slack.
    /// </summary>
    public static bool GetVisiblePointCloud(ShadowFrustum camera, ISceneExtents scene, List<Vector3> fp)
    {
        if (!scene.TryGetVisibleExtents(camera, out var min, out var max))
            return false;

        Span<Vector4> bp =
        [
            new(-1, 0, 0, min.X), new(0, -1, 0, min.Y), new(0, 0, -1, min.Z),
            new(1, 0, 0, -max.X), new(0, 1, 0, -max.Y), new(0, 0, 1, -max.Z),
        ];

        var pp = new List<Vector3>(64)
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z),
        };
        for (int i = 0; i < 8; i++)
            pp.Add(camera.Corners[i]);

        ReadOnlySpan<int> bs = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 3, 7, 2, 6];
        for (int i = 0; i < 12; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                var cp = camera.Planes[j];
                Vector3 v1 = pp[bs[i * 2]], v2 = pp[bs[i * 2 + 1]];
                Vector3 nrm = new(cp.X, cp.Y, cp.Z);
                Vector3 line = v1 - v2;
                float d1 = Vector3.Dot(line, nrm);
                float d2 = -(Vector3.Dot(nrm, v2) + cp.W);
                float t = d2 / d1;
                if (t > 0f && t < 1f)
                    pp.Add(v2 + line * t);
            }
        }

        ReadOnlySpan<int> fs = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < 12; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                Vector3 v1 = pp[fs[i * 2] + 8], v2 = pp[fs[i * 2 + 1] + 8];
                var cp = bp[j];
                Vector3 nrm = new(cp.X, cp.Y, cp.Z);
                Vector3 line = v1 - v2;
                float d1 = Vector3.Dot(line, nrm);
                float d2 = -(Vector3.Dot(nrm, v2) + cp.W);
                float t = d2 / d1;
                if (t > 0f && t < 1f)
                    pp.Add(v2 + line * t);
            }
        }

        Vector3 ext0 = min - new Vector3(0.05f), ext1 = max + new Vector3(0.05f);
        foreach (var p in pp)
        {
            bool found = p.X >= ext0.X && p.X <= ext1.X
                && p.Y >= ext0.Y && p.Y <= ext1.Y
                && p.Z >= ext0.Z && p.Z <= ext1.Z;
            if (found)
            {
                for (int j = 0; j < 6; j++)
                {
                    if (camera.Distance(j, p) > 0.05f)
                    {
                        found = false;
                        break;
                    }
                }
            }
            if (found)
                fp.Add(p);
        }

        return fp.Count > 0;
    }
}

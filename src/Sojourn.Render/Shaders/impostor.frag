// An avatar impostor into the G-buffer (Frame/Renderer.Impostors.cs): the eye's ray through the box, marched against
// the capture's depth (a height field along the capture direction) to the first surface, whose G-buffer texel is
// written with its own depth. Every eye marches its own ray, so each sees the avatar's surfaces where they were, with
// their parallax, rather than a card. Normals were captured in world space and go to this eye's space here.
// Material: diffuse/specular/normal/emissive slots are the atlas's, bump its depth's; diffuse.offset and .scale the
// tile's pixel origin and size.
#include "common.glsl"
#include "gbuffer_util.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) flat in uint vInstance;

layout(location = 0) out vec4 frag_data0;
layout(location = 1) out vec4 frag_data1;
layout(location = 2) out vec4 frag_data2;
layout(location = 3) out vec4 frag_data3;

const int STEPS = 24;

Material M;
ivec2 Origin, Size;

ivec2 Texel(vec3 q)
{
    vec2 uv = vec2(q.x * 0.5 + 0.5, 0.5 - q.y * 0.5);
    return Origin + clamp(ivec2(uv * vec2(Size)), ivec2(0), Size - 1);
}

// the surface's height in the box's z (-1 back, 1 front, toward the camera at capture); -2 where there's nothing
float Surface(vec3 q)
{
    float d = texelFetch(textures[nonuniformEXT(int(M.bump))], Texel(q), 0).r;
    return d > 0.0 ? d * 2.0 - 1.0 : -2.0;
}

void main()
{
    Instance inst = INSTANCE(vInstance);
    M = MATERIAL(inst.material);
    Origin = ivec2(M.diffuse.offset);
    Size = ivec2(M.diffuse.scale);
    vec3 ax = inst.model[0].xyz, ay = inst.model[1].xyz, az = inst.model[2].xyz, c = inst.model[3].xyz;
    vec3 ia = ax / dot(ax, ax), ib = ay / dot(ay, ay), ic = az / dot(az, az);

    // the ray in box coordinates ([-1, 1] on every axis); t = 1 is this back-face fragment
    vec3 eye = VIEW.cameraPos.xyz;
    vec3 dir = vWorldPos - eye;
    vec3 o = vec3(dot(eye - c, ia), dot(eye - c, ib), dot(eye - c, ic));
    vec3 d = vec3(dot(dir, ia), dot(dir, ib), dot(dir, ic));
    vec3 t0 = (-1.0 - o) / d, t1 = (1.0 - o) / d;
    vec3 lo = min(t0, t1), hi = max(t0, t1);
    float tin = max(max(lo.x, lo.y), max(lo.z, 0.0));
    float tout = min(min(hi.x, hi.y), hi.z);
    if (tin >= tout) discard;

    float dt = (tout - tin) / float(STEPS);
    float t = tin, hit = -1.0;
    for (int i = 0; i < STEPS; i++)
    {
        t += dt;
        vec3 q = o + d * t;
        if (q.z <= Surface(q)) { hit = t; break; }
    }
    if (hit < 0.0) discard;
    // refine between the last step above the surface and the first below it
    float a = hit - dt, b = hit;
    for (int i = 0; i < 5; i++)
    {
        float m = 0.5 * (a + b);
        vec3 q = o + d * m;
        if (q.z <= Surface(q)) b = m; else a = m;
    }
    vec3 q = o + d * b;
    ivec2 px = Texel(q);

    // colours filtered between the texels around the hit that hold the avatar (a tile is about the impostor's size on
    // screen, and nearest texels show as steps); the normal and its flags as the nearest texel has them
    vec2 at = vec2(q.x * 0.5 + 0.5, 0.5 - q.y * 0.5) * vec2(Size) - 0.5;
    ivec2 base = ivec2(floor(at));
    vec2 f = at - vec2(base);
    vec4 diffuse = vec4(0), spec = vec4(0), emissive = vec4(0);
    float wsum = 0.0;
    for (int k = 0; k < 4; k++)
    {
        ivec2 off = ivec2(k & 1, k >> 1);
        ivec2 t = Origin + clamp(base + off, ivec2(0), Size - 1);
        if (texelFetch(textures[nonuniformEXT(int(M.bump))], t, 0).r <= 0.0) continue;
        float w = (off.x == 1 ? f.x : 1.0 - f.x) * (off.y == 1 ? f.y : 1.0 - f.y);
        diffuse += w * texelFetch(textures[nonuniformEXT(M.diffuse.index)], t, 0);
        spec += w * texelFetch(textures[nonuniformEXT(M.specular.index)], t, 0);
        emissive += w * texelFetch(textures[nonuniformEXT(M.emissive.index)], t, 0);
        wsum += w;
    }
    if (wsum > 1e-4)
    {
        diffuse /= wsum; spec /= wsum; emissive /= wsum;
    }
    else
    {
        diffuse = texelFetch(textures[nonuniformEXT(M.diffuse.index)], px, 0);
        spec = texelFetch(textures[nonuniformEXT(M.specular.index)], px, 0);
        emissive = texelFetch(textures[nonuniformEXT(M.emissive.index)], px, 0);
    }
    vec4 nrm = gbNormIn(texelFetch(textures[nonuniformEXT(M.normal.index)], px, 0));
    vec3 n = normalize(mat3(VIEW.view) * decodeOct(nrm.xy));

    frag_data0 = diffuse;
    frag_data1 = spec;
    frag_data2 = gbNormOut(encodeNormal(n, nrm.z, nrm.w));
    frag_data3 = emissive;
    vec4 clip = VIEW.proj * (VIEW.view * vec4(eye + dir * b, 1.0));
    gl_FragDepth = clip.z / clip.w;
}

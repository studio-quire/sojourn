// PBR terrain (the region's four terrain slots naming GLTF material assets), heightmap-with-noise painting.
// Ported from the Second Life viewer's class1/deferred/pbrterrainF.glsl, pbrterrainUtilF.glsl, pbrterrainV.glsl and
// textureUtilV.glsl (LGPL 2.1, Linden Research, Inc.); the triplanar mapping in them is adapted from Inigo Quilez'
// example shader (MIT, https://www.shadertoy.com/view/MtsGWH). TERRAIN_PBR_DETAIL 0 (every map), as the viewer's Mid
// presets and up. The terrain material (MAT_TERRAIN | MAT_TERRAIN_PBR) is followed in the list by the four slots'
// materials: colour = base colour factor, specColor.rgb = emissive factor, diffuse/normal/specular/emissive = base
// colour, normal, ORM and emissive maps, the diffuse ref carrying the slot's base colour KHR transform (scale already
// divided by RenderTerrainPBRScale), alphaCutoff = minimum alpha. envIntensity = planar sample count (1 or 3).

#define TERRAIN_RAMP_MIX_THRESHOLD 0.1
#define TERRAIN_TRIPLANAR_MIX_THRESHOLD 0.01
#define TERRAIN_TRIPLANAR_BLEND_FACTOR 8.0   // RenderTerrainPBRTriplanarBlendFactor

struct TerrainPbrMix
{
    vec4 col;       // linear
    vec3 orm;
    vec3 vNt;
    vec3 emissive;  // linear
};

TerrainPbrMix terrainPbrZero()
{
    TerrainPbrMix x;
    x.col = vec4(0); x.orm = vec3(0); x.vNt = vec3(0); x.emissive = vec3(0);
    return x;
}

TerrainPbrMix terrainPbrAdd(TerrainPbrMix a, TerrainPbrMix b, float w)
{
    a.col += b.col * w; a.orm += b.orm * w; a.vNt += b.vNt * w; a.emissive += b.emissive * w;
    return a;
}

// terrain_texture_transform: KHR_texture_transform in a left-handed UV system
vec2 terrainPbrUv(vec2 p, TexRef t)
{
    p.y = -p.y;
    float c = cos(t.rotation), s = sin(t.rotation);
    vec2 sc = p * t.scale;
    p = vec2(c * sc.x + s * sc.y, -s * sc.x + c * sc.y) + t.offset;
    p.y = -p.y;
    return p;
}

// terrain_tangent_space_transform
vec4 terrainPbrTangent(vec4 t, vec3 n, TexRef x)
{
    vec2 w = vec2(1, 0);
    float r = -x.rotation;
    w = mat2(cos(r), -sin(r), sin(r), cos(r)) * w;
    vec2 ss = sign(x.scale);
    w *= ss;
    w.x += 1.0 - abs(sign(sign(w.x) + (0.5 * sign(w.y))));
    w.y = -w.y;
    vec3 b = t.w * cross(n, t.xyz);
    return vec4((w.x * t.xyz) + (w.y * b), t.w * ss.x * ss.y);
}

TerrainPbrMix terrainPbrSample(Material d, vec2 uv)
{
    TerrainPbrMix x;
    x.col = sampleTex(d.diffuse.index, uv, vec4(1));
    x.col.rgb = srgb_to_linear(x.col.rgb);
    x.orm = sampleTex(d.specular.index, uv, vec4(1)).xyz;
    x.vNt = sampleTex(d.normal.index, uv, vec4(0.5, 0.5, 1.0, 1.0)).xyz * 2.0 - 1.0;
    x.emissive = srgb_to_linear(sampleTex(d.emissive.index, uv, vec4(1)).rgb);
    return x;
}

// _t_normal_post_1: the normal's xy turned round where the face looks down its axis
vec3 terrainPbrFlip(vec3 v, float signOrZero)
{
    v.xy *= signOrZero < 0.0 ? -1.0 : 1.0;
    return v;
}

// one slot, sampled flat (xy) or triplanar, times its factors; vNt still in tangent space
TerrainPbrMix terrainPbrSlot(Material d, vec3 p, vec3 vertexNormal, bool triplanar, float tangentSign)
{
    TerrainPbrMix x;
    if (!triplanar) x = terrainPbrSample(d, terrainPbrUv(p.xy, d.diffuse));
    else
    {
        vec3 n = vertexNormal;
        vec3 ws = pow(abs(n), vec3(TERRAIN_TRIPLANAR_BLEND_FACTOR));
        ws /= (ws.x + ws.y + ws.z);
        ws -= vec3(TERRAIN_TRIPLANAR_MIX_THRESHOLD);
        vec3 w = max(vec3(0), ws);
        w /= (w.x + w.y + w.z);
        x = terrainPbrZero();
        if (ws.x > 0.0)
        {
            vec2 uv = n.x > 0.0 ? terrainPbrUv(p.yz, d.diffuse) : terrainPbrUv(p.yz * vec2(-1, 1), d.diffuse);
            TerrainPbrMix s = terrainPbrSample(d, uv);
            s.vNt = terrainPbrFlip(s.vNt, sign(n.x));
            s.vNt.xy = vec2(-s.vNt.y, s.vNt.x) * tangentSign;
            x = terrainPbrAdd(x, s, w.x);
        }
        if (ws.y > 0.0)
        {
            vec2 uv = n.y > 0.0 ? terrainPbrUv(p.xz * vec2(-1, 1), d.diffuse) : terrainPbrUv(p.xz, d.diffuse);
            TerrainPbrMix s = terrainPbrSample(d, uv);
            s.vNt = terrainPbrFlip(s.vNt, sign(n.y));
            s.vNt.xy = -s.vNt.xy;
            x = terrainPbrAdd(x, s, w.y);
        }
        if (ws.z > 0.0)
        {
            vec2 uv = n.z > 0.0 ? terrainPbrUv(p.xy, d.diffuse) : vec2(0);
            TerrainPbrMix s = terrainPbrSample(d, uv);
            s.vNt = terrainPbrFlip(s.vNt, sign(n.z));
            x = terrainPbrAdd(x, s, w.z);
        }
    }
    x.col *= d.color;
    x.orm *= vec3(1.0, d.roughness, d.metallic);
    x.emissive *= d.specColor.rgb;
    return x;
}

// mikktspace with the slot's transformed tangent (eye space)
vec3 terrainPbrNormal(vec3 vNt, vec3 vN, vec4 t)
{
    vec3 vT = normalize(t.xyz);
    vec3 vB = t.w * cross(vN, vT);
    vec3 tnorm = normalize(vNt.x * vT + vNt.y * vB + vNt.z * vN);
    return tnorm * (gl_FrontFacing ? 1.0 : -1.0);
}

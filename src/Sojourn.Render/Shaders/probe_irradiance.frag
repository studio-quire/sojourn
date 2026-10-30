#version 460
// Lambertian irradiance for one face of a probe (pc.i1 = source cube slot, pc.v0 = (face, 0, max lod, width)).
// Ported from the Second Life viewer's class2/interface/irradianceGenF.glsl (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"
#include "cubeface.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

#define MATH_PI 3.1415926535897932384626433832795

const float u_roughness = 1.0;
const int u_sampleCount = 32;
const float u_lodBias = 2.0;
const int u_width = 64;

float radicalInverse_VdC(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

vec2 hammersley2d(int i, int N) {
    return vec2(float(i)/float(N), radicalInverse_VdC(uint(i)));
}

// the viewer builds the frame with y up (its cube space); our cube space is region space, z up
mat3 generateTBN(vec3 normal)
{
    vec3 bitangent = vec3(0.0, 0.0, 1.0);
    vec3 tangent = normalize(cross(bitangent, normal));
    bitangent = cross(normal, tangent);
    return mat3(tangent, bitangent, normal);
}

vec4 filterColor(vec3 N, float max_probe_lod)
{
    int src = int(pc.i1);
    vec4 color = vec4(0.f);
    for(int i = 0; i < u_sampleCount; ++i)
    {
        vec2 xi = hammersley2d(i, u_sampleCount);
        float cosTheta = sqrt(1.0 - xi.y);
        float sinTheta = sqrt(xi.y);
        float phi = 2.0 * MATH_PI * xi.x;
        float pdf = cosTheta / MATH_PI;
        vec3 localSpaceDirection = normalize(vec3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta));
        vec3 H = generateTBN(N) * localSpaceDirection;

        float lod = 0.5 * log2( 6.0 * float(u_width) * float(u_width) / (float(u_sampleCount) * pdf));
        lod += u_lodBias;
        lod = clamp(lod, 0, max_probe_lod);
        color += textureLod(cubes[nonuniformEXT(src)], H, lod);
    }
    color /= float(u_sampleCount);
    return color;
}

void main()
{
    vec3 N = cubeFaceDir(int(pc.v0.x), vary_fragcoord);
    frag_color = max(filterColor(N, pc.v0.z), vec4(0));
}

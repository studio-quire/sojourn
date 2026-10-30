#version 460
// Ported from the Second Life viewer's class1/environment/waterV.glsl (LGPL 2.1, Linden Research, Inc.).
// Positions are region coordinates on the water plane; the instance's model matrix carries the region offset.
#include "common.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;

layout(location = 0) out vec4 refCoord;
layout(location = 1) out vec4 littleWave;
layout(location = 2) out vec4 view;
layout(location = 3) out vec3 vary_position;
layout(location = 4) out vec3 vary_light_dir;
layout(location = 5) out vec3 vary_tangent;
layout(location = 6) out vec3 vary_normal;
out float gl_ClipDistance[1];

void main()
{
    Instance inst = INSTANCE(gl_InstanceIndex);
    vec4 pos = inst.model * vec4(inPosition, 1.0);
    mat3 normal_matrix = mat3(VIEW.view);
    vec3 eyeVec = SKY.eye_vec;
    float time = SKY.water_time;

    vec4 position = pos;
    vary_position = (VIEW.view * pos).xyz;
    vary_light_dir = normal_matrix * SKY.water_light_dir;
    vary_normal = normal_matrix * vec3(0, 0, 1);
    vary_tangent = normal_matrix * vec3(1, 0, 0);

    vec3 oEyeVec = pos.xyz - eyeVec;
    float d = length(oEyeVec.xy);
    float ld = min(d, 2560.0);
    pos.xy = eyeVec.xy + oEyeVec.xy / max(d, 1e-4) * ld;
    view.xyz = oEyeVec;

    vec4 oPosition = VIEW.proj * VIEW.view * position;
    refCoord.xyz = oPosition.xyz + vec3(0, 0, 0.2);

    // sweeping horizontal waves
    vec3 v = pos.xyz;
    v.x += (cos(v.x * 0.08) + sin(v.y * 0.02)) * 6.0;

    vec2 bigWave = (v.xy) * vec2(0.04, 0.04) + SKY.waveDir1 * time * 0.055;
    littleWave.xy = (v.xy) * vec2(0.45, 0.9) + SKY.waveDir2 * time * 0.13;
    littleWave.zw = (v.xy) * vec2(0.1, 0.2) + SKY.waveDir1 * time * 0.1;
    view.w = bigWave.y;
    refCoord.w = bigWave.x;

    // refCoord.xy/z must give [-1,1] screen coords like GL's clip.xy/clip.w with w = -z_eye; our reverse-Z clip
    // has w = -z_eye too, and z = near (constant), so divide by w instead of z
    refCoord.z = oPosition.w;
    gl_Position = oPosition;
    gl_ClipDistance[0] = viewClip(position.xyz, 0u);
}

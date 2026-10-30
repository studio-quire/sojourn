// Direction through a cube map texel: face 0..5 = +X -X +Y -Y +Z -Z, uv with v running down the face
// (the Vulkan/GL cube map convention).
vec3 cubeFaceDir(int face, vec2 uv)
{
    vec2 sc = uv * 2.0 - 1.0;
    vec3 d;
    if (face == 0) d = vec3( 1.0, -sc.y, -sc.x);
    else if (face == 1) d = vec3(-1.0, -sc.y,  sc.x);
    else if (face == 2) d = vec3( sc.x,  1.0,  sc.y);
    else if (face == 3) d = vec3( sc.x, -1.0, -sc.y);
    else if (face == 4) d = vec3( sc.x, -sc.y,  1.0);
    else d = vec3(-sc.x, -sc.y, -1.0);
    return normalize(d);
}

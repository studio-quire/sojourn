// The G-buffer's normal format (Frame/RenderTargets.cs): this file as it stands is the wide one (RGBA16F). With
// RenderTargets.Narrow the renderer puts a version defining NARROW_GBUFFER in its place (Renderer's constructor):
// A2B10G10R10 UNORM (gbuffer_util.glsl gbNormOut, gbNormIn).

using System.Diagnostics;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Client.Xr;

// OpenXR's Session, not the Sojourn.Session namespace (which a bare name in Sojourn.* finds first)
using Session = Silk.NET.OpenXR.Session;

/// <summary>
/// The lenses' hidden area per eye (XR_KHR_visibility_mask, which Meta's PC runtime offers): the runtime's hidden-triangle
/// mesh, its vertices on each view's z = -1 plane, expanded into a triangle list for the renderer's lens mask
/// (Render/Frame/Renderer.Lens.cs: what's well inside it isn't shaded). Asked for again every few seconds (the area
/// changes with the lens spacing); an unchanged mesh keeps its array, so the renderer doesn't redraw its mask.
/// Written by Claude Opus 5.5, 2026-10-27.
/// </summary>
sealed unsafe class XrLensMask
{
    public const string Extension = "XR_KHR_visibility_mask";
    readonly KhrVisibilityMask? _ext;
    readonly Session _session;
    readonly Action<string> _log;
    readonly Vector2[]?[] _mesh = new Vector2[]?[2];
    long _next;

    public XrLensMask(XrRuntime rt, Session session, Action<string> log)
    {
        _session = session;
        _log = log;
        if (rt.Extensions.Contains(Extension) && rt.Api.TryGetInstanceExtension(null, rt.Instance, out KhrVisibilityMask vm)) _ext = vm;
        else log("OpenXR: no XR_KHR_visibility_mask: every pixel of the eyes is shaded");
    }

    /// <summary>This eye's hidden triangles (z = -1 tangents), or null.</summary>
    public Vector2[]? Hidden(int eye)
    {
        if (_ext == null) return null;
        long now = Stopwatch.GetTimestamp();
        if (now >= _next)
        {
            _next = now + 5 * Stopwatch.Frequency;
            for (uint i = 0; i < 2; i++) _mesh[i] = Fetch(i, _mesh[i]);
        }
        return _mesh[eye];
    }

    Vector2[]? Fetch(uint view, Vector2[]? last)
    {
        try
        {
            var m = new VisibilityMaskKHR { Type = StructureType.VisibilityMaskKhr };
            var r = _ext!.GetVisibilityMask(_session, ViewConfigurationType.PrimaryStereo, view, VisibilityMaskTypeKHR.VisibilityMaskTypeHiddenTriangleMeshKhr, &m);
            if (r != Result.Success || m.VertexCountOutput == 0 || m.IndexCountOutput < 3) return null;
            var verts = new Vector2f[m.VertexCountOutput];
            var idx = new uint[m.IndexCountOutput];
            fixed (Vector2f* pv = verts)
            fixed (uint* pi = idx)
            {
                m.VertexCapacityInput = (uint)verts.Length;
                m.IndexCapacityInput = (uint)idx.Length;
                m.Vertices = pv;
                m.Indices = pi;
                r = _ext.GetVisibilityMask(_session, ViewConfigurationType.PrimaryStereo, view, VisibilityMaskTypeKHR.VisibilityMaskTypeHiddenTriangleMeshKhr, &m);
            }
            if (r != Result.Success) return null;
            int n = (int)Math.Min(m.IndexCountOutput, (uint)idx.Length) / 3 * 3;
            var tris = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                uint i = idx[k];
                if (i >= verts.Length) return null;
                tris[k] = new Vector2(verts[i].X, verts[i].Y);
            }
            if (last != null && last.AsSpan().SequenceEqual(tris)) return last;
            _log($"OpenXR: eye {view}'s hidden area, {n / 3} triangles");
            return tris;
        }
        catch (Exception e)
        {
            _log($"OpenXR: the visibility mask: {e.Message}");
            return null;
        }
    }
}

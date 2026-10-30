namespace Sojourn.Render.Realtime;

public sealed partial class FrameRenderer
{
    /// <summary>A point on one face of a prim and its outward normal, in that prim's region's frame (RegionScene.FaceAim),
    /// or null while it isn't drawn yet. Render thread, between frames.</summary>
    public (Vector3 Point, Vector3 Normal)? FaceAim(ulong regionHandle, uint localId, int face)
    {
        CheckWorld();
        return _world.Regions.FirstOrDefault(r => r.Source.Handle == regionHandle)?.FaceAim(localId, face);
    }

    /// <summary>Every face of a prim as drawn (RegionScene.FaceList), or null while it isn't drawn yet. Render thread,
    /// between frames.</summary>
    public List<Sojourn.Render.World.FaceShape>? FaceList(ulong regionHandle, uint localId)
    {
        CheckWorld();
        return _world.Regions.FirstOrDefault(r => r.Source.Handle == regionHandle)?.FaceList(localId);
    }

    /// <summary>Whether a prim is drawn with its triangles kept, so picks land on its surface. Render thread, between frames.</summary>
    public bool HasTriangles(ulong regionHandle, uint localId)
    {
        CheckWorld();
        return _world.Regions.FirstOrDefault(r => r.Source.Handle == regionHandle)?.HasTriangles(localId) == true;
    }
}

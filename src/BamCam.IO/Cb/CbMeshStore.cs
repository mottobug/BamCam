using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.IO.Cb;

/// <summary>
/// The meshes of undo snapshots: a snapshot refers to a mesh instead of containing it as text. With a million triangles
/// that text takes seconds and about 150 MB per undo step, although moving or adding an object does not change the mesh.
/// </summary>
public sealed class CbMeshStore
{
    private readonly Dictionary<List<int>, int> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly List<(List<Point3> Vertices, List<int> Faces)> _meshes = [];

    public int Count => _meshes.Count;

    /// <summary>Forgets all meshes, when no undo step refers to them any more.</summary>
    public void Clear()
    {
        _ids.Clear();
        _meshes.Clear();
    }

    internal int Add(SurfaceEntity surface)
    {
        if (_ids.TryGetValue(surface.Faces, out var id)) return id;
        id = _meshes.Count;
        _meshes.Add((surface.Vertices, surface.Faces));
        _ids[surface.Faces] = id;
        return id;
    }

    internal void Restore(SurfaceEntity surface, int id)
    {
        var (vertices, faces) = _meshes[id];
        surface.ShareMesh(vertices, faces);
    }
}

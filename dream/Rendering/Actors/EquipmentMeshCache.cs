using Dream.Rendering.Core;
using Dream.Rendering.World;

namespace Dream.Rendering.Actors;

// The GL buffers behind EquipmentShapeLibrary: one StaticMeshBuffer per EquipmentShape, built on
// first use and owned until Dispose.
//
// Each shape's mesh is pure data and never changes, so a buffer is uploaded exactly once per shape
// per cache, the first time a part asks to be drawn with it. A figure wearing nothing but boxes
// therefore still costs one buffer, and a shape nothing in the world uses costs none. Both
// equipment renderers keep their own cache for the same reason they duplicate PrepareState: the
// held path and the worn path stay independent, and each disposes exactly what it made.
//
// Render thread only: Get creates GL objects, Dispose deletes them (the same contract as the
// _cube field this replaces).
public sealed class EquipmentMeshCache : IDisposable
{
    private readonly StaticMeshBuffer?[] _buffers = new StaticMeshBuffer?[EquipmentShapeLibrary.ShapeCount];
    private bool _disposed;

    // The buffer for this shape, built on first use. An out-of-range shape (content from a newer
    // build) falls back to the Box, which is the legacy unit cube, so a part is never invisible.
    public StaticMeshBuffer Get(EquipmentShape shape)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int index = (int)shape;
        if (index < 0 || index >= _buffers.Length)
        {
            index = (int)EquipmentShape.Box;
            shape = EquipmentShape.Box;
        }

        StaticMeshBuffer? buffer = _buffers[index];
        if (buffer == null)
        {
            StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);
            buffer = new StaticMeshBuffer(mesh);
            _buffers[index] = buffer;
        }

        return buffer;
    }

    // How many shapes have been built. Diagnostics and tests only.
    public int BuiltCount
    {
        get
        {
            int built = 0;
            for (int i = 0; i < _buffers.Length; i++)
            {
                if (_buffers[i] != null)
                {
                    built++;
                }
            }

            return built;
        }
    }

    // Every built buffer is released and the slot cleared, so a second Dispose does nothing.
    public void Dispose()
    {
        _disposed = true;
        for (int i = 0; i < _buffers.Length; i++)
        {
            StaticMeshBuffer? buffer = _buffers[i];
            _buffers[i] = null;
            buffer?.Dispose();
        }
    }
}

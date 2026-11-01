using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Effects;

// The per-instance VBO of the effects pass, following InstanceMatrixBuffer: the GL buffer is
// created on the first upload and kept, and each upload orphans the storage (glBufferData with no
// data) before writing with glBufferSubData, so a draw still reading the last frame's instances
// never stalls it. Unlike the matrix buffer its capacity is fixed at FxLimits.MaxInstances, so
// the storage is allocated once and never grows.
//
// Render thread only. The VAO that names this buffer is owned by FxRenderer, which disposes both.
public sealed class FxInstanceBuffer : IDisposable
{
    private int _handle;
    private bool _disposed;

    public int Handle => _handle;

    public int Capacity => FxLimits.MaxInstances;

    public int UploadCount { get; private set; }

    // Writes the first `count` instances and leaves the buffer bound to GL_ARRAY_BUFFER.
    public void Upload(FxInstance[] instances, int count)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Math.Min(instances.Length, Capacity));
        if (count == 0)
        {
            return;
        }

        if (_handle == 0)
        {
            _handle = GL.GenBuffer();
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _handle);
        GL.BufferData(BufferTarget.ArrayBuffer, Capacity * FxInstance.Stride, IntPtr.Zero, BufferUsageHint.StreamDraw);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, count * FxInstance.Stride, instances);
        UploadCount++;
    }

    // Points the three instance attributes of the currently bound VAO at this buffer.
    public void BindAttributes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle == 0)
        {
            _handle = GL.GenBuffer();
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _handle);
        GL.EnableVertexAttribArray(FxInstance.PositionHalfSizeLocation);
        GL.VertexAttribPointer(FxInstance.PositionHalfSizeLocation, 4, VertexAttribPointerType.Float, false, FxInstance.Stride, 0);
        GL.VertexAttribDivisor(FxInstance.PositionHalfSizeLocation, 1);
        GL.EnableVertexAttribArray(FxInstance.ColorLocation);
        GL.VertexAttribPointer(FxInstance.ColorLocation, 4, VertexAttribPointerType.UnsignedByte, true, FxInstance.Stride, FxInstance.ColorOffset);
        GL.VertexAttribDivisor(FxInstance.ColorLocation, 1);
        GL.EnableVertexAttribArray(FxInstance.RotationFrameBiasLocation);
        GL.VertexAttribPointer(FxInstance.RotationFrameBiasLocation, 4, VertexAttribPointerType.Float, false, FxInstance.Stride, FxInstance.RotationOffset);
        GL.VertexAttribDivisor(FxInstance.RotationFrameBiasLocation, 1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != 0)
        {
            GL.DeleteBuffer(_handle);
            _handle = 0;
        }
    }
}

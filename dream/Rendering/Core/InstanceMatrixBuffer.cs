using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

// A persistent per-instance model-matrix VBO for instanced draws. The GL buffer is created on the
// first upload and kept for the owner's lifetime; its storage only grows (to the next power-of-two
// style step), so a frame that draws as many instances as an earlier one allocates nothing, on the
// CPU or in the driver. Each upload orphans the current storage (glBufferData with no data, same
// size) before writing with glBufferSubData, so a draw still reading the previous contents never
// stalls the upload.
//
// The matrices feed the `instanceModel` attribute of cubeVertex.glsl (locations 6..9, one vec4
// column each, divisor 1). Attribute pointers are VAO state, so BindAttributes runs with the
// target VAO bound; the pointers keep naming this buffer across orphaning and growth.
//
// Render thread only. Disposed by its owner.
public sealed class InstanceMatrixBuffer : IDisposable
{
    public const int FirstAttributeLocation = 6;
    public const int AttributeColumnCount = 4;
    public const int MinimumCapacity = 16;
    public static readonly int MatrixStride = Unsafe.SizeOf<Matrix4>();

    private int _handle;
    private int _capacity;
    private bool _disposed;

    public int Handle => _handle;

    // In matrices. Never shrinks.
    public int Capacity => _capacity;

    // How many times the GPU storage was (re)allocated to a larger size.
    public int GrowthCount { get; private set; }

    public int UploadCount { get; private set; }

    // The capacity needed to hold `required` matrices: unchanged when it already fits, otherwise
    // at least double the current one, so a slowly growing scene reallocates O(log n) times.
    public static int ComputeCapacity(int currentCapacity, int required)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        if (required <= currentCapacity)
        {
            return currentCapacity;
        }

        long doubled = Math.Max(MinimumCapacity, (long)currentCapacity * 2);
        return (int)Math.Min(int.MaxValue / MatrixStride, Math.Max(required, doubled));
    }

    // Writes the first `count` matrices into the buffer and leaves it bound to GL_ARRAY_BUFFER.
    public void Upload(Matrix4[] matrices, int count)
    {
        ArgumentNullException.ThrowIfNull(matrices);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, matrices.Length);
        if (count == 0)
        {
            return;
        }

        if (_handle == 0)
        {
            _handle = GL.GenBuffer();
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _handle);
        int capacity = ComputeCapacity(_capacity, count);
        if (capacity != _capacity)
        {
            _capacity = capacity;
            GrowthCount++;
        }

        // Orphan: same size, no data. The driver hands back fresh storage instead of waiting for
        // draws that still read the old contents. No managed memory is allocated.
        GL.BufferData(BufferTarget.ArrayBuffer, _capacity * MatrixStride, IntPtr.Zero, BufferUsageHint.StreamDraw);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, count * MatrixStride, matrices);
        UploadCount++;
    }

    // Points the instanceModel attribute of the currently bound VAO at this buffer.
    public void BindAttributes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _handle);
        for (int column = 0; column < AttributeColumnCount; column++)
        {
            int attributeLocation = FirstAttributeLocation + column;
            GL.EnableVertexAttribArray(attributeLocation);
            GL.VertexAttribPointer(
                attributeLocation,
                4,
                VertexAttribPointerType.Float,
                normalized: false,
                MatrixStride,
                column * 4 * sizeof(float));
            GL.VertexAttribDivisor(attributeLocation, 1);
        }
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

        _capacity = 0;
    }
}

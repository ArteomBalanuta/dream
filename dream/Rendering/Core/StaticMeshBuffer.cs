using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dream.Rendering.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public sealed class StaticMeshBuffer : IDisposable
{
    public const int VertexStride = 32;
    public const int PositionOffset = 0;
    public const int NormalOffset = 12;
    public const int TexCoordOffset = 24;

    // Must match the attribute locations declared in shaders/cubeVertex.glsl.
    public const int PositionAttributeLocation = 0;
    public const int NormalAttributeLocation = 1;
    public const int TexCoordAttributeLocation = 3;
    private readonly int _vertexArrayObject;
    private readonly int _vertexBufferObject;
    private readonly int _elementBufferObject;
    private int _indexCount;
    private bool _disposed;

    static StaticMeshBuffer()
    {
        VerifyVertexLayout();
    }

    // Creates the VAO, VBO and EBO once; the attribute layout is recorded in the VAO here and
    // never changes, so later uploads only replace buffer contents.
    public StaticMeshBuffer(StaticWorldChunkMesh mesh)
    {
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();
        _elementBufferObject = GL.GenBuffer();

        GL.BindVertexArray(_vertexArrayObject);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _elementBufferObject);
        GL.EnableVertexAttribArray(PositionAttributeLocation);
        GL.VertexAttribPointer(PositionAttributeLocation, 3, VertexAttribPointerType.Float, false, VertexStride, PositionOffset);
        GL.EnableVertexAttribArray(NormalAttributeLocation);
        GL.VertexAttribPointer(NormalAttributeLocation, 3, VertexAttribPointerType.Float, false, VertexStride, NormalOffset);
        GL.EnableVertexAttribArray(TexCoordAttributeLocation);
        GL.VertexAttribPointer(TexCoordAttributeLocation, 2, VertexAttribPointerType.Float, false, VertexStride, TexCoordOffset);
        GL.BindVertexArray(0);

        Upload(mesh);
    }

    public int IndexCount => _indexCount;

    // Replaces the buffer contents in place: the same VAO/VBO/EBO handles, no new GL objects.
    public void Upload(StaticWorldChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReadOnlySpan<StaticWorldVertex> vertices = AsReadOnlySpan(mesh.Vertices);
        ReadOnlySpan<uint> indices = AsReadOnlySpan(mesh.Indices);

        // The element buffer binding is VAO state, so bind this VAO before touching the EBO.
        GL.BindVertexArray(_vertexArrayObject);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            vertices.Length * VertexStride,
            ref MemoryMarshal.GetReference(vertices),
            BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _elementBufferObject);
        GL.BufferData(
            BufferTarget.ElementArrayBuffer,
            indices.Length * sizeof(uint),
            ref MemoryMarshal.GetReference(indices),
            BufferUsageHint.StaticDraw);

        GL.BindVertexArray(0);
        _indexCount = indices.Length;
    }

    public void Draw()
    {
        if (_indexCount == 0)
        {
            return;
        }

        GL.BindVertexArray(_vertexArrayObject);
        GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        GL.DeleteBuffer(_elementBufferObject);
        GL.DeleteBuffer(_vertexBufferObject);
        GL.DeleteVertexArray(_vertexArrayObject);
        _disposed = true;
    }

    // Vertices are uploaded as raw memory, so the struct layout must match the attribute offsets above.
    public static void VerifyVertexLayout()
    {
        if (Unsafe.SizeOf<StaticWorldVertex>() != VertexStride)
        {
            throw new InvalidOperationException(
                $"StaticWorldVertex is {Unsafe.SizeOf<StaticWorldVertex>()} bytes; the GL layout expects {VertexStride}.");
        }

        StaticWorldVertex probe = new(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f), new Vector2(7f, 8f));
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<StaticWorldVertex>(in probe));
        if (MemoryMarshal.Read<Vector3>(bytes.Slice(PositionOffset)) != probe.Position ||
            MemoryMarshal.Read<Vector3>(bytes.Slice(NormalOffset)) != probe.Normal ||
            MemoryMarshal.Read<Vector2>(bytes.Slice(TexCoordOffset)) != probe.TexCoord)
        {
            throw new InvalidOperationException("StaticWorldVertex field offsets do not match the GL attribute offsets.");
        }
    }

    private static ReadOnlySpan<T> AsReadOnlySpan<T>(IReadOnlyList<T> items)
    {
        return items switch
        {
            List<T> list => CollectionsMarshal.AsSpan(list),
            T[] array => array,
            _ => items.ToArray()
        };
    }
}

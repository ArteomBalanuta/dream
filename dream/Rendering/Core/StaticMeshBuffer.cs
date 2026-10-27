using Dream.Rendering.World;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Core;

public sealed class StaticMeshBuffer : IDisposable
{
    private const int FloatsPerVertex = 8;

    // Must match the attribute locations declared in shaders/cubeVertex.glsl.
    public const int PositionAttributeLocation = 0;
    public const int NormalAttributeLocation = 1;
    public const int TexCoordAttributeLocation = 3;
    private readonly int _vertexArrayObject;
    private readonly int _vertexBufferObject;
    private readonly int _elementBufferObject;
    private readonly int _indexCount;
    private bool _disposed;

    public StaticMeshBuffer(StaticWorldChunkMesh mesh)
    {
        _indexCount = mesh.Indices.Count;
        _vertexArrayObject = GL.GenVertexArray();
        _vertexBufferObject = GL.GenBuffer();
        _elementBufferObject = GL.GenBuffer();

        float[] vertices = FlattenVertices(mesh.Vertices);
        uint[] indices = mesh.Indices.ToArray();

        GL.BindVertexArray(_vertexArrayObject);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBufferObject);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            vertices.Length * sizeof(float),
            vertices,
            BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _elementBufferObject);
        GL.BufferData(
            BufferTarget.ElementArrayBuffer,
            indices.Length * sizeof(uint),
            indices,
            BufferUsageHint.StaticDraw);

        int stride = FloatsPerVertex * sizeof(float);
        GL.EnableVertexAttribArray(PositionAttributeLocation);
        GL.VertexAttribPointer(PositionAttributeLocation, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(NormalAttributeLocation);
        GL.VertexAttribPointer(NormalAttributeLocation, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(TexCoordAttributeLocation);
        GL.VertexAttribPointer(TexCoordAttributeLocation, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

        GL.BindVertexArray(0);
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

    private static float[] FlattenVertices(IReadOnlyList<StaticWorldVertex> vertices)
    {
        float[] result = new float[vertices.Count * FloatsPerVertex];
        for (int i = 0; i < vertices.Count; i++)
        {
            StaticWorldVertex vertex = vertices[i];
            int offset = i * FloatsPerVertex;
            result[offset] = vertex.Position.X;
            result[offset + 1] = vertex.Position.Y;
            result[offset + 2] = vertex.Position.Z;
            result[offset + 3] = vertex.Normal.X;
            result[offset + 4] = vertex.Normal.Y;
            result[offset + 5] = vertex.Normal.Z;
            result[offset + 6] = vertex.TexCoord.X;
            result[offset + 7] = vertex.TexCoord.Y;
        }

        return result;
    }
}

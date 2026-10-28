using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public partial class Render
{
    // Common method to set transformation uniforms.
    private void SetTransforms(Shader shader, Matrix4 model, Matrix4 view)
    {
        shader.SetModelMatrix(model);
        shader.SetMatrix4("view", view);
        shader.SetMatrix4("projection", _projection);
    }

    private void DrawCubeInternal(Shader shader, Matrix4 model, Matrix4 view)
    {
        shader.Use();
        SetTransforms(shader, model, view);
        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
    }

    private void DrawCubeWireframe(Camera camera, Matrix4 modelMatrix)
    {
        Shader shader = _shaders.TryGet("default", out var s) ? s : _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(1.0f, 1.0f, 1.0f)); // white
        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        // Enable wireframe mode.
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
    }

    public void DrawCollisionShape(Matrix4 modelMatrix, Camera camera)
    {
        Shader shader = _shaders["default"];
        shader.Use();
        shader.SetVector3("color", new Vector3(1.0f, 0.0f, 0.0f)); // Red color for collision

        SetTransforms(shader, modelMatrix, camera.GetViewMatrix());

        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
        GL.BindVertexArray(_vertexArrayObject);
        DrawCubeFaceFans();
        GL.BindVertexArray(0);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
    }

    private void DrawCubeFaceFans()
    {
        foreach ((int first, int count) in CubeFaceDrawRanges)
        {
            GL.DrawArrays(PrimitiveType.TriangleFan, first, count);
            RenderCounters.CountDraw();
        }
    }

    private static (int First, int Count)[] CreateCubeFaceDrawRanges(int vertexCount)
    {
        const int faceVertexCount = 4;
        if (vertexCount % faceVertexCount != 0)
        {
            throw new ArgumentException("Cube vertex count must contain complete four-vertex faces.", nameof(vertexCount));
        }

        (int First, int Count)[] ranges = new (int First, int Count)[vertexCount / faceVertexCount];
        for (int face = 0; face < ranges.Length; face++)
        {
            ranges[face] = (face * faceVertexCount, faceVertexCount);
        }

        return ranges;
    }

    public void DrawCollisionBody(BulletSharp.RigidBody body, Camera camera)
    {
        if (body.MotionState == null)
        {
            return;
        }

        body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
        if (body.CollisionShape is BulletSharp.BoxShape boxShape)
        {
            DrawCollisionShape(
                CubeTransforms.CreateCollisionDebugModelMatrix(
                    transform,
                    boxShape.HalfExtentsWithMargin.ToOpenTK()),
                camera);
            return;
        }

        DrawCollisionShape(CubeTransforms.CreateCollisionDebugModelMatrix(transform), camera);
    }
}

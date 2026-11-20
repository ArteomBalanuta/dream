using Dream.Rendering.Models;
using Dream.Rendering.Post;
using Dream.Rendering.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public partial class Render
{
    // The warm-up camera: a perspective view down -Z at the origin, which holds the skinned warm-up triangle (z = 0) and
    // the fallback cube (the unit cube at the origin's +X/+Y/+Z octant) in frame, so every draw has real fragments.
    public static readonly Matrix4 WarmUpView = Matrix4.LookAt(new Vector3(0f, 0f, 3f), Vector3.Zero, Vector3.UnitY);

    public static readonly Matrix4 WarmUpProjection = Matrix4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 100f);

    // One real draw per registry program and per vertex layout the program is drawn with, so the driver's draw-time
    // specialisation happens now. The registry is enumerated, so a program added to InitializeShaders or a MASK variant
    // attached by LoadWorldShader is warmed without touching this method. See ShaderWarmup for the rules.
    //
    // The layouts are the shipped ones, never restated here:
    // - static: StaticMeshBuffer over the engine's own terrain fallback cube mesh (position, normal, texcoord, locations 0/1/3);
    // - skinned: AnimatedModel's CreateBuffers over a one-triangle primitive (locations 0..5);
    // - each of those again with InstanceMatrixBuffer.BindAttributes (locations 6..9, divisor 1), because the vertex
    //   attribute set is part of the state the driver specialises on and the instanced draws enable more of it.
    // A program without the instanceModel attribute (the default debug program) is a position-only program: it gets the
    // static layout and the debug cube's own VAO.
    //
    // `scene` is the offscreen colour target (same format and sample count as the real scene target). The shadow caster
    // program draws into the sun shadow map instead, as the real pass does.
    public void WarmUpWorldPrograms(ShaderWarmupSession session, SceneTarget scene)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scene);

        using WarmUpGeometry geometry = new();
        string[] names = [.. _shaders.Keys];
        Array.Sort(names, StringComparer.Ordinal);

        // Shadow caster first, into the sun map: depth only, no colour attachment, state set by BeginSun/End.
        if (_shadowTargets is { Enabled: true } shadowTargets && _shaders.TryGet(ShadowDepthShaderName, out Shader? caster))
        {
            session.Step(ShadowDepthShaderName, () =>
            {
                shadowTargets.BeginSun();
                try
                {
                    WarmProgramFamily(session, ShadowDepthShaderName, caster, geometry);
                }
                finally
                {
                    shadowTargets.End();
                }
            });
        }

        BeginWarmUpColourPass(scene);

        foreach (string name in names)
        {
            if (name == ShadowDepthShaderName || !_shaders.TryGet(name, out Shader? program))
            {
                continue;
            }

            session.Step(name, () => WarmProgramFamily(session, name, program, geometry));
        }
    }

    // Binds the offscreen scene target, clears it and sets the world pass's state: opaque, depth tested, back faces culled
    // (AnimatedModel.BeginRender). The sky, effects and mist draws that follow restore this state themselves.
    public static void BeginWarmUpColourPass(SceneTarget scene)
    {
        scene.BindForScene();
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
    }

    // The opaque program and, if it has one, its MASK variant (a separate program the driver specialises separately).
    private void WarmProgramFamily(ShaderWarmupSession session, string name, Shader program, WarmUpGeometry geometry)
    {
        WarmProgram(session, name, program, geometry);
        if (program.AlphaMaskVariant is { } variant)
        {
            WarmProgram(session, name + "/mask", variant, geometry);
        }
    }

    private void WarmProgram(ShaderWarmupSession session, string label, Shader program, WarmUpGeometry geometry)
    {
        program.Use();
        program.SetModelMatrix(Matrix4.Identity);
        program.SetMatrix4("view", WarmUpView);
        program.SetMatrix4("projection", WarmUpProjection);
        bool isWorldProgram = GL.GetAttribLocation(program.Handle, "instanceModel") >= 0;
        try
        {
            if (!isWorldProgram)
            {
                geometry.StaticPlain.Draw();
                session.CountDraw(program.Handle, label + " static");
                GL.BindVertexArray(_vertexArrayObject);
                DrawCubeFaceFans();
                GL.BindVertexArray(0);
                session.CountDraw(program.Handle, label + " cube");
                return;
            }

            program.SetBool("useSkinning", false);
            program.SetBool("useInstancing", false);
            geometry.StaticPlain.Draw();
            session.CountDraw(program.Handle, label + " static");
            geometry.Skinned.Draw(null);
            session.CountDraw(program.Handle, label + " skinned");

            program.SetBool("useInstancing", true);
            geometry.StaticInstanced.DrawInstanced(geometry.Instances, 1);
            session.CountDraw(program.Handle, label + " static+instanced");
            geometry.SkinnedInstanced.Draw(geometry.Instances);
            session.CountDraw(program.Handle, label + " skinned+instanced");
        }
        finally
        {
            // Every real draw sets these itself (AnimatedModel.BeginRender), but the shipped default is off and a
            // program must leave the warm-up as it entered.
            if (isWorldProgram)
            {
                program.SetBool("useInstancing", false);
                program.SetBool("useSkinning", false);
            }

            GL.BindVertexArray(0);
        }
    }

    // The probe meshes. Separate VAOs for the plain and the instanced layouts, so the plain draw never inherits the
    // instance attributes that the instanced draw enables on its VAO.
    private sealed class WarmUpGeometry : IDisposable
    {
        public readonly StaticMeshBuffer StaticPlain = new(TerrainFallbackCubeMesh.Create());
        public readonly StaticMeshBuffer StaticInstanced = new(TerrainFallbackCubeMesh.Create());
        public readonly AnimatedModel.WarmUpMesh Skinned = AnimatedModel.CreateWarmUpMesh();
        public readonly AnimatedModel.WarmUpMesh SkinnedInstanced = AnimatedModel.CreateWarmUpMesh();
        public readonly InstanceMatrixBuffer Instances = new();

        public WarmUpGeometry()
        {
            Instances.Upload([Matrix4.Identity], 1);
        }

        public void Dispose()
        {
            StaticPlain.Dispose();
            StaticInstanced.Dispose();
            Skinned.Release();
            SkinnedInstanced.Release();
            Instances.Dispose();
        }
    }
}

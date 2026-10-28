using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// The GL uniform buffer behind the FrameLighting block (shaders/lighting.glsl). One buffer holds
// two ranges of FrameLighting.SizeInBytes each:
// - the world range at offset 0: world-space directions, CameraPosition.xyz = the camera;
// - the view-model range at ViewModelOffset (144 rounded up to GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT):
//   the view model draws with view = identity, so its fragPos is in eye space. Its sun and moon
//   directions are rotated into eye space, its camera sits at the origin, and its ambient never
//   drops below ViewModelAmbientFloor so the hands stay visible at night.
// Both ranges are written once per frame (Upload) with BufferSubData of a stack struct, so a frame
// allocates nothing. The binding point is global context state: the world range stays bound
// except while ViewModelRenderer draws.
public sealed class FrameLightingBuffer : IDisposable
{
    // Sits a little under the dark-night up-facing light at the default night brightness (about 0.064
    // luma), so the hands read without glowing against the terrain; a held torch lights them anyway.
    public const float ViewModelAmbientFloor = 0.05f;

    private int _handle;

    public FrameLightingBuffer()
    {
        GLUtils.AssertRenderThread("FrameLighting buffer creation");
        GL.GetInteger(GetPName.UniformBufferOffsetAlignment, out int alignment);
        ViewModelOffset = RoundUp(FrameLighting.SizeInBytes, alignment);

        _handle = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferData(BufferTarget.UniformBuffer, ViewModelOffset + FrameLighting.SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        GLUtils.CheckGLError("FrameLighting buffer creation");
    }

    // Link-time setup, called by Shader after linking: GL 3.3 core has no layout(binding = N) on
    // a uniform block, so a program that declares the FrameLighting block gets it pointed at
    // FrameLighting.BindingPoint here. This is program state set once, not a per-draw upload.
    // Returns false when the program has no such block.
    public static bool BindProgramBlock(int program)
    {
        int block = GL.GetUniformBlockIndex(program, FrameLighting.BlockName);
        if (block < 0)
        {
            return false;
        }

        GL.UniformBlockBinding(program, block, FrameLighting.BindingPoint);
        return true;
    }

    // Byte offset of the view-model range.
    public int ViewModelOffset { get; }

    // `size` rounded up to a multiple of `alignment` (alignment <= 0 is treated as 1).
    public static int RoundUp(int size, int alignment)
    {
        if (alignment <= 1)
        {
            return size;
        }

        return (size + alignment - 1) / alignment * alignment;
    }

    // The world range: the frame's lighting with the camera position filled in (fog start kept).
    public static FrameLighting ForWorld(in FrameLighting lighting, Vector3 cameraPosition)
    {
        FrameLighting world = lighting;
        world.CameraPosition = new Vector4(cameraPosition, lighting.CameraPosition.W);
        return world;
    }

    // The view-model range: directions rotated by mat3(view) into eye space (intensities kept),
    // the camera at the eye-space origin (fog start kept), ambient floored per channel.
    public static FrameLighting ForViewModel(in FrameLighting lighting, in Matrix4 view)
    {
        FrameLighting viewModel = lighting;
        viewModel.SunDirection = new Vector4(ToEyeSpace(lighting.SunDirection.Xyz, in view), lighting.SunDirection.W);
        viewModel.MoonDirection = new Vector4(ToEyeSpace(lighting.MoonDirection.Xyz, in view), lighting.MoonDirection.W);
        viewModel.CameraPosition = new Vector4(0f, 0f, 0f, lighting.CameraPosition.W);
        viewModel.AmbientSky = FloorAmbient(lighting.AmbientSky);
        viewModel.AmbientGround = FloorAmbient(lighting.AmbientGround);
        return viewModel;
    }

    // Writes both ranges and binds the world range.
    public void Upload(in FrameLighting lighting, Vector3 cameraPosition, in Matrix4 view)
    {
        FrameLighting world = ForWorld(in lighting, cameraPosition);
        FrameLighting viewModel = ForViewModel(in lighting, in view);

        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, FrameLighting.SizeInBytes, ref world);
        GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)ViewModelOffset, FrameLighting.SizeInBytes, ref viewModel);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        RenderCounters.CountUniformUpload();
        BindWorld();
    }

    public void BindWorld()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, FrameLighting.BindingPoint, _handle, IntPtr.Zero, FrameLighting.SizeInBytes);
    }

    public void BindViewModel()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, FrameLighting.BindingPoint, _handle, (IntPtr)ViewModelOffset, FrameLighting.SizeInBytes);
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("FrameLighting buffer disposal");
        GL.DeleteBuffer(_handle);
        _handle = 0;
    }

    // OpenTK uses row vectors, and a matrix uploaded untransposed reads as its transpose in GLSL,
    // so GLSL's mat3(view) * d is d * mat3(view) here.
    private static Vector3 ToEyeSpace(Vector3 direction, in Matrix4 view) => Vector3.TransformVector(direction, view);

    private static Vector4 FloorAmbient(Vector4 ambient) => new(
        MathF.Max(ambient.X, ViewModelAmbientFloor),
        MathF.Max(ambient.Y, ViewModelAmbientFloor),
        MathF.Max(ambient.Z, ViewModelAmbientFloor),
        ambient.W);
}

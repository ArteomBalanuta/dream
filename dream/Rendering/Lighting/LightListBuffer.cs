using Dream.Rendering.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// The GL uniform buffer behind the LightList block (shaders/pointLights.glsl). Like
// FrameLightingBuffer, one buffer holds two ranges of LightList.SizeInBytes each:
// - the world range at offset 0: world-space light positions;
// - the view-model range at ViewModelOffset (528 rounded up to GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT):
//   the same lights with eye-space positions, because the view model draws with view = identity.
// Both ranges are written once per frame (Upload, counted as one uniform upload) with
// BufferSubData of stack structs, so a frame allocates nothing. The world range stays bound at
// LightList.BindingPoint except while ViewModelRenderer draws.
public sealed class LightListBuffer : IDisposable
{
    private int _handle;

    public LightListBuffer()
    {
        GLUtils.AssertRenderThread("LightList buffer creation");
        GL.GetInteger(GetPName.UniformBufferOffsetAlignment, out int alignment);
        ViewModelOffset = FrameLightingBuffer.RoundUp(LightList.SizeInBytes, alignment);

        _handle = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferData(BufferTarget.UniformBuffer, ViewModelOffset + LightList.SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        GLUtils.CheckGLError("LightList buffer creation");
    }

    // Byte offset of the view-model range.
    public int ViewModelOffset { get; }

    // Link-time setup, called by Shader after linking (GL 3.3 core has no layout(binding = N) on a
    // block). Returns false when the program has no LightList block.
    public static bool BindProgramBlock(int program)
    {
        int block = GL.GetUniformBlockIndex(program, LightList.BlockName);
        if (block < 0)
        {
            return false;
        }

        GL.UniformBlockBinding(program, block, LightList.BindingPoint);
        return true;
    }

    // Writes the world range and the view-model range (world moved into eye space by `view`),
    // then binds the world range.
    public void Upload(in LightList world, in Matrix4 view)
    {
        LightList viewModel = default;
        LightSelection.ToEyeSpace(in world, in view, ref viewModel);
        LightList worldCopy = world;

        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, LightList.SizeInBytes, ref worldCopy);
        GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)ViewModelOffset, LightList.SizeInBytes, ref viewModel);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        RenderCounters.CountUniformUpload();
        BindWorld();
    }

    public void BindWorld()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, LightList.BindingPoint, _handle, IntPtr.Zero, LightList.SizeInBytes);
    }

    public void BindViewModel()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, LightList.BindingPoint, _handle, (IntPtr)ViewModelOffset, LightList.SizeInBytes);
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("LightList buffer disposal");
        GL.DeleteBuffer(_handle);
        _handle = 0;
    }
}

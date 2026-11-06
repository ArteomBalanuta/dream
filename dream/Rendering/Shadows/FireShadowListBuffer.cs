using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Shadows;

// The GL uniform buffer behind the FireShadowList block (shaders/fireShadows.glsl), laid out like LightListBuffer:
// - the world range at offset 0: the frame's casting fires, world-space positions;
// - the view-model range at ViewModelOffset: ALWAYS count 0. The view model draws with an eye-space fragPos and
//   eye-space lights, while the atlas holds WORLD-space depth, so a lookup there would read an arbitrary tile.
//   Count 0 makes dreamFireVisibility return a literal 1.0 with no fetch (the same reason FrameLightingBuffer.ForViewModel
//   zeroes the sun shadow).
// Both ranges are written once per frame (Upload, one counted uniform upload) from stack structs, so a frame
// allocates nothing. The world range stays bound at FireShadowList.BindingPoint except while ViewModelRenderer draws.
public sealed class FireShadowListBuffer : IDisposable
{
    private int _handle;

    public FireShadowListBuffer()
    {
        GLUtils.AssertRenderThread("FireShadowList buffer creation");
        GL.GetInteger(GetPName.UniformBufferOffsetAlignment, out int alignment);
        ViewModelOffset = FrameLightingBuffer.RoundUp(FireShadowList.SizeInBytes, alignment);

        _handle = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferData(BufferTarget.UniformBuffer, ViewModelOffset + FireShadowList.SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        GLUtils.CheckGLError("FireShadowList buffer creation");
    }

    // Byte offset of the view-model range.
    public int ViewModelOffset { get; }

    // Link-time setup, called by Shader after linking (GL 3.3 core has no layout(binding = N) on a block).
    // Without it the block would alias binding point 0 and read FrameLighting. Returns false when the program has no such block.
    public static bool BindProgramBlock(int program)
    {
        int block = GL.GetUniformBlockIndex(program, FireShadowList.BlockName);
        if (block < 0)
        {
            return false;
        }

        GL.UniformBlockBinding(program, block, FireShadowList.BindingPoint);
        return true;
    }

    // Writes the world range from the frame and a count-0 view-model range, then binds the world range.
    public void Upload(in ShadowFrame frame)
    {
        FireShadowList world = FireShadowList.FromFrame(in frame);
        Upload(in world);
    }

    public void Upload(in FireShadowList world)
    {
        FireShadowList worldCopy = world;
        FireShadowList viewModel = default;

        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, FireShadowList.SizeInBytes, ref worldCopy);
        GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)ViewModelOffset, FireShadowList.SizeInBytes, ref viewModel);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        RenderCounters.CountUniformUpload();
        BindWorld();
    }

    public void BindWorld()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, FireShadowList.BindingPoint, _handle, IntPtr.Zero, FireShadowList.SizeInBytes);
    }

    public void BindViewModel()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, FireShadowList.BindingPoint, _handle, (IntPtr)ViewModelOffset, FireShadowList.SizeInBytes);
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("FireShadowList buffer disposal");
        GL.DeleteBuffer(_handle);
        _handle = 0;
    }
}

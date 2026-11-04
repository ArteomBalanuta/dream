using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Dream.Rendering.Mist;

// The GL uniform buffer behind the BoundaryMist block (shaders/boundaryMist.glsl), laid out like
// LightListBuffer: one buffer, two ranges of BoundaryMistFrame.SizeInBytes each. The world range
// at offset 0 carries the strength; the view-model range at ViewModelOffset (48 rounded up to
// GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT) has strength 0. Both are written once per frame with
// BufferSubData of stack structs, so a frame allocates nothing. The world range stays bound at
// BoundaryMistFrame.BindingPoint except while ViewModelRenderer draws.
public sealed class BoundaryMistBuffer : IDisposable
{
    private int _handle;

    public BoundaryMistBuffer()
    {
        GLUtils.AssertRenderThread("BoundaryMist buffer creation");
        GL.GetInteger(GetPName.UniformBufferOffsetAlignment, out int alignment);
        ViewModelOffset = FrameLightingBuffer.RoundUp(BoundaryMistFrame.SizeInBytes, alignment);

        _handle = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferData(BufferTarget.UniformBuffer, ViewModelOffset + BoundaryMistFrame.SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        GLUtils.CheckGLError("BoundaryMist buffer creation");
    }

    // Byte offset of the view-model range.
    public int ViewModelOffset { get; }

    // Link-time setup, called by Shader after linking (GL 3.3 core has no layout(binding = N) on a
    // block). Returns false when the program has no BoundaryMist block.
    public static bool BindProgramBlock(int program)
    {
        int block = GL.GetUniformBlockIndex(program, BoundaryMistFrame.BlockName);
        if (block < 0)
        {
            return false;
        }

        GL.UniformBlockBinding(program, block, BoundaryMistFrame.BindingPoint);
        return true;
    }

    // Writes both ranges, then binds the world range.
    public void Upload(in BoundaryMistInput input, Vector3 cameraFront)
    {
        BoundaryMistFrame world = BoundaryMistFrame.ForWorld(in input, cameraFront);
        BoundaryMistFrame viewModel = BoundaryMistFrame.ForViewModel(in input, cameraFront);

        GL.BindBuffer(BufferTarget.UniformBuffer, _handle);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, BoundaryMistFrame.SizeInBytes, ref world);
        GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)ViewModelOffset, BoundaryMistFrame.SizeInBytes, ref viewModel);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        RenderCounters.CountUniformUpload();
        BindWorld();
    }

    public void BindWorld()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, BoundaryMistFrame.BindingPoint, _handle, IntPtr.Zero, BoundaryMistFrame.SizeInBytes);
    }

    public void BindViewModel()
    {
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, BoundaryMistFrame.BindingPoint, _handle, (IntPtr)ViewModelOffset, BoundaryMistFrame.SizeInBytes);
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        GLUtils.AssertRenderThread("BoundaryMist buffer disposal");
        GL.DeleteBuffer(_handle);
        _handle = 0;
    }
}

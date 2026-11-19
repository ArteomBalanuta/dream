using Dream.Rendering.Core;
using Dream.Tests.Support;
using OpenTK.Graphics.OpenGL4;
using Xunit;

namespace Dream.Tests.Rendering.Core;

[Collection(RenderThreadAffinityCollection.Name)]
public sealed class GpuDebugTests
{
    private static readonly GpuDebugLabel s_label = new("test pass");

    [Theory]
    [InlineData(null, false, false, false, false)]
    [InlineData(null, true, false, true, true)]
    [InlineData(null, false, true, true, true)]
    [InlineData(null, false, false, true, false)]
    [InlineData("1", false, false, true, true)]
    [InlineData("0", true, true, true, false)]
    [InlineData("1", true, true, false, false)]
    [InlineData("0", false, false, false, false)]
    public void ResolveEnabled_FollowsTheTable(string? env, bool debug, bool renderDoc, bool supported, bool expected)
    {
        Assert.Equal(expected, GpuDebug.ResolveEnabled(env, debug, renderDoc, supported));
    }

    [Fact]
    public void BeforeInitialize_EverythingDoesNothing_AndPopNeverUnderflows()
    {
        GpuDebug.ResetForTests();
        Assert.False(GpuDebug.IsEnabled);

        GpuDebug.Pop();
        GpuDebug.Push(s_label);
        using (GpuDebug.Scope(s_label))
        {
        }

        GpuDebug.Pop();
        GpuDebug.LabelObject(ObjectLabelIdentifier.Program, 1, "x");
        Assert.Equal(0, GpuDebug.DepthForTests);
    }

    [Fact]
    public void LabelEncodesNulTerminatedUtf8Once()
    {
        GpuDebugLabel label = new("aé");
        Assert.Equal("aé", label.Name);
        Assert.Equal(new byte[] { 0x61, 0xC3, 0xA9, 0 }, label.Utf8);
    }

    [Fact]
    public void ObjectLabelLength_IsTheUtf8ByteCount_NotTheCharCount()
    {
        Assert.Equal(3, GpuDebug.ObjectLabelByteLength("abc"));
        Assert.Equal(4, GpuDebug.ObjectLabelByteLength("aéb"));
        Assert.Equal(6, GpuDebug.ObjectLabelByteLength("ééé"));
    }

    [GlFact]
    public void OnAHiddenContext_PushAndPopBalance_WithNoGlError_AndAllocateNothing()
    {
        string? previous = Environment.GetEnvironmentVariable(GpuDebug.EnvironmentVariable);
        Environment.SetEnvironmentVariable(GpuDebug.EnvironmentVariable, "1");
        try
        {
            HiddenGlContext.Run(() =>
            {
                GpuDebug.Initialize();
                try
                {
                    if (!GpuDebug.IsEnabled)
                    {
                        // KHR_debug missing on this driver: the calls must still be harmless.
                        GpuDebug.Push(s_label);
                        GpuDebug.Pop();
                        return;
                    }

                    for (int i = 0; i < 8; i++)
                    {
                        GpuDebug.Push(s_label);
                        GpuDebug.Pop();
                    }

                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 1000; i++)
                    {
                        GpuDebug.Push(s_label);
                        using (GpuDebug.Scope(s_label))
                        {
                        }

                        GpuDebug.Pop();
                    }

                    Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
                    Assert.Equal(0, GpuDebug.DepthForTests);
                    GpuDebug.Pop();
                    Assert.Equal(0, GpuDebug.DepthForTests);
                    Assert.Equal(ErrorCode.NoError, GL.GetError());
                }
                finally
                {
                    GpuDebug.ResetForTests();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(GpuDebug.EnvironmentVariable, previous);
        }
    }
}

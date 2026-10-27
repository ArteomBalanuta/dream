using System.Reflection;
using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

[Collection("RenderThreadAffinity")]
public class ShaderLifetimeTests
{
    [Fact]
    public void No_Type_In_Dream_Assembly_Declares_A_Finalizer()
    {
        List<string> offenders = new();
        foreach (Type type in typeof(Shader).Assembly.GetTypes())
        {
            MethodInfo? finalizer = type.GetMethod(
                "Finalize",
                BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Instance);
            if (finalizer != null)
            {
                offenders.Add(type.FullName ?? type.Name);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Shader_Implements_IDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(Shader)));
    }

    [Fact]
    public async Task IsCurrent_Is_True_On_Bound_Thread_And_False_On_Other_Thread()
    {
        try
        {
            RenderThreadAffinity.Bind();
            Assert.True(RenderThreadAffinity.IsCurrent);
            bool other = await Task.Run(() => RenderThreadAffinity.IsCurrent);
            Assert.False(other);
        }
        finally
        {
            RenderThreadAffinity.Unbind();
        }
    }

    [Fact]
    public async Task AssertCurrent_Throws_Off_Thread_In_Debug()
    {
        try
        {
            RenderThreadAffinity.Bind();
            RenderThreadAffinity.AssertCurrent("same thread");
#if DEBUG
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Task.Run(() => RenderThreadAffinity.AssertCurrent("other thread")));
#else
            await Task.Run(() => RenderThreadAffinity.AssertCurrent("other thread"));
#endif
        }
        finally
        {
            RenderThreadAffinity.Unbind();
        }
    }

    [Fact]
    public async Task AssertCurrent_Does_Not_Throw_When_Unbound()
    {
        RenderThreadAffinity.Unbind();
        RenderThreadAffinity.AssertCurrent("unbound");
        await Task.Run(() => RenderThreadAffinity.AssertCurrent("unbound other thread"));
    }
}

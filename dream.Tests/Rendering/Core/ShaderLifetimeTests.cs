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

    // The "other thread" side runs on a dedicated thread. A Task.Run lambda could run on the very
    // thread-pool thread that called Bind() (the test itself may be on a pool thread), which made
    // IsCurrent true and the assertion fail intermittently.
    [Fact]
    public void IsCurrent_Is_True_On_Bound_Thread_And_False_On_Other_Thread()
    {
        try
        {
            RenderThreadAffinity.Bind();
            Assert.True(RenderThreadAffinity.IsCurrent);
            bool other = RunOnDedicatedThread(() => RenderThreadAffinity.IsCurrent);
            Assert.False(other);
        }
        finally
        {
            RenderThreadAffinity.Unbind();
        }
    }

    [Fact]
    public void AssertCurrent_Throws_Off_Thread_In_Debug()
    {
        try
        {
            RenderThreadAffinity.Bind();
            RenderThreadAffinity.AssertCurrent("same thread");
            Exception? offThread = RunOnDedicatedThread(() => Record.Exception(
                () => RenderThreadAffinity.AssertCurrent("other thread")));
#if DEBUG
            Assert.IsType<InvalidOperationException>(offThread);
#else
            Assert.Null(offThread);
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

    private static T RunOnDedicatedThread<T>(Func<T> func)
    {
        T result = default!;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new InvalidOperationException("Dedicated thread failed.", failure);
        }

        Assert.NotEqual(Environment.CurrentManagedThreadId, thread.ManagedThreadId);
        return result;
    }
}

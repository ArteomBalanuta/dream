using System.Runtime.ExceptionServices;
using Dream.Rendering.Core;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace Dream.Tests.Support;

// Runs an action on a dedicated thread with a current OpenGL context from an invisible 64x64
// window. The whole life of the window (create, make current, use, dispose) is on that one thread,
// so no test thread ever owns GL state. RenderThreadAffinity is bound to the dedicated thread for the
// action and unbound afterwards. Tests using it belong in RenderThreadAffinityCollection.
internal static class HiddenGlContext
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            NativeWindow? window = null;
            try
            {
                GLFWProvider.CheckForMainThread = false;
                try
                {
                    window = new NativeWindow(new NativeWindowSettings
                    {
                        StartVisible = false,
                        StartFocused = false,
                        ClientSize = new Vector2i(64, 64),
#if DEBUG
                        // The same flags Game uses.
                        Flags = ContextFlags.Debug,
#endif
                    });
                    window.MakeCurrent();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not create a hidden OpenGL context ({ex.GetType().Name}: {ex.Message}). " +
                        $"Set {GlFactAttribute.EnvironmentVariable}=0 to skip GL tests on a machine without a GPU or display.",
                        ex);
                }

                GLUtils.BindRenderThread();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    window?.Dispose();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                RenderThreadAffinity.Unbind();
            }
        })
        {
            IsBackground = true,
            Name = "HiddenGlContext",
        };
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

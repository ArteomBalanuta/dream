using System.Diagnostics;

namespace Dream.Rendering.Core;

public static class RenderThreadAffinity
{
    private static int _boundThreadId;

    public static bool IsBound => Volatile.Read(ref _boundThreadId) != 0;

    public static bool IsCurrent => Volatile.Read(ref _boundThreadId) == Environment.CurrentManagedThreadId;

    public static void Bind() => Volatile.Write(ref _boundThreadId, Environment.CurrentManagedThreadId);

    public static void Unbind() => Volatile.Write(ref _boundThreadId, 0);

    [Conditional("DEBUG")]
    public static void AssertCurrent(string context)
    {
        if (IsBound && !IsCurrent)
        {
            throw new InvalidOperationException(
                $"{context} must run on the render thread (bound thread {Volatile.Read(ref _boundThreadId)}, current {Environment.CurrentManagedThreadId}).");
        }
    }
}

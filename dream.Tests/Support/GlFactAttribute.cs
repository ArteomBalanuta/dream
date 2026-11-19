using Xunit;

namespace Dream.Tests.Support;

// A test that needs a real (hidden) OpenGL context. DREAM_GL_TESTS=0 skips it with a reason, for
// machines and CI runners with no GPU or display. Unset (or anything else) runs it, and then a
// context that cannot be created FAILS the test (see HiddenGlContext) rather than skipping it
// quietly: a gate that can silently stop measuring is not a gate.
public sealed class GlFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "DREAM_GL_TESTS";

    public GlFactAttribute()
    {
        if (IsDisabled)
        {
            Skip = $"GL test; {EnvironmentVariable}=0 skips tests that need a hidden OpenGL context.";
        }
    }

    public static bool IsDisabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "0", StringComparison.Ordinal);
}

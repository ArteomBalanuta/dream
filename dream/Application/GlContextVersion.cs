namespace Dream.Application;

/// <summary>
/// The OpenGL context versions the desktop client asks GLFW for, in order. A 4.5 core context is tried
/// first; a driver that cannot make one gets the 3.3 core context every shader is written for.
/// <c>DREAM_GL_VERSION=3.3</c> skips the 4.5 attempt.
/// </summary>
public static class GlContextVersion
{
    public const string EnvironmentVariable = "DREAM_GL_VERSION";

    public static readonly Version Preferred = new(4, 5);
    public static readonly Version Fallback = new(3, 3);

    public static IReadOnlyList<Version> Attempts(string? environmentValue)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue)
            && Version.TryParse(environmentValue.Trim(), out Version? requested)
            && requested.Major == Fallback.Major
            && requested.Minor == Fallback.Minor)
        {
            return [Fallback];
        }

        return [Preferred, Fallback];
    }

    public static IReadOnlyList<Version> AttemptsFromEnvironment() =>
        Attempts(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static string Describe(Version version) => $"{version.Major}.{version.Minor}";
}

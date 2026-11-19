using System.Globalization;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Core;

// Anisotropic filtering for mipmapped world textures. The extension is optional: Apply does nothing
// when it is absent or when the level resolves to 1.
public static class TextureAnisotropy
{
    public const string EnvironmentVariable = "DREAM_ANISOTROPY";
    public const float DefaultLevel = 8f;
    public const float MaxLevel = 16f;

    private const int TextureMaxAnisotropy = 0x84FE;
    private const int MaxTextureMaxAnisotropy = 0x84FF;

    private static bool s_resolved;
    private static float s_level = 1f;

    // Unset or invalid gives DefaultLevel; a number <= 1 turns it off (1); anything else clamps to [1, 16].
    public static float ResolveLevel(string? environmentValue)
    {
        if (string.IsNullOrWhiteSpace(environmentValue)
            || !float.TryParse(environmentValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || float.IsNaN(value))
        {
            return DefaultLevel;
        }

        return value <= 1f ? 1f : Math.Min(value, MaxLevel);
    }

    // The level actually applied: 1 when off or unsupported. Valid once Apply has run.
    public static float AppliedLevel => s_level;

    // Render thread, against the texture bound to target.
    public static void Apply(TextureTarget target)
    {
        if (!s_resolved)
        {
            Resolve();
        }

        if (s_level > 1f)
        {
            GL.TexParameter(target, (TextureParameterName)TextureMaxAnisotropy, s_level);
        }
    }

    private static void Resolve()
    {
        s_resolved = true;
        float requested = ResolveLevel(Environment.GetEnvironmentVariable(EnvironmentVariable));
        if (requested <= 1f)
        {
            s_level = 1f;
            Console.WriteLine("[startup] anisotropic filtering: off");
            return;
        }

        if (!IsSupported())
        {
            s_level = 1f;
            Console.WriteLine("[startup] anisotropic filtering: unsupported");
            return;
        }

        GL.GetFloat((GetPName)MaxTextureMaxAnisotropy, out float max);
        s_level = Math.Max(1f, Math.Min(requested, max));
        Console.WriteLine($"[startup] anisotropic filtering: {s_level.ToString("0.##", CultureInfo.InvariantCulture)}x");
    }

    private static bool IsSupported()
    {
        int major = GL.GetInteger(GetPName.MajorVersion);
        int minor = GL.GetInteger(GetPName.MinorVersion);
        if (major > 4 || (major == 4 && minor >= 6))
        {
            return true;
        }

        int count = GL.GetInteger(GetPName.NumExtensions);
        for (int i = 0; i < count; i++)
        {
            string name = GL.GetString(StringNameIndexed.Extensions, i);
            if (name == "GL_EXT_texture_filter_anisotropic" || name == "GL_ARB_texture_filter_anisotropic")
            {
                return true;
            }
        }

        return false;
    }
}

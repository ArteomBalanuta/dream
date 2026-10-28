using System.IO;

namespace Dream.Tests.Utilities;

// Structural tests read a class's source by path. A class split into partial files (Render.cs
// plus Render.Hud.cs, Render.Terrain.cs, ...) is read as the union of its files: the named file
// first, then its "<Name>.*.cs" partials in ordinal order. A file with no partials reads as itself.
internal static class PartialClassSource
{
    public static string Read(string path)
    {
        return string.Join("\n", Files(path).Select(File.ReadAllText));
    }

    public static IReadOnlyList<string> Files(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Expected source file to exist: {path}", fullPath);
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        string name = Path.GetFileNameWithoutExtension(fullPath);
        string[] partials = Directory.GetFiles(directory, name + ".*.cs", SearchOption.TopDirectoryOnly);
        Array.Sort(partials, StringComparer.Ordinal);
        return [fullPath, .. partials];
    }
}

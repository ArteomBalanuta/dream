using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Dream.Rendering.Actors;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// GL is not available in tests, so ownership is checked structurally: each owner is IDisposable
// and its source releases every disposable field it declares.
public class RenderOwnershipTests
{
    public static TheoryData<Type, string> Owners => new()
    {
        { typeof(Render), Path.Combine("dream", "Rendering", "Core", "Render.cs") },
        { typeof(WorldRenderer), Path.Combine("dream", "Rendering", "World", "WorldRenderer.cs") },
        { typeof(LocalPlayerRenderer), Path.Combine("dream", "Rendering", "Actors", "LocalPlayerRenderer.cs") },
        { typeof(RemotePlayerRenderer), Path.Combine("dream", "Rendering", "Actors", "RemotePlayerRenderer.cs") }
    };

    // Fields that hold a disposable they do not own. Child renderers keep a back-reference to
    // the Render that owns them; disposing it from a child would dispose the owner.
    private static readonly HashSet<(Type Owner, string Field)> NonOwningFields =
    [
        (typeof(WorldRenderer), "_render"),
        (typeof(LocalPlayerRenderer), "_render"),
        (typeof(RemotePlayerRenderer), "_render")
    ];

    [Theory]
    [MemberData(nameof(Owners))]
    public void Owner_Implements_IDisposable(Type owner, string relativePath)
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(owner), $"{owner.Name} ({relativePath}) must implement IDisposable.");
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public void Owner_Disposes_Every_Owned_Disposable_Field(Type owner, string relativePath)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));
        List<string> ownedFields = [];
        List<string> missing = [];

        foreach (FieldInfo field in owner.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (NonOwningFields.Contains((owner, field.Name)))
            {
                continue;
            }

            FieldKind kind = Classify(field.FieldType);
            if (kind == FieldKind.None)
            {
                continue;
            }

            ownedFields.Add(field.Name);
            if (!IsReleased(source, field.Name, kind))
            {
                missing.Add($"{field.Name} ({field.FieldType.Name})");
            }
        }

        Assert.Empty(missing);
        if (owner != typeof(Render))
        {
            Assert.NotEmpty(ownedFields);
        }
    }

    // Guards the classification itself, so a reflection change cannot make the test above vacuous.
    [Fact]
    public void Render_Owned_Fields_Include_Shaders_Hud_And_Child_Renderers()
    {
        string[] expected =
        [
            "_shaders",
            "_hudShader",
            "_shaderObj",
            "_fallbackCubeBuffer",
            "_staticTerrainTextures",
            "_worldRenderer",
            "_localPlayerRenderer",
            "_remotePlayerRenderer"
        ];

        foreach (string name in expected)
        {
            FieldInfo? field = typeof(Render).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.NotEqual(FieldKind.None, Classify(field.FieldType));
        }
    }

    [Fact]
    public void RemotePlayerRenderer_Treats_Player_Model_Cache_As_Owned()
    {
        FieldInfo? field = typeof(RemotePlayerRenderer).GetField("_playerModels", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Equal(FieldKind.Collection, Classify(field.FieldType));
    }

    [Fact]
    public void Render_Disposes_Debug_Cube_Buffers_And_Destroys_Its_ImGui_Context()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "dream", "Rendering", "Core", "Render.cs"));

        Assert.Contains("GL.DeleteBuffer(_vertexBufferObject)", source);
        Assert.Contains("GL.DeleteVertexArray(_vertexArrayObject)", source);
        Assert.Contains("ImGui.DestroyContext(_imGuiContext)", source);
        Assert.DoesNotContain("public void DisposeHUD()", source);
    }

    // The player renderers own only the per-instance player models; the shared ModelAssets cache
    // (enemy, viewmodel and cube models) is released elsewhere and must never be disposed here.
    [Theory]
    [InlineData("LocalPlayerRenderer.cs")]
    [InlineData("RemotePlayerRenderer.cs")]
    public void Player_Renderers_Create_Only_Per_Instance_Models(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "dream", "Rendering", "Actors", fileName));

        Assert.Contains("ModelAssets.RequestPlayerInstance()", source);
        Assert.DoesNotContain("ModelAssets.Get(", source);
        Assert.DoesNotContain("ModelAssets.DisposeAll", source);
    }

    private static bool IsReleased(string source, string fieldName, FieldKind kind)
    {
        string name = Regex.Escape(fieldName);
        if (Regex.IsMatch(source, $@"(?<![\w.]){name}\??\.(Dispose|DisposeAll)\(\)"))
        {
            return true;
        }

        // A method group passed to a disposal step, such as RunDisposeStep(_shaders.DisposeAll, ...).
        if (Regex.IsMatch(source, $@"(?<![\w.]){name}\.(Dispose|DisposeAll)(?=\s*[,)])"))
        {
            return true;
        }

        // A nullable field moved into a local first, so a re-entrant call cannot dispose it twice.
        if (Regex.IsMatch(source, $@"(\w+)\s*=\s*{name}\s*;[\s\S]*?\1\??\.Dispose\(\)"))
        {
            return true;
        }

        return kind == FieldKind.Collection &&
            Regex.IsMatch(source, $@"foreach\s*\([^)]*\bin\s+{name}(\.Values)?\)\s*\{{[^}}]*\.Dispose\(\)");
    }

    private static FieldKind Classify(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DisposableRegistry<,>))
        {
            return FieldKind.Registry;
        }

        if (typeof(IDisposable).IsAssignableFrom(type))
        {
            return FieldKind.Disposable;
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            Type[] arguments = type.GetGenericArguments();
            if (definition == typeof(Dictionary<,>) && typeof(IDisposable).IsAssignableFrom(arguments[1]))
            {
                return FieldKind.Collection;
            }

            if (definition == typeof(List<>) && typeof(IDisposable).IsAssignableFrom(arguments[0]))
            {
                return FieldKind.Collection;
            }
        }

        return FieldKind.None;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    public enum FieldKind
    {
        None,
        Disposable,
        Registry,
        Collection
    }
}

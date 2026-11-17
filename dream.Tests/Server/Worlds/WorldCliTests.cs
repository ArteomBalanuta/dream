using Xunit;
using Dream.Application;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Worlds;

namespace Dream.Tests.Server.Worlds;

public sealed class WorldCliTests
{
    private static (int Exit, string Out, string Err) Run(WorldCatalogTestDatabase db, params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = WorldCli.Run(args, () => db.Catalog, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void CreateWithoutSize_UsesTheServerNewWorldSize()
    {
        using WorldCatalogTestDatabase db = new();

        (int exit, string created, _) = Run(db, "create", "Frontier");
        Assert.Equal(0, exit);
        Assert.Equal("100x8x100", created.TrimEnd().Split('	')[2]);
    }

    [Fact]
    public void CreateWithGenerator_StoresTheChosenGeneratorOnTheRow()
    {
        using WorldCatalogTestDatabase db = new();

        (int exit, string created, _) = Run(db, "create", "Wilds", "--generator", "natural", "--size", "20");
        Assert.Equal(0, exit);
        Assert.Equal(["1", "Wilds", "20x8x20", "natural:1", "0"], created.TrimEnd().Split('	'));

        Dream.Simulation.World.WorldDescriptor descriptor = db.Catalog.TryGetByName("Wilds")!.Descriptor;
        Assert.Equal("natural", descriptor.GeneratorKey);
        Assert.Equal(1, descriptor.GeneratorVersion);
        descriptor.Validate();

        // Without the flag a new world gets WorldGeneratorCatalog.NewWorldKey, the natural generator.
        (exit, string plain, _) = Run(db, "create", "Home", "--size", "20");
        Assert.Equal(0, exit);
        Assert.Equal("natural:1", plain.TrimEnd().Split('	')[3]);
    }

    [Fact]
    public void CreateWithABadGenerator_IsRejectedAndWritesNoRow()
    {
        using WorldCatalogTestDatabase db = new();

        // An unregistered key, the flag with nothing after it, and the flag followed by another
        // flag: all usage errors, and none of them may leave a world behind.
        Assert.Equal(WorldCli.ExitUsage, Run(db, "create", "X", "--generator", "nope").Exit);
        Assert.Equal(WorldCli.ExitUsage, Run(db, "create", "X", "--generator").Exit);
        Assert.Equal(WorldCli.ExitUsage, Run(db, "create", "X", "--generator", "--size", "20").Exit);
        Assert.Equal(string.Empty, Run(db, "list").Out.Trim());
    }

    [Fact]
    public void CreateListCopyDelete_RoundTripWithTabSeparatedOutputAndNoSeed()
    {
        using WorldCatalogTestDatabase db = new();

        (int exit, string created, _) = Run(db, "create", "Alpha", "--size", "20");
        Assert.Equal(0, exit);
        string[] fields = created.TrimEnd().Split('\t');
        Assert.Equal(["1", "Alpha", "20x8x20", "natural:1", "0"], fields);

        ulong seed = db.Catalog.TryGetByName("Alpha")!.Seed!.Value;

        Assert.Equal(0, Run(db, "copy", "alpha", "Beta").Exit);
        (exit, string listed, _) = Run(db, "list");
        Assert.Equal(0, exit);
        string[] lines = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2\tBeta\t20x8x20\tnatural:1\t0", lines[1]);
        Assert.DoesNotContain(seed.ToString(), created + listed, StringComparison.Ordinal);

        (exit, string deleted, _) = Run(db, "delete", "Beta", "--yes");
        Assert.Equal(0, exit);
        Assert.StartsWith("deleted\t2\tBeta", deleted);
        Assert.Single(db.Catalog.List());
    }

    [Fact]
    public void UsageErrorsExitOne()
    {
        using WorldCatalogTestDatabase db = new();
        db.Catalog.Create(new Dream.Server.Worlds.WorldCreateOptions("Alpha", 20));

        Assert.Equal(1, Run(db).Exit);
        Assert.Equal(1, Run(db, "frobnicate").Exit);
        Assert.Equal(1, Run(db, "list", "extra").Exit);
        Assert.Equal(1, Run(db, "create").Exit);
        Assert.Equal(1, Run(db, "create", "X", "--size", "abc").Exit);
        Assert.Equal(1, Run(db, "create", "X", "--size", "4").Exit);
        Assert.Equal(1, Run(db, "copy", "Alpha").Exit);
        Assert.Equal(1, Run(db, "delete", "Alpha").Exit);
        Assert.Equal(1, Run(db, "delete", "0", "--yes").Exit);
        Assert.Single(db.Catalog.List());
    }

    [Fact]
    public void NotFoundAndNameConflictExitTwo()
    {
        using WorldCatalogTestDatabase db = new();
        db.Catalog.Create(new Dream.Server.Worlds.WorldCreateOptions("Alpha", 20));

        Assert.Equal(2, Run(db, "create", "ALPHA").Exit);
        Assert.Equal(2, Run(db, "copy", "Alpha", "alpha").Exit);
        Assert.Equal(2, Run(db, "copy", "Missing", "Z").Exit);
        Assert.Equal(2, Run(db, "delete", "Missing", "--yes").Exit);
        Assert.Equal(2, Run(db, "delete", "99", "--yes").Exit);
    }

    [Fact]
    public void SchemaTooNewExitsThree()
    {
        StringWriter output = new();
        StringWriter error = new();

        int exit = WorldCli.Run(["list"], () => throw new SchemaTooNewException(9, 3), output, error);

        Assert.Equal(3, exit);
        Assert.Contains("9", error.ToString());
    }

    [Fact]
    public void Program_ParsesServerWorldAndWorldsArguments()
    {
        Assert.True(Program.TrySelectProcessMode([], out ProcessMode mode, out _, out _));
        Assert.Equal(ProcessMode.Desktop, mode);

        Assert.True(Program.TrySelectProcessMode(["--server"], out mode, out var options, out _));
        Assert.Equal(ProcessMode.Server, mode);
        Assert.Null(options.WorldSelector);
        Assert.Equal(100, options.NewWorldSize);

        Assert.True(Program.TrySelectProcessMode(["--server", "--world", "Alpha"], out mode, out options, out _));
        Assert.Equal("Alpha", options.WorldSelector);

        Assert.True(Program.TrySelectProcessMode(["--worlds", "list"], out mode, out _, out _));
        Assert.Equal(ProcessMode.Worlds, mode);

        Assert.False(Program.TrySelectProcessMode(["--server", "--world"], out _, out _, out string? error));
        Assert.NotNull(error);
        Assert.False(Program.TrySelectProcessMode(["--server", "extra"], out _, out _, out _));
        Assert.False(Program.TrySelectProcessMode(["--bogus"], out _, out _, out _));
    }
}

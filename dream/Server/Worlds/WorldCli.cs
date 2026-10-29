using System.Globalization;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Worlds;
using Dream.Simulation.World;

namespace Dream.Server.Worlds;

// The `--worlds` command line: list | create <name> [--size N] | copy <src> <name> |
// delete <id|name> --yes | export <src> <name> [--terrain-only] | reset-terrain <id|name> --yes. Output is tab-separated (id, name, size, generator, world time ticks); the
// seed is never printed.
public static class WorldCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitNotFoundOrConflict = 2;
    public const int ExitSchemaTooNew = 3;
    public const int ExitTerrainInvalid = 4;

    public const string Usage =
        "Usage: --worlds list | create <name> [--size N] | copy <id|name> <new name> | delete <id|name> --yes | " +
        "export <id|name> <new name> [--terrain-only] | reset-terrain <id|name> --yes";

    // Export and reset are offline commands: nothing detects a running server (a hosting lease is Phase 5).
    internal const string OfflineNote = "Note: stop the server hosting this world first.";

    // Runs against the default database (migrating it first). args are the arguments after --worlds.
    public static int Run(string[] args)
    {
        return Run(args, OpenDefaultCatalog, Console.Out, Console.Error);
    }

    public static int Run(string[] args, Func<IWorldCatalog> openCatalog, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(openCatalog);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        Func<IWorldCatalog, int>? command = Parse(args, output, error, out string? usageError);
        if (command == null)
        {
            error.WriteLine(usageError);
            error.WriteLine(Usage);
            return ExitUsage;
        }

        try
        {
            return command(openCatalog());
        }
        catch (SchemaTooNewException ex)
        {
            error.WriteLine(
                $"The database schema is at version {ex.StoredVersion}, newer than the version " +
                $"{ex.LatestVersion} this build knows. Run a newer build.");
            return ExitSchemaTooNew;
        }
        catch (WorldNotFoundException ex)
        {
            error.WriteLine(ex.Message);
            return ExitNotFoundOrConflict;
        }
        catch (WorldNameTakenException ex)
        {
            error.WriteLine(ex.Message);
            return ExitNotFoundOrConflict;
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(Usage);
            return ExitUsage;
        }
    }

    private static IWorldCatalog OpenDefaultCatalog()
    {
        SqliteSchemaBootstrapper schema = new(ServerBootstrap.DefaultDatabasePath, ServerBootstrap.DefaultSchemaScriptPath);
        schema.EnsureSchema();
        return new SqliteWorldCatalog(schema);
    }

    private static Func<IWorldCatalog, int>? Parse(string[] args, TextWriter output, TextWriter error, out string? usageError)
    {
        usageError = null;
        if (args.Length == 0)
        {
            usageError = "Missing --worlds command.";
            return null;
        }

        string verb = args[0].ToLowerInvariant();
        string[] rest = args[1..];
        switch (verb)
        {
            case "list":
                if (rest.Length != 0)
                {
                    usageError = "'list' takes no arguments.";
                    return null;
                }

                return catalog =>
                {
                    foreach (WorldRecord record in catalog.List())
                    {
                        output.WriteLine(Format(record));
                    }

                    return ExitOk;
                };

            case "create":
                return ParseCreate(rest, output, out usageError);

            case "copy":
            {
                if (rest.Length != 2)
                {
                    usageError = "'copy' needs a source world and a new name.";
                    return null;
                }

                WorldSelector copySource = ParseSelector(rest[0], out usageError);
                if (usageError != null)
                {
                    return null;
                }

                string copyName = rest[1];
                return catalog =>
                {
                    WorldRecord source = ResolveExisting(catalog, copySource);
                    output.WriteLine(Format(catalog.Copy(source.Descriptor.Id, copyName)));
                    return ExitOk;
                };
            }

            case "delete":
            {
                bool yes = rest.Contains("--yes", StringComparer.Ordinal);
                string[] positional = rest.Where(a => a != "--yes").ToArray();
                if (positional.Length != 1)
                {
                    usageError = "'delete' needs exactly one world id or name.";
                    return null;
                }

                if (!yes)
                {
                    usageError = "'delete' removes the world and all its data; pass --yes to confirm.";
                    return null;
                }

                WorldSelector deleteTarget = ParseSelector(positional[0], out usageError);
                if (usageError != null)
                {
                    return null;
                }

                return catalog =>
                {
                    WorldRecord target = ResolveExisting(catalog, deleteTarget);
                    if (!catalog.Delete(target.Descriptor.Id))
                    {
                        throw new WorldNotFoundException(target.Descriptor.Id.ToString());
                    }

                    output.WriteLine($"deleted\t{target.Descriptor.Id}\t{target.Descriptor.Name}");
                    return ExitOk;
                };
            }

            case "export":
            {
                bool terrainOnly = rest.Contains("--terrain-only", StringComparer.Ordinal);
                string[] positional = rest.Where(a => a != "--terrain-only").ToArray();
                if (positional.Length != 2 || positional.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
                {
                    usageError = "'export' needs a source world and a new name, and takes only --terrain-only.";
                    return null;
                }

                WorldSelector exportSource = ParseSelector(positional[0], out usageError);
                if (usageError != null)
                {
                    return null;
                }

                string exportName = positional[1];
                return catalog =>
                {
                    error.WriteLine(OfflineNote);
                    WorldRecord source = ResolveExisting(catalog, exportSource);
                    string trimmed = exportName.Trim();
                    if (catalog.TryGetByName(trimmed) != null)
                    {
                        throw new WorldNameTakenException(trimmed);
                    }

                    WorldTerrainSource input = catalog.ReadTerrainSource(source.Descriptor.Id);
                    BakedTerrain baked;
                    try
                    {
                        baked = WorldTerrainBaker.Bake(input.Record, input.Seed, input.StoredChunks, input.Overrides);
                    }
                    catch (InvalidDataException ex)
                    {
                        error.WriteLine($"The terrain cannot be exported: {ex.Message}");
                        return ExitTerrainInvalid;
                    }

                    WorldRecord created = catalog.CreateStored(source.Descriptor.Id, exportName, baked.Chunks, terrainOnly);
                    output.WriteLine(Format(created));
                    return ExitOk;
                };
            }

            case "reset-terrain":
            {
                bool yes = rest.Contains("--yes", StringComparer.Ordinal);
                string[] positional = rest.Where(a => a != "--yes").ToArray();
                if (positional.Length != 1)
                {
                    usageError = "'reset-terrain' needs exactly one world id or name.";
                    return null;
                }

                if (!yes)
                {
                    usageError = "'reset-terrain' restores the world's base terrain and drops its rubble; pass --yes to confirm.";
                    return null;
                }

                WorldSelector resetTarget = ParseSelector(positional[0], out usageError);
                if (usageError != null)
                {
                    return null;
                }

                return catalog =>
                {
                    error.WriteLine(OfflineNote);
                    WorldRecord target = ResolveExisting(catalog, resetTarget);
                    TerrainResetResult result = catalog.ResetTerrain(target.Descriptor.Id);
                    output.WriteLine(
                        $"reset-terrain\t{target.Descriptor.Id}\t{target.Descriptor.Name}\t" +
                        $"cleared={result.OverridesCleared}\trubble={result.RubbleRowsDeleted}\t" +
                        $"kept={result.CellsKeptForPlacedCubes}\tinside={result.Inside}" +
                        (result.PlayerNames is { Count: > 0 } names ? $"\tinside_players={string.Join(',', names)}" : string.Empty));
                    return ExitOk;
                };
            }

            default:
                usageError = $"Unknown --worlds command '{args[0]}'.";
                return null;
        }
    }

    private static WorldSelector ParseSelector(string text, out string? usageError)
    {
        usageError = null;
        try
        {
            WorldSelector selector = WorldSelector.Parse(text);
            if (selector.IsDefault)
            {
                usageError = "A world id or name is required.";
            }

            return selector;
        }
        catch (ArgumentException ex)
        {
            usageError = ex.Message;
            return default;
        }
    }

    private static Func<IWorldCatalog, int>? ParseCreate(string[] rest, TextWriter output, out string? usageError)
    {
        usageError = null;
        string? name = null;
        int size = WorldDefaults.StartupFloorSize;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--size")
            {
                if (i + 1 >= rest.Length ||
                    !int.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out size))
                {
                    usageError = "--size needs a whole number.";
                    return null;
                }

                i++;
            }
            else if (name == null && !rest[i].StartsWith("--", StringComparison.Ordinal))
            {
                name = rest[i];
            }
            else
            {
                usageError = $"Unexpected argument '{rest[i]}'.";
                return null;
            }
        }

        if (name == null)
        {
            usageError = "'create' needs a name.";
            return null;
        }

        int createSize = size;
        string createName = name;
        return catalog =>
        {
            output.WriteLine(Format(catalog.Create(new WorldCreateOptions(createName, createSize))));
            return ExitOk;
        };
    }

    // An id or name that must already exist (never creates).
    private static WorldRecord ResolveExisting(IWorldCatalog catalog, WorldSelector selector)
    {
        WorldRecord? record = selector.Id is WorldId id ? catalog.TryGet(id) : catalog.TryGetByName(selector.Name!);
        return record ?? throw new WorldNotFoundException(selector.ToString());
    }

    // id, name, size, generator, world time ticks. Never the seed.
    internal static string Format(WorldRecord record)
    {
        WorldDescriptor d = record.Descriptor;
        return string.Join(
            '\t',
            d.Id.ToString(),
            d.Name,
            $"{d.Width}x{d.Height}x{d.Depth}",
            d.MapSource == WorldMapSource.Stored
                ? $"stored:{d.GeneratorKey}:{d.GeneratorVersion}"
                : $"{d.GeneratorKey}:{d.GeneratorVersion}",
            record.WorldTimeTicks.ToString(CultureInfo.InvariantCulture));
    }
}

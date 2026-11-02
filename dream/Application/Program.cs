using System;
using Dream.Server;
using Dream.Server.Accounts;
using Dream.Server.Characters;
using Dream.Server.Items;
using Dream.Server.Persistence;
using Dream.Server.Worlds;
using Dream.Simulation.World;

namespace Dream.Application;


internal enum ProcessMode
{
    Desktop,
    Server,
    Worlds,
    Database,
    Characters,
    Accounts,
    Items
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (!TrySelectProcessMode(args, out var mode, out var serverOptions, out var errorMessage))
        {
            Console.Error.WriteLine(errorMessage);
            return 1;
        }

        return mode switch
        {
            ProcessMode.Desktop => RunDesktop(),
            ProcessMode.Server => ServerBootstrap.RunServerMode(serverOptions),
            ProcessMode.Worlds => WorldCli.Run(args[1..]),
            ProcessMode.Database => DatabaseCli.Run(args[1..]),
            ProcessMode.Characters => CharacterCli.Run(args[1..]),
            ProcessMode.Accounts => AccountCli.Run(args[1..]),
            ProcessMode.Items => ItemCli.Run(args[1..]),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown process mode.")
        };
    }

    // No arguments: desktop. "--server [--world <id|name>]": the dedicated server. "--worlds ...":
    // the world catalog command line (WorldCli parses what follows).
    internal static bool TrySelectProcessMode(
        string[] args,
        out ProcessMode mode,
        out ServerStartOptions serverOptions,
        out string? errorMessage)
    {
        serverOptions = ServerStartOptions.Default;
        if (args.Length == 0)
        {
            mode = ProcessMode.Desktop;
            errorMessage = null;
            return true;
        }

        if (string.Equals(args[0], "--worlds", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessMode.Worlds;
            errorMessage = null;
            return true;
        }

        if (string.Equals(args[0], "--characters", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessMode.Characters;
            errorMessage = null;
            return true;
        }

        if (string.Equals(args[0], "--accounts", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessMode.Accounts;
            errorMessage = null;
            return true;
        }

        if (string.Equals(args[0], "--items", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessMode.Items;
            errorMessage = null;
            return true;
        }

        if (string.Equals(args[0], "--db", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessMode.Database;
            errorMessage = null;
            return true;
        }

        if (string.Equals(args[0], "--server", StringComparison.OrdinalIgnoreCase))
        {
            string? world = null;
            bool valid = true;
            if (args.Length == 3 && string.Equals(args[1], "--world", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[2]))
            {
                world = args[2];
            }
            else if (args.Length != 1)
            {
                valid = false;
            }

            if (valid)
            {
                mode = ProcessMode.Server;
                serverOptions = new ServerStartOptions(WorldSelector: world, NewWorldSize: WorldDefaults.ServerNewWorldSize);
                errorMessage = null;
                return true;
            }
        }

        mode = default;
        errorMessage =
            $"Unsupported startup argument(s): {string.Join(", ", args)}. " +
            "Supported modes: no arguments for desktop, --server [--world <id|name>] for the dedicated server, " +
            "or --worlds list|create|copy|delete for the world catalog, or --characters list|release|rename for characters, or --accounts grant-admin|revoke-admin|unban for accounts, or --items grant|list for item rolls, or --db backup|restore for the database file.";
        return false;
    }

    private static int RunDesktop()
    {
        using var game = new Game();
        game.Run();
        return 0;
    }
}

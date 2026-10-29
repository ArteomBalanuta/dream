using System;
using Dream.Server;
using Dream.Server.Worlds;

namespace Dream.Application;


internal enum ProcessMode
{
    Desktop,
    Server,
    Worlds
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
                serverOptions = new ServerStartOptions(WorldSelector: world);
                errorMessage = null;
                return true;
            }
        }

        mode = default;
        errorMessage =
            $"Unsupported startup argument(s): {string.Join(", ", args)}. " +
            "Supported modes: no arguments for desktop, --server [--world <id|name>] for the dedicated server, " +
            "or --worlds list|create|copy|delete for the world catalog.";
        return false;
    }

    private static int RunDesktop()
    {
        using var game = new Game();
        game.Run();
        return 0;
    }
}

using System;

internal enum ProcessMode
{
    Desktop,
    Server
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (!TrySelectProcessMode(args, out var mode, out var errorMessage))
        {
            Console.Error.WriteLine(errorMessage);
            return 1;
        }

        return mode switch
        {
            ProcessMode.Desktop => RunDesktop(),
            ProcessMode.Server => RunUnimplementedServerMode(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown process mode.")
        };
    }

    private static bool TrySelectProcessMode(string[] args, out ProcessMode mode, out string? errorMessage)
    {
        if (args.Length == 0)
        {
            mode = ProcessMode.Desktop;
            errorMessage = null;
            return true;
        }

        if (args.Length == 1 && string.Equals(args[0], "--server", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessMode.Server;
            errorMessage = null;
            return true;
        }

        mode = default;
        errorMessage =
            $"Unsupported startup argument(s): {string.Join(", ", args)}. " +
            "Supported modes: no arguments for desktop, or --server for the reserved server path.";
        return false;
    }

    private static int RunDesktop()
    {
        using var game = new Game();
        game.Run();
        return 0;
    }

    private static int RunUnimplementedServerMode()
    {
        Console.Error.WriteLine(
            "The --server startup path is not implemented yet. " +
            "This phase only reserves the startup seam, so no headless host or transport bootstrap runs yet.");
        return 1;
    }
}

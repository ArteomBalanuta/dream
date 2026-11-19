using System;
using Dream.Server;
using Dream.Server.Accounts;
using Dream.Server.Characters;
using Dream.Server.Items;
using Dream.Server.Persistence;
using Dream.Server.Worlds;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Application;


internal enum ProcessMode
{
    Desktop,
    Server,
    Worlds,
    Database,
    Characters,
    Accounts,
    Items,
    ValidateContent
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
            ProcessMode.ValidateContent => ContentValidateCli.Run(args[1..], Console.Out, Console.Error),
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

        if (string.Equals(args[0], "--validate-content", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessMode.ValidateContent;
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
            "or --worlds list|create|copy|delete for the world catalog, or --characters list|release|rename for characters, or --accounts grant-admin|revoke-admin|unban for accounts, or --items grant|list for item rolls, or --db backup|restore for the database file, or --validate-content [--dir <dir>] [--ui <dir>] [--strict] to check the content files.";
        return false;
    }

    // Picks the first attempt whose context can be created. A probe that throws GLFWException, while
    // another attempt remains, means that version is unavailable; the last attempt is never probed.
    internal static Version SelectContextVersion(
        IReadOnlyList<Version> attempts,
        Action<Version> probe,
        Action<string> log)
    {
        for (int i = 0; i + 1 < attempts.Count; i++)
        {
            try
            {
                probe(attempts[i]);
                return attempts[i];
            }
            catch (GLFWException ex)
            {
                log(
                    $"[startup] gl: {GlContextVersion.Describe(attempts[i])} context unavailable ({ex.Message}), "
                    + $"falling back to {GlContextVersion.Describe(attempts[i + 1])}");
            }
        }

        return attempts[^1];
    }

    // Creates and destroys a hidden 1x1 core-profile window, so a refused version is found before
    // the Game constructor has side effects. OpenTK's default GLFW error handler throws from inside the
    // native callback, mid glfwCreateWindow, which skips GLFW's own cleanup and leaks a hidden window per
    // refused probe. So the handler is swapped for one that only records the message while the probe runs,
    // the failure is raised here outside the native frame, and a throwing handler is always put back.
    private static void ProbeContext(Version version)
    {
        string? recorded = null;
        GLFWProvider.SetErrorCallback((error, description) => recorded ??= $"{error}: {description}");
        try
        {
            using NativeWindow probe = new(new NativeWindowSettings
            {
                APIVersion = version,
                Profile = ContextProfile.Core,
                ClientSize = new Vector2i(1, 1),
                StartVisible = false,
                StartFocused = false,
#if DEBUG
                // The same flag the real window asks for, so a driver that refuses a debug 4.5 context falls back here.
                Flags = ContextFlags.Debug,
#endif
            });
        }
        catch (Exception ex) when (recorded != null && ex is not GLFWException)
        {
            throw new GLFWException(recorded);
        }
        finally
        {
            // Same behaviour as OpenTK's default handler: surface every GLFW error as an exception.
            GLFWProvider.SetErrorCallback((error, description) => throw new GLFWException(description, error));
        }

        if (recorded != null)
        {
            throw new GLFWException(recorded);
        }
    }

    private static int RunDesktop()
    {
        // RenderDoc hooks the GL driver when it loads, so it attaches before the first context (the probe below) exists.
        RenderDocHook.TryAttach(Environment.GetEnvironmentVariable);
        IReadOnlyList<Version> attempts = GlContextVersion.AttemptsFromEnvironment();
        Version version = attempts[0];
        if (attempts.Count > 1)
        {
            // GLFW init failures (no display, no driver) are not a version problem and propagate here.
            GLFWProvider.EnsureInitialized();
            version = SelectContextVersion(attempts, ProbeContext, Console.WriteLine);
        }

        // Game is constructed once, with no retry: only the context creation above is ever retried.
        using var game = new Game(version);
        try
        {
            game.Run();
        }
        catch (Exception ex)
        {
            // GameWindow.Run has no exception handling of its own, so a frame callback or a teardown
            // failure would otherwise end the process as an unhandled exception. Report it and return a
            // failure code; the `using` above still runs Game.Dispose, which stops the integrated server.
            Console.Error.WriteLine($"[fatal] desktop client stopped: {ex}");
            return DesktopFailureExitCode;
        }

        return 0;
    }

    // Returned when the desktop client ends on an exception rather than a clean window close.
    private const int DesktopFailureExitCode = 3;
}

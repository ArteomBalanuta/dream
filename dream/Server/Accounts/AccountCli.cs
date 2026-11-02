using Dream.Server.Characters;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Repositories;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Accounts;

// The `--accounts` command line: grant-admin <name> | revoke-admin <name> | unban <name>. <name> is a
// character of the account (display name, ignoring ASCII case). Admin is granted here and nowhere else.
// Like `--characters list`, it takes no world lease, so it runs while a server runs; the server reads the
// role when a player joins (an admin already online keeps the role they joined with), and a ban is read on
// the next join. A token hash is never printed. Output is tab-separated.
public static class AccountCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitNotFound = 2;
    public const int ExitSchemaTooNew = 3;
    public const int ExitWorldHeld = 7;

    public const string Usage = "Usage: --accounts grant-admin <name> | revoke-admin <name> | unban <name>";

    public static int Run(string[] args)
    {
        DatabaseMaintenanceLock? maintenance = null;
        try
        {
            return Run(
                args,
                () =>
                {
                    SqliteSchemaBootstrapper schema = new(ServerBootstrap.DefaultDatabasePath, ServerBootstrap.DefaultSchemaScriptPath);
                    maintenance = DatabaseMaintenanceLock.EnsureSchemaGuarded(schema, ServerBootstrap.DefaultDatabasePath, log: null);
                    return schema;
                },
                Console.Out,
                Console.Error);
        }
        finally
        {
            maintenance?.Dispose();
        }
    }

    public static int Run(string[] args, Func<SqliteSchemaBootstrapper> openSchema, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(openSchema);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? usageError = Parse(args, out string verb, out string name);
        if (usageError != null)
        {
            error.WriteLine(usageError);
            error.WriteLine(Usage);
            return ExitUsage;
        }

        try
        {
            SqliteSchemaBootstrapper schema = openSchema();
            SqliteAccountRepository accounts = new(schema);
            bool changed = verb switch
            {
                "grant-admin" => accounts.TrySetRole(name, 1),
                "revoke-admin" => accounts.TrySetRole(name, 0),
                _ => accounts.TrySetBan(name, null, null),
            };
            if (!changed)
            {
                error.WriteLine($"No account owns a character named '{CharacterCli.Sanitize(name)}'.");
                return ExitNotFound;
            }

            output.WriteLine($"{verb}\t{CharacterCli.Sanitize(name)}");
            return ExitOk;
        }
        catch (SchemaTooNewException ex)
        {
            error.WriteLine(
                $"The database schema is at version {ex.StoredVersion}, newer than the version " +
                $"{ex.LatestVersion} this build knows. Run a newer build.");
            return ExitSchemaTooNew;
        }
        catch (DatabaseBusyException ex)
        {
            error.WriteLine($"{ex.Message} Try again when it has finished.");
            return ExitWorldHeld;
        }
        catch (LockFileUnavailableException ex)
        {
            error.WriteLine($"Cannot create the lock file {ex.LockPath}: {ex.InnerException?.Message ?? ex.Message}");
            return ExitNotFound;
        }
        catch (SqliteException ex)
        {
            error.WriteLine($"The database could not be used: {ex.Message}.");
            return ExitNotFound;
        }
    }

    private static string? Parse(string[] args, out string verb, out string name)
    {
        verb = string.Empty;
        name = string.Empty;
        if (args.Length == 0)
        {
            return "Missing --accounts command.";
        }

        verb = args[0].ToLowerInvariant();
        if (verb is not ("grant-admin" or "revoke-admin" or "unban"))
        {
            return $"Unknown --accounts command '{CharacterCli.Sanitize(args[0])}'.";
        }

        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            return $"'{verb}' needs exactly one character name.";
        }

        name = args[1].Trim();
        return null;
    }
}

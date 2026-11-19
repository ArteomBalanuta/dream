using System.Diagnostics;
using System.Globalization;
using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Server;
using Dream.Simulation.Content;
using Dream.Simulation.Rules;

namespace Dream.Application;

// "dream --validate-content [--dir <contentDir>] [--ui <uiDir>] [--strict]": loads the content files
// through the one parser (ContentCatalog.LoadFromDirectory), runs the startup formula probe, and warns
// about content that has no art. No lock, no database, no window.
// Exit codes: 0 ok (warnings allowed), 1 usage, 8 invalid content / failed probe / warnings under --strict.
internal static class ContentValidateCli
{
    public const int UsageExitCode = 1;

    internal const string Usage =
        "usage: --validate-content [--dir <contentDir>] [--ui <uiDir>] [--strict]. " +
        "Formulas stored in a world database are not checked.";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string? dir = null;
        string? ui = null;
        bool strict = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            // A value that starts with "--" is the next option, not a path: "--dir --strict" is a usage error.
            bool hasValue = i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])
                && !args[i + 1].TrimStart().StartsWith("--", StringComparison.Ordinal);
            if (string.Equals(arg, "--strict", StringComparison.OrdinalIgnoreCase))
            {
                strict = true;
            }
            else if (string.Equals(arg, "--dir", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                dir = args[++i];
            }
            else if (string.Equals(arg, "--ui", StringComparison.OrdinalIgnoreCase) && hasValue)
            {
                ui = args[++i];
            }
            else
            {
                bool takesValue = string.Equals(arg, "--dir", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "--ui", StringComparison.OrdinalIgnoreCase);
                stderr.WriteLine(takesValue
                    ? $"error: '{arg}' needs a value (a path that does not start with '--')."
                    : $"error: unsupported argument '{arg}'.");
                stderr.WriteLine(Usage);
                return UsageExitCode;
            }
        }

        dir ??= ContentCatalog.ShippedDirectory;
        ui ??= AssetPaths.UiDirectory;

        long start = Stopwatch.GetTimestamp();
        ContentCatalog catalog;
        try
        {
            catalog = ContentCatalog.LoadFromDirectory(dir);
            ServerBootstrap.ProbeFormulas(CombatFormulaService.Default, catalog);
        }
        catch (ContentValidationException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return ServerBootstrap.ContentInvalidExitCode;
        }

        List<string> warnings = [];
        if (UiSpriteTable.TryLoad(ui, out UiSpriteTable? sprites, out string uiError) && sprites is not null)
        {
            CollectSpriteWarnings(catalog, sprites, warnings);
        }
        else
        {
            warnings.Add($"ui sprites not checked: {uiError}");
        }

        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        foreach (string w in warnings)
        {
            stdout.WriteLine($"warning: {w}");
        }

        stdout.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ok: digest={catalog.ContentDigest} items={catalog.Items.Count} enemies={catalog.Enemies.Count} " +
            $"loot={catalog.LootTables.Count} camps={catalog.Camps.Count} npcs={catalog.Npcs.Count} " +
            $"skills={catalog.Skills.Count} quests={catalog.Quests.Count} ({ms:F0} ms)"));
        return strict && warnings.Count > 0 ? ServerBootstrap.ContentInvalidExitCode : 0;
    }

    // The names the HUD looks up: a skill's iconKey (QuickbarHudState.ResolveEntry) and
    // "item.<item key>" (HudItemCatalog.TryGetIcon, which ignores case).
    private static void CollectSpriteWarnings(ContentCatalog catalog, UiSpriteTable sprites, List<string> warnings)
    {
        HashSet<string> itemIcons = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in sprites.Names)
        {
            if (name.StartsWith("item.", StringComparison.OrdinalIgnoreCase))
            {
                itemIcons.Add(name);
            }
        }

        foreach (SkillDefinition skill in catalog.Skills)
        {
            if (string.IsNullOrEmpty(skill.IconKey) || !sprites.Contains(skill.IconKey))
            {
                warnings.Add($"skill '{skill.Key}' has no sprite for iconKey '{skill.IconKey}'");
            }
        }

        foreach (ItemDefinition item in catalog.Items)
        {
            string name = UiSpriteNames.Item(item.Key);
            if (!itemIcons.Contains(name))
            {
                warnings.Add($"item '{item.Key}' has no sprite '{name}'");
            }
        }
    }
}

using System.Runtime.CompilerServices;
using Dream.Simulation.Content;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Support;

// The shipped content without its spawn camps (M2-B): the world-wide wolf population step (one wolf
// by day, two at night, a first wolf at the far corner) that every scenario test written before camps
// relies on. A runtime built with the shipped content now starts empty and fills its camps on the first
// tick. The test assembly makes this the default content of every runtime (DefaultEnemyContent), so the
// scenario tests keep the one wolf they were written for; the camp tests set EnemyContent to the shipped
// catalog or their own. Apply and AndTheFirstWolf put a single runtime back on it explicitly.
internal static class LegacyWolfPopulation
{
    [ModuleInitializer]
    internal static void UseAsTheDefaultContent() => WorldSessionRuntime.DefaultEnemyContent = Content;

    // Also without kill gold (M3): the scenario tests pin exact loot notices and inventory pushes, and a gold
    // notice would join every kill. The gold tests set EnemyContent to content that pays.
    private static readonly Lazy<ContentCatalog> Catalog = new(() => ContentCatalog.Load(
        Read(ContentCatalog.ItemsFileName),
        System.Text.RegularExpressions.Regex.Replace(Read(ContentCatalog.EnemiesFileName), "\"gold(Min|Max)\":\\s*\\d+", "\"gold$1\": 0"),
        Read(ContentCatalog.LootFileName)));

    public static ContentCatalog Content => Catalog.Value;

    public static WorldSessionRuntime Apply(WorldSessionRuntime runtime)
    {
        runtime.EnemyContent = Content;
        return runtime;
    }

    // Also spawns the first wolf when the world has no enemy (what a restore of an empty world used to do).
    public static WorldSessionRuntime AndTheFirstWolf(WorldSessionRuntime runtime)
    {
        Apply(runtime);
        if (runtime.Enemies.ActiveActors.Count == 0)
        {
            ((IRuntimePersistenceHost)runtime).SpawnInitialWolf();
        }

        return runtime;
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, fileName));
}

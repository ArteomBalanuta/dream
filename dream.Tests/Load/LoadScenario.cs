using System.Globalization;
using Dream.Simulation.World;

namespace Dream.Tests.Load;

// What each bot does. Rates are per bot per second; bots move every command frame.
public sealed record LoadActionMix(
    // Fraction of time a bot walks (the rest it stands still) in its seeded random walk.
    double WalkFraction = 0.75d,
    // How often a bot picks a new heading.
    double TurnsPerSecond = 0.75d,
    double JumpsPerSecond = 0.25d,
    // Build: switch to building mode, then press primary use a few frames later (the command path
    // the desktop client uses; player.action build/combat intents are being rejected by NETR-6).
    double BuildsPerSecond = 0.5d,
    // Attack: switch to combat mode, then press primary use.
    double AttacksPerSecond = 1d,
    // Sent through QueuePlayerAction, so the action path carries traffic too.
    double EquipActionsPerSecond = 0.2d)
{
    public static LoadActionMix Default { get; } = new();
}

public sealed record LoadScenario(
    string Name,
    int Players,
    int Enemies,
    int Cubes,
    int RubbleCells,
    int FloorSize,
    TimeSpan Duration,
    LoadActionMix Actions,
    // Soak churn: every interval one bot disconnects and a new bot joins with a fresh session id.
    TimeSpan? ReconnectInterval = null,
    // Forced full GCs (for flat-memory checks) are taken this often, plus once after warm-up and
    // once at the end. Null means only those two.
    TimeSpan? ForcedGcInterval = null,
    TimeSpan? WarmUp = null,
    int Seed = 20261028,
    // Largest frame payload either side accepts. Null uses the production directional limits
    // (FrameLimitOptions.Default).
    int? MaxFramePayloadBytes = null,
    int CommandRateHz = 64)
{
    // Overrides every scenario's duration (seconds), for quick harness checks.
    public const string DurationOverrideEnvironmentVariable = "DREAM_LOAD_DURATION_SECONDS";

    public TimeSpan EffectiveWarmUp => WarmUp ?? TimeSpan.FromSeconds(Math.Min(5d, Duration.TotalSeconds / 4d));

    public static LoadScenario Smoke8 { get; } = new(
        "Smoke8",
        Players: 8,
        Enemies: 4,
        Cubes: 2_000,
        RubbleCells: 100,
        FloorSize: WorldDefaults.StartupFloorSize,
        Duration: TimeSpan.FromSeconds(20),
        LoadActionMix.Default);

    // Smoke8's bots on a light world: the tick is cheap enough that the loop runs one tick per
    // iteration, so the snapshot interval (NETR-7) decides the snapshot rate. In Smoke8 the loop
    // already runs two ticks per iteration and sends 32 snapshots a second at any interval.
    public static LoadScenario Light8 { get; } = Smoke8 with
    {
        Name = "Light8",
        Cubes = 200,
        RubbleCells = 20
    };

    public static LoadScenario Players32 { get; } = Smoke8 with
    {
        Name = "Players32",
        Players = 32,
        Enemies = 8,
        Duration = TimeSpan.FromSeconds(30)
    };

    public static LoadScenario World10k { get; } = new(
        "World10k",
        Players: 8,
        Enemies: 8,
        Cubes: 10_000,
        RubbleCells: 1_000,
        FloorSize: 100,
        Duration: TimeSpan.FromSeconds(30),
        // The JoinAccepted world for 10k cubes was larger than the old symmetric 1 MB frame limit
        // (measured 2026-10-28: 0/8 bots joined). Since protocol v3 (SCALE-1) the world travels as
        // 64 KB chunks, so it fits the default 8 MB server-to-client limit with room to spare.
        LoadActionMix.Default);

    public static LoadScenario Soak { get; } = new(
        "Soak",
        Players: 8,
        Enemies: 4,
        Cubes: 2_000,
        RubbleCells: 100,
        FloorSize: WorldDefaults.StartupFloorSize,
        Duration: TimeSpan.FromMinutes(30),
        LoadActionMix.Default,
        ReconnectInterval: TimeSpan.FromSeconds(5),
        ForcedGcInterval: TimeSpan.FromMinutes(1),
        WarmUp: TimeSpan.FromMinutes(2));

    public LoadScenario WithEnvironmentOverrides()
    {
        string? value = Environment.GetEnvironmentVariable(DurationOverrideEnvironmentVariable);
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0d)
        {
            return this;
        }

        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        return this with
        {
            Duration = duration,
            WarmUp = WarmUp is { } warmUp && warmUp < duration / 2 ? warmUp : null
        };
    }
}

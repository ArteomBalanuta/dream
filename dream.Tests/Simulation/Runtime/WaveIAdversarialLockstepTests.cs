using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;

namespace Dream.Tests.Simulation.Runtime;

// Wave I tester: the Dire Wolf on the clock's edges, and two servers fed the same script of kills,
// deaths and respawns that must agree on every tick.
public sealed class WaveIAdversarialLockstepTests(ITestOutputHelper output)
{
    private static readonly WorldClockSettings OneMinuteDay = WorldClockSettings.FromDayLengthMinutes(1);
    private static readonly PlayerSessionId A = new("lock-a");
    private static readonly PlayerSessionId B = new("lock-b");

    private static WorldSessionRuntime Create(WorldClockSettings settings, long worldTicks)
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, WaveIRuntimeKit.FarStart, new SceneEventHub());
        runtime.AttachRemoteSession(A, A.Value);
        runtime.ConfigureWorldClock(settings, worldTicks);
        return runtime;
    }

    private static string Kinds(WorldSessionRuntime runtime) =>
        string.Join(',', runtime.Enemies.ActiveActors.Where(actor => actor.IsActive).Select(actor => actor.EnemyKind).Order(StringComparer.Ordinal));

    private static void KillAll(WorldSessionRuntime runtime, string? kind = null)
    {
        foreach (EnemyActor enemy in runtime.Enemies.ActiveActors.Where(actor => actor.IsActive && (kind == null || actor.EnemyKind == kind)).ToArray())
        {
            enemy.Combatant.Health.ApplyDamage(1_000_000);
        }
    }

    // ---- a Dire Wolf at the dawn boundary ----

    [Theory]
    [InlineData(-3)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ADireWolfKilledAroundTheDawnTick_LeavesOneWolfAndNoReplacementDire_InTwoLockstepRuns(int killOffset)
    {
        long dawn = WaveT2NightSpawnTests.WorldTicksAt(OneMinuteDay, dayIndex: 1, hour: 4);
        long start = dawn - 60;
        ulong dawnTick = (ulong)(dawn - start);
        using WorldSessionRuntime first = Create(OneMinuteDay, start);
        using WorldSessionRuntime second = Create(OneMinuteDay, start);
        ulong tick1 = 0;
        ulong tick2 = 0;
        List<string> history = [];
        for (ulong tick = 1; tick <= dawnTick + 20; tick++)
        {
            if (tick == (ulong)((long)dawnTick + killOffset))
            {
                KillAll(first, "DireWolf");
                KillAll(second, "DireWolf");
            }

            WaveIRuntimeKit.Tick(first, ref tick1);
            WaveIRuntimeKit.Tick(second, ref tick2);
            Assert.Equal(SimulationStateChecksum.Compute(first), SimulationStateChecksum.Compute(second));
            Assert.Equal(Kinds(first), Kinds(second));
            history.Add($"{tick}:{first.WorldClock.Phase}:{Kinds(first)}");
            if (tick == dawnTick)
            {
                Assert.NotEqual(WorldDayPhase.Night, first.WorldClock.Phase);
            }

            if (tick >= dawnTick + 2)
            {
                // Day: exactly the wolf, whichever way the dire met the dawn.
                Assert.Equal("Wolf", Kinds(first));
            }

            Assert.True(first.Enemies.ActiveActors.Count(actor => actor.IsActive) <= 2, history[^1]);
        }

        output.WriteLine(string.Join(" | ", history.Skip((int)dawnTick - 3).Take(6)));
    }

    // ---- nightfall: the first night tick, with both enemies killed over and over ----

    [Fact]
    public void KillingEveryEnemyOnTheNightfallTick_RebuildsOneWolfAndOneDire_OneChangePerTick_InTwoLockstepRuns()
    {
        long nightfall = WaveT2NightSpawnTests.WorldTicksAt(OneMinuteDay, dayIndex: 0, hour: 20);
        long start = nightfall - 4;
        ulong nightTick = (ulong)(nightfall - start);
        using WorldSessionRuntime first = Create(OneMinuteDay, start);
        using WorldSessionRuntime second = Create(OneMinuteDay, start);
        ulong tick1 = 0;
        ulong tick2 = 0;
        for (ulong tick = 1; tick <= nightTick + 8; tick++)
        {
            if (tick >= nightTick && tick <= nightTick + 2)
            {
                KillAll(first);
                KillAll(second);
            }

            WaveIRuntimeKit.Tick(first, ref tick1);
            WaveIRuntimeKit.Tick(second, ref tick2);
            Assert.Equal(SimulationStateChecksum.Compute(first), SimulationStateChecksum.Compute(second));
            Assert.Equal(Kinds(first), Kinds(second));
            Assert.True(first.Enemies.ActiveActors.Count(actor => actor.IsActive) <= 2, Kinds(first));
            output.WriteLine($"{tick} {first.WorldClock.Phase} {Kinds(first)}");
        }

        Assert.Equal("DireWolf,Wolf", Kinds(first));
    }

    // ---- lockstep across two servers with kills and respawns ----

    private sealed record ScriptResult(List<string> Log, int Kills, int Respawns, int Pushes, int Deaths, string[] FinalPacks);

    private static ScriptResult ScriptedRun()
    {
        using WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, WaveIRuntimeKit.FarStart, new SceneEventHub());
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 2, common: 3, uncommon: 2, rare: 1);
        runtime.ConfigureWorldClock(WorldClockSettings.Default, WaveT2NightSpawnTests.WorldTicksAt(WorldClockSettings.Default, dayIndex: 0, hour: 23));
        PlayerSessionId[] seats = [A, B];
        foreach (PlayerSessionId seat in seats)
        {
            ConnectedPlayer player = runtime.AttachRemoteSession(seat, seat.Value);
            Assert.True(runtime.InventoryService.Ensure(player.CharacterKey.Value));
        }

        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 3);
        WaveIRuntimeKit.EnterCombat(runtime, A);
        WaveIRuntimeKit.EnterCombat(runtime, B);

        List<string> log = [];
        List<SessionInventoryPush> drained = [];
        bool[] wasAlive = [true, true];
        int kills = 0;
        int respawns = 0;
        int pushes = 0;
        int deaths = 0;
        ulong[] sequence = [1UL << 40, 1UL << 41];
        for (int i = 0; i < 1500; i++)
        {
            for (int s = 0; s < seats.Length; s++)
            {
                bool hits = (i + (s * 41)) % 83 == 5 && runtime.Enemies.ActiveActors.Any(actor => actor.IsActive);
                if (hits)
                {
                    AuthoritativeActionResult result = WaveIRuntimeKit.HitWolf(runtime, seats[s], ++sequence[s]);
                    if (result.Payload is CombatResolutionResult { TargetDied: true })
                    {
                        kills++;
                    }

                    log.Add($"{tick}:hit{s}:{result.Accepted}:{(result.Payload as CombatResolutionResult)?.TargetDied}");
                }

                if ((i + (s * 97)) % 211 == 120)
                {
                    WaveIRuntimeKit.Kill(runtime, seats[s].Value);
                    deaths++;
                }
            }

            WaveIRuntimeKit.Tick(runtime, ref tick);
            if (runtime.HasPendingInventoryPushes)
            {
                runtime.DrainInventoryPushesInto(drained);
                foreach (SessionInventoryPush push in drained.OrderBy(p => p.SessionId.Value, StringComparer.Ordinal))
                {
                    pushes++;
                    log.Add($"{tick}:push:{push.SessionId}:{push.Message.Inventory?.Slots.Count(slot => slot.Item != null)}:" +
                        string.Join(',', push.Message.Notices.Select(n => $"{n.ItemDisplayName}/{n.Rarity}/{n.Lost}")));
                }

                drained.Clear();
            }

            StringBuilderLine(runtime, tick, seats, wasAlive, log, ref respawns);
        }

        string[] packs =
        [
            .. seats.Select(seat => string.Join(
                ';',
                runtime.TryCaptureInventorySnapshot(seat)!.Value.Slots.Where(slot => slot.Item != null).Select(slot =>
                    $"{slot.SlotIndex}:{slot.Item!.Value.ItemKind}:{slot.Item.Value.Rarity}:" +
                    string.Join(',', (slot.Item.Value.Modifiers ?? []).Select(m => $"{m.Stat}/{m.Op}/{BitConverter.SingleToInt32Bits(m.Value)}")))))
        ];
        return new ScriptResult(log, kills, respawns, pushes, deaths, packs);
    }

    private static void StringBuilderLine(WorldSessionRuntime runtime, ulong tick, PlayerSessionId[] seats, bool[] wasAlive, List<string> log, ref int respawns)
    {
        string line = $"{tick}:{SimulationStateChecksum.Compute(runtime)}:{Kinds(runtime)}";
        for (int s = 0; s < seats.Length; s++)
        {
            IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, seats[s].Value);
            if (!wasAlive[s] && target.IsAlive)
            {
                respawns++;
            }

            wasAlive[s] = target.IsAlive;
            line += $"|{s}:{(target.IsAlive ? 1 : 0)}:{target.Combatant.Health.CurrentHealth}/{target.Combatant.Health.MaxHealth}:" +
                $"{BitConverter.SingleToInt32Bits(target.Position.X)},{BitConverter.SingleToInt32Bits(target.Position.Y)},{BitConverter.SingleToInt32Bits(target.Position.Z)}";
        }

        log.Add(line);
    }

    [Fact]
    public void TwoServersFedTheSameKillsDeathsAndRespawns_AgreeOnEveryTickAndEveryItem()
    {
        ScriptResult first = ScriptedRun();
        ScriptResult second = ScriptedRun();

        output.WriteLine($"{first.Kills} kills, {first.Deaths} deaths, {first.Respawns} respawns, {first.Pushes} pushes, {first.Log.Count} log lines");
        Assert.Equal(first.Log.Count, second.Log.Count);
        for (int i = 0; i < first.Log.Count; i++)
        {
            Assert.True(first.Log[i] == second.Log[i], $"line {i}: {first.Log[i]} != {second.Log[i]}");
        }

        Assert.Equal(first.FinalPacks, second.FinalPacks);
        // The script must actually exercise what it claims to compare.
        Assert.True(first.Kills >= 5, $"only {first.Kills} kills");
        Assert.True(first.Deaths >= 4, $"only {first.Deaths} deaths");
        Assert.True(first.Respawns >= 3, $"only {first.Respawns} respawns");
        Assert.True(first.Pushes >= 3, $"only {first.Pushes} pushes");
        Assert.True(first.FinalPacks.Any(pack => pack.Length > 0), "no loot reached either backpack");
        output.WriteLine($"final packs: {string.Join(" || ", first.FinalPacks.Select(p => p.Length))}");
    }
}

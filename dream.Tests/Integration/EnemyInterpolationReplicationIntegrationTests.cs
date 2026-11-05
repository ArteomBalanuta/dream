using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// NET-4 under real replication: the production authoritative host runs the runtime's own wolf
// (first actor id 2000, 5.0 m/s) chasing a walking player, the player kills it and it respawns at
// its spawn point under a fresh actor id. Every snapshot goes through the wire codec and feeds a ServerTickClock and a
// ClientScenePresenter frame by frame, the PlayScene order. A lagging client is modelled with the
// server's own per-connection merge (ServerSnapshotEnvelope.TryCoalesce, what ServerConnection
// does while a writer is blocked): runs of snapshots are merged and arrive as one.
public class EnemyInterpolationReplicationIntegrationTests(ITestOutputHelper output)
{
    private const int WolfActorId = WorldSessionRuntime.FirstEnemyActorId;
    private const string Session = "session-a";
    private const float Tolerance = 1e-4f;
    private const float OnPathTolerance = 0.02f;

    // Coalescing windows while the wolf chases: (first tick of the window, snapshots merged). The
    // last one is deliberately longer than TickInterpolationBuffer.DefaultMaxTickSpan, so its
    // delivery does restart the buffer and AssertNoFalseTeleportWithinALife has a restart to judge.
    private static readonly (int Start, int Length)[] ChaseLag = [(40, 3), (60, 8), (90, 15), (130, 30), (180, 70)];

    [Fact]
    public void ChasingWolf_UnderCoalescedSnapshots_NeverRendersBackwards_AndARespawnSnaps()
    {
        using EnemyReplicationDriver driver = new(output, oldSpawn: true);
        Vector3 spawn = driver.WolfPosition!.Value;

        // The player runs off along +Z; the wolf chases. Several coalescing
        // windows of growing length hit the chase.
        const int ChaseTicks = 260;
        int chaseStart = driver.Tick + 1;
        for (int i = 0; i < ChaseTicks; i++)
        {
            bool lagging = ChaseLag.Any(window => i >= window.Start && i < window.Start + window.Length);
            // The player moves every tick and the wolf still keeps up, because the run cruise it
            // settles at (PlayerSimulationTuning.DefaultRunSpeed, 4.3 m/s) is below the wolf's
            // 5.0 m/s chase, so the wolf closes rather than losing aggro.
            //
            // This used to move 3 ticks in 10 to fake an average 1.8 m/s: back when the player had
            // one flat 6.0 m/s speed it outran the wolf outright, and throttling the duty cycle was
            // the only way to stay catchable. Two things from the gaits wave retired that trick:
            // the default cruise is now slower than the wolf, so no throttling is needed, and the
            // motor ramps instead of snapping to speed, so a 3-tick burst never escapes the ramp.
            // Left as bursts the player moved centimetres and the wolf never left its spawn.
            driver.Step(driver.Walk(forward: 1f), lagging);
        }

        int chaseEnd = driver.Tick;

        // Then it stops and fights; the death snapshot itself is merged into the next ones, so the
        // client never sees the wolf missing (the respawn decides between a snap and a slide).
        int deathTick = driver.FightUntilTheWolfDies(lagDeathTick: true);
        Vector3 deathPosition = driver.LastAlivePosition(life: 1);
        Assert.True((deathPosition - spawn).Length > ClientScenePresenter.EnemyTeleportDistance + 1f, $"The wolf died at {deathPosition}, too close to its spawn {spawn}.");
        driver.Idle(ticks: 40);

        driver.Report();

        // Server truth: within its first life the wolf only moved forward along +Z while chasing.
        IReadOnlyList<Vector3> serverChase = driver.ServerPath(life: 1, fromTick: chaseStart, toTick: chaseEnd);
        AssertMonotonic(serverChase, Vector3.UnitZ, "server wolf");
        Assert.True(serverChase[^1].Z - serverChase[0].Z > 4f, $"The wolf only chased {serverChase[^1].Z - serverChase[0].Z:F2} m.");

        // The client never draws the chasing wolf moving backwards, across every merged gap.
        List<RenderSample> chaseFrames = driver.Samples
            .Where(sample => sample.HasPose && sample.RenderTick >= chaseStart && sample.RenderTick <= chaseEnd && sample.Life == 1)
            .ToList();
        Assert.True(chaseFrames.Count > ChaseTicks / 2, $"Only {chaseFrames.Count} chase frames were sampled.");
        AssertMonotonic(chaseFrames.Select(sample => sample.Position).ToList(), Vector3.UnitZ, "rendered wolf");

        // Once the respawned wolf reaches the client it is drawn only where the respawned wolf
        // was, never part way between its death position and the spawn point.
        AssertRespawnSnaps(driver, deathTick);

        // No false snap: within one life no delivery restarted the buffer unless the gap was too
        // long to interpolate anyway.
        AssertNoFalseTeleportWithinALife(driver);
    }

    // The teleport threshold (4 m) against what a legitimately moving wolf can cover between two
    // samples the buffer still interpolates: TickInterpolationBuffer drops samples older than
    // DefaultMaxTickSpan ticks, so a longer coalescing gap restarts the timeline whatever the
    // distance. The wolf's top speed times that span must stay under the threshold.
    [Fact]
    public void TeleportThreshold_ExceedsTheWolfsReachWithinTheInterpolationSpan()
    {
        using EnemyReplicationDriver driver = new(output);
        float wolfSpeed = driver.WolfMoveSpeed;
        double maxGapSeconds = TickInterpolationBuffer.DefaultMaxTickSpan / (double)SimulationClock.AuthoritativeTicksPerSecond;
        double reach = wolfSpeed * maxGapSeconds;
        double ticksToThreshold = ClientScenePresenter.EnemyTeleportDistance / (wolfSpeed * SimulationClock.FixedDeltaSeconds);
        output.WriteLine(
            $"wolf {wolfSpeed} m/s x {TickInterpolationBuffer.DefaultMaxTickSpan} ticks ({maxGapSeconds:F3} s) = {reach:F3} m " +
            $"< {ClientScenePresenter.EnemyTeleportDistance} m; the threshold needs a {ticksToThreshold:F1}-tick gap");
        Assert.True(reach < ClientScenePresenter.EnemyTeleportDistance);
        Assert.True(ticksToThreshold > TickInterpolationBuffer.DefaultMaxTickSpan);

        // The runtime's persisted-enemy path uses the persisted MovementSpeed; the shipped wolf
        // persists its own chase speed, re-based from 2.8 to 5.0 this wave. The threshold above is
        // derived from the FASTEST shipped chase speed (the Grave Howler's 6.0), so it has to clear
        // that too - this test runs the wolf, which is not the worst case.
        Assert.Equal(5.0f, wolfSpeed);
        Assert.True(
            6.0f * maxGapSeconds < ClientScenePresenter.EnemyTeleportDistance,
            $"The fastest shipped chase speed covers {6.0f * maxGapSeconds:F2} m across the interpolation span, " +
            $"which the {ClientScenePresenter.EnemyTeleportDistance} m teleport threshold must exceed.");
    }

    // Regression for a production bug: a wolf killed near its spawn point slides back to the
    // spawn instead of snapping when the client never sees it missing. On the authoritative host
    // the kill is resolved after the enemies update (deferred primary use), so the kill tick's
    // snapshot has no wolf and the next tick's has the respawned wolf under the same actor id
    // (EnsureWolfExists used to reuse 2000). ClientScenePresenter.ApplyEnemySample
    // (ClientScenePresenter.cs:326-356) restarts the buffer only when the wolf was missing from a
    // snapshot or moved more than EnemyTeleportDistance (4 m). When the client lags, the server
    // merges the kill tick's snapshot into the next one (ServerSnapshotEnvelope.TryCoalesce keeps
    // the newer actors), the absence is lost, and a respawn within 4 m of the death position is
    // interpolated as a slide. Fixed on the server: every spawn takes a fresh actor id
    // (WorldSessionRuntime.AllocateEnemyActorId), so the respawn is a new actor with a new buffer.
    [Fact]
    public void WolfKilledNearItsSpawn_WithTheDeathSnapshotCoalesced_SnapsInsteadOfSliding()
    {
        RunNearSpawnDeath(lagDeathTick: true);
    }

    // The same kill near the spawn with every snapshot delivered: the client sees the wolf missing
    // for one snapshot and the respawn snaps.
    [Fact]
    public void WolfKilledNearItsSpawn_WithEverySnapshotDelivered_Snaps()
    {
        RunNearSpawnDeath(lagDeathTick: false);
    }

    // The server side of the fix: the respawned wolf is a different actor, so a client that never
    // saw the death still sees a new actor id and starts a fresh buffer for it.
    [Fact]
    public void RespawnedWolf_TakesAFreshActorId()
    {
        using EnemyReplicationDriver driver = new(output);
        driver.Idle(ticks: 60);
        driver.FightUntilTheWolfDies(lagDeathTick: false);
        driver.Idle(ticks: 5);

        Assert.Equal(WolfActorId, driver.WolfIdOfLife(1));
        Assert.Equal(WolfActorId + 1, driver.WolfIdOfLife(2));
        Assert.Equal(WolfActorId + 1, driver.CurrentWolf!.Combatant.Actor.Id);
    }

    private void RunNearSpawnDeath(bool lagDeathTick)
    {
        using EnemyReplicationDriver driver = new(output);
        Vector3 spawn = driver.WolfPosition!.Value;
        // The wolf closes in on the standing player, who then kills it next to the spawn point.
        driver.Idle(ticks: 60);
        int deathTick = driver.FightUntilTheWolfDies(lagDeathTick);
        Vector3 deathPosition = driver.LastAlivePosition(life: 1);
        output.WriteLine($"died at {deathPosition}, {(deathPosition - spawn).Length:F2} m from the spawn {spawn}");
        Assert.True((deathPosition - spawn).Length > 0.5f, "The wolf never left its spawn point.");
        Assert.True((deathPosition - spawn).Length < ClientScenePresenter.EnemyTeleportDistance);
        driver.Idle(ticks: 40);
        driver.Report();
        AssertRespawnSnaps(driver, deathTick);
    }

    // Once the respawned wolf's first snapshot has been applied, every frame draws the wolf where
    // the respawned wolf was at that render tick, or at its spawn point for render ticks before the
    // respawn: never at a position of its previous life, and never part way back from it.
    private static void AssertRespawnSnaps(EnemyReplicationDriver driver, int deathTick)
    {
        int respawnDelivery = driver.Samples.FindIndex(sample => sample.NewestDeliveredTick > (ulong)deathTick);
        Assert.True(respawnDelivery >= 0, "The respawn never reached the client.");
        IReadOnlyList<(int Tick, Vector3 Position)> secondLife = driver.ServerTrack(life: 2);
        Assert.NotEmpty(secondLife);
        int checkedFrames = 0;
        for (int i = respawnDelivery; i < driver.Samples.Count; i++)
        {
            RenderSample sample = driver.Samples[i];
            Assert.True(sample.HasPose, $"frame {sample.Frame}: the respawned wolf has no render pose.");
            Vector3 expected = PoseAt(secondLife, sample.RenderTick);
            float error = (sample.Position - expected).Length;
            Assert.True(
                error <= OnPathTolerance,
                $"frame {sample.Frame} (render tick {sample.RenderTick:F2}): the wolf is drawn at {sample.Position}, {error:F3} m from " +
                $"the respawned wolf's {expected} (spawned at tick {secondLife[0].Tick} at {secondLife[0].Position}, killed at tick {deathTick}).");
            checkedFrames++;
        }

        Assert.True(checkedFrames >= 10, $"Only {checkedFrames} frames after the respawn were checked.");
    }

    // The server track linearly interpolated at a render tick, clamped to its ends.
    private static Vector3 PoseAt(IReadOnlyList<(int Tick, Vector3 Position)> track, double renderTick)
    {
        if (renderTick <= track[0].Tick)
        {
            return track[0].Position;
        }

        for (int i = 1; i < track.Count; i++)
        {
            if (renderTick <= track[i].Tick)
            {
                float alpha = (float)((renderTick - track[i - 1].Tick) / (track[i].Tick - track[i - 1].Tick));
                return Vector3.Lerp(track[i - 1].Position, track[i].Position, alpha);
            }
        }

        return track[^1].Position;
    }

    private void AssertNoFalseTeleportWithinALife(EnemyReplicationDriver driver)
    {
        int restarts = 0;
        float maxStep = 0f;
        ulong maxGap = 0;
        foreach (Delivery delivery in driver.Deliveries)
        {
            if (!delivery.SameLifeAsPrevious)
            {
                continue;
            }

            maxStep = MathF.Max(maxStep, delivery.DistanceFromPrevious);
            maxGap = Math.Max(maxGap, delivery.Gap);
            if (delivery.SamplesBefore > 0 && delivery.SamplesAfter == 1)
            {
                restarts++;
                Assert.True(
                    delivery.Gap > TickInterpolationBuffer.DefaultMaxTickSpan,
                    $"tick {delivery.Tick}: the buffer restarted after a {delivery.Gap}-tick gap over {delivery.DistanceFromPrevious:F2} m " +
                    "within one life (a false teleport snap).");
            }
        }

        output.WriteLine($"same-life deliveries: largest gap {maxGap} ticks, largest step {maxStep:F3} m, restarts {restarts}");
        Assert.True(maxGap >= 30, $"The largest coalescing gap was only {maxGap} ticks.");
        // The premise of the check inside the loop: with no restart at all it never runs, and the
        // longest coalescing window is there precisely to force one.
        Assert.True(restarts > 0, $"No same-life delivery restarted the buffer (largest gap {maxGap} ticks).");
    }

    private static void AssertMonotonic(IReadOnlyList<Vector3> positions, Vector3 direction, string label)
    {
        for (int i = 1; i < positions.Count; i++)
        {
            float step = Vector3.Dot(positions[i] - positions[i - 1], direction);
            Assert.True(step >= -Tolerance, $"{label}: moved backwards at sample {i}: {positions[i - 1]} -> {positions[i]}");
        }
    }

    internal sealed record RenderSample(int Frame, double RenderTick, ulong NewestDeliveredTick, int Life, bool HasPose, Vector3 Position);

    internal sealed record Delivery(ulong Tick, ulong Gap, bool SameLifeAsPrevious, int SamplesBefore, int SamplesAfter, float DistanceFromPrevious);

    // The production authoritative host with one session, ticked in-process; each snapshot is
    // encoded and decoded like on the wire, merged while the client lags, and presented frame by
    // frame. It tracks the wolf's lives on the server (a life ends when the wolf is missing or its
    // actor id changes; each life has its own actor id).
    private sealed class EnemyReplicationDriver : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly DedicatedServerHost _host;
        private readonly TransportJsonCodec _codec = new();
        // Every export is encoded in order, as one connection's stream; the cache rebuilds full
        // actors from the delta frames as the client session does (SCALE-2).
        private readonly ActorStateCache _actorStates = new();
        private readonly ClientScenePresenter _presenter = new(Session);
        private readonly ServerTickClock _clock = new();
        private readonly List<(int Tick, int Life, Vector3 Position)> _serverWolf = [];
        private readonly Dictionary<ulong, int> _lifeByTick = [];
        private readonly Dictionary<int, int> _wolfIdByLife = [];
        private ServerSnapshotEnvelope? _held;
        private ulong _sequence;
        private ulong _newestDelivered;
        private int _life = 1;
        private bool _wolfWasPresent = true;
        private int _trackedWolfId = WolfActorId;
        private Vector3? _lastDeliveredWolf;
        private int _lastDeliveredLife;
        private int _frame;

        // The day wolf spawns in the far corner now, 47 m from the player's start. oldSpawn puts it back at
        // (5.5, 1, 2.5), next to the start, for the walking chase; otherwise the player is moved to 3 m from
        // the wolf's real spawn, so a respawn still lands near the death place. Either way the join
        // window is ended: these tests are about replication, not the window.
        public EnemyReplicationDriver(ITestOutputHelper output, bool oldSpawn = false)
        {
            _output = output;
            _host = ServerBootstrap.CreateAuthoritativeHost(persistence: null);
            Runtime = _host.Runtime!;
            Assert.Empty(_host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(Session, "Name A")));
            AdvanceOneTick();
            PlayerSessionId session = new(Session);
            Dream.Tests.Simulation.Runtime.WaveIRuntimeKit.EndProtection(Runtime, session);
            if (oldSpawn)
            {
                CurrentWolf!.SetPosition(new Vector3(5.5f, 1f, 2.5f));
            }
            else
            {
                Assert.True(Runtime.TryGetSessionPlayerTarget(session, out IAuthoritativePlayerTarget? target));
                target!.SetWorldPosition(CurrentWolf!.Position + new Vector3(-3f, 0.5f, 0f));
            }

            List<PendingConnectionReply> replies = [];
            _host.DrainPendingJoinRepliesInto(replies);
            JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(Assert.Single(replies, reply => reply.Message is JoinAcceptedMessage).Message);
            _presenter.ApplyJoinAccepted(accepted);
            _clock.Reset(accepted.CurrentServerTick);
            _ = _host.CreateSnapshotMessage();
            _newestDelivered = accepted.CurrentServerTick;
            Assert.NotNull(WolfPosition);
            Assert.Equal(WolfActorId, CurrentWolf!.Combatant.Actor.Id);
            _wolfIdByLife[_life] = WolfActorId;
        }

        public WorldSessionRuntime Runtime { get; }

        public int Tick => (int)Runtime.CurrentServerTick;

        public List<RenderSample> Samples { get; } = [];

        public List<Delivery> Deliveries { get; } = [];

        public float WolfMoveSpeed => CurrentWolf!.MoveSpeed;

        // The runtime's one wolf, whatever actor id its current life has.
        public EnemyActor? CurrentWolf => Runtime.Enemies.ActiveActors.SingleOrDefault(enemy => enemy.IsActive);

        public Vector3? WolfPosition => CurrentWolf?.Position;

        public int WolfIdOfLife(int life) => _wolfIdByLife[life];

        public PlayerCommandFrame Walk(float forward)
        {
            return Command(PlayerInteractionMode.Building, new PlayerMoveIntent(forward, 0f), Vector3.UnitZ, attack: false);
        }

        public PlayerCommandFrame Stand()
        {
            return Command(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        }

        public void Idle(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Step(Command(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false), lagging: false);
            }
        }

        // Attacks the wolf every tick until it dies; returns the kill tick, whose snapshot lacks the
        // wolf. With lagDeathTick the client lags exactly over the kill: the kill tick's snapshot is
        // merged into the next tick's, which already carries the respawned wolf.
        public int FightUntilTheWolfDies(bool lagDeathTick)
        {
            for (int i = 0; i < 2000; i++)
            {
                Vector3 eye = PlayerPosition + new Vector3(0f, 0.35f, 0f);
                EnemyActor wolf = CurrentWolf!;
                Vector3 toWolf = wolf.Combatant.GetCombatAnchorWorldPosition() - eye;
                Step(Command(PlayerInteractionMode.Combat, PlayerMoveIntent.None, toWolf.Normalized(), attack: true), lagging: false, holdIfTheWolfDied: lagDeathTick);
                if (WolfPosition == null)
                {
                    int deathTick = Tick;
                    Step(Command(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false), lagging: false);
                    Assert.NotNull(WolfPosition);
                    return deathTick;
                }
            }

            throw new InvalidOperationException($"The wolf never died: player {PlayerPosition}, wolf {WolfPosition}.");
        }

        public Vector3 LastAlivePosition(int life) => _serverWolf.Last(entry => entry.Life == life).Position;

        public IReadOnlyList<(int Tick, Vector3 Position)> ServerTrack(int life)
        {
            return _serverWolf.Where(entry => entry.Life == life).Select(entry => (entry.Tick, entry.Position)).ToList();
        }

        public IReadOnlyList<Vector3> ServerPath(int life, int fromTick, int toTick)
        {
            return _serverWolf
                .Where(entry => entry.Life == life && entry.Tick >= fromTick && entry.Tick <= toTick)
                .Select(entry => entry.Position)
                .ToList();
        }

        public void Step(PlayerCommandFrame command, bool lagging, bool holdIfTheWolfDied = false)
        {
            _host.HandleClientMessage("connection-a", new PlayerCommandMessage(command));
            AdvanceOneTick();
            _host.DrainPendingActionResults();
            ServerSnapshotMessage sent = _host.CreateSnapshotMessage();
            TrackServerWolf();
            lagging |= holdIfTheWolfDied && WolfPosition == null;

            ServerSnapshotEnvelope snapshot = RoundTrip(sent).Snapshot;
            if (_held is ServerSnapshotEnvelope held)
            {
                Assert.True(ServerSnapshotEnvelope.TryCoalesce(held, snapshot, out ServerSnapshotEnvelope merged));
                _held = merged;
            }
            else
            {
                _held = snapshot;
            }

            // A frame per tick: advance the clock, apply what arrived, then render.
            _clock.Advance(SimulationClock.FixedDeltaSeconds);
            if (!lagging)
            {
                Deliver(_held.Value);
                _held = null;
            }

            Render();
        }

        public void Report()
        {
            foreach (Delivery delivery in Deliveries.Where(delivery => delivery.Gap > 1 || !delivery.SameLifeAsPrevious))
            {
                _output.WriteLine(
                    $"  delivered tick {delivery.Tick}: gap {delivery.Gap}, same life {delivery.SameLifeAsPrevious}, " +
                    $"samples {delivery.SamplesBefore}->{delivery.SamplesAfter}, step {delivery.DistanceFromPrevious:F3} m");
            }
        }

        public void Dispose()
        {
            _host.Dispose();
        }

        private Vector3 PlayerPosition => Runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == Session).Position;

        private PlayerCommandFrame Command(PlayerInteractionMode mode, PlayerMoveIntent move, Vector3 facing, bool attack)
        {
            return new PlayerCommandFrame(
                new PlayerSessionId(Session),
                ++_sequence,
                new PlayerModeSwitchIntent(mode),
                move,
                new PlayerLookIntent(0f, 0f, facing),
                PrimaryUsePressed: attack);
        }

        private void TrackServerWolf()
        {
            EnemyActor? current = CurrentWolf;
            if (current == null)
            {
                if (_wolfWasPresent)
                {
                    _life++;
                }

                _wolfWasPresent = false;
                return;
            }

            int wolfId = current.Combatant.Actor.Id;
            if (_wolfWasPresent && wolfId != _trackedWolfId)
            {
                _life++;
            }

            _wolfWasPresent = true;
            _trackedWolfId = wolfId;
            _wolfIdByLife[_life] = wolfId;
            Vector3? wolf = current.Position;
            _lifeByTick[Runtime.CurrentServerTick] = _life;
            _serverWolf.Add((Tick, _life, wolf.Value));
        }

        private void Deliver(ServerSnapshotEnvelope snapshot)
        {
            ActorSnapshot? wolf = snapshot.Actors.Where(actor => actor.Kind == CreatureKind.Enemy && actor.IsAlive).Cast<ActorSnapshot?>().SingleOrDefault();
            int deliveredWolfId = wolf?.ActorId ?? WolfActorId;
            int before = _presenter.GetRemoteEnemySampleCount(deliveredWolfId);
            _clock.Observe(snapshot.ServerTick);
            _presenter.ApplySnapshot(snapshot);
            int after = _presenter.GetRemoteEnemySampleCount(deliveredWolfId);
            if (wolf != null)
            {
                int life = _lifeByTick[snapshot.ServerTick];
                Assert.Equal(_wolfIdByLife[life], wolf.Value.ActorId);
                Deliveries.Add(new Delivery(
                    snapshot.ServerTick,
                    snapshot.ServerTick - _newestDelivered,
                    life == _lastDeliveredLife && _lastDeliveredWolf.HasValue,
                    before,
                    after,
                    _lastDeliveredWolf.HasValue ? (wolf.Value.Position - _lastDeliveredWolf.Value).Length : 0f));
                _lastDeliveredWolf = wolf.Value.Position;
                _lastDeliveredLife = life;
            }
            else
            {
                _lastDeliveredWolf = null;
                _lastDeliveredLife = 0;
            }

            _newestDelivered = snapshot.ServerTick;
        }

        private void Render()
        {
            double renderTick = _clock.RenderTick;
            int life = _lifeByTick.TryGetValue(_newestDelivered, out int deliveredLife) ? deliveredLife : 0;
            // The client draws the wolf of the newest delivered life, under that life's actor id.
            int wolfId = _wolfIdByLife.TryGetValue(life, out int id) ? id : WolfActorId;
            bool hasPose = _presenter.TryGetEnemyRenderPose(wolfId, renderTick, out InterpolatedActorPose pose);
            Samples.Add(new RenderSample(_frame++, renderTick, _newestDelivered, life, hasPose, hasPose ? pose.Position : default));
        }

        private ServerSnapshotMessage RoundTrip(ServerSnapshotMessage message)
        {
            byte[] payload = _codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, message.Snapshot.ServerTick), message);
            return _actorStates.Apply(Assert.IsType<ServerSnapshotMessage>(_codec.Decode(payload).Message));
        }

        private void AdvanceOneTick()
        {
            int executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            if (executed == 0)
            {
                executed = _host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
            }

            Assert.Equal(1, executed);
        }
    }
}

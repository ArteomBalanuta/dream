using BulletSharp;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;

// NETR-6: the server validates client input at the protocol boundary. Build and combat use are
// server-created from command-frame primary use only, command frames are sanitized before they
// reach the simulation, and the action queue is bounded and purged when a connection goes away.
public class ClientInputValidationTests
{
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";

    public static TheoryData<IPlayerActionPayload> ServerOnlyPayloads => new()
    {
        new BuildUseIntent(new Vector3(6f, 1f, 5f)),
        new CombatUseIntent(new Vector3(5f, 1.85f, 5f), -Vector3.UnitZ),
        new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(10, 2.5f, 12))
    };

    [Theory]
    [MemberData(nameof(ServerOnlyPayloads))]
    public void WireBuildOrCombatUse_IsRejected_AndNeverQueued(IPlayerActionPayload payload)
    {
        PlayerActionQueue queue = new();
        ServerRoutingContext context = new("connection-1", new FakeAuthority(), actionQueue: queue)
        {
            LastJoinRequest = new ClientJoinRequestMessage(SessionA, "player-a")
        };

        new PlayerActionHandler().Handle(context, new PlayerActionMessage(new PlayerSessionId(SessionA), 5, payload));

        ServerActionMessage reply = Assert.IsType<ServerActionMessage>(Assert.Single(context.OutboundMessages));
        Assert.False(reply.Result.Accepted);
        Assert.Equal((ulong)5, reply.Result.ActionSequence);
        Assert.Equal(SessionA, reply.Result.SessionId);
        string reason = Assert.IsType<RejectedUseDelta>(reply.Result.Rejection).Reason;
        Assert.Contains(payload.GetType().Name, reason, StringComparison.Ordinal);
        Assert.Contains("cannot be sent by clients", reason, StringComparison.Ordinal);
        Assert.Empty(queue.Drain([new PlayerSessionId(SessionA)]));
    }

    [Fact]
    public void WireEquipAndAttributeSpend_AreStillQueued()
    {
        PlayerActionQueue queue = new();
        ServerRoutingContext context = new("connection-1", new FakeAuthority { DevCommandsAllowed = true }, actionQueue: queue)
        {
            LastJoinRequest = new ClientJoinRequestMessage(SessionA, "player-a")
        };
        PlayerActionHandler handler = new();

        handler.Handle(context, new PlayerActionMessage(new PlayerSessionId(SessionA), 1, new InventoryEquipIntent(2)));
        handler.Handle(context, new PlayerActionMessage(
            new PlayerSessionId(SessionA),
            2,
            new PlayerAttributeSpendIntent(Dream.Simulation.Stats.StatId.Strength, 1)));

        Assert.Empty(context.OutboundMessages);
        Assert.Collection(
            queue.Drain([new PlayerSessionId(SessionA)]),
            first => Assert.IsType<InventoryEquipIntent>(first.Message.Payload),
            second => Assert.IsType<PlayerAttributeSpendIntent>(second.Message.Payload));
    }

    [Fact]
    public void DedicatedHost_HostileNoclipFreezeAndStats_AreIgnoredOrRejected()
    {
        FakeAuthority authority = new();
        LatestPlayerCommandInbox inbox = new();
        PlayerActionQueue queue = new();
        ServerRoutingContext context = new("connection-1", authority, inbox, queue)
        {
            LastJoinRequest = new ClientJoinRequestMessage(SessionA, "player-a")
        };
        PlayerSessionId session = new(SessionA);

        new PlayerCommandHandler().Handle(context, new PlayerCommandMessage(new PlayerCommandFrame(
            session,
            1,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(1f, 0f, 0f, JumpPressed: true, FreezePressed: true, ToggleNoclipPressed: true),
            new PlayerLookIntent(0f, 0f, null),
            PrimaryUsePressed: true)));
        new PlayerActionHandler().Handle(context, new PlayerActionMessage(
            session,
            2,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(10, 2.5f, 12))));

        Assert.True(inbox.TryDrain(session, out PlayerCommandFrame frame));
        Assert.False(frame.MoveIntent.FreezePressed);
        Assert.False(frame.MoveIntent.ToggleNoclipPressed);
        Assert.True(frame.MoveIntent.JumpPressed);
        Assert.Equal(1f, frame.MoveIntent.ForwardAxis);
        Assert.True(frame.PrimaryUsePressed);
        Assert.False(Assert.IsType<ServerActionMessage>(Assert.Single(context.OutboundMessages)).Result.Accepted);
        Assert.Empty(queue.Drain([session]));

        authority.DevCommandsAllowed = true;
        new PlayerCommandHandler().Handle(context, new PlayerCommandMessage(frame with
        {
            Sequence = 3,
            MoveIntent = frame.MoveIntent with { FreezePressed = true, ToggleNoclipPressed = true }
        }));
        Assert.True(inbox.TryDrain(session, out PlayerCommandFrame allowed));
        Assert.True(allowed.MoveIntent.FreezePressed);
        Assert.True(allowed.MoveIntent.ToggleNoclipPressed);
    }

    [Fact]
    public void RuntimeHost_WireBuildUse_IsRejectedImmediately_AndPlacesNoCube()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(SessionA, "A"))));
        Vector3 eye = host.Runtime!.CaptureActorSnapshots().Single(actor => actor.SessionId == SessionA).Position;
        // A cell within the old 3 m range, which the wire path used to accept without cadence.
        Vector3 cell = new(MathF.Round(eye.X) + 1f, 1f, MathF.Round(eye.Z));
        int cubesBefore = host.Runtime.Map.SolidCubes.Count;

        IReadOnlyList<IServerToClientMessage> reply = host.HandleClientMessage(
            "connection-a",
            new PlayerActionMessage(new PlayerSessionId(SessionA), 1, new BuildUseIntent(cell)));
        for (int i = 0; i < 4; i++)
        {
            host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        }

        Assert.False(Assert.IsType<ServerActionMessage>(Assert.Single(reply)).Result.Accepted);
        Assert.Empty(host.DrainPendingActionResults());
        Assert.Equal(cubesBefore, host.Runtime.Map.SolidCubes.Count);
    }

    [Fact]
    public void SanitizeCommandFrame_ClampsAxes_DropsNonFiniteLook_AndKeepsTheRest()
    {
        PlayerCommandFrame frame = new(
            new PlayerSessionId(SessionA),
            42,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
            new PlayerMoveIntent(3e38f, float.NaN, float.NegativeInfinity, JumpPressed: true, FreezePressed: true, ToggleNoclipPressed: true),
            new PlayerLookIntent(float.PositiveInfinity, float.NaN, new Vector3(float.NaN, 0f, 1f)),
            PrimaryUsePressed: true);

        PlayerCommandFrame sanitized = PlayerCommandHandler.SanitizeCommandFrame(frame);

        Assert.Equal(1f, sanitized.MoveIntent.ForwardAxis);
        Assert.Equal(0f, sanitized.MoveIntent.StrafeAxis);
        Assert.Equal(0f, sanitized.MoveIntent.VerticalAxis);
        Assert.Equal(0f, sanitized.LookIntent.YawDelta);
        Assert.Equal(0f, sanitized.LookIntent.PitchDelta);
        Assert.Null(sanitized.LookIntent.Facing);
        // The one-argument overload adds no noclip or freeze gate (the handler applies it).
        Assert.True(sanitized.MoveIntent.JumpPressed);
        Assert.True(sanitized.MoveIntent.FreezePressed);
        Assert.True(sanitized.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(frame.SessionId, sanitized.SessionId);
        Assert.Equal(frame.Sequence, sanitized.Sequence);
        Assert.Equal(frame.RequestedMode, sanitized.RequestedMode);
        Assert.True(sanitized.PrimaryUsePressed);
    }

    [Fact]
    public void SanitizeCommandFrame_ClampsNegativeAxes_DropsZeroFacing_AndLeavesValidFramesAlone()
    {
        PlayerCommandFrame wild = new(
            new PlayerSessionId(SessionA),
            1,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(-3e38f, -2f, 0.25f),
            new PlayerLookIntent(1.5f, -0.5f, Vector3.Zero),
            PrimaryUsePressed: false);

        PlayerCommandFrame sanitized = PlayerCommandHandler.SanitizeCommandFrame(wild);

        Assert.Equal(-1f, sanitized.MoveIntent.ForwardAxis);
        Assert.Equal(-1f, sanitized.MoveIntent.StrafeAxis);
        Assert.Equal(0.25f, sanitized.MoveIntent.VerticalAxis);
        Assert.Equal(1.5f, sanitized.LookIntent.YawDelta);
        Assert.Equal(-0.5f, sanitized.LookIntent.PitchDelta);
        Assert.Null(sanitized.LookIntent.Facing);

        PlayerCommandFrame valid = new(
            new PlayerSessionId(SessionA),
            2,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(0.5f, -1f, 0f),
            new PlayerLookIntent(0.1f, 0.2f, new Vector3(0f, 0f, -2f)),
            PrimaryUsePressed: true);
        Assert.Equal(valid, PlayerCommandHandler.SanitizeCommandFrame(valid));
    }

    [Fact]
    public void CommandHandler_EnqueuesTheSanitizedFrame()
    {
        LatestPlayerCommandInbox inbox = new();
        ServerRoutingContext context = new("connection-1", new FakeAuthority(), commandInbox: inbox)
        {
            LastJoinRequest = new ClientJoinRequestMessage(SessionA, "player-a")
        };

        new PlayerCommandHandler().Handle(context, new PlayerCommandMessage(new PlayerCommandFrame(
            new PlayerSessionId(SessionA),
            3,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(3e38f, float.NaN),
            new PlayerLookIntent(float.NaN, 0f, new Vector3(float.PositiveInfinity, 0f, 0f)),
            PrimaryUsePressed: false)));

        List<PlayerCommandFrame> drained = [];
        inbox.DrainInto([new PlayerSessionId(SessionA)], drained);
        PlayerCommandFrame frame = Assert.Single(drained);
        Assert.Equal(1f, frame.MoveIntent.ForwardAxis);
        Assert.Equal(0f, frame.MoveIntent.StrafeAxis);
        Assert.Equal(0f, frame.LookIntent.YawDelta);
        Assert.Null(frame.LookIntent.Facing);
    }

    // Noclip and freeze are dev movement: a dedicated server (the default) clears them, the
    // integrated single-player host (allowDevMovement) passes them through.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandHandler_ClearsNoclipAndFreeze_UnlessDevMovementIsAllowed(bool allowDevMovement)
    {
        LatestPlayerCommandInbox inbox = new();
        ServerRoutingContext context = new("connection-1", new FakeAuthority(), commandInbox: inbox)
        {
            LastJoinRequest = new ClientJoinRequestMessage(SessionA, "player-a")
        };

        new PlayerCommandHandler(allowDevMovement).Handle(context, new PlayerCommandMessage(new PlayerCommandFrame(
            new PlayerSessionId(SessionA),
            4,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(0.5f, 0f, 0f, JumpPressed: true, FreezePressed: true, ToggleNoclipPressed: true),
            new PlayerLookIntent(0f, 0f, null),
            PrimaryUsePressed: false)));

        List<PlayerCommandFrame> drained = [];
        inbox.DrainInto([new PlayerSessionId(SessionA)], drained);
        PlayerCommandFrame frame = Assert.Single(drained);
        Assert.Equal(allowDevMovement, frame.MoveIntent.FreezePressed);
        Assert.Equal(allowDevMovement, frame.MoveIntent.ToggleNoclipPressed);
        Assert.True(frame.MoveIntent.JumpPressed);
        Assert.Equal(0.5f, frame.MoveIntent.ForwardAxis);
        Assert.False(new ServerStartOptions().AllowDevMovement);
    }

    [Fact]
    public void HugeAndNaNAxes_OverTheWire_KeepEveryBulletBodyFinite_AndThePlayerStillWalks()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(SessionA, "A"));
        host.HandleClientMessage("connection-b", new ClientJoinRequestMessage(SessionB, "B"));
        WorldSessionRuntime runtime = host.Runtime!;
        Vector3 startA = PositionOf(runtime, SessionA);

        for (int tick = 1; tick <= 60; tick++)
        {
            // Session A (the primary seat) pushes a huge finite axis, B (a secondary seat) NaN;
            // halfway through they swap, so both seats see both kinds of bad input.
            float huge = tick % 2 == 0 ? 3e38f : -3e38f;
            bool swap = tick > 30;
            host.HandleClientMessage("connection-a", new PlayerCommandMessage(Command(SessionA, tick, swap ? float.NaN : 3e38f, huge)));
            host.HandleClientMessage("connection-b", new PlayerCommandMessage(Command(SessionB, tick, swap ? 3e38f : float.NaN, float.NaN)));
            host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
            AssertEveryBodyFinite(runtime, tick);
        }

        foreach (ActorSnapshot actor in runtime.CaptureActorSnapshots())
        {
            Assert.True(IsFinite(actor.Position), $"Actor {actor.ActorId} has a non-finite position {actor.Position}.");
        }

        Vector3 velocity = runtime.Player.PhysicsBody.LinearVelocity.ToOpenTK();
        Assert.True(IsFinite(velocity), $"The primary seat's velocity is {velocity}.");
        // The clamped axis still moves the player: the bad input became full-speed input, not NaN.
        Vector3 moved = PositionOf(runtime, SessionA) - startA;
        Assert.True(new Vector2(moved.X, moved.Z).Length > 0.1f, $"Session A moved only {moved}.");
    }

    [Fact]
    public void ActionQueue_RejectsThe17thPendingActionOfASession_ButNotOtherSessions()
    {
        PlayerActionQueue queue = new();
        PlayerSessionId a = new(SessionA);
        PlayerSessionId b = new(SessionB);

        for (int i = 1; i <= PlayerActionQueue.MaxPendingActionsPerSession; i++)
        {
            Assert.True(queue.TryEnqueue(Equip(a, (ulong)i), out _));
        }

        Assert.Equal(16, PlayerActionQueue.MaxPendingActionsPerSession);
        Assert.False(queue.TryEnqueue(Equip(a, 17), out _));
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(Equip(a, 18)));
        Assert.True(queue.TryEnqueue(Equip(b, 1), out _));

        // A drain frees the session's slots again.
        Assert.Equal(16, queue.Drain([a]).Count);
        Assert.True(queue.TryEnqueue(Equip(a, 19), out _));
    }

    [Fact]
    public void Handler_Answers_The17thQueuedAction_WithARejection()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(SessionA, "A"));

        for (int i = 1; i <= PlayerActionQueue.MaxPendingActionsPerSession; i++)
        {
            Assert.Empty(host.HandleClientMessage("connection-a", Equip(new PlayerSessionId(SessionA), (ulong)i)));
        }

        IReadOnlyList<IServerToClientMessage> overflow = host.HandleClientMessage("connection-a", Equip(new PlayerSessionId(SessionA), 17));

        AuthoritativeActionResult rejected = Assert.IsType<ServerActionMessage>(Assert.Single(overflow)).Result;
        Assert.False(rejected.Accepted);
        Assert.Equal((ulong)17, rejected.ActionSequence);
        Assert.Contains("Too many pending player actions", Assert.IsType<RejectedUseDelta>(rejected.Rejection).Reason, StringComparison.Ordinal);

        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        IReadOnlyList<AuthoritativeActionResult> processed = host.DrainPendingActionResults();
        Assert.Equal(16, processed.Count);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (ulong)i), processed.Select(result => result.ActionSequence));
    }

    [Fact]
    public void ActionQueue_RemoveSession_DropsOnlyThatSessionsActions()
    {
        PlayerActionQueue queue = new();
        PlayerSessionId a = new(SessionA);
        PlayerSessionId b = new(SessionB);
        queue.Enqueue(Equip(a, 1));
        queue.Enqueue(Equip(b, 1));
        queue.Enqueue(Equip(a, 2));

        Assert.Equal(2, queue.RemoveSession(a));
        Assert.Equal(0, queue.RemoveSession(a));

        Assert.Empty(queue.Drain([a]));
        Assert.Equal(SessionB, Assert.Single(queue.Drain([b])).SessionId.Value);
    }

    [Fact]
    public void ReleasedConnection_PurgesItsSessionsQueuedActions()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(SessionA, "A"));
        for (int i = 1; i <= 3; i++)
        {
            Assert.Empty(host.HandleClientMessage("connection-a", Equip(new PlayerSessionId(SessionA), (ulong)i)));
        }

        host.ReleaseConnection("connection-a");
        // The session rejoins on a new connection; its old connection's actions must not run.
        host.HandleClientMessage("connection-a2", new ClientJoinRequestMessage(SessionA, "A"));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);

        Assert.Empty(host.DrainPendingActionResults());

        // A full backlog is purged too, so the rejoined session can queue again at once.
        for (int i = 1; i <= PlayerActionQueue.MaxPendingActionsPerSession; i++)
        {
            Assert.Empty(host.HandleClientMessage("connection-a2", Equip(new PlayerSessionId(SessionA), (ulong)(10 + i))));
        }

        host.ReleaseConnection("connection-a2");
        host.HandleClientMessage("connection-a3", new ClientJoinRequestMessage(SessionA, "A"));
        Assert.Empty(host.HandleClientMessage("connection-a3", Equip(new PlayerSessionId(SessionA), 100)));
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Equal((ulong)100, Assert.Single(host.DrainPendingActionResults()).ActionSequence);
    }

    private static PlayerActionMessage Equip(PlayerSessionId session, ulong sequence)
    {
        return new PlayerActionMessage(session, sequence, new InventoryEquipIntent(0));
    }

    private static PlayerCommandFrame Command(string session, int tick, float forward, float strafe)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(session),
            (ulong)tick,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(forward, strafe),
            new PlayerLookIntent(float.NaN, 3e38f, new Vector3(float.NaN, float.NaN, float.NaN)),
            PrimaryUsePressed: false);
    }

    private static Vector3 PositionOf(WorldSessionRuntime runtime, string session)
    {
        return runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == session).Position;
    }

    private static void AssertEveryBodyFinite(WorldSessionRuntime runtime, int tick)
    {
        AlignedCollisionObjectArray objects = runtime.Physics.World.CollisionObjectArray;
        for (int i = 0; i < objects.Count; i++)
        {
            CollisionObject collisionObject = objects[i];
            Vector3 origin = collisionObject.WorldTransform.Origin.ToOpenTK();
            Assert.True(IsFinite(origin), $"Tick {tick}: body {i} is at {origin}.");
            if (collisionObject is RigidBody body)
            {
                Vector3 linear = body.LinearVelocity.ToOpenTK();
                Vector3 angular = body.AngularVelocity.ToOpenTK();
                Assert.True(IsFinite(linear), $"Tick {tick}: body {i} has linear velocity {linear}.");
                Assert.True(IsFinite(angular), $"Tick {tick}: body {i} has angular velocity {angular}.");
            }
        }
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private sealed class FakeAuthority : IAuthoritativeSessionHost
    {
        public bool DevCommandsAllowed { get; set; }

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName) => throw new NotSupportedException();

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message) => throw new NotSupportedException();

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false) => throw new NotSupportedException();
    }
}

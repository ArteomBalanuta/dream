using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// RuntimeAuthoritativeSessionHost.JoinSession for a session id the runtime already knows: a
// duplicate join from a session that is still attached, and a rejoin after ReleaseSession. The
// existing-session branch routes through WorldSessionRuntime.AttachRemoteSession; these pin down that
// it neither duplicates a body for an attached session nor leaks one over repeated rejoins.
public class RuntimeAuthoritativeSessionHostRejoinTests
{
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";
    private const string SessionC = "session-c";

    [Fact]
    public void DuplicateJoin_FromAttachedSecondary_DoesNotCreateASecondBody()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        host.JoinSession(SessionA, SessionA);
        JoinAcceptedMessage first = host.JoinSession(SessionB, SessionB);
        int collisionObjects = host.Runtime.Physics.World.NumCollisionObjects;
        int actors = host.Runtime.CaptureActorSnapshots().Count;

        JoinAcceptedMessage again = host.JoinSession(SessionB, SessionB);

        Assert.Equal(first.ActorId, again.ActorId);
        Assert.Equal(collisionObjects, host.Runtime.Physics.World.NumCollisionObjects);
        Assert.Equal(actors, host.Runtime.CaptureActorSnapshots().Count);
        Assert.Single(host.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == SessionB);
        Assert.Equal(SessionA, host.Runtime.PrimaryRemoteSessionId?.Value);
    }

    [Fact]
    public void DuplicateJoin_FromAttachedPrimary_KeepsTheSeatAndCreatesNoBody()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        JoinAcceptedMessage first = host.JoinSession(SessionA, SessionA);
        host.JoinSession(SessionB, SessionB);
        int collisionObjects = host.Runtime.Physics.World.NumCollisionObjects;
        int actors = host.Runtime.CaptureActorSnapshots().Count;

        JoinAcceptedMessage again = host.JoinSession(SessionA, SessionA);

        Assert.Equal(first.ActorId, again.ActorId);
        Assert.Equal(SessionA, host.Runtime.PrimaryRemoteSessionId?.Value);
        Assert.False(host.Runtime.Physics.IsPlayerBodyParked);
        Assert.Equal(collisionObjects, host.Runtime.Physics.World.NumCollisionObjects);
        Assert.Equal(actors, host.Runtime.CaptureActorSnapshots().Count);
        Assert.Single(host.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == SessionA);
    }

    [Fact]
    public void FormerPrimary_RejoinsAfterTheSeatWasTaken_GetsItsOwnSecondarySeat()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        JoinAcceptedMessage primaryJoin = host.JoinSession(SessionA, SessionA);
        host.JoinSession(SessionB, SessionB);
        host.ReleaseSession(SessionA);
        host.JoinSession(SessionC, SessionC);
        Assert.Equal(SessionC, host.Runtime.PrimaryRemoteSessionId?.Value);

        JoinAcceptedMessage rejoined = host.JoinSession(SessionA, SessionA);

        Assert.Equal(SessionC, host.Runtime.PrimaryRemoteSessionId?.Value);
        Assert.NotEqual(primaryJoin.ActorId, rejoined.ActorId);
        Assert.True(host.Runtime.TryResolveCombatActor(rejoined.ActorId, out _));
        IReadOnlyList<ActorSnapshot> actors = host.Runtime.CaptureActorSnapshots();
        ActorSnapshot a = Assert.Single(actors, actor => actor.SessionId == SessionA);
        Assert.Equal(rejoined.ActorId, a.ActorId);
        Assert.Single(actors, actor => actor.SessionId == SessionB);
        Assert.Single(actors, actor => actor.SessionId == SessionC);
    }

    [Fact]
    public void Secondary_RepeatedLeaveAndRejoin_DoesNotLeakBodies()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        host.JoinSession(SessionA, SessionA);
        JoinAcceptedMessage first = host.JoinSession(SessionB, SessionB);
        int collisionObjects = host.Runtime.Physics.World.NumCollisionObjects;

        for (int i = 0; i < 5; i++)
        {
            host.ReleaseSession(SessionB);
            Assert.DoesNotContain(host.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == SessionB);
            JoinAcceptedMessage rejoined = host.JoinSession(SessionB, SessionB);
            Assert.Equal(first.ActorId, rejoined.ActorId);
        }

        Assert.Equal(collisionObjects, host.Runtime.Physics.World.NumCollisionObjects);
        Assert.Single(host.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == SessionB);
    }

    private static RuntimeAuthoritativeSessionHost CreateHost()
    {
        WorldSessionRuntime runtime = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false);
        runtime.Enemies.Clear();
        return new RuntimeAuthoritativeSessionHost(runtime);
    }
}

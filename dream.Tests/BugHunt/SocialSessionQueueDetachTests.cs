using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SOCIAL-2, below the network on a bare runtime (the harness TrustTradeRequestCooldownTests uses): the
// detach hook purged a session's queued messages without decrementing their counts, so a reused session id
// inherited a floor and its queue depth only ever shrank over reconnects.
//
// One runtime per test, and no more: a WorldSessionRuntime carries its own physics, and several of them
// standing up at once in the parallel pool starves the loopback tests that wait on a real host's progress.
public sealed class SocialSessionQueueDetachTests
{
    private const string NameA = "Social Seat A";
    private const string NameB = "Social Seat B";
    private static readonly PlayerSessionId SessionA = new("social-a");
    private static readonly PlayerSessionId SessionB = new("social-b");

    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        runtime.WriteCheckpointOnDispose = false;
        runtime.AttachRemoteSession(SessionA, NameA);
        runtime.AttachRemoteSession(SessionB, NameB);
        return runtime;
    }

    // The production drain shape: a predicate, so the per-session counts follow the delivery one entry at a
    // time (the predicate-free overload clears every count, which only tests reach).
    private static List<SessionServerMessage> Drain(WorldSessionRuntime runtime)
    {
        List<SessionServerMessage> drained = [];
        runtime.DrainSessionMessagesInto(drained, _ => true);
        return drained;
    }

    private static List<string> ChatTexts(IEnumerable<SessionServerMessage> drained, PlayerSessionId session)
        => [.. drained
            .Where(entry => entry.SessionId == session && entry.Message is ServerChatMessage)
            .Select(entry => ((ServerChatMessage)entry.Message).Text)];

    // The detached session's queued messages are purged; its count must go with them, or the same session id
    // rejoins carrying a floor it can never pay off. Four reconnect cycles, each leaving messages behind, so
    // the ratchet this test would have seen before the fix is 40 deep.
    //
    // Also the control: a reconnecting player can still be sent messages, and the cap still bites at exactly
    // the cap - the fix must stop the depth falling, not raise the ceiling.
    [Fact]
    public void ADetachedSessionsQueuedMessages_DoNotRatchetDownTheReusedSessionIdsQueueDepth()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        int cap = WorldSessionRuntime.MaxQueuedSessionMessagesPerSession;

        for (int cycle = 0; cycle < 4; cycle++)
        {
            for (int i = 0; i < 10; i++)
            {
                runtime.QueueSessionMessage(SessionA, new ServerChatMessage(ChatChannel.System, "", $"stale {cycle}-{i}", 1));
            }

            runtime.DetachRemoteSession(SessionA);
            // The purge dropped the entries themselves (that half always worked).
            Assert.DoesNotContain(ChatTexts(Drain(runtime), SessionA), text => text.StartsWith("stale", StringComparison.Ordinal));
            // The ordinary reconnect: the client asks for the same session id back.
            runtime.AttachRemoteSession(SessionA, NameA);
            Drain(runtime);
        }

        // Control: the reconnected seat can still be sent a message at all.
        runtime.QueueSessionMessage(SessionA, new ServerChatMessage(ChatChannel.System, "", "welcome back", 2));
        Assert.Contains("welcome back", ChatTexts(Drain(runtime), SessionA));

        for (int i = 0; i < cap + 5; i++)
        {
            runtime.QueueSessionMessage(SessionA, new ServerChatMessage(ChatChannel.System, "", $"over {i}", 3));
        }

        List<string> over = [.. ChatTexts(Drain(runtime), SessionA).Where(text => text.StartsWith("over", StringComparison.Ordinal))];
        // Before the fix the leaked floor held the queue at 88 of the 128 it is entitled to.
        Assert.Equal(cap, over.Count);
        // Its own oldest went first, as the per-session cap promises, and only the five over the cap went.
        Assert.Equal("over 5", over[0]);
        Assert.Equal($"over {cap + 4}", over[^1]);
    }
}

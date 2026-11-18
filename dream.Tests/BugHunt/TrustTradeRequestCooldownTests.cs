using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Multiplayer;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-TRUST-1: trade.request had no cooldown and spent no budget, so a request/cancel loop pushed a
// ServerTradeState at a player the sender named, once per accepted request, as fast as the action queue
// drained (16 pending per session, drained whole every tick at 64 Hz). A request the target never
// answered now starts TradeRules.RequestCooldownTicks on its sender, the number and the shape party
// invites use; a trade the target answered starts none, so normal play is untouched.
public sealed class TrustTradeRequestCooldownTests
{
    private const string Attacker = "Grief Sender";
    private const string Victim = "Named Victim";
    private static readonly PlayerSessionId AttackerSession = new("trust-attacker");
    private static readonly PlayerSessionId VictimSession = new("trust-victim");

    private static WorldSessionRuntime CreateTradeRuntime()
    {
        // Both seats attach at the world spawn, so they are in trade range of each other.
        WorldSessionRuntime runtime = new(
            WorldDefaults.StartupFloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub());
        runtime.WriteCheckpointOnDispose = false;
        runtime.AttachRemoteSession(AttackerSession, Attacker);
        runtime.AttachRemoteSession(VictimSession, Victim);
        return runtime;
    }

    // One client action at a chosen server tick, through the queue the dedicated host drains into.
    private static AuthoritativeActionResult Act(
        WorldSessionRuntime runtime,
        ulong tick,
        PlayerSessionId session,
        ulong sequence,
        IPlayerActionPayload payload)
    {
        PlayerActionMessage message = new(session, sequence, payload);
        return runtime.ProcessQueuedAuthoritativeActions(
            tick,
            [new QueuedPlayerAction(sequence, session, message)])[0];
    }

    private static int CountQueuedTradeStates(WorldSessionRuntime runtime, PlayerSessionId session)
    {
        List<SessionServerMessage> drained = [];
        runtime.DrainSessionMessagesInto(drained);
        int count = 0;
        for (int i = 0; i < drained.Count; i++)
        {
            if (drained[i].SessionId == session && drained[i].Message is ServerTradeState)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void TradeRequestCancelLoop_IsRefusedByTheCooldown_AndTheNamedVictimIsNotFlooded()
    {
        using WorldSessionRuntime runtime = CreateTradeRuntime();
        ulong sequence = 0;
        int accepted = 0;

        // One request/cancel pair a tick for a second: an attacker can send sixteen pairs a tick, so
        // this is far below what the action queue allows.
        for (ulong tick = 1; tick <= 64; tick++)
        {
            if (Act(runtime, tick, AttackerSession, ++sequence, new TradeRequestIntent(Victim)).Accepted)
            {
                accepted++;
            }

            Act(runtime, tick, AttackerSession, ++sequence, new TradeCancelIntent());
        }

        // Only the first request opens a trade; the rest land inside RequestCooldownTicks (128 ticks).
        Assert.Equal(1, accepted);
        Assert.Equal(63, runtime.TradeRequestCooldownRefusals);
        Assert.Equal(0, runtime.ActiveTradeCount);
        // The victim saw that one trade open and close, not one per tick.
        Assert.Equal(1, CountQueuedTradeStates(runtime, VictimSession));
    }

    [Fact]
    public void TradeRequest_IsAcceptedAgainAfterTheCooldown_AndTheCooldownIsPerSender()
    {
        using WorldSessionRuntime runtime = CreateTradeRuntime();

        // An unanswered request, withdrawn: asking again once the cooldown has passed is accepted.
        Assert.True(Act(runtime, 10UL, AttackerSession, 1UL, new TradeRequestIntent(Victim)).Accepted);
        Act(runtime, 11UL, AttackerSession, 2UL, new TradeCancelIntent());
        ulong afterCooldown = 11UL + (ulong)TradeRules.RequestCooldownTicks;
        Assert.True(Act(runtime, afterCooldown, AttackerSession, 3UL, new TradeRequestIntent(Victim)).Accepted);
        Assert.Equal(1, runtime.ActiveTradeCount);

        // The other player's own request is not held back by the sender's cooldown: the cooldown is
        // per sender, so the one it just started for the attacker leaves the victim free to ask.
        Act(runtime, afterCooldown + 1UL, AttackerSession, 4UL, new TradeCancelIntent());
        Assert.True(Act(runtime, afterCooldown + 2UL, VictimSession, 1UL, new TradeRequestIntent(Attacker)).Accepted);
        Assert.Equal(1, runtime.ActiveTradeCount);
        Assert.Equal(0, runtime.TradeRequestCooldownRefusals);
    }

    [Fact]
    public void AnAnsweredTrade_CanBeCancelled_AndReopenedAtOnce()
    {
        using WorldSessionRuntime runtime = CreateTradeRuntime();

        // Normal play, which the cooldown must not touch: a trade the partner accepted, cancelled and
        // opened again on the next tick (how the M5 loopback tests drive several trades in a row).
        for (ulong round = 0; round < 3; round++)
        {
            ulong tick = 5UL + (round * 3UL);
            Assert.True(Act(runtime, tick, AttackerSession, (round * 3UL) + 1UL, new TradeRequestIntent(Victim)).Accepted);
            Assert.True(Act(runtime, tick + 1UL, VictimSession, round + 1UL, new TradeRespondIntent(Accept: true)).Accepted);
            Assert.Equal(1, runtime.ActiveTradeCount);
            Assert.True(Act(runtime, tick + 2UL, AttackerSession, (round * 3UL) + 2UL, new TradeCancelIntent()).Accepted);
            Assert.Equal(0, runtime.ActiveTradeCount);
        }

        Assert.Equal(0, runtime.TradeRequestCooldownRefusals);
    }
}

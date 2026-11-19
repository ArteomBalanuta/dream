using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Presentation;

// A MeleeArc cast event in a snapshot is the remote player's Cleave swing: the presenter counts it on that player's
// entry (SkillSwingSerial) and the remote renderer feeds the serial to the animation state machine.
public class ClientScenePresenterSkillSwingTests
{
    private const string Local = "local-player";

    private static ActorSnapshot LocalActor() =>
        new(1, CreatureKind.Player, Local, Vector3.Zero, -Vector3.UnitZ, 100, 100, true);

    private static ActorSnapshot Remote(int actorId, string session) =>
        new(actorId, CreatureKind.Player, session, new Vector3(10f, 1.5f, 10f), -Vector3.UnitZ, 100, 100, true,
            null, null, session);

    private static CastEventDelta Cast(int actorId, SkillKind kind = SkillKind.MeleeArc) =>
        new(actorId, kind, Vector3.Zero, Vector3.UnitZ, 3.5f);

    private static ServerSnapshotEnvelope Snapshot(ulong tick, ActorSnapshot[] remotes, CastEventDelta[]? casts)
    {
        List<ActorSnapshot> actors = [LocalActor(), .. remotes];
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>(),
            CastEvents: casts);
    }

    private static int SerialOf(ClientScenePresenter presenter, double tick, int actorId)
    {
        foreach (RemotePlayerPresentationState state in presenter.BuildRemotePlayerStates(tick))
        {
            if (state.ActorId == actorId)
            {
                return state.SkillSwingSerial;
            }
        }

        throw new InvalidOperationException($"no remote player {actorId}");
    }

    [Fact]
    public void AMeleeArcCast_CountsOnThatRemotePlayerOnly()
    {
        ClientScenePresenter presenter = new(Local);
        ActorSnapshot[] remotes = [Remote(2, "remote-a"), Remote(3, "remote-b")];
        presenter.ApplySnapshot(Snapshot(10, remotes, null));

        presenter.ApplySnapshot(Snapshot(12, remotes, [Cast(3)]));

        Assert.Equal(0, SerialOf(presenter, 12, 2));
        Assert.Equal(1, SerialOf(presenter, 12, 3));
    }

    [Fact]
    public void EachMeleeArcCastEvent_IsOneSwing()
    {
        ClientScenePresenter presenter = new(Local);
        ActorSnapshot[] remotes = [Remote(2, "remote-a")];
        presenter.ApplySnapshot(Snapshot(10, remotes, null));

        presenter.ApplySnapshot(Snapshot(12, remotes, [Cast(2), Cast(2)]));
        presenter.ApplySnapshot(Snapshot(14, remotes, [Cast(2)]));

        Assert.Equal(3, SerialOf(presenter, 14, 2));
    }

    [Fact]
    public void OtherSkillKinds_DoNotCount()
    {
        ClientScenePresenter presenter = new(Local);
        ActorSnapshot[] remotes = [Remote(2, "remote-a")];
        presenter.ApplySnapshot(Snapshot(10, remotes, null));

        presenter.ApplySnapshot(Snapshot(12, remotes, [Cast(2, SkillKind.RangedBolt), Cast(2, SkillKind.SelfHeal)]));

        Assert.Equal(0, SerialOf(presenter, 12, 2));
    }

    [Fact]
    public void ACastNamingTheLocalPlayerOrAnUnknownActor_CountsOnNoRemote()
    {
        ClientScenePresenter presenter = new(Local);
        ActorSnapshot[] remotes = [Remote(2, "remote-a")];
        presenter.ApplySnapshot(Snapshot(10, remotes, null));

        presenter.ApplySnapshot(Snapshot(12, remotes, [Cast(1), Cast(99)]));

        Assert.Equal(0, SerialOf(presenter, 12, 2));
    }

    [Fact]
    public void ASnapshotWithoutCastEvents_ChangesNothing()
    {
        ClientScenePresenter presenter = new(Local);
        ActorSnapshot[] remotes = [Remote(2, "remote-a")];
        presenter.ApplySnapshot(Snapshot(10, remotes, [Cast(2)]));

        presenter.ApplySnapshot(Snapshot(12, remotes, null));
        presenter.ApplySnapshot(Snapshot(14, remotes, []));

        Assert.Equal(1, SerialOf(presenter, 14, 2));
    }

    // The server reuses a player actor id once its session leaves: the new player must not inherit the old one's count.
    [Fact]
    public void AnActorIdReusedByAnotherSession_StartsFromZero()
    {
        ClientScenePresenter presenter = new(Local);
        presenter.ApplySnapshot(Snapshot(10, [Remote(2, "remote-a")], null));
        presenter.ApplySnapshot(Snapshot(12, [Remote(2, "remote-a")], [Cast(2)]));
        Assert.Equal(1, SerialOf(presenter, 12, 2));

        presenter.ApplySnapshot(Snapshot(14, [Remote(2, "remote-b")], null));

        Assert.Equal(0, SerialOf(presenter, 14, 2));
    }

    // Keyed on the actor, not on a position in the list: reordering the remotes between snapshots moves nothing.
    [Fact]
    public void TheCountFollowsTheActor_NotItsPositionInTheSnapshot()
    {
        ClientScenePresenter presenter = new(Local);
        presenter.ApplySnapshot(Snapshot(10, [Remote(2, "remote-a"), Remote(3, "remote-b")], [Cast(2)]));

        presenter.ApplySnapshot(Snapshot(12, [Remote(3, "remote-b"), Remote(2, "remote-a")], [Cast(3)]));

        Assert.Equal(1, SerialOf(presenter, 12, 2));
        Assert.Equal(1, SerialOf(presenter, 12, 3));
    }

    private static PlayerAnimationInputs Draw(int skillSerial) =>
        new(true, 0f, false, 0, 0, false, false, false, 0f, skillSerial);

    // A remote player first seen in the same snapshot as their cast: the presenter counts 1 before anything is drawn,
    // and the renderer seeds the machine through SeedRemote, so that first cleave still plays.
    [Fact]
    public void ACastInTheSnapshotThatCreatesThePlayer_StillPlaysTheFirstCleave()
    {
        ClientScenePresenter presenter = new(Local);
        presenter.ApplySnapshot(Snapshot(10, [Remote(2, "remote-a")], [Cast(2)]));
        int serial = SerialOf(presenter, 10, 2);
        Assert.Equal(1, serial);

        PlayerAnimationStateMachine machine = new();
        PlayerAnimationInputs inputs = Draw(serial);
        machine.SeedRemote(in inputs);
        PlayerAnimationFrame frame = machine.Update(in inputs, 1f / 60f);

        Assert.Equal(PlayerAnimationStateMachine.CleaveClip, frame.Clip);
    }

    // Older casts seen before the first draw (a model that finished loading late) are not replayed.
    [Fact]
    public void StaleCastsCountedBeforeTheFirstDraw_AreNotReplayed()
    {
        ClientScenePresenter presenter = new(Local);
        ActorSnapshot[] remotes = [Remote(2, "remote-a")];
        presenter.ApplySnapshot(Snapshot(10, remotes, [Cast(2)]));
        presenter.ApplySnapshot(Snapshot(12, remotes, [Cast(2), Cast(2)]));
        int serial = SerialOf(presenter, 12, 2);
        Assert.Equal(3, serial);

        PlayerAnimationStateMachine machine = new();
        PlayerAnimationInputs inputs = Draw(serial);
        machine.SeedRemote(in inputs);
        PlayerAnimationFrame frame = machine.Update(in inputs, 1f / 60f);

        Assert.NotEqual(PlayerAnimationStateMachine.CleaveClip, frame.Clip);
    }

    // The first-serial-is-a-baseline rule for attack serials is unchanged by seeding.
    [Fact]
    public void SeedRemote_StillBaselinesTheAttackSerial()
    {
        PlayerAnimationStateMachine machine = new();
        PlayerAnimationInputs inputs = new(true, 0f, false, 4, 0, true, false, false, 0f, 0);
        machine.SeedRemote(in inputs);

        PlayerAnimationFrame frame = machine.Update(in inputs, 1f / 60f);

        Assert.NotEqual(PlayerAnimationStateMachine.MeleeClip, frame.Clip);
    }
}

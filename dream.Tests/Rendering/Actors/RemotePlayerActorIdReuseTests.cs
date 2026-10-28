using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Actors;

// The server reuses a player actor id once its session leaves. When the id shows up under another
// session, even with no frame in between, the renderer releases the old player's instance handle
// and clip so the new player starts fresh. Driven with fake handles, mirroring Draw's steps.
public class RemotePlayerActorIdReuseTests
{
    [Fact]
    public void Same_Sessions_Frame_To_Frame_Reassign_Nothing()
    {
        Dictionary<int, string> sessions = [];
        List<int> reassigned = [];

        RemotePlayerRenderer.CollectReassignedActorIds([Player(1, "a"), Player(2, "b")], sessions, reassigned);
        Assert.Empty(reassigned);
        RemotePlayerRenderer.CollectReassignedActorIds([Player(1, "a"), Player(2, "b")], sessions, reassigned);

        Assert.Empty(reassigned);
        Assert.Equal("a", sessions[1]);
        Assert.Equal("b", sessions[2]);
    }

    [Fact]
    public void An_Id_Taken_Over_By_Another_Session_Releases_The_Old_Handle_And_Clip()
    {
        ReuseHarness harness = new();
        harness.Step([Player(1, "a"), Player(2, "b")]);
        FakeHandle oldHandle = harness.Models[1];
        FakeHandle otherHandle = harness.Models[2];
        Assert.False(harness.Tracker.TryApplyClip(1, PlayerModelAssetDefinition.IdleClip, harness.PlayClip));

        // Same frame cadence, no gap: session "c" now holds id 1.
        harness.Step([Player(1, "c"), Player(2, "b")]);

        Assert.True(oldHandle.Disposed);
        Assert.False(otherHandle.Disposed);
        Assert.Equal([1], harness.Reassigned);
        Assert.NotSame(oldHandle, harness.Models[1]);
        Assert.Same(otherHandle, harness.Models[2]);
        // The new player's clip is played again rather than assumed from the old player.
        Assert.Equal(2, harness.ClipPlaysFor(1));
        Assert.Equal(1, harness.ClipPlaysFor(2));
    }

    [Fact]
    public void A_Reassigned_Id_Without_A_Handle_Still_Forgets_Its_Clip()
    {
        Dictionary<int, FakeHandle> models = [];
        PlayerAnimationClipTracker tracker = new();
        Action<string> playClip = _ => { };
        tracker.TryApplyClip(4, PlayerModelAssetDefinition.IdleClip, playClip);

        int released = RemotePlayerRenderer.ReleaseReassignedEntries([4], models, tracker);

        Assert.Equal(0, released);
        Assert.True(tracker.TryApplyClip(4, PlayerModelAssetDefinition.IdleClip, playClip));
    }

    [Fact]
    public void An_Id_That_Left_Before_It_Is_Reused_Is_Pruned_And_Not_Reported_Twice()
    {
        Dictionary<int, string> sessions = [];
        HashSet<int> active = [];
        List<int> stale = [];
        List<int> reassigned = [];
        RemotePlayerRenderer.CollectReassignedActorIds([Player(1, "a")], sessions, reassigned);

        IReadOnlyList<RemotePlayerPresentationState> empty = [];
        RemotePlayerRenderer.CollectActiveActorIds(empty, active);
        RemotePlayerRenderer.PruneStaleSessions(sessions, active, stale);
        Assert.Empty(sessions);

        RemotePlayerRenderer.CollectReassignedActorIds([Player(1, "c")], sessions, reassigned);
        Assert.Empty(reassigned);
        Assert.Equal("c", sessions[1]);
    }

    [Fact]
    public void Reassignment_Bookkeeping_Allocates_Nothing()
    {
        List<RemotePlayerPresentationState> first = [];
        List<RemotePlayerPresentationState> second = [];
        for (int i = 0; i < 6; i++)
        {
            first.Add(Player(i + 1, $"first-{i}"));
            second.Add(Player(i + 1, i % 2 == 0 ? $"second-{i}" : $"first-{i}"));
        }

        Dictionary<int, string> sessions = [];
        Dictionary<int, FakeHandle> models = [];
        HashSet<int> active = [];
        List<int> stale = [];
        List<int> reassigned = [];
        PlayerAnimationClipTracker tracker = new();
        Action<string> playClip = _ => { };
        RemotePlayerRenderer.CollectReassignedActorIds(first, sessions, reassigned);
        RemotePlayerRenderer.CollectReassignedActorIds(second, sessions, reassigned);
        for (int i = 1; i <= 6; i++)
        {
            tracker.TryApplyClip(i, PlayerModelAssetDefinition.IdleClip, playClip);
        }

        int reassignedCount = 0;
        long bytes = AllocationAssert.AtMost(0, () =>
        {
            // Every call flips half the ids to the other session, so each one reassigns.
            RemotePlayerRenderer.CollectActiveActorIds(first, active);
            RemotePlayerRenderer.PruneStaleSessions(sessions, active, stale);
            RemotePlayerRenderer.CollectReassignedActorIds(first, sessions, reassigned);
            RemotePlayerRenderer.ReleaseReassignedEntries(reassigned, models, tracker);
            RemotePlayerRenderer.CollectReassignedActorIds(second, sessions, reassigned);
            RemotePlayerRenderer.ReleaseReassignedEntries(reassigned, models, tracker);
            reassignedCount = reassigned.Count;
        });

        Assert.Equal(0, bytes);
        Assert.Equal(3, reassignedCount);
    }

    private static RemotePlayerPresentationState Player(int actorId, string sessionId)
    {
        return new RemotePlayerPresentationState(
            actorId,
            sessionId,
            sessionId,
            new InterpolatedActorPose(new Vector3(0f, 2f, -5f - actorId), -Vector3.UnitZ),
            100,
            100,
            true);
    }

    private sealed class FakeHandle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    // Mirrors RemotePlayerRenderer.Draw: prune inactive ids, release reassigned ones, request a
    // handle for every player without one, then apply each player's clip.
    private sealed class ReuseHarness
    {
        private readonly Dictionary<int, string> _sessions = [];
        private readonly HashSet<int> _active = [];
        private readonly List<int> _stale = [];
        private readonly Dictionary<int, int> _clipPlays = [];
        private int _currentActorId;

        public ReuseHarness()
        {
            PlayClip = _ => _clipPlays[_currentActorId] = ClipPlaysFor(_currentActorId) + 1;
        }

        public Dictionary<int, FakeHandle> Models { get; } = [];

        public PlayerAnimationClipTracker Tracker { get; } = new();

        public List<int> Reassigned { get; } = [];

        public Action<string> PlayClip { get; }

        public int ClipPlaysFor(int actorId)
        {
            return _clipPlays.TryGetValue(actorId, out int plays) ? plays : 0;
        }

        public void Step(IReadOnlyList<RemotePlayerPresentationState> players)
        {
            RemotePlayerRenderer.CollectActiveActorIds(players, _active);
            Tracker.RemoveExcept(_active);
            RemotePlayerRenderer.PruneStaleSessions(_sessions, _active, _stale);
            RemotePlayerRenderer.PruneStaleEntries(Models, _active, _stale);

            RemotePlayerRenderer.CollectReassignedActorIds(players, _sessions, Reassigned);
            RemotePlayerRenderer.ReleaseReassignedEntries(Reassigned, Models, Tracker);

            foreach (RemotePlayerPresentationState player in players)
            {
                if (!Models.ContainsKey(player.ActorId))
                {
                    Models[player.ActorId] = new FakeHandle();
                }

                _currentActorId = player.ActorId;
                Tracker.TryApplyClip(player.ActorId, PlayerModelAssetDefinition.IdleClip, PlayClip);
            }
        }
    }
}

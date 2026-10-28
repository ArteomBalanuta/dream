using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Actors;

// The remote player model streaming policy, driven with fake handles (no GL, no loader): models
// are requested near the camera, released far from it with hysteresis, and capped in flight.
public class RemotePlayerModelStreamingTests
{
    private static readonly Vector3 CameraPosition = new(0f, 2f, 0f);

    [Fact]
    public void Distances_Leave_A_Margin_Beyond_Render_Distance_And_A_Wider_Release_Band()
    {
        Assert.True(RemotePlayerRenderer.ModelRequestDistance > WorldVisibility.MaxRenderDistance);
        Assert.True(RemotePlayerRenderer.ModelReleaseDistance > RemotePlayerRenderer.ModelRequestDistance);
        Assert.Equal(4, RemotePlayerRenderer.MaxPendingModelRequests);
    }

    [Fact]
    public void Near_Players_Get_Requests_And_Far_Players_Do_Not()
    {
        float margin = (WorldVisibility.MaxRenderDistance + RemotePlayerRenderer.ModelRequestDistance) / 2f;
        float band = (RemotePlayerRenderer.ModelRequestDistance + RemotePlayerRenderer.ModelReleaseDistance) / 2f;
        List<RemotePlayerPresentationState> players =
        [
            CreatePlayer(1, 10f),
            CreatePlayer(2, margin),
            CreatePlayer(3, band),
            CreatePlayer(4, RemotePlayerRenderer.ModelReleaseDistance + 30f)
        ];
        Streamer streamer = new();

        streamer.Step(players);

        Assert.Equal([1, 2], streamer.Models.Keys.Order());
        Assert.Equal(2, streamer.Requests);
    }

    [Fact]
    public void Capped_Frame_Requests_Visible_Players_Before_Margin_Players()
    {
        float margin = (WorldVisibility.MaxRenderDistance + RemotePlayerRenderer.ModelRequestDistance) / 2f;
        List<RemotePlayerPresentationState> players =
        [
            CreatePlayer(1, margin),
            CreatePlayer(2, margin),
            CreatePlayer(3, 5f),
            CreatePlayer(4, 6f)
        ];
        Dictionary<int, FakeHandle> models = [];
        List<int> requests = [];

        RemotePlayerRenderer.CollectModelRequestActorIds(CameraPosition, players, models, freeSlots: 3, requests);

        Assert.Equal([3, 4, 1], requests);
    }

    [Fact]
    public void Players_Crossing_The_Boundary_Do_Not_Thrash()
    {
        float justInside = RemotePlayerRenderer.ModelRequestDistance - 1f;
        float justBeyondRequest = RemotePlayerRenderer.ModelRequestDistance + 1f;
        float justInsideRelease = RemotePlayerRenderer.ModelReleaseDistance - 1f;
        float beyondRelease = RemotePlayerRenderer.ModelReleaseDistance + 1f;
        Streamer streamer = new();

        // Hovering between the request and release distances never re-requests or releases.
        streamer.Step([CreatePlayer(1, justBeyondRequest)]);
        Assert.Empty(streamer.Models);
        for (int frame = 0; frame < 20; frame++)
        {
            float distance = frame % 2 == 0 ? justInside : justInsideRelease;
            streamer.Step([CreatePlayer(1, distance)]);
        }

        Assert.Equal(1, streamer.Requests);
        Assert.Equal(0, streamer.Releases);
        Assert.Single(streamer.Models);

        // Beyond the release distance the handle is disposed; coming back into the band does not
        // request again, only coming inside the request distance does.
        FakeHandle released = streamer.Models[1];
        streamer.Step([CreatePlayer(1, beyondRelease)]);
        Assert.True(released.Disposed);
        Assert.Empty(streamer.Models);
        Assert.Equal(1, streamer.Releases);

        streamer.Step([CreatePlayer(1, justInsideRelease)]);
        streamer.Step([CreatePlayer(1, justBeyondRequest)]);
        Assert.Empty(streamer.Models);
        Assert.Equal(1, streamer.Requests);

        streamer.Step([CreatePlayer(1, justInside)]);
        Assert.Single(streamer.Models);
        Assert.Equal(2, streamer.Requests);
    }

    [Fact]
    public void Concurrent_Pending_Requests_Respect_The_Cap()
    {
        List<RemotePlayerPresentationState> players = [];
        for (int i = 0; i < 10; i++)
        {
            players.Add(CreatePlayer(i + 1, 5f + i));
        }

        Streamer streamer = new();

        streamer.Step(players);
        Assert.Equal(RemotePlayerRenderer.MaxPendingModelRequests, streamer.Requests);
        Assert.Equal(RemotePlayerRenderer.MaxPendingModelRequests, streamer.PendingCount);

        // Nothing finished, so no slot is free.
        streamer.Step(players);
        Assert.Equal(RemotePlayerRenderer.MaxPendingModelRequests, streamer.Requests);

        // Two finish: exactly two more requests go out, nearest first.
        streamer.Models[1].Pending = false;
        streamer.Models[2].Pending = false;
        streamer.Step(players);
        Assert.Equal(6, streamer.Requests);
        Assert.Equal([1, 2, 3, 4, 5, 6], streamer.Models.Keys.Order());

        // Finishing everything as it arrives eventually loads all ten, never above the cap.
        for (int frame = 0; frame < 10 && streamer.Models.Count < players.Count; frame++)
        {
            Assert.True(streamer.PendingCount <= RemotePlayerRenderer.MaxPendingModelRequests);
            foreach (FakeHandle handle in streamer.Models.Values)
            {
                handle.Pending = false;
            }

            streamer.Step(players);
        }

        Assert.Equal(10, streamer.Models.Count);
        Assert.Equal(10, streamer.Requests);
        Assert.True(streamer.MaxObservedPending <= RemotePlayerRenderer.MaxPendingModelRequests);
    }

    [Fact]
    public void Releasing_A_Far_Pending_Handle_Frees_Its_Slot()
    {
        List<RemotePlayerPresentationState> players = [];
        for (int i = 0; i < 5; i++)
        {
            players.Add(CreatePlayer(i + 1, 5f + i));
        }

        Streamer streamer = new();
        streamer.Step(players);
        Assert.False(streamer.Models.ContainsKey(5));

        players[0] = CreatePlayer(1, RemotePlayerRenderer.ModelReleaseDistance + 5f);
        streamer.Step(players);

        Assert.False(streamer.Models.ContainsKey(1));
        Assert.True(streamer.Models.ContainsKey(5));
        Assert.Equal(RemotePlayerRenderer.MaxPendingModelRequests, streamer.PendingCount);
    }

    [Fact]
    public void Streaming_Bookkeeping_Allocates_Nothing_In_Steady_State()
    {
        List<RemotePlayerPresentationState> players = [];
        for (int i = 0; i < 6; i++)
        {
            players.Add(CreatePlayer(i + 1, 10f + i * 20f));
        }

        Dictionary<int, FakeHandle> models = new()
        {
            [1] = new FakeHandle(),
            [2] = new FakeHandle(),
            [3] = new FakeHandle(),
            [5] = new FakeHandle(),
            [6] = new FakeHandle()
        };
        List<int> far = [];
        List<int> requests = [];
        RemotePlayerRenderer.CollectFarModelActorIds(CameraPosition, players, models, far);
        RemotePlayerRenderer.CollectModelRequestActorIds(CameraPosition, players, models, 4, requests);

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            RemotePlayerRenderer.CollectFarModelActorIds(CameraPosition, players, models, far);
            RemotePlayerRenderer.CollectModelRequestActorIds(CameraPosition, players, models, 4, requests);
        });

        Assert.Equal(0, bytes);
        Assert.Equal([5, 6], far);
        Assert.Empty(requests);
    }

    private static RemotePlayerPresentationState CreatePlayer(int actorId, float distance)
    {
        return new RemotePlayerPresentationState(
            actorId,
            $"session-{actorId}",
            $"Player {actorId}",
            new InterpolatedActorPose(CameraPosition + new Vector3(0f, 0f, -distance), -Vector3.UnitZ),
            100,
            100,
            true);
    }

    private sealed class FakeHandle : IDisposable
    {
        public bool Pending { get; set; } = true;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    // Mirrors RemotePlayerRenderer.Draw's streaming steps: release far handles, count the pending
    // ones, then request into the free slots.
    private sealed class Streamer
    {
        private readonly List<int> _scratch = [];

        public Dictionary<int, FakeHandle> Models { get; } = [];

        public int Requests { get; private set; }

        public int Releases { get; private set; }

        public int MaxObservedPending { get; private set; }

        public int PendingCount => Models.Values.Count(handle => handle.Pending);

        public void Step(IReadOnlyList<RemotePlayerPresentationState> players)
        {
            RemotePlayerRenderer.CollectFarModelActorIds(CameraPosition, players, Models, _scratch);
            foreach (int actorId in _scratch)
            {
                FakeHandle handle = Models[actorId];
                Models.Remove(actorId);
                handle.Dispose();
                Releases++;
            }

            int freeSlots = RemotePlayerRenderer.MaxPendingModelRequests - PendingCount;
            if (freeSlots > 0)
            {
                RemotePlayerRenderer.CollectModelRequestActorIds(CameraPosition, players, Models, freeSlots, _scratch);
                foreach (int actorId in _scratch)
                {
                    Models[actorId] = new FakeHandle();
                    Requests++;
                }
            }

            MaxObservedPending = Math.Max(MaxObservedPending, PendingCount);
        }
    }
}

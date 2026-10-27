using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Multiplayer.Diagnostics;

// MEM-7 moved the per-snapshot diagnostics behind IsEnabled guards and state-passing overloads.
// With diagnostics on, each line must read exactly as the old closures built it; those old
// expressions are rebuilt here as the expected text.
public class NetworkDiagnosticsLogCaptureTests
{
    private static bool EnabledByEnvironment => IsTruthy(
        Environment.GetEnvironmentVariable(NetworkDiagnosticsLog.EnabledEnvironmentVariable));

    [Fact]
    public void CaptureOnCurrentThread_EnablesOnlyWithinScope()
    {
        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            Assert.True(NetworkDiagnosticsLog.IsEnabled);
            NetworkDiagnosticsLog.Write("test.capture", "inside");
        }

        Assert.Equal([("test.capture", "inside")], lines);
        if (!EnabledByEnvironment)
        {
            Assert.False(NetworkDiagnosticsLog.IsEnabled);
            NetworkDiagnosticsLog.Write("test.capture", "outside", static (string _) => throw new InvalidOperationException());
            NetworkDiagnosticsLog.WriteEvery(
                "test.capture.disabled",
                TimeSpan.FromSeconds(1),
                "test.capture",
                "outside",
                static (string _) => throw new InvalidOperationException());
        }
    }

    [Fact]
    public void WriteEveryWithState_PassesStateAndRateLimitsByKey()
    {
        string key = $"test.capture.every.{Guid.NewGuid():N}";
        List<string> messages = [];
        using IDisposable capture = NetworkDiagnosticsLog.CaptureOnCurrentThread((_, message) => messages.Add(message));

        for (int i = 0; i < 3; i++)
        {
            NetworkDiagnosticsLog.WriteEvery(
                key,
                TimeSpan.FromMinutes(1),
                "test.capture",
                (Count: i, Name: "state"),
                static state => $"{state.Name}={state.Count}");
        }

        Assert.Equal(["state=0"], messages);
    }

    [Fact]
    public void PresenterApplyAndPrune_LogTheOldText()
    {
        string localSessionId = $"local-{Guid.NewGuid():N}";
        int removedActorId = 1_000_000 + Random.Shared.Next(1_000_000);
        ClientScenePresenter presenter = new(localSessionId);
        ServerSnapshotEnvelope first = CreateSnapshot(
            5,
            Player(1, localSessionId, "Local"),
            Player(removedActorId, "remote-gone", "Gone"),
            Player(3, "remote-kept", null));
        ServerSnapshotEnvelope second = CreateSnapshot(
            6,
            Player(1, localSessionId, "Local"),
            Player(3, "remote-kept", null));

        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            presenter.ApplySnapshot(first);
            presenter.ApplySnapshot(second);
        }

        string firstRoster = $"[{localSessionId}#1/Local, remote-gone#{removedActorId}/Gone, remote-kept#3/remote-kept]";
        Assert.Equal(
            [
                ("client.presenter.apply",
                    $"local={localSessionId} remoteEntries=2 roster={firstRoster} " +
                    NetworkDiagnosticsLog.DescribeSnapshot(first)),
                ("client.presenter.prune",
                    $"local={localSessionId} actor={removedActorId} session=remote-gone snapshotTick=6 " +
                    NetworkDiagnosticsLog.DescribeSnapshot(second))
            ],
            lines);
    }

    [Fact]
    public void CubeMoveMiss_LogsTheOldText()
    {
        Map map = new(width: 10, depth: 10);
        WorldMutationDelta mutation = WorldMutationDelta.Moved(new Vector3(3f, 2f, 3f), new Vector3(4.5f, 2.5f, 3.5f))
            with { Sequence = 42, ActorId = 77 };

        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            RemoteWorldMutationApplier.ApplyWorldMutations(map, [mutation]);
        }

        Assert.Equal(
            [("client.world.move-miss", $"source=(3,2,3) target={mutation.TargetCellPosition} seq={mutation.Sequence}")],
            lines);
        Assert.Null(map.TryGetCubeAtCell(4, 2, 3));
    }

    [Fact]
    public void FallbackFracture_LogsTheOldText()
    {
        Map map = new(width: 10, depth: 10);
        Assert.NotNull(map.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: false, materialId: GeometryMaterialId.Stone));
        WorldMutationDelta mutation = new(
            WorldMutationKind.CubeFractured,
            new Vector3(3f, 2f, 3f),
            MaterialId: GeometryMaterialId.Stone,
            Sequence: 9);

        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            RemoteWorldMutationApplier.ApplyWorldMutations(map, [mutation]);
        }

        // The key is process-wide and limited to one line per 5 s; this is its only capturing user.
        Assert.Contains(
            ("client.world.fracture-fallback", $"source=mutation seq=9 cell=(3,2,3) material={GeometryMaterialId.Stone}"),
            lines);
    }

    private static ActorSnapshot Player(int actorId, string sessionId, string? displayName)
    {
        return new ActorSnapshot(
            actorId,
            CreatureKind.Player,
            sessionId,
            new Vector3(actorId % 7, 1.5f, 2f),
            -Vector3.UnitZ,
            100,
            100,
            true,
            DisplayName: displayName);
    }

    private static ServerSnapshotEnvelope CreateSnapshot(ulong tick, params ActorSnapshot[] actors)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong> { ["remote-kept"] = 4 },
            actors,
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null &&
            (value == "1" ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}

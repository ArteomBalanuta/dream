using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Scenes;
using Dream.Rendering.Actors;
using Dream.Rendering.Effects;
using Dream.Rendering.Lighting;
using Dream.Simulation.Inventory;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Scenes;

// Wave T2b: PlayScene.CollectHeldLights needs a scene with a GL context, so these tests run its
// body, PlayScene.AddHeldLights, with the same inputs.
public sealed class PlaySceneHeldLightsTests
{
    private static readonly Vector3 CameraPosition = new(0f, 1.6f, 0f);
    private static readonly Vector3 CameraFront = -Vector3.UnitZ;
    private static readonly Vector3 CameraRight = Vector3.UnitX;
    private static readonly Vector3 CameraUp = Vector3.UnitY;

    [Fact]
    public void AddsOneLightPerLitPlayer()
    {
        LightCollector lights = new();
        PlayScene.LocalHeldLight local = new(HeldLightKind.Torch, ActorId: 1, FirstPerson: true, Vector3.Zero, -Vector3.UnitZ);
        RemotePlayerPresentationState[] remotes =
        [
            Remote(2, new Vector3(10f, 0f, 0f), HeldLightKind.Lantern),
            Remote(3, new Vector3(20f, 0f, 0f), HeldLightKind.None),
            Remote(4, new Vector3(30f, 0f, 0f), HeldLightKind.Torch),
            Remote(5, new Vector3(40f, 0f, 0f), HeldLightKind.Torch, isAlive: false),
        ];

        PlayScene.AddHeldLights(lights, in local, CameraPosition, CameraFront, CameraRight, CameraUp, remotes, timeSeconds: 3.0);

        Assert.Equal(3, lights.Count);
        PointLight localLight = lights.Lights[0];
        AssertWithinTheFlameWander(HeldLightAnchor.FirstPerson(CameraPosition, CameraFront, CameraRight, CameraUp), localLight.Position);
        Assert.InRange(localLight.Radius, 7.29f, 8.71f);

        PointLight lantern = lights.Lights[1];
        Assert.Equal(HeldLightAnchor.Body(remotes[0].Pose.Position, remotes[0].Pose.Facing), lantern.Position);
        Assert.Equal(HeldLightCatalog.LanternRadius, lantern.Radius);
        Assert.Equal(HeldLightCatalog.LanternColor, lantern.Color);

        PointLight torch = lights.Lights[2];
        AssertWithinTheFlameWander(HeldLightAnchor.Body(remotes[2].Pose.Position, remotes[2].Pose.Facing), torch.Position);
        Assert.InRange(torch.Radius, 7.29f, 8.71f);
    }

    // A held torch sits at its anchor plus FireFlicker's wander offset, so the only bound it obeys is
    // the wander box: the lateral constant on X and Z, the vertical one on Y. Derived from those
    // constants, never restated as a number; TheFlameWanderReach_IsTheDiagonalOfTheWanderBox pins
    // the relation between the box and the radius used here.
    private const float WanderEpsilon = 1e-5f;

    private static readonly float FlameWanderReach = MathF.Sqrt(
        (2f * FireFlicker.OffsetLateral * FireFlicker.OffsetLateral)
        + (FireFlicker.OffsetVertical * FireFlicker.OffsetVertical));

    private static void AssertWithinTheFlameWander(Vector3 anchor, Vector3 lightPosition)
    {
        Vector3 drift = lightPosition - anchor;
        Assert.True(MathF.Abs(drift.X) <= FireFlicker.OffsetLateral + WanderEpsilon, $"lateral X drift {drift.X}");
        Assert.True(MathF.Abs(drift.Z) <= FireFlicker.OffsetLateral + WanderEpsilon, $"lateral Z drift {drift.Z}");
        Assert.True(MathF.Abs(drift.Y) <= FireFlicker.OffsetVertical + WanderEpsilon, $"vertical drift {drift.Y}");
        Assert.True(drift.Length <= FlameWanderReach + WanderEpsilon, $"drift {drift.Length} exceeds the wander reach {FlameWanderReach}");
    }

    // What the drift bound above is worth, asserted against the thing it bounds: the SAMPLED offset. Eight seconds of
    // clock at 240 Hz on the seeds this file's held lights actually carry (HeldLightCatalog passes the actor id straight
    // through as the flicker seed) must never leave the reach - and must get most of the way out to it, so a reach that
    // were merely enormous fails here as surely as one that were too small. Comparing the reach against the expression
    // that defines it would assert nothing at all.
    [Fact]
    public void TheFlameWanderReach_BoundsTheSampledOffset_AndIsNearlyReached()
    {
        const int Rate = 240;
        float worst = 0f;
        foreach (int actorId in new[] { 1, 2, 4, 5 })
        {
            for (int i = 0; i < 8 * Rate; i++)
            {
                Vector3 offset = FireFlicker.Sample(FireFlickerKind.HeldTorch, Vector3.Zero, (double)i / Rate, actorId).Offset;
                Assert.True(
                    offset.Length <= FlameWanderReach + WanderEpsilon,
                    $"actor {actorId} at t={(double)i / Rate}: offset {offset} is {offset.Length} long, past the wander reach {FlameWanderReach}");
                worst = MathF.Max(worst, offset.Length);
            }
        }

        Assert.True(
            worst >= 0.7f * FlameWanderReach,
            $"the flame never got past {worst / FlameWanderReach:0.00} of the wander reach {FlameWanderReach}, so the bound measures nothing");
    }

    [Fact]
    public void ThirdPerson_AnchorsTheLocalLightToTheBody()
    {
        LightCollector lights = new();
        Vector3 body = new(5f, 1f, 5f);
        PlayScene.LocalHeldLight local = new(HeldLightKind.Lantern, ActorId: 1, FirstPerson: false, body, Vector3.UnitX);

        PlayScene.AddHeldLights(lights, in local, CameraPosition, CameraFront, CameraRight, CameraUp, [], timeSeconds: 0.0);

        Assert.Equal(1, lights.Count);
        Assert.Equal(HeldLightAnchor.Body(body, Vector3.UnitX), lights.Lights[0].Position);
    }

    [Fact]
    public void ThirdPerson_WithAFlamePoint_AnchorsTheLocalLightToTheFlame()
    {
        LightCollector lights = new();
        Vector3 body = new(5f, 1f, 5f);
        Vector3 facing = Vector3.UnitX;
        Vector3 flameModelPoint = new(0.4f, 1.9f, 0.2f);
        PlayScene.LocalHeldLight local = new(
            HeldLightKind.Torch, ActorId: 1, FirstPerson: false, body, facing, HasFlame: true, FlameModelPoint: flameModelPoint);

        PlayScene.AddHeldLights(lights, in local, CameraPosition, CameraFront, CameraRight, CameraUp, [], timeSeconds: 1.0);

        Assert.Equal(1, lights.Count);
        Vector3 expected = Vector3.TransformPosition(flameModelPoint, PlayerRenderState.CreateModelMatrix(body, facing));
        AssertWithinTheFlameWander(expected, lights.Lights[0].Position);
    }

    [Fact]
    public void RemotePlayer_WithAFlamePoint_AnchorsItsLightToTheFlame_AndWithoutOneToTheBody()
    {
        RemotePlayerPresentationState hit = Remote(2, new Vector3(10f, 0f, 3f), HeldLightKind.Torch);
        RemotePlayerPresentationState miss = Remote(3, new Vector3(20f, 0f, 3f), HeldLightKind.Lantern);
        Vector3 flameModelPoint = new(-0.3f, 1.7f, 0.5f);
        FakeFlames flames = new(hit.ActorId, flameModelPoint);
        LightCollector lights = new();
        PlayScene.LocalHeldLight none = new(HeldLightKind.None, ActorId: 1, FirstPerson: true, Vector3.Zero, -Vector3.UnitZ);

        PlayScene.AddHeldLights(
            lights, in none, CameraPosition, CameraFront, CameraRight, CameraUp, [hit, miss], timeSeconds: 1.0, remoteFlames: flames);

        Assert.Equal(2, lights.Count);
        Vector3 expected = Vector3.TransformPosition(flameModelPoint, PlayerRenderState.CreateModelMatrix(hit.Pose.Position, hit.Pose.Facing));
        AssertWithinTheFlameWander(expected, lights.Lights[0].Position);
        Assert.Equal(HeldLightAnchor.Body(miss.Pose.Position, miss.Pose.Facing), lights.Lights[1].Position);
    }

    private sealed class FakeFlames(int actorId, Vector3 point) : IHeldFlamePointLookup
    {
        public bool TryGetHeldFlameModelPoint(
            int id, string sessionId, in PlayerEquipmentVisuals equipment, out Vector3 modelPoint)
        {
            modelPoint = id == actorId ? point : Vector3.Zero;
            return id == actorId;
        }
    }

    [Fact]
    public void NoHeldLight_AddsNothing()
    {
        LightCollector lights = new();
        PlayScene.LocalHeldLight local = new(HeldLightKind.None, ActorId: 1, FirstPerson: true, Vector3.Zero, -Vector3.UnitZ);

        PlayScene.AddHeldLights(
            lights,
            in local,
            CameraPosition,
            CameraFront,
            CameraRight,
            CameraUp,
            [Remote(2, Vector3.Zero, HeldLightKind.None)],
            timeSeconds: 0.0);

        Assert.Equal(0, lights.Count);
    }

    [Fact]
    public void CollectingHeldLights_AllocatesNothingPerFrame()
    {
        LightCollector lights = new();
        PlayScene.LocalHeldLight local = new(HeldLightKind.Torch, ActorId: 1, FirstPerson: true, Vector3.Zero, -Vector3.UnitZ);
        // A List, as the presenter's live list is, seen through the interface.
        List<RemotePlayerPresentationState> remoteList = [];
        for (int i = 0; i < 8; i++)
        {
            remoteList.Add(Remote(10 + i, new Vector3(i, 0f, -i), i % 2 == 0 ? HeldLightKind.Torch : HeldLightKind.Lantern));
        }

        IReadOnlyList<RemotePlayerPresentationState> remotes = remoteList;
        double time = 0.0;

        AllocationAssert.AtMost(0, () =>
        {
            lights.Clear();
            time += 1.0 / 60.0;
            PlayScene.AddHeldLights(lights, in local, CameraPosition, CameraFront, CameraRight, CameraUp, remotes, time);
        }, iterations: 1000);

        Assert.Equal(9, lights.Count);
    }

    [Fact]
    public void CollectingHeldLights_WithFlamePoints_AllocatesNothingPerFrame()
    {
        LightCollector lights = new();
        PlayScene.LocalHeldLight local = new(
            HeldLightKind.Torch, ActorId: 1, FirstPerson: false, Vector3.Zero, -Vector3.UnitZ,
            HasFlame: true, FlameModelPoint: new Vector3(0.4f, 1.9f, 0.2f));
        List<RemotePlayerPresentationState> remoteList = [];
        for (int i = 0; i < 8; i++)
        {
            remoteList.Add(Remote(10 + i, new Vector3(i, 0f, -i), i % 2 == 0 ? HeldLightKind.Torch : HeldLightKind.Lantern));
        }

        IReadOnlyList<RemotePlayerPresentationState> remotes = remoteList;
        IHeldFlamePointLookup flames = new AlwaysFlames();
        double time = 0.0;

        AllocationAssert.AtMost(0, () =>
        {
            lights.Clear();
            time += 1.0 / 60.0;
            PlayScene.AddHeldLights(lights, in local, CameraPosition, CameraFront, CameraRight, CameraUp, remotes, time, flames);
        }, iterations: 1000);

        Assert.Equal(9, lights.Count);
    }

    private sealed class AlwaysFlames : IHeldFlamePointLookup
    {
        public bool TryGetHeldFlameModelPoint(
            int actorId, string sessionId, in PlayerEquipmentVisuals equipment, out Vector3 modelPoint)
        {
            modelPoint = new Vector3(-0.3f, 1.7f, 0.5f);
            return true;
        }
    }

    [Fact]
    public void TheGalleryKey_DressesTheRemotePlayers_WithoutChangingTheirLights()
    {
        string? key = PlayScene.LoopbackGalleryVisualKey(0f, shieldOffset: 2);
        PlayerEquipmentVisuals gallery = PlayerEquipmentVisuals.Parse(key);
        Assert.True(gallery.HasWorn);
        RemotePlayerPresentationState dressed = Remote(2, new Vector3(10f, 0f, 0f), HeldLightKind.Torch) with { Equipment = gallery };
        RemotePlayerPresentationState bare = Remote(2, new Vector3(10f, 0f, 0f), HeldLightKind.Torch);
        LightCollector withGallery = new();
        LightCollector without = new();
        PlayScene.LocalHeldLight none = new(HeldLightKind.None, ActorId: 1, FirstPerson: true, Vector3.Zero, -Vector3.UnitZ);

        PlayScene.AddHeldLights(withGallery, in none, CameraPosition, CameraFront, CameraRight, CameraUp, [dressed], timeSeconds: 0.0);
        PlayScene.AddHeldLights(without, in none, CameraPosition, CameraFront, CameraRight, CameraUp, [bare], timeSeconds: 0.0);

        Assert.Equal(1, withGallery.Count);
        Assert.Equal(without.Lights[0].Position, withGallery.Lights[0].Position);
        Assert.Equal(without.Lights[0].Radius, withGallery.Lights[0].Radius);
    }

    private static RemotePlayerPresentationState Remote(int actorId, Vector3 position, HeldLightKind heldLight, bool isAlive = true)
    {
        return new RemotePlayerPresentationState(
            actorId,
            $"session-{actorId}",
            $"Player {actorId}",
            new InterpolatedActorPose(position, -Vector3.UnitZ),
            CurrentHealth: isAlive ? 100 : 0,
            MaxHealth: 100,
            IsAlive: isAlive,
            HeldLight: heldLight);
    }

    [Fact]
    public void TheCarrierIdIsTheServerIdForTheLocalPlayer_AndNeverCollidesWithARemote()
    {
        // The client registry id is 1 on every client, the same number as the first-joined remote player. The local light
        // carries the server id the scene passes in, so the remote with id 1 is not the local carrier.
        LightCollector lights = new();
        PlayScene.LocalHeldLight local = new(
            HeldLightKind.Torch, ActorId: 1, FirstPerson: false, Vector3.Zero, -Vector3.UnitZ, CarrierActorId: 7);
        RemotePlayerPresentationState[] remotes = [Remote(1, new Vector3(10f, 0f, 0f), HeldLightKind.Torch)];

        PlayScene.AddHeldLights(lights, in local, CameraPosition, CameraFront, CameraRight, CameraUp, remotes, timeSeconds: 3.0);

        Assert.Equal(2, lights.Count);
        Assert.Equal(7, lights.Lights[0].CarrierActorId);
        Assert.Equal(1, lights.Lights[1].CarrierActorId);
        Assert.NotEqual(lights.Lights[0].CarrierActorId, lights.Lights[1].CarrierActorId);
    }

    [Fact]
    public void ALocalCarrierOfZeroExcludesNobody()
    {
        // Before the join list names the local actor the scene passes 0: no exclusion rather than a colliding key.
        LightCollector lights = new();
        PlayScene.LocalHeldLight local = new(HeldLightKind.Torch, ActorId: 1, FirstPerson: false, Vector3.Zero, -Vector3.UnitZ);

        PlayScene.AddHeldLights(
            lights, in local, CameraPosition, CameraFront, CameraRight, CameraUp, Array.Empty<RemotePlayerPresentationState>(), timeSeconds: 3.0);

        Assert.Equal(1, lights.Count);
        Assert.Equal(0, lights.Lights[0].CarrierActorId);
    }
}

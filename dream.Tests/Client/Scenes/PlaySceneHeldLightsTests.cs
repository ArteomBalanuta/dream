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

    [Fact]
    public void TheFlameWanderReach_IsTheDiagonalOfTheWanderBox()
    {
        Vector3 corner = new(FireFlicker.OffsetLateral, FireFlicker.OffsetVertical, FireFlicker.OffsetLateral);
        Assert.Equal(corner.Length, FlameWanderReach, 6);
        Assert.True(FlameWanderReach > FireFlicker.OffsetLateral);
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
}

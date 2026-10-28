using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Scenes;
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
        Assert.Equal(HeldLightAnchor.FirstPerson(CameraPosition, CameraFront, CameraRight, CameraUp), localLight.Position);
        Assert.Equal(HeldLightCatalog.TorchRadius, localLight.Radius);

        PointLight lantern = lights.Lights[1];
        Assert.Equal(HeldLightAnchor.Body(remotes[0].Pose.Position, remotes[0].Pose.Facing), lantern.Position);
        Assert.Equal(HeldLightCatalog.LanternRadius, lantern.Radius);
        Assert.Equal(HeldLightCatalog.LanternColor, lantern.Color);

        PointLight torch = lights.Lights[2];
        Assert.Equal(HeldLightAnchor.Body(remotes[2].Pose.Position, remotes[2].Pose.Facing), torch.Position);
        Assert.Equal(HeldLightCatalog.TorchRadius, torch.Radius);
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

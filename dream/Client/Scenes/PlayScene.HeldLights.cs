using OpenTK.Mathematics;
using Dream.Application;
using Dream.Client.Presentation;
using Dream.Rendering.Lighting;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;

namespace Dream.Client.Scenes;

// Wave T2b: the lights of held items (torch, lantern). The local player's comes from its own
// inventory snapshot (_localHeldLight, set by ApplyInventorySnapshot); a remote player's from its
// replicated RemotePlayerPresentationState.HeldLight, which stays None until protocol v6 (T2f)
// carries it. Runs every frame before the world draw and allocates nothing.
public partial class PlayScene
{
    // The light of the local player's equipped item, from the latest inventory snapshot.
    private HeldLightKind _localHeldLight;

    partial void CollectHeldLights(
        LightCollector lights,
        in WorldSessionPresentationState presentation,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        Player player = _runtime.Player;
        Camera camera = presentation.Camera;
        LocalHeldLight local = new(
            player.Combatant.Health.IsAlive ? _localHeldLight : HeldLightKind.None,
            player.Combatant.Actor.Id,
            _settings.CameraViewMode == CameraViewMode.FirstPerson,
            player.GetWorldPosition(),
            player.Camera.HorizontalForward);
        AddHeldLights(lights, in local, camera.Position, camera.Front, camera.Right, camera.Up, remotePlayers, _clientPresentationTimeSeconds);
    }

    // The local player's held light inputs for one frame.
    internal readonly record struct LocalHeldLight(
        HeldLightKind Kind,
        int ActorId,
        bool FirstPerson,
        Vector3 BodyPosition,
        Vector3 BodyFacing);

    // Adds one light per lit player: the local one (anchored to the camera in first person, to the
    // body in third person) and each living remote player whose HeldLight is not None. Static, with
    // its inputs as values, so tests can run it without a scene or GL context.
    internal static void AddHeldLights(
        LightCollector lights,
        in LocalHeldLight local,
        Vector3 cameraPosition,
        Vector3 cameraFront,
        Vector3 cameraRight,
        Vector3 cameraUp,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers,
        double timeSeconds)
    {
        if (local.Kind != HeldLightKind.None)
        {
            Vector3 anchor = local.FirstPerson
                ? HeldLightAnchor.FirstPerson(cameraPosition, cameraFront, cameraRight, cameraUp)
                : HeldLightAnchor.Body(local.BodyPosition, local.BodyFacing);
            if (HeldLightCatalog.TryCreate(local.Kind, anchor, timeSeconds, local.ActorId, out PointLight light))
            {
                lights.Add(in light);
            }
        }

        // Indexed, not foreach: the presenter's list is an interface here, and its enumerator would box.
        for (int i = 0; i < remotePlayers.Count; i++)
        {
            RemotePlayerPresentationState remote = remotePlayers[i];
            if (!remote.IsAlive || remote.HeldLight == HeldLightKind.None)
            {
                continue;
            }

            Vector3 anchor = HeldLightAnchor.Body(remote.Pose.Position, remote.Pose.Facing);
            if (HeldLightCatalog.TryCreate(remote.HeldLight, anchor, timeSeconds, remote.ActorId, out PointLight light))
            {
                lights.Add(in light);
            }
        }
    }
}

using OpenTK.Mathematics;
using Dream.Application;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Lighting;
using Dream.Simulation.Inventory;
using Dream.Simulation.Physics;
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

    // What the local player holds in each hand, from the latest inventory snapshot; drawn in the
    // hands of the third-person and isometric avatar.
    private PlayerEquipmentVisuals _localEquipmentVisuals;

    // Animation events for the local avatar: counters it compares, so a swing or a hit is played once.
    private int _localAttackSerial;
    private int _localHitSerial;
    // Counts the local player's accepted MeleeArc casts; a new value plays the cleave swing.
    private int _localSkillSwingSerial;
    private int _localLastHealth = -1;
    private double _localLastAttackSeconds = double.NegativeInfinity;
    private const double LocalAttackRepeatSeconds = 0.2;
    private const float AirborneVerticalSpeed = 0.5f;

    partial void CollectHeldLights(
        LightCollector lights,
        in WorldSessionPresentationState presentation,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        Player player = _runtime.Player;
        Camera camera = presentation.Camera;
        PushLocalAvatarInputs(player);
        // The server-numbered id, the key remote casters use; the registry id is 1 on every client and would collide
        // with the first remote player. 0 (no exclusion) until the join list names the local actor.
        int carrierId = _serverLocalActorId;
        _render.LocalPlayers.HeldLightActorId = carrierId;
        // The interpolated render pose (raw body in first person), so a third-person light rides
        // with the drawn model instead of stepping on prediction ticks.
        bool firstPerson = _settings.CameraViewMode == CameraViewMode.FirstPerson;
        bool hasFlame = false;
        Vector3 flameModelPoint = default;
        if (!firstPerson)
        {
            hasFlame = _render.LocalPlayers.TryGetHeldFlameModelPoint(out flameModelPoint);
        }

        LocalHeldLight local = new(
            player.Combatant.Health.IsAlive ? _localHeldLight : HeldLightKind.None,
            player.Combatant.Actor.Id,
            firstPerson,
            _runtime.LocalRenderPose.Position,
            _runtime.LocalRenderPose.Facing,
            hasFlame,
            flameModelPoint,
            carrierId);
        AddHeldLights(
            lights, in local, camera.Position, camera.Front, camera.Right, camera.Up, remotePlayers,
            FireClockSeconds, _render.RemotePlayers);
    }

    // Hands the local avatar renderer this frame's held items and animation events: a swing when the
    // prediction tick used the primary action, a hit when health dropped, a jump or fall when the
    // body moves vertically. Runs before the actors draw; the Draw signature stays the pose alone.
    private void PushLocalAvatarInputs(Player player)
    {
        var health = player.Combatant.Health;
        int currentHealth = health.CurrentHealth;
        if (_localLastHealth >= 0 && health.IsAlive && currentHealth < _localLastHealth)
        {
            _localHitSerial++;
        }

        _localLastHealth = currentHealth;

        if (_tickPrimaryUseTriggered &&
            _clientPresentationTimeSeconds - _localLastAttackSeconds >= LocalAttackRepeatSeconds)
        {
            _localAttackSerial++;
            _localLastAttackSeconds = _clientPresentationTimeSeconds;
        }

        OpenTK.Mathematics.Vector3 velocity = player.PhysicsBody.LinearVelocity.ToOpenTK();
        // The loopback debug mode (DREAM_LOOPBACK_REMOTE_PLAYERS) also puts the item gallery in the local hands.
        PlayerEquipmentVisuals equipment = _enableLoopbackRemotePlayers
            ? PlayerEquipmentVisuals.Parse(LoopbackGalleryVisualKey((float)_clientPresentationTimeSeconds, shieldOffset: 2))
            : _localEquipmentVisuals;
        _render.LocalPlayers.SetFrameInputs(new LocalPlayerFrameInputs(
            equipment,
            health.IsAlive,
            MathF.Sqrt((velocity.X * velocity.X) + (velocity.Z * velocity.Z)),
            MathF.Abs(velocity.Y) > AirborneVerticalSpeed,
            _localAttackSerial,
            _localHitSerial,
            _runtime.LocalRenderPose.TravelDirection,
            SkillSwingSerial: _localSkillSwingSerial));
    }

    // The local player's held light inputs for one frame.
    internal readonly record struct LocalHeldLight(
        HeldLightKind Kind,
        int ActorId,
        bool FirstPerson,
        Vector3 BodyPosition,
        Vector3 BodyFacing,
        // The flame of the held item in the model's own space, from the last local draw (third person only).
        bool HasFlame = false,
        Vector3 FlameModelPoint = default,
        // The server-numbered id the actor shadow casters are owned by; 0 while unknown (no shadow exclusion).
        int CarrierActorId = 0);

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
        double timeSeconds,
        IHeldFlamePointLookup? remoteFlames = null)
    {
        if (local.Kind != HeldLightKind.None)
        {
            // Third person: the flame of the item in the animated hand when the last draw posed one, else the
            // fixed body anchor.
            Vector3 anchor = local.FirstPerson
                ? HeldLightAnchor.FirstPerson(cameraPosition, cameraFront, cameraRight, cameraUp)
                : local.HasFlame
                    ? Vector3.TransformPosition(
                        local.FlameModelPoint, PlayerRenderState.CreateModelMatrix(local.BodyPosition, local.BodyFacing))
                    : HeldLightAnchor.Body(local.BodyPosition, local.BodyFacing);
            if (HeldLightCatalog.TryCreate(local.Kind, anchor, timeSeconds, local.ActorId, out PointLight light, local.CarrierActorId))
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

            Vector3 anchor = remoteFlames != null && remoteFlames.TryGetHeldFlameModelPoint(
                remote.ActorId, remote.SessionId, remote.Equipment, out Vector3 flame)
                ? Vector3.TransformPosition(flame, PlayerRenderState.CreateModelMatrix(remote.Pose.Position, remote.Pose.Facing))
                : HeldLightAnchor.Body(remote.Pose.Position, remote.Pose.Facing);
            if (HeldLightCatalog.TryCreate(remote.HeldLight, anchor, timeSeconds, remote.ActorId, out PointLight light, remote.ActorId))
            {
                lights.Add(in light);
            }
        }
    }
}

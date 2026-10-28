using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Building: build use actions and build placement material resolution (the session's building
// menu selection, else the supporting cell's material; see BuildMaterialCatalog).
public sealed partial class WorldSessionRuntime
{
    private const float MaxAuthoritativeBuildDistance = 3f;

    private AuthoritativeActionResult ProcessBuildUseAction(
        PlayerActionMessage message,
        BuildUseIntent intent,
        PlayerInteractionMode interactionMode,
        Vector3 authoritativeOrigin)
    {
        if (interactionMode != PlayerInteractionMode.Building)
        {
            return CreateRejectedActionResult(message, "Build action rejected because the authoritative mode is not Building.");
        }

        if ((intent.PlacementTarget - authoritativeOrigin).Length > MaxAuthoritativeBuildDistance)
        {
            return CreateRejectedActionResult(message, "Build placement rejected because the target is out of range.");
        }

        GeometryMaterialId placementMaterial = BuildMaterialCatalog.ResolvePlacement(
            ResolveSelectedBuildMaterial(message.SessionId),
            ResolveBuildSupportMaterial(intent.PlacementTarget));
        Cube? placedCube = Map.TryAddCube(
            intent.PlacementTarget,
            materialId: placementMaterial);
        if (placedCube == null || placedCube.Position.Y <= 0f)
        {
            return CreateRejectedActionResult(message, "Build placement rejected by authoritative validation.");
        }

        string ownerPlayerName = TryResolveDisplayName(message.SessionId);
        // The CubePlaced delta below carries the new id, so no CubeIdentified is exported for it.
        WorldObjectRecord record;
        _suppressCubeIdExports = true;
        try
        {
            record = _worldObjects.RegisterCube(
                placedCube,
                Map,
                actorId: null,
                ownerPlayerName: ownerPlayerName);
        }
        finally
        {
            _suppressCubeIdExports = false;
        }
        PersistPlacedCube(record, placementMaterial);
        Vector3 placedCell = ToCellPosition(placedCube.Position);
        _events.Publish(new CubePlacedEvent(placedCube, placedCell, placementMaterial));

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new WorldMutationResult(
                WorldMutationKind.CubePlaced,
                placedCell,
                Applied: true));
    }

    // The supporting cell's material, or null over the ground plane (no cell below).
    private GeometryMaterialId? ResolveBuildSupportMaterial(Vector3 placementTarget)
    {
        Vector3 belowCell = new(
            MathF.Floor(placementTarget.X + 1e-6f),
            MathF.Floor(placementTarget.Y - 1f + 1e-6f),
            MathF.Floor(placementTarget.Z + 1e-6f));
        GeometryCellRecord? supportCell = Map.TryGetGeometryCellAtCell((int)belowCell.X, (int)belowCell.Y, (int)belowCell.Z);
        return supportCell?.Definition.MaterialId;
    }
}

using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// Building menu: each session's selected build material ("build.material"). The selection is
// session state, like the interaction mode: not persisted, not part of the world checksum, and it
// changes nothing in the world by itself. It is applied in tick order with the session's other
// actions, and the authoritative build placement reads it (WorldSessionRuntime.Building.cs), so a
// selection and a build in the same tick resolve in queue order on every server alike.
public sealed partial class WorldSessionRuntime
{
    // Sessions with a non-Auto selection. An entry holds the ConnectedPlayer it was made for, so a
    // session id registered again (a new ConnectedPlayer) starts from Auto. Stale entries are
    // pruned whenever a selection is stored, so the map stays bounded by the registry.
    private readonly Dictionary<PlayerSessionId, BuildMaterialSelection> _buildMaterialSelections = new();
    private readonly List<PlayerSessionId> _staleBuildMaterialSelections = [];

    private readonly record struct BuildMaterialSelection(ConnectedPlayer Owner, GeometryMaterialId MaterialId);

    // The session's selected material, or null for Auto (also for an unknown session).
    public GeometryMaterialId? ResolveSelectedBuildMaterial(PlayerSessionId sessionId)
    {
        if (!_buildMaterialSelections.TryGetValue(sessionId, out BuildMaterialSelection selection))
        {
            return null;
        }

        return _sessions.TryGet(sessionId, out ConnectedPlayer? player) && ReferenceEquals(player, selection.Owner)
            ? selection.MaterialId
            : null;
    }

    // Sessions holding a non-Auto selection (tests): at most the registry's Count.
    internal int BuildMaterialSelectionCount => _buildMaterialSelections.Count;

    private AuthoritativeActionResult ProcessBuildMaterialSelectAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        BuildMaterialSelectIntent intent)
    {
        if (!BuildMaterialCatalog.IsValidSelection(intent.MaterialId))
        {
            return CreateRejectedActionResult(
                message,
                $"Build material rejected because material {(int)intent.MaterialId!.Value} is not placeable.");
        }

        PruneStaleBuildMaterialSelections();
        if (intent.MaterialId is GeometryMaterialId materialId)
        {
            _buildMaterialSelections[player.SessionId] = new BuildMaterialSelection(player, materialId);
        }
        else
        {
            _buildMaterialSelections.Remove(player.SessionId);
        }

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: null);
    }

    private void PruneStaleBuildMaterialSelections()
    {
        foreach (KeyValuePair<PlayerSessionId, BuildMaterialSelection> entry in _buildMaterialSelections)
        {
            if (!_sessions.TryGet(entry.Key, out ConnectedPlayer? player) || !ReferenceEquals(player, entry.Value.Owner))
            {
                _staleBuildMaterialSelections.Add(entry.Key);
            }
        }

        foreach (PlayerSessionId stale in _staleBuildMaterialSelections)
        {
            _buildMaterialSelections.Remove(stale);
        }

        _staleBuildMaterialSelections.Clear();
    }
}

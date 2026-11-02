using OpenTK.Mathematics;
using Dream.Simulation.Content;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Content NPCs (M3): the vendors of npcs.json. They are not enemies and have no body: they are not in the
// enemy registry or in physics, so nothing can hit them, and they never move. They exist only in the
// actor snapshots (CreatureKind.Npc, HP 1/1, the content's visual key) and as the target of
// NpcInteractIntent, VendorBuyIntent and VendorSellIntent. Nothing is persisted: they are spawned from
// the content on every start, at an offset from the world spawn, so a stored world finds them near its
// players whatever its spawn is.
public sealed partial class WorldSessionRuntime
{
    // One spawned vendor. The actor id is ReplicatedActorIdRanges.FirstNpcId plus the definition's index.
    internal sealed record SpawnedNpc(int ActorId, NpcDefinition Definition, Vector3 Position);

    private readonly List<SpawnedNpc> _npcs = [];

    internal IReadOnlyList<SpawnedNpc> Npcs => _npcs;

    // Spawns the content's vendors (EnemyContent.Npcs). Called once by ServerBootstrap.CreateRuntimeHost,
    // never by the runtime's constructor: a runtime built for a test has no NPCs, so its actor lists and
    // goldens are as they were. A second call changes nothing.
    public void SpawnContentNpcs()
    {
        if (_npcs.Count > 0)
        {
            return;
        }

        IReadOnlyList<NpcDefinition> definitions = EnemyContent.Npcs;
        for (int i = 0; i < definitions.Count; i++)
        {
            NpcDefinition definition = definitions[i];
            Vector3 position = new(
                Math.Clamp(_worldSpawn.X + definition.OffsetX, 0.5f, Math.Max(0.5f, Map.Width - 0.5f)),
                _worldSpawn.Y,
                Math.Clamp(_worldSpawn.Z + definition.OffsetZ, 0.5f, Math.Max(0.5f, Map.Depth - 0.5f)));
            _npcs.Add(new SpawnedNpc(ReplicatedActorIdRanges.FirstNpcId + i, definition, position));
        }
    }

    internal bool TryFindNpc(int actorId, out SpawnedNpc npc)
    {
        int index = actorId - ReplicatedActorIdRanges.FirstNpcId;
        if (index >= 0 && index < _npcs.Count)
        {
            npc = _npcs[index];
            return true;
        }

        npc = null!;
        return false;
    }

    // The NPCs' snapshots, appended to the actor list after the enemies.
    private void AppendNpcSnapshots(List<ActorSnapshot> actors)
    {
        for (int i = 0; i < _npcs.Count; i++)
        {
            SpawnedNpc npc = _npcs[i];
            actors.Add(new ActorSnapshot(
                npc.ActorId,
                Combat.CreatureKind.Npc,
                null,
                npc.Position,
                Vector3.UnitZ,
                1,
                1,
                true,
                DisplayName: npc.Definition.DisplayName,
                VisualKey: npc.Definition.VisualKey));
        }
    }
}

using Dream.Client.Replication;
using Dream.Rendering.Actors;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Providers;

// Villagers: name, and the talk hint when the local player is in range.
public sealed class NpcHoverInfoProvider : IHoverInfoProvider
{
    private readonly ClientNpcCache _npcs;

    public NpcHoverInfoProvider(ClientNpcCache npcs)
    {
        ArgumentNullException.ThrowIfNull(npcs);
        _npcs = npcs;
    }

    // The local player's position; the scene sets it each frame.
    public Vector3 PlayerPosition { get; set; }

    public HoverKind Kind => HoverKind.Npc;

    public bool TryDescribe(HoverTargetId id, HoverInfo info)
    {
        if (id.Kind != HoverKind.Npc || id.Id < int.MinValue || id.Id > int.MaxValue
            || !_npcs.TryGet((int)id.Id, out ClientNpc npc))
        {
            return false;
        }

        float range = ClientNpcCache.InteractRange;
        info.Clear();
        info.Target = id;
        info.Disposition = HoverDisposition.Friendly;
        info.Title = npc.DisplayName;
        info.Hint = ClientGroundItemCache.HorizontalDistanceSquared(PlayerPosition, npc.Position) <= range * range
            ? WorldEntityRenderer.TalkHint
            : string.Empty;
        return true;
    }
}

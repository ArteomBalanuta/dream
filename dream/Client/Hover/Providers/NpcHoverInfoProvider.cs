using Dream.Client.Replication;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Providers;

// Villagers: name, and the talk hint when the local player is in range.
public sealed class NpcHoverInfoProvider : IHoverInfoProvider
{
    public NpcHoverInfoProvider(ClientNpcCache npcs)
    {
        throw new NotImplementedException("world-hover stub");
    }

    // The local player's position; the scene sets it each frame.
    public Vector3 PlayerPosition { get; set; }

    public HoverKind Kind => HoverKind.Npc;

    public bool TryDescribe(HoverTargetId id, HoverInfo info) => throw new NotImplementedException("world-hover stub");
}

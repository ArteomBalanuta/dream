using Dream.Client.Presentation;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Living remote players as hover targets, from the list the last render built. Id is the player's actor id.
public sealed class RemotePlayerHoverSource : IHoverSource
{
    public const float Height = 1.8f;
    public const float Radius = 0.4f;
    public const float AnchorHeight = 1.0f;

    // The scene sets this after each render builds the remote player states.
    public IReadOnlyList<RemotePlayerPresentationState> Players { get; set; } = Array.Empty<RemotePlayerPresentationState>();

    public HoverKind Kind => HoverKind.RemotePlayer;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit)
    {
        hit = default;
        bool found = false;
        float bestDepth = 0f;
        long bestId = 0;
        IReadOnlyList<RemotePlayerPresentationState> players = Players;
        for (int i = 0; i < players.Count; i++)
        {
            RemotePlayerPresentationState player = players[i];
            if (!player.IsAlive)
            {
                continue;
            }

            float radius = sticky.Kind == HoverKind.RemotePlayer && sticky.Id == player.ActorId ? Radius * inflate : Radius;
            Vector3 feet = player.Pose.Position;
            if (!HoverGeometry.RayCapsule(ray.Origin, ray.Direction, feet, Height, radius, out float depth)
                || depth > ray.WallDepth)
            {
                continue;
            }

            if (!found || depth < bestDepth || (depth == bestDepth && player.ActorId < bestId))
            {
                found = true;
                bestDepth = depth;
                bestId = player.ActorId;
                hit = new HoverHit(new HoverTargetId(HoverKind.RemotePlayer, player.ActorId), depth, Anchor(in feet));
            }
        }

        return found;
    }

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor)
    {
        anchor = default;
        if (id.Kind != HoverKind.RemotePlayer)
        {
            return false;
        }

        IReadOnlyList<RemotePlayerPresentationState> players = Players;
        for (int i = 0; i < players.Count; i++)
        {
            RemotePlayerPresentationState player = players[i];
            if (player.IsAlive && player.ActorId == id.Id)
            {
                anchor = Anchor(player.Pose.Position);
                return true;
            }
        }

        return false;
    }

    private static Vector3 Anchor(in Vector3 feet) => new(feet.X, feet.Y + AnchorHeight, feet.Z);
}

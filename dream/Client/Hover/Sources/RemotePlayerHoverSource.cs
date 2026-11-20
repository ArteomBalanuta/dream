using Dream.Client.Presentation;
using Dream.Rendering.Ui;
using OpenTK.Mathematics;

namespace Dream.Client.Hover.Sources;

// Living remote players as hover targets, from the list the last render built. Id is the player's actor id.
public sealed class RemotePlayerHoverSource : IHoverSource
{
    // The scene sets this after each render builds the remote player states.
    public IReadOnlyList<RemotePlayerPresentationState> Players { get; set; } = Array.Empty<RemotePlayerPresentationState>();

    public HoverKind Kind => HoverKind.RemotePlayer;

    public bool TryHit(in HoverRay ray, HoverTargetId sticky, float inflate, out HoverHit hit) =>
        throw new NotImplementedException("world-hover stub");

    public bool TryGetLive(HoverTargetId id, out Vector3 anchor) => throw new NotImplementedException("world-hover stub");
}

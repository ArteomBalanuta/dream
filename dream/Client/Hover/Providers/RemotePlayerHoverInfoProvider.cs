using Dream.Client.Presentation;
using Dream.Rendering.Ui;

namespace Dream.Client.Hover.Providers;

// Remote players: name, level, health bar.
public sealed class RemotePlayerHoverInfoProvider : IHoverInfoProvider
{
    // The scene sets this after each render builds the remote player states.
    public IReadOnlyList<RemotePlayerPresentationState> Players { get; set; } = Array.Empty<RemotePlayerPresentationState>();

    public HoverKind Kind => HoverKind.RemotePlayer;

    public bool TryDescribe(HoverTargetId id, HoverInfo info) => throw new NotImplementedException("world-hover stub");
}

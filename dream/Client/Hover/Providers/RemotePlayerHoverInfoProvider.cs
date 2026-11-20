using Dream.Client.Presentation;
using Dream.Rendering.Ui;

namespace Dream.Client.Hover.Providers;

// Remote players: name, level, health bar.
public sealed class RemotePlayerHoverInfoProvider : IHoverInfoProvider
{
    private readonly HoverTextCache _text = new();

    // The scene sets this after each render builds the remote player states.
    public IReadOnlyList<RemotePlayerPresentationState> Players { get; set; } = Array.Empty<RemotePlayerPresentationState>();

    public HoverKind Kind => HoverKind.RemotePlayer;

    public bool TryDescribe(HoverTargetId id, HoverInfo info)
    {
        if (id.Kind != HoverKind.RemotePlayer || id.Id < int.MinValue || id.Id > int.MaxValue)
        {
            return false;
        }

        IReadOnlyList<RemotePlayerPresentationState> players = Players;
        for (int n = 0; n < players.Count; n++)
        {
            RemotePlayerPresentationState player = players[n];
            if (player.ActorId != (int)id.Id)
            {
                continue;
            }

            if (!player.IsAlive)
            {
                return false;
            }

            int max = Math.Max(0, player.MaxHealth);
            int current = Math.Clamp(player.CurrentHealth, 0, max);
            info.Clear();
            info.Target = id;
            info.Disposition = HoverDisposition.Friendly;
            info.Title = player.DisplayName ?? string.Empty;
            info.Subtitle = _text.Level(player.Level);
            info.BarCount = 1;
            info.Bars[0] = new HoverBar(HoverBarKind.Health, current, max, _text.Bar(current, max));
            return true;
        }

        return false;
    }
}

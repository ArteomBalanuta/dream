using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.Scenes;

// M3 on the client: ground items, content NPCs, the E key and the vendor window's state. The server is
// the authority for all of it; this side keeps what it was told, shows it, and sends one intent per
// pick. The fields are filled by their parameterless constructors in the headless play scene.
public partial class PlayScene
{
    private readonly ClientGroundItemCache _groundItems = new();
    private readonly ClientNpcCache _npcs = new();
    private readonly VendorHudState _vendorHud = new();
    private bool _wasInteractKeyPressed;

    // A vendor window closes when the player walks this far past the server's interact range.
    private const float VendorCloseMargin = 1f;

    // The local player's actor id as the SERVER numbers it (ground item claims use it), learned from
    // the join list and snapshots by session id. The client registry's own id is 1 on every client, so
    // it is only the fallback before the first list that names the player. 0 once the session ends.
    private int _serverLocalActorId;

    private int LocalActorId => _serverLocalActorId != 0 ? _serverLocalActorId : _runtime.Player.Combatant.Actor.Id;

    private ulong EstimatedServerTick => (ulong)Math.Max(0d, _serverTickClock.EstimatedTick);

    private void DrawEconomyEntities(Camera camera) =>
        _render.DrawWorldEntities(
            camera, _groundItems, _npcs, _vendorHud, _runtime.Player.GetWorldPosition(), LocalActorId, EstimatedServerTick);

    private void SendEconomyIntent(IPlayerActionPayload payload) =>
        _remoteClient.QueuePlayerAction(new PlayerActionMessage(
            new PlayerSessionId(_remoteClient.SessionId),
            _actionSequences.NextActionSequence(),
            payload));

    // Once a frame, outside chat: closes the vendor window on Esc or when it no longer fits (left the
    // range, died, lost the session, closed the inventory), ends a dead session's lists, and fires E.
    private void HandleInteractKey(KeyboardState keyboardState)
    {
        if (keyboardState.IsKeyDown(Keys.Escape) && !_wasSettingsTogglePressed)
        {
            _vendorHud.Close();
        }

        UpdateEconomyState();
        bool pressed = keyboardState.IsKeyDown(Keys.E);
        bool edge = pressed && !_wasInteractKeyPressed;
        _wasInteractKeyPressed = pressed;
        if (edge && _settings.CurrentRuntimeState.AllowGameplayInput)
        {
            TryInteractWithNearest();
        }
    }

    internal void UpdateEconomyState()
    {
        if (!_remoteClient.IsConnected)
        {
            // The session is over: nothing on the ground or at a vendor belongs to it any more.
            _serverLocalActorId = 0;
            _groundItems.Clear();
            _npcs.Clear();
            _vendorHud.Close();
            return;
        }

        if (!_vendorHud.IsOpen)
        {
            return;
        }

        if (_deathTracker.IsDead
            || !_settings.InventoryVisible
            || !_npcs.TryGet(_vendorHud.NpcActorId, out ClientNpc npc)
            || HorizontalDistance(_runtime.Player.GetWorldPosition(), npc.Position) > ClientNpcCache.InteractRange + VendorCloseMargin)
        {
            _vendorHud.Close();
        }
    }

    // E: talk to the nearest NPC in range, else pick up the nearest free ground item in range. One
    // intent; the server decides. False when there was nothing to act on.
    internal bool TryInteractWithNearest()
    {
        if (!_remoteClient.IsConnected || _deathTracker.IsDead)
        {
            return false;
        }

        Vector3 position = _runtime.Player.GetWorldPosition();
        if (_npcs.TryGetNearest(position, ClientNpcCache.InteractRange, out ClientNpc npc))
        {
            SendEconomyIntent(new NpcInteractIntent(npc.ActorId));
            return true;
        }

        if (_groundItems.TryGetNearest(position, ClientGroundItemCache.PickupRange, LocalActorId, EstimatedServerTick, out ClientGroundItem item))
        {
            SendEconomyIntent(new GroundPickupIntent(item.ItemInstanceId));
            return true;
        }

        return false;
    }

    // The HUD's economy picks of one frame, one intent each. The vendor picks need the window to be
    // open; a buy must name something the open vendor sells.
    internal void ApplyEconomyRequest(in HudInteractionRequest request)
    {
        if (request.CloseVendor)
        {
            _vendorHud.Close();
        }

        if (request.GroundPickupId is long groundId && groundId > 0)
        {
            SendEconomyIntent(new GroundPickupIntent(groundId));
        }

        if (request.InteractActorId is int npcId)
        {
            SendEconomyIntent(new NpcInteractIntent(npcId));
        }

        if (request.Vendor is VendorHudAction action && _vendorHud.IsOpen)
        {
            if (action.Kind == VendorHudActionKind.Buy && _vendorHud.SellsKind(action.ItemKind))
            {
                SendEconomyIntent(new VendorBuyIntent(_vendorHud.NpcActorId, action.ItemKind!));
            }
            else if (action.Kind == VendorHudActionKind.Sell && action.ItemInstanceId > 0 && (uint)action.SlotIndex < HudViewSlotLimit)
            {
                SendEconomyIntent(new VendorSellIntent(_vendorHud.NpcActorId, action.SlotIndex, action.ItemInstanceId));
            }
        }
    }

    private const uint HudViewSlotLimit = BagGrid.CellCount;

    // The answer to NpcInteractIntent. Wire data is hostile: the NPC must be one the client knows and
    // is near, the stock is capped and each line must name a shipped item at a sane price.
    private void ApplyVendorOpened(VendorOpenedResult? result)
    {
        if (result is null
            || _deathTracker.IsDead
            || !_npcs.TryGet(result.NpcActorId, out ClientNpc npc)
            || HorizontalDistance(_runtime.Player.GetWorldPosition(), npc.Position) > ClientNpcCache.InteractRange + VendorCloseMargin
            || _settings.SettingsVisible)
        {
            return;
        }

        List<VendorStockLine> lines = new(VendorHudState.MaxStock);
        IReadOnlyList<VendorStockEntry>? stock = result.Stock;
        int seen = Math.Min(stock?.Count ?? 0, VendorHudState.MaxStock * 4);
        for (int n = 0; n < seen && lines.Count < VendorHudState.MaxStock; n++)
        {
            VendorStockEntry entry = stock![n];
            if (entry.Price <= 0
                || entry.Price > InventoryConstants.MaxGold
                || !ClientGroundItemCache.TryResolveName(entry.ItemKind, out string name)
                || lines.Exists(line => string.Equals(line.ItemKind, entry.ItemKind, StringComparison.Ordinal)))
            {
                continue;
            }

            lines.Add(new VendorStockLine(entry.ItemKind, name, entry.Price));
        }

        // The window sits over the inventory: it opens it.
        if (!_settings.InventoryVisible)
        {
            _settings.ToggleInventory();
        }

        string title = string.IsNullOrWhiteSpace(result.DisplayName) ? npc.DisplayName : ClientNpcCache.CleanName(result.DisplayName);
        _vendorHud.Open(npc.ActorId, title, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines));
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) =>
        MathF.Sqrt(ClientGroundItemCache.HorizontalDistanceSquared(a, b));
}

using Dream.Multiplayer.Protocol;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Client.Replication;

// One ground item as the client shows it. The name comes from the shipped catalog, never from the wire.
public readonly record struct ClientGroundItem(
    long ItemInstanceId,
    string ItemKind,
    string DisplayName,
    ItemRarity Rarity,
    Vector3 Position,
    int ClaimantActorId,
    ulong ClaimExpiresTick);

// The ground items the server told this client about (M3). Fed by ServerGroundItems and nothing else:
// a Reset replaces the list, an upsert adds or replaces by item id, a removal drops by id, all
// idempotent, so a repeated or reordered delta changes nothing more. JoinAccepted does not touch it
// (chunked joins may arrive after the first deltas); the scene clears it when the session ends.
// The wire is hostile: the list is capped, non-finite or absurd positions and ids that are not
// positive are dropped, an item kind the shipped catalog does not know is dropped, and the display
// name is the catalog's. Parameterless constructor: the headless play scene builds it by reflection.
public sealed class ClientGroundItemCache
{
    // The server holds at most 256 ground items; the client never keeps more.
    public const int MaxItems = 256;

    // A coordinate beyond this is not a place in any world.
    public const float MaxCoordinate = 1_000_000f;

    public const int MaxKindLength = 64;

    // The server's pickup range (GroundPickupRange): the client only offers a pickup inside it.
    public const float PickupRange = 4f;

    private readonly List<ClientGroundItem> _items = new();

    public int Count => _items.Count;

    // Entries dropped as hostile or unknown since the cache was made (for tests and diagnostics).
    public int RejectedCount { get; private set; }

    public ClientGroundItem this[int index] => _items[index];

    public void Clear() => _items.Clear();

    public void Apply(ServerGroundItems? message)
    {
        if (message is null)
        {
            return;
        }

        if (message.Reset)
        {
            _items.Clear();
        }

        IReadOnlyList<GroundItemSnapshot>? upserts = message.Upserts;
        if (upserts is not null)
        {
            int limit = Math.Min(upserts.Count, MaxItems);
            for (int n = 0; n < limit; n++)
            {
                Upsert(upserts[n]);
            }
        }

        IReadOnlyList<long>? removed = message.RemovedIds;
        if (removed is not null)
        {
            int limit = Math.Min(removed.Count, MaxItems);
            for (int n = 0; n < limit; n++)
            {
                Remove(removed[n]);
            }
        }
    }

    public bool TryGet(long itemInstanceId, out ClientGroundItem item)
    {
        int index = IndexOf(itemInstanceId);
        if (index >= 0)
        {
            item = _items[index];
            return true;
        }

        item = default;
        return false;
    }

    // The nearest item within range of the point on the ground plane, by horizontal distance. An item
    // another player holds a claim on is skipped.
    public bool TryGetNearest(Vector3 from, float range, int localActorId, ulong serverTick, out ClientGroundItem nearest)
    {
        nearest = default;
        float best = range * range;
        bool found = false;
        for (int n = 0; n < _items.Count; n++)
        {
            ClientGroundItem item = _items[n];
            if (IsHeldBack(in item, localActorId, serverTick))
            {
                continue;
            }

            float d = HorizontalDistanceSquared(from, item.Position);
            if (d <= best)
            {
                best = d;
                nearest = item;
                found = true;
            }
        }

        return found;
    }

    // True while the item is claimed by another actor and the claim has not run out: it is not the
    // local player's to take yet.
    public static bool IsHeldBack(in ClientGroundItem item, int localActorId, ulong serverTick) =>
        item.ClaimantActorId != 0 && item.ClaimantActorId != localActorId && serverTick < item.ClaimExpiresTick;

    public static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    // The display name of a shipped item kind, or false for an unknown kind, an over-long one, or a
    // catalog that cannot be read.
    public static bool TryResolveName(string? itemKind, out string displayName)
    {
        displayName = string.Empty;
        if (string.IsNullOrEmpty(itemKind) || itemKind.Length > MaxKindLength)
        {
            return false;
        }

        try
        {
            if (!ContentCatalog.Shipped.TryGetItem(itemKind, out ItemDefinition definition))
            {
                return false;
            }

            displayName = definition.DisplayName;
            return true;
        }
        catch (ContentValidationException)
        {
            return false;
        }
    }

    public static bool IsPlausible(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
        && MathF.Abs(position.X) <= MaxCoordinate && MathF.Abs(position.Y) <= MaxCoordinate
        && MathF.Abs(position.Z) <= MaxCoordinate;

    private void Upsert(GroundItemSnapshot snapshot)
    {
        if (snapshot.ItemInstanceId <= 0 || !IsPlausible(snapshot.Position)
            || !TryResolveName(snapshot.ItemKind, out string displayName))
        {
            RejectedCount++;
            return;
        }

        ItemRarity rarity = Enum.IsDefined(snapshot.Rarity) ? snapshot.Rarity : ItemRarity.Common;
        // A claim is a pair: an actor with a deadline, or neither.
        bool claimed = snapshot.ClaimantActorId > 0 && snapshot.ClaimExpiresTick > 0;
        ClientGroundItem item = new(
            snapshot.ItemInstanceId,
            snapshot.ItemKind,
            displayName,
            rarity,
            snapshot.Position,
            claimed ? snapshot.ClaimantActorId : 0,
            claimed ? snapshot.ClaimExpiresTick : 0);

        int index = IndexOf(item.ItemInstanceId);
        if (index >= 0)
        {
            _items[index] = item;
        }
        else if (_items.Count < MaxItems)
        {
            _items.Add(item);
        }
        else
        {
            RejectedCount++;
        }
    }

    private void Remove(long itemInstanceId)
    {
        int index = IndexOf(itemInstanceId);
        if (index >= 0)
        {
            _items.RemoveAt(index);
        }
    }

    private int IndexOf(long itemInstanceId)
    {
        for (int n = 0; n < _items.Count; n++)
        {
            if (_items[n].ItemInstanceId == itemInstanceId)
            {
                return n;
            }
        }

        return -1;
    }
}

// One content NPC from the actor snapshots.
public readonly record struct ClientNpc(int ActorId, string DisplayName, string? VisualKey, Vector3 Position, Vector3 Facing);

// The NPCs of the latest actor list (M3): a snapshot or join list replaces the set, like the enemy
// registry. Only actors of kind Npc count. The list is hostile: capped, a non-finite or absurd
// position is skipped, a repeated id keeps the first, names lose control characters and are cut.
// Parameterless constructor for the headless play scene.
public sealed class ClientNpcCache
{
    public const int MaxNpcs = 32;
    public const int MaxNameChars = 32;
    public const int MaxVisualKeyChars = 64;

    // The server's NpcInteractRange: the client only offers a talk inside it.
    public const float InteractRange = 6f;

    private readonly List<ClientNpc> _npcs = new();

    public int Count => _npcs.Count;

    public ClientNpc this[int index] => _npcs[index];

    public void Clear() => _npcs.Clear();

    public void ApplyActors(IReadOnlyList<Dream.Simulation.World.ActorSnapshot>? actors)
    {
        _npcs.Clear();
        if (actors is null)
        {
            return;
        }

        for (int n = 0; n < actors.Count && _npcs.Count < MaxNpcs; n++)
        {
            Dream.Simulation.World.ActorSnapshot actor = actors[n];
            if (actor.Kind != Dream.Simulation.Combat.CreatureKind.Npc
                || actor.ActorId <= 0
                || !ClientGroundItemCache.IsPlausible(actor.Position)
                || TryGet(actor.ActorId, out _))
            {
                continue;
            }

            Vector3 facing = float.IsFinite(actor.Facing.X) && float.IsFinite(actor.Facing.Y) && float.IsFinite(actor.Facing.Z)
                ? actor.Facing
                : -Vector3.UnitZ;
            string? visual = actor.VisualKey is { Length: > 0 and <= MaxVisualKeyChars } key ? key : null;
            _npcs.Add(new ClientNpc(actor.ActorId, CleanName(actor.DisplayName), visual, actor.Position, facing));
        }
    }

    public bool TryGet(int actorId, out ClientNpc npc)
    {
        for (int n = 0; n < _npcs.Count; n++)
        {
            if (_npcs[n].ActorId == actorId)
            {
                npc = _npcs[n];
                return true;
            }
        }

        npc = default;
        return false;
    }

    public bool TryGetNearest(Vector3 from, float range, out ClientNpc nearest)
    {
        nearest = default;
        float best = range * range;
        bool found = false;
        for (int n = 0; n < _npcs.Count; n++)
        {
            float d = ClientGroundItemCache.HorizontalDistanceSquared(from, _npcs[n].Position);
            if (d <= best)
            {
                best = d;
                nearest = _npcs[n];
                found = true;
            }
        }

        return found;
    }

    // A name from the wire for display: control characters out, cut to MaxNameChars, never empty.
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Stranger";
        }

        Span<char> buffer = stackalloc char[MaxNameChars];
        int length = 0;
        foreach (char ch in name)
        {
            if (length == MaxNameChars)
            {
                break;
            }

            if (!char.IsControl(ch) && !char.IsSurrogate(ch))
            {
                buffer[length++] = ch;
            }
        }

        string cleaned = new string(buffer[..length]).Trim();
        return cleaned.Length == 0 ? "Stranger" : cleaned;
    }
}

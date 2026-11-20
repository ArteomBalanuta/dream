using System.Globalization;
using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Runtime;

// The /dev staging verbs (DevCommands, DedicatedServerHost.DevCommands). Each acts on one session's seat and
// answers (ok, text) without the "[dev] " prefix, which the host adds. They reuse what the game already does:
// SpawnEnemy for spawns, the respawn body test for standing places, the vitals the respawn writes, the loot
// grant for rolled items, the vendor grant for fixed ones and the dawn despawn's removal. Who may call them
// (the DevCommandsAllowed flag and a loopback connection) is the host's decision, made under the same tick
// lock this runs in; here only the seat is re-checked: it must be spawned and alive at the moment of effect.
public sealed partial class WorldSessionRuntime
{
    private readonly List<int> _devClearScratch = [];

    internal (bool Ok, string Message) DevSpawn(PlayerSessionId session, string enemyKey, int count, float distance)
    {
        if (!TryGetDevSeat(session, out _, out IAuthoritativePlayerTarget? seat, out string? refusal))
        {
            return (false, refusal!);
        }

        if (!TryFindEnemyTemplate(enemyKey, out EnemyTemplate? template))
        {
            return (false, $"Unknown enemy '{enemyKey}'. Known: {string.Join(", ", EnemyContent.Enemies.Select(e => e.Key))}.");
        }

        Vector3 origin = seat!.Position;
        int placed = 0;
        for (int i = 0; i < count; i++)
        {
            // Evenly round the ring; the first one stands at +X so a single spawn is predictable.
            float angle = i * MathF.Tau / count;
            float x = origin.X + (MathF.Cos(angle) * distance);
            float z = origin.Z + (MathF.Sin(angle) * distance);
            if (TryFindStandable(x, z, origin.Y, out Vector3 position))
            {
                SpawnEnemy(template!, position, campKey: null);
                placed++;
            }
        }

        if (placed == 0)
        {
            return (false, $"No standable ground {Meters(distance)} m around you.");
        }

        return (true, string.Create(CultureInfo.InvariantCulture, $"spawned {placed} {template!.Key} near {origin.X:0.0},{origin.Z:0.0}"));
    }

    // mode: "xz" (x, z), "camp" or "npc" (key names it) or "spawn".
    internal (bool Ok, string Message) DevTeleport(PlayerSessionId session, string mode, string? key, float x, float z)
    {
        if (!TryGetDevSeat(session, out _, out IAuthoritativePlayerTarget? seat, out string? refusal))
        {
            return (false, refusal!);
        }

        Vector3 destination;
        string label;
        switch (mode)
        {
            case "xz":
                if (!TryFindStandable(x, z, null, out destination))
                {
                    return (false, string.Create(CultureInfo.InvariantCulture, $"No standable ground at {x:0.0},{z:0.0} (the map is {Map.Width} x {Map.Depth} m)."));
                }

                label = string.Create(CultureInfo.InvariantCulture, $"{x:0.0},{z:0.0}");
                break;
            case "camp":
                SpawnCamp? camp = null;
                foreach (SpawnCamp candidate in EnemyContent.Camps)
                {
                    if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        camp = candidate;
                        break;
                    }
                }

                if (camp == null)
                {
                    return (false, $"Unknown camp '{key}'. Known: {string.Join(", ", EnemyContent.Camps.Select(c => c.Key))}.");
                }

                Vector3 centre = WolfSpawnPolicy.ResolveCampSpawn(camp.X, camp.Z, 0, 1, Map.Width, Map.Depth, camp.IsAbsolute);
                if (!TryFindStandable(centre.X, centre.Z, centre.Y, out destination))
                {
                    return (false, $"No standable ground at camp '{camp.Key}'.");
                }

                label = "camp " + camp.Key;
                break;
            case "npc":
                SpawnedNpc? npc = null;
                for (int i = 0; i < _npcs.Count; i++)
                {
                    if (string.Equals(_npcs[i].Definition.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        npc = _npcs[i];
                        break;
                    }
                }

                if (npc == null)
                {
                    return (false, $"Unknown npc '{key}'. Known: {string.Join(", ", _npcs.Select(n => n.Definition.Key))}.");
                }

                if (!TryFindStandable(npc.Position.X, npc.Position.Z, npc.Position.Y, out destination))
                {
                    return (false, $"No standable ground at npc '{npc.Definition.Key}'.");
                }

                label = "npc " + npc.Definition.Key;
                break;
            case "spawn":
                destination = ResolveRespawnPosition(Map, _worldSpawn, _respawnRubbleScratch);
                label = "the world spawn";
                break;
            default:
                return (false, "Unknown teleport target.");
        }

        seat!.SetWorldPosition(destination);
        return (true, string.Create(CultureInfo.InvariantCulture, $"teleported to {label} ({destination.X:0.0},{destination.Z:0.0})"));
    }

    // rarity applies to items that roll; a fixed item ignores it.
    internal (bool Ok, string Message) DevGive(PlayerSessionId session, string itemKey, string? rarity, int count)
    {
        if (!TryGetDevSeat(session, out ConnectedPlayer? player, out _, out string? refusal))
        {
            return (false, refusal!);
        }

        ItemDefinition? definition = null;
        foreach (ItemDefinition candidate in ContentCatalog.Shipped.Items)
        {
            if (string.Equals(candidate.Key, itemKey, StringComparison.OrdinalIgnoreCase))
            {
                definition = candidate;
                break;
            }
        }

        if (definition == null)
        {
            return (false, $"Unknown item '{itemKey}'.");
        }

        ItemRarity tier = ItemRarity.Common;
        if (rarity != null && (!Enum.TryParse(rarity, ignoreCase: true, out tier) || !Enum.IsDefined(tier)))
        {
            return (false, "Rarity is common, uncommon or rare.");
        }

        string characterKey = player!.CharacterKey.Value;
        ulong sourceHash = StableHash.Of("dev-give:" + characterKey);
        Func<bool>? gate = _inventory.ActionGate;
        _inventory.ActionGate = null; // a dev grant does not spend the player's own inventory action budget
        int landed = 0;
        InventoryEquipOutcome last = InventoryEquipOutcome.Equipped;
        try
        {
            for (int i = 0; i < count; i++)
            {
                last = definition.Rolls != null
                    ? _inventory.TryGrantRolled(
                        characterKey,
                        definition.Key,
                        tier,
                        new ItemRollSeed(SimulationSeed, CurrentServerTick, sourceHash, Seed.FromInt(unchecked((int)_inventory.NextItemInstanceId)), i))
                    : GrantFixedItem(characterKey, definition.Key);
                if (last != InventoryEquipOutcome.Equipped)
                {
                    break;
                }

                landed++;
            }
        }
        finally
        {
            _inventory.ActionGate = gate;
        }

        if (landed > 0)
        {
            MarkInventoryPush(session);
        }

        if (landed == count)
        {
            return (true, $"gave {landed} {definition.Key}");
        }

        string why = last == InventoryEquipOutcome.BackpackFull ? "the bag is full" : last.ToString();
        return (landed > 0, $"gave {landed} of {count} {definition.Key} ({why})");
    }

    // A free unrolled item: the vendor grant with a 1 gold price, funded by a borrowed gold piece when the
    // wallet is empty and refunded on success, so the wallet ends where it began. Nothing is borrowed unless
    // the purchase will go through: with an empty wallet TryBuy is first probed (it refuses a failed-write
    // inventory or an unknown kind before it looks at gold, and answers NotEnoughGold once those pass), and
    // the bag is checked for room. The caller has lifted the action budget, so the purchase cannot throttle.
    private InventoryEquipOutcome GrantFixedItem(string characterKey, string itemKey)
    {
        if (!_inventory.Ensure(characterKey))
        {
            return InventoryEquipOutcome.StillLoading;
        }

        bool borrowed = false;
        if (_inventory.GoldOf(characterKey) < 1)
        {
            InventoryEquipOutcome probe = _inventory.TryBuy(characterKey, itemKey, 1);
            if (probe != InventoryEquipOutcome.NotEnoughGold)
            {
                return probe;
            }

            if (!_inventory.HasRoomFor(characterKey, itemKey))
            {
                return InventoryEquipOutcome.BackpackFull;
            }

            if (!_inventory.TryAddGold(characterKey, 1))
            {
                return InventoryEquipOutcome.StillLoading;
            }

            borrowed = true;
        }

        InventoryEquipOutcome outcome = _inventory.TryBuy(characterKey, itemKey, 1);
        if (outcome == InventoryEquipOutcome.Equipped && !borrowed)
        {
            _inventory.TryAddGold(characterKey, 1);
        }
        else if (borrowed && outcome != InventoryEquipOutcome.Equipped)
        {
            _inventory.TryRemoveGold(characterKey, 1);
        }

        return outcome;
    }

    // radius 0 removes every live enemy; otherwise those within radius metres (flat distance) of the seat.
    internal (bool Ok, string Message) DevClear(PlayerSessionId session, float radius)
    {
        if (!TryGetDevSeat(session, out _, out IAuthoritativePlayerTarget? seat, out string? refusal))
        {
            return (false, refusal!);
        }

        Vector3 origin = seat!.Position;
        float limit = radius * radius;
        _devClearScratch.Clear();
        IReadOnlyList<EnemyActor> enemies = _enemies.ActiveActors;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyActor enemy = enemies[i];
            if (!enemy.IsActive)
            {
                continue;
            }

            float dx = enemy.Position.X - origin.X;
            float dz = enemy.Position.Z - origin.Z;
            if (radius <= 0f || (dx * dx) + (dz * dz) <= limit)
            {
                _devClearScratch.Add(enemy.Combatant.Actor.Id);
            }
        }

        int removed = 0;
        for (int i = 0; i < _devClearScratch.Count; i++)
        {
            if (_enemies.Remove(_devClearScratch[i]))
            {
                DeletePersistedEnemy(_devClearScratch[i]);
                removed++;
            }
        }

        _devClearScratch.Clear();
        return (true, radius <= 0f ? $"cleared {removed} enemies" : $"cleared {removed} enemies within {Meters(radius)} m");
    }

    // The dev loadout: attributes, level and full vitals in one line, so a fresh character is worth
    // testing with. The admin role is NOT set here - it lives on the account, not the seat, and the host
    // owns that write (DedicatedServerHost.DevCommands).
    //
    // ApplyStats and ApplyProgression are used rather than poking BaseStats, because max health and max
    // mana are derived from vitality, energy and level: setting the fields alone would leave the bars
    // stale until something else happened to recalculate them.
    internal (bool Ok, string Message) DevBoost(PlayerSessionId session, int attribute, int level)
    {
        if (!TryGetDevSeat(session, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? seat, out string? refusal))
        {
            return (false, refusal!);
        }

        int value = Math.Clamp(attribute, 1, PlayerCombatStats.AttributeCeiling);
        PlayerCombatStats stats = new()
        {
            Strength = value,
            Vitality = value,
            Agility = value,
            Energy = value,
            AttackSpeed = seat!.Combatant.BaseStats.AttackSpeed
        };

        seat.Combatant.ApplyStats(stats);
        seat.Combatant.ApplyProgression(Math.Max(1, level), seat.Combatant.Experience);

        int maxHealth = seat.Combatant.Health.MaxHealth;
        seat.Combatant.Health.SetAuthoritativeState(maxHealth, maxHealth);
        seat.Combatant.SetMana(seat.Combatant.MaxMana);
        player!.VitalsDirty = true;
        return (true, $"boosted to level {seat.Combatant.Level}, attributes {value}, {maxHealth} health, {seat.Combatant.MaxMana} mana");
    }

    internal (bool Ok, string Message) DevHeal(PlayerSessionId session)
    {
        if (!TryGetDevSeat(session, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? seat, out string? refusal))
        {
            return (false, refusal!);
        }

        int maxHealth = seat!.Combatant.Health.MaxHealth;
        seat.Combatant.Health.SetAuthoritativeState(maxHealth, maxHealth);
        seat.Combatant.SetMana(seat.Combatant.MaxMana);
        player!.VitalsDirty = true;
        return (true, $"healed to {maxHealth} health and {seat.Combatant.MaxMana} mana");
    }

    internal (bool Ok, string Message) DevSetHealth(PlayerSessionId session, int value)
    {
        if (!TryGetDevSeat(session, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? seat, out string? refusal))
        {
            return (false, refusal!);
        }

        int maxHealth = seat!.Combatant.Health.MaxHealth;
        int set = Math.Clamp(value, 1, Math.Max(1, maxHealth));
        seat.Combatant.Health.SetAuthoritativeState(set, maxHealth);
        player!.VitalsDirty = true;
        return (true, $"health {set}/{maxHealth}");
    }

    internal (bool Ok, string Message) DevSetMana(PlayerSessionId session, int value)
    {
        if (!TryGetDevSeat(session, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? seat, out string? refusal))
        {
            return (false, refusal!);
        }

        seat!.Combatant.SetMana(value);
        player!.VitalsDirty = true;
        return (true, $"mana {seat.Combatant.CurrentMana}/{seat.Combatant.MaxMana}");
    }

    // The session's seat, only while it is spawned and alive (a dead player is waiting to respawn; staging
    // that seat would fight the respawn).
    private bool TryGetDevSeat(
        PlayerSessionId session,
        out ConnectedPlayer? player,
        out IAuthoritativePlayerTarget? seat,
        out string? refusal)
    {
        refusal = null;
        if (!TryResolveAuthoritativePlayerTarget(session, out player, out seat)
            || player == null
            || seat == null
            || !seat.IsSpawned)
        {
            refusal = "You are not in the world.";
            return false;
        }

        if (!seat.IsAlive)
        {
            refusal = "You are dead.";
            return false;
        }

        return true;
    }

    private bool TryFindEnemyTemplate(string key, out EnemyTemplate? template)
    {
        foreach (EnemyTemplate candidate in EnemyContent.Enemies)
        {
            if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                template = candidate;
                return true;
            }
        }

        template = null;
        return false;
    }

    // A place a body can stand at world metres x, z: a solid cell below, the one-cell-wide two-cell-tall body
    // clear (the respawn test), inside the map. The surface nearest referenceY, or the highest one when it
    // is null. False outside the map or with no such surface in the column.
    private bool TryFindStandable(float x, float z, float? referenceY, out Vector3 position)
    {
        position = default;
        Map map = Map;
        if (!float.IsFinite(x) || !float.IsFinite(z)
            || x < 0.5f || z < 0.5f || x > map.Width - 0.5f || z > map.Depth - 0.5f)
        {
            return false;
        }

        List<(GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId)> rubble = _respawnRubbleScratch;
        rubble.Clear();
        map.CaptureFracturePiecesInto(rubble);
        int cellX = (int)MathF.Floor(x);
        int cellZ = (int)MathF.Floor(z);
        int best = -1;
        for (int y = map.Height - 2; y >= 1; y--)
        {
            if (!IsSolidCell(map, cellX, y - 1, cellZ) || !BodyFits(map, rubble, x, z, y))
            {
                continue;
            }

            if (referenceY is not { } reference)
            {
                best = y;
                break;
            }

            if (best < 0 || MathF.Abs(y - reference) < MathF.Abs(best - reference))
            {
                best = y;
            }
        }

        if (best < 0)
        {
            return false;
        }

        position = new Vector3(x, best, z);
        return true;
    }

    private static string Meters(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}

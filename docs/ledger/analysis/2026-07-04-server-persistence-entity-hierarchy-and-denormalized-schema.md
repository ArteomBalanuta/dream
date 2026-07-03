Purpose: Analyze how to evolve server-side player/enemy/NPC/item/inventory abstractions together with the new SQLite persistence direction.
Status: Analysis
Scope: Authoritative runtime hierarchy, denormalized persistence tables, inventory ownership, item/world relationships, and future NPC/enemy extensibility.

## Goal

Address the roadmap ideas in `roadmap_ideas/npcs_enemies_players.txt` and `roadmap_ideas/server.txt` without introducing a brittle "everything inherits from Entity" engine core.

The target outcome is:

- server-owned multiplayer state
- player persistence by name
- persistent player position, cubes, and active enemies
- future-ready abstractions for players, enemies, NPCs, items, and inventories
- a denormalized SQLite schema that is simple to query and restore at startup

## Existing Shape

The current codebase already has several useful seams:

- `Player` and `RemoteSessionPlayerState` represent player actors at different authority depths
- `EnemyActor` and `EnemyCombatant` represent the first enemy family
- `WorldObjectRegistry` covers destructible world objects
- `CombatActorHandle`, `CreatureKind`, and `ActorSnapshot` already provide a lightweight shared actor identity layer
- `PlayerCombatStats` currently exists as a player-owned stat model
- `ViewModelCatalog` and held-item presentation already assume a future "equipped item changes what appears in-hand" path

The main gaps are:

- no server-owned inventory model
- no item instance model
- no persistent NPC/enemy/player hierarchy policy
- no persistent world-entity schema beyond the first player profile DDL
- too much of the current state still assumes player-only or enemy-only code paths

## Design Pressure

The user wants:

- per-player server-owned mode/stats
- persistent player coordinates
- persistent cubes owned by players
- persistent active enemies such as wolves
- server-tracked item ownership and pickup
- denormalized database tables

That means the engine should avoid one highly normalized "entities + components + joins everywhere" schema. The code can still use interfaces and focused runtime types, but persistence should favor table-per-runtime-family for fast startup restore and straightforward writes.

## Recommended Runtime Hierarchy

### Principle

Use capability-oriented runtime types, not a deep inheritance tree.

Recommended shape:

- shared lightweight identity contracts
- per-family runtime types
- family-specific persistence records

### Runtime Families

#### Actors

Actors are things that can exist in the shared world with position and authoritative identity.

Families:

- `Player`
- `RemoteSessionPlayerState`
- future `NpcActor`
- `EnemyActor`

Shared actor contracts should stay lightweight:

- actor id
- kind
- display name
- position
- facing
- optional health/stats hooks

Do not force one monolithic `EntityBase` with rendering, AI, inventory, and combat all mixed together.

#### Combat-Capable Creatures

Players, enemies, and future combat NPCs should share a small combat capability seam.

Recommended shared concepts:

- `HealthState`
- shared stat snapshot/value object
- optional combatant wrapper per family

Do not force non-combat world items into this layer.

#### Items

Items should be first-class authoritative server objects with instance identity.

They need:

- item instance id
- item definition id/kind
- display name
- visual id/model id for client presentation
- world position when dropped
- owner player name when held
- inventory slot when equipped

Items are not creatures and should not share the creature hierarchy. They are world objects plus inventory state.

#### Inventory

Inventory should be modeled as a player-owned container relationship, not as six hardcoded fields on the player row.

The runtime can expose:

- inventory collection
- active hotbar slots `1..6`
- equipped/held item selection

But persistence can stay denormalized through one row per player-slot assignment plus one item instance row.

## Recommended Stat Policy

The current player-only stat model should evolve into a shared actor-stat model only for creature families that really need it.

Recommended creature stat fields for the persistence layer:

- current HP
- max HP
- strength
- agility
- vitality
- energy
- movement speed
- attack speed
- level

For the code layer:

- keep simple family-specific wrappers where needed
- add one reusable stat snapshot/value object for persistence and replication
- avoid introducing a speculative modifier framework for items/buffs until inventory/equipment actually needs it in runtime logic

## Recommended Denormalized Schema Direction

### Why Denormalized Fits Here

A denormalized schema is a good fit because startup restore is a dominant use case.

The server wants to answer questions like:

- which players exist and where should they spawn?
- which cubes survive and who owns them?
- which enemies are alive and where are they?
- which items are on the ground?
- which items belong to which player and which hotbar slot?

Those are direct world-restore queries, not analytics-style relational exploration.

### Recommended Table Families

#### Player tables

- `player_profiles`
  - persistent identity by player name
  - display/runtime defaults and last authoritative mode/stats

- `player_world_state`
  - last persisted position/facing
  - current HP/energy and other reconnect state

This intentionally duplicates some fields from `player_profiles` if needed. That is acceptable in this design because restore simplicity matters more than strict normalization.

#### Enemy/NPC tables

- `enemy_instances`
  - one row per active alive enemy instance
  - kind, display name, level, HP, map position, facing, stat fields

- future `npc_instances`
  - if NPC behavior diverges enough from enemies

Do not collapse enemies and NPCs into one generic table yet unless their runtime behavior truly converges.

#### World/cube tables

- `placed_cube_instances`
  - cube id
  - owner player name
  - material/shape data
  - cell/world position
  - optional durability/HP if cube-like objects remain damageable in persistence

#### Item tables

- `item_instances`
  - one row per item instance, whether world-dropped or player-owned
  - item kind, display name, visual id/model id
  - owner player name nullable
  - world position nullable
  - stack count if stacking is introduced later
  - current state such as `World`, `Inventory`, `Equipped`

- `player_inventory_slots`
  - player name
  - slot index `1..6`
  - item instance id

This is still denormalized in practice because item presentation and ownership data live directly on the item row, while the hotbar mapping stays extremely simple.

## Recommended Restore Flow

1. Open SQLite.
2. Run schema bootstrap.
3. Load player profiles and player world state by name.
4. Load surviving placed cubes and instantiate them into the authoritative map.
5. Load active enemy instances and reconstruct enemy runtime actors.
6. Load item instances and inventory slot assignments.
7. If the database is empty, use bootstrap/generated defaults as fallback.

The database is primary truth once data exists.

## Key Architectural Recommendation

Keep runtime abstraction denormalized by family in both code and storage:

- players stay player types
- enemies stay enemy types
- NPCs become their own runtime family when introduced
- items become item instances
- inventory becomes a relationship around player name plus item instances

Use shared interfaces for capability and replication, not a deep inheritance tree or a giant generic `entity_instances` table.

## Risks

- building one generic "entity" base class that becomes an everything-bagel
- over-normalizing the schema and turning startup restore into many fragile joins
- forcing items into creature/combat inheritance
- introducing NPC tables before NPC behavior exists in runtime
- over-abstracting stats before a second real stat-bearing actor family actually needs all fields in combat

## Recommendation

The next durable design step should define:

- the runtime family boundaries
- the shared actor/stat snapshots used across replication and persistence
- the denormalized SQLite table contract for players, cubes, enemies, items, and hotbar slots

That should then drive implementation in small phases rather than one giant persistence rewrite.

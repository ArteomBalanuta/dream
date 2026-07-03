Purpose: Define the approved first-pass HUD inventory, server-backed tool ownership, and equipped-item flow.
Status: Approved
Scope: Authoritative inventory ownership, join/action replication, HUD overlay behavior, and held-tool viewmodel switching.

## Goals

- Make inventory a server-owned multiplayer feature.
- Show inventory in the gameplay HUD, not inside the settings menu.
- Let `V` toggle a `4x4` inventory grid overlay.
- Let players click an occupied slot to equip that tool.
- Show item name and tooltip stats/details on hover.
- Keep hands and held item visually separated in first-person view.

## Non-Goals

- full loot, pickup, or drop gameplay
- stack splitting or drag-and-drop inventory management
- remote third-person held-item attachments in this slice
- item stat modifiers changing combat math in this slice

## Authoritative Model

### Ownership

- The server owns inventory contents and equipped state.
- Inventory is keyed by player display-name identity, which is already unique among active sessions.
- Each player has:
  - backpack slots `0..15`
  - one dedicated equipped slot

### Items

- Items are runtime instances with persistent `item_instance_id`.
- Item definitions are resolved from `item_kind`.
- Tool presentation uses `visual_id`, which maps to held-item viewmodel definitions on the client.

### Starter Loadout

On first join of a new named player, the server seeds a small starter loadout into inventory and marks one tool equipped by default.

## Replication Contract

### Join

`JoinAcceptedMessage` now carries the joining player's full inventory snapshot.

### Action

The client sends an explicit equip action with the clicked backpack slot index.

The server validates the slot, updates equipped state, persists it, and returns a fresh authoritative inventory snapshot in the action result.

## HUD Behavior

### Overlay

- `V` toggles the inventory overlay.
- Inventory opening blocks gameplay movement/look input and releases the cursor.
- `Esc` still owns the settings menu and closes inventory if settings are opened.

### Layout

- one standalone HUD window for the backpack grid
- one visible equipped-tool panel
- hover tooltip with item name, description, and detail lines
- empty slots remain visible so the layout is stable

## Held Item Visuals

- Hands remain rendered by the existing hands viewmodel slot.
- Equipped tool selection swaps the held-item viewmodel definition.
- The first-pass visuals may reuse the current held-item asset while still keeping per-tool `visual_id` seams.

## Persistence

- item ownership and `visual_id` persist through `item_instances`
- slot assignments persist through `player_inventory_slots`
- equipped state persists as the dedicated equipped-slot assignment

## Validation Rules

- only occupied backpack slots can be equipped
- slots outside `0..15` are rejected
- stale or replayed equip actions are rejected by existing action-sequence rules

## Test Expectations

- inventory snapshot round-trips through protocol JSON
- new players receive starter inventory
- equip actions change the authoritative equipped slot
- reconnect restores previously equipped tool
- runtime mode and cursor behavior reflect inventory overlay visibility

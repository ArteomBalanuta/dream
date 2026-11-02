using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;

// M4 (protocol v17). AimDirection null means the server's own view ray (facing). A value is a
// direction only, never an origin or a target: the origin stays the server's. The current camera
// sends null; click-to-move can send the eye-to-cursor direction without a protocol change.
public sealed record SkillUseIntent(string SkillKey, Vector3? AimDirection = null) : IPlayerActionPayload;

// The item is named like InventoryDestroyIntent, so a queued use cannot hit whatever moved there.
public sealed record ItemUseIntent(int SlotIndex, long ItemInstanceId) : IPlayerActionPayload;

// Key is a skill key, an item kind, or empty for QuickbarEntryKind.None.
public sealed record QuickbarBindIntent(int Slot, QuickbarEntryKind Kind, string Key) : IPlayerActionPayload;

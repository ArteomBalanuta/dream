using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Client.Scenes;

// One synthetic pointer state for one frame of the capture script.
internal readonly record struct ScriptedPointerFrame(Vector2 Position, bool LeftDown, bool RightDown);

internal enum HudCaptureVerb : byte
{
    Play,
    Inventory,
    Settings,
    Admin,
    Hover,
    Drag,
    Dragging,
    Burst,
    Release,
    RightClick,
    Menu,
    Confirm,
    Escape,
    Key,
    Close,
    Wait,
    Size,
    Snap,
    Log,
    Fight,
    WalkTo
}

// A parsed capture step: the verb and up to two numbers (slots, a menu row, frames).
internal readonly record struct HudCaptureStepSpec(HudCaptureVerb Verb, int A, int B, int C = 0)
{
    public const int ConfirmDestroy = 0;
    public const int ConfirmCancel = 1;
    public const int MaxBurstMoves = 100;

    // Burst steps pack the second slot and the move count into B.
    public int BurstSlotB => B & 0xFFFF;

    public int BurstMoves => B >> 16;

    // Slot names for the script: bag anchors are 0..63 (row * 8 + col), the twelve wells 100..111, named
    // main, off, head, body, hands, feet, legs, neck, ring1, ring2, back and pet.
    public static bool TryParseSlot(string text, out int slot)
    {
        slot = text switch
        {
            "main" => EquipmentSlots.ToSlotIndex(EquipmentSlot.MainHand),
            "off" => EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand),
            "head" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Head),
            "body" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Body),
            "hands" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Hands),
            "feet" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Feet),
            "legs" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Legs),
            "neck" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Neck),
            "ring1" => EquipmentSlots.ToSlotIndex(EquipmentSlot.RingLeft),
            "ring2" => EquipmentSlots.ToSlotIndex(EquipmentSlot.RingRight),
            "back" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Back),
            "pet" => EquipmentSlots.ToSlotIndex(EquipmentSlot.Companion),
            _ => int.TryParse(text, out int n) ? n : -1
        };

        return (slot >= 0 && slot < BagGrid.CellCount) || (slot >= EquipmentSlots.FirstSlotIndex && slot <= EquipmentSlots.LastSlotIndex);
    }

    // The step grammar of DREAM_HUD_CAPTURE_SCRIPT, one comma-separated token. False for anything
    // unknown, so a typo drops the step instead of doing something else.
    public static bool TryParse(string token, out HudCaptureStepSpec spec)
    {
        spec = default;
        string[] parts = token.Split(':', StringSplitOptions.TrimEntries);
        switch (parts[0])
        {
            case "play" when parts.Length == 1:
                spec = new(HudCaptureVerb.Play, 0, 0);
                return true;
            case "inventory" when parts.Length == 1:
                spec = new(HudCaptureVerb.Inventory, 0, 0);
                return true;
            case "settings" when parts.Length == 1:
                spec = new(HudCaptureVerb.Settings, 0, 0);
                return true;
            case "admin" when parts.Length == 1:
                spec = new(HudCaptureVerb.Admin, 0, 0);
                return true;
            case "esc" when parts.Length == 1:
                spec = new(HudCaptureVerb.Escape, 0, 0);
                return true;
            case "v" when parts.Length == 1:
                spec = new(HudCaptureVerb.Key, 0, 0);
                return true;
            case "close" when parts.Length == 1:
                spec = new(HudCaptureVerb.Close, 0, 0);
                return true;
            case "release" when parts.Length == 1:
                spec = new(HudCaptureVerb.Release, 0, 0);
                return true;
            case "snap" when parts.Length == 1:
                spec = new(HudCaptureVerb.Snap, 0, 0);
                return true;
            case "log" when parts.Length == 1:
                spec = new(HudCaptureVerb.Log, 0, 0);
                return true;
            case "wait" when parts.Length == 2 && int.TryParse(parts[1], out int frames) && frames is > 0 and <= 2000:
                spec = new(HudCaptureVerb.Wait, frames, 0);
                return true;
            case "fight" when parts.Length == 2 && int.TryParse(parts[1], out int fightFrames) && fightFrames is > 0 and <= 20000:
                spec = new(HudCaptureVerb.Fight, fightFrames, 0);
                return true;
            case "walkto" when parts.Length == 4 && int.TryParse(parts[1], out int walkX) && int.TryParse(parts[2], out int walkZ)
                && int.TryParse(parts[3], out int walkFrames) && walkFrames is > 0 and <= 20000:
                // x and z are the target cell, the frame cap rides in C.
                spec = new(HudCaptureVerb.WalkTo, walkX, walkZ, walkFrames);
                return true;
            case "size" when parts.Length == 3 && int.TryParse(parts[1], out int width) && int.TryParse(parts[2], out int height)
                && width is >= 640 and <= 4096 && height is >= 480 and <= 2304:
                spec = new(HudCaptureVerb.Size, width, height);
                return true;
            case "hover" when parts.Length == 2 && TryParseSlot(parts[1], out int hoverSlot):
                spec = new(HudCaptureVerb.Hover, hoverSlot, 0);
                return true;
            case "rightclick" when parts.Length == 2 && TryParseSlot(parts[1], out int clickSlot):
                spec = new(HudCaptureVerb.RightClick, clickSlot, 0);
                return true;
            case "drag" or "dragging" when parts.Length == 3 && TryParseSlot(parts[1], out int from) && TryParseSlot(parts[2], out int to):
                spec = new(parts[0] == "drag" ? HudCaptureVerb.Drag : HudCaptureVerb.Dragging, from, to);
                return true;
            case "burst" when parts.Length == 4 && TryParseSlot(parts[1], out int burstA) && TryParseSlot(parts[2], out int burstB)
                && int.TryParse(parts[3], out int moves) && moves is > 0 and <= MaxBurstMoves:
                // B holds the second slot; the move count rides in the high bits.
                spec = new(HudCaptureVerb.Burst, burstA, burstB | (moves << 16));
                return true;
            case "menu" when parts.Length == 2 && int.TryParse(parts[1], out int row) && row is >= 0 and < 8:
                spec = new(HudCaptureVerb.Menu, row, 0);
                return true;
            case "confirm" when parts.Length == 2 && parts[1] is "destroy" or "cancel":
                spec = new(HudCaptureVerb.Confirm, parts[1] == "destroy" ? ConfirmDestroy : ConfirmCancel, 0);
                return true;
            default:
                return false;
        }
    }
}

// Turns a pointer step into the per-frame pointer states that go through the real UiInputRouter. Pure:
// the layout (and the menu or confirm rect the last frame drew) is all it needs.
internal static class HudCapturePointerPlan
{
    // Frames the pointer rests on a widget before pressing, so the router has it as the hot id.
    public const int HoverLeadFrames = 2;

    // Steps from the press to the drop; the first already exceeds the router's drag threshold.
    public const int DragSteps = 4;

    public const int HoldFrames = 2;

    // Where the pointer rests to pick an item up or hover it: the middle of a bag anchor's cell (inside the
    // item, whatever its footprint) or of a well.
    public static bool TryCentre(in UiLayout layout, int slot, out Vector2 centre)
    {
        UiRect r;
        if (slot >= 0 && slot < BagGrid.CellCount)
        {
            r = InventoryWindow.SlotRect(layout, layout.Inventory, slot);
        }
        else if (EquipmentSlots.TryFromSlotIndex(slot, out EquipmentSlot equipment))
        {
            r = InventoryWindow.WellRect(layout, layout.Inventory, (int)equipment);
        }
        else
        {
            centre = default;
            return false;
        }

        centre = new Vector2(r.X + (r.W * 0.5f), r.Y + (r.H * 0.5f));
        return true;
    }

    // Where the pointer goes to drop an item with this footprint on a slot. A well is its middle. For a
    // bag anchor it is the middle of the footprint placed there, because the drop is centred on the
    // pointer (decision D12); an anchor where the footprint does not fit lands on the nearest one that
    // does, as a hand would get, and the point stays inside the board.
    public static bool TryDropPoint(in UiLayout layout, int slot, ItemFootprint footprint, out Vector2 point)
    {
        if (!(slot >= 0 && slot < BagGrid.CellCount))
        {
            return TryCentre(in layout, slot, out point);
        }

        UiRect cell = InventoryWindow.SlotRect(layout, layout.Inventory, slot);
        UiRect board = InventoryWindow.BagRect(layout, layout.Inventory);
        float x = cell.X + (footprint.Cols * 0.5f * cell.W);
        float y = cell.Y + (footprint.Rows * 0.5f * cell.H);
        point = new Vector2(
            Math.Clamp(x, board.X, board.Right - 0.5f),
            Math.Clamp(y, board.Y, board.Bottom - 0.5f));
        return true;
    }

    public static Vector2 CloseButtonCentre(in UiLayout layout)
    {
        UiRect r = layout.FromUnits(layout.Inventory, 19f, 395f, 24f, 24f);
        return new Vector2(r.X + (r.W * 0.5f), r.Y + (r.H * 0.5f));
    }

    // Where the pointer rests when nothing aims it: left of the windows, over the world.
    public static Vector2 Parked(in UiLayout layout) => new(layout.ViewportWidth * 0.3f, layout.ViewportHeight * 0.4f);

    private static void Rest(List<ScriptedPointerFrame> frames, Vector2 at, int count, bool left = false, bool right = false)
    {
        for (int n = 0; n < count; n++)
        {
            frames.Add(new ScriptedPointerFrame(at, left, right));
        }
    }

    // A left click on a point: rest, press, release.
    public static void Click(List<ScriptedPointerFrame> frames, Vector2 at)
    {
        Rest(frames, at, HoverLeadFrames);
        Rest(frames, at, 1, left: true);
        Rest(frames, at, 1);
    }

    public static void RightClick(List<ScriptedPointerFrame> frames, Vector2 at)
    {
        Rest(frames, at, HoverLeadFrames);
        Rest(frames, at, 1, right: true);
        Rest(frames, at, 1);
    }

    // Press on from, carry to to in DragSteps moves, hold there, then release unless the button is
    // to stay down (the "dragging" capture, which shows the ghost and the drop highlight).
    public static void Drag(
        List<ScriptedPointerFrame> frames, Vector2 from, Vector2 to, bool release,
        int lead = HoverLeadFrames, int steps = DragSteps, int hold = HoldFrames)
    {
        Rest(frames, from, lead);
        Rest(frames, from, 1, left: true);
        for (int step = 1; step <= steps; step++)
        {
            frames.Add(new ScriptedPointerFrame(Vector2.Lerp(from, to, step / (float)steps), true, false));
        }

        Rest(frames, to, hold, left: true);
        if (release)
        {
            Rest(frames, to, 1);
        }
    }

    // Builds the frames of a pointer step. last is where the pointer was left (a release lets go
    // there). view, when given, tells how big the dragged item is so a drop point can centre its
    // footprint on the target anchor; without it every item counts as 1x1. False when the step needs a
    // rect that is not on screen.
    public static bool TryBuild(
        HudCaptureStepSpec spec,
        in UiLayout layout,
        Vector2 last,
        UiRect? menuRow,
        UiRect? confirmButton,
        List<ScriptedPointerFrame> frames,
        HudView? view = null)
    {
        frames.Clear();
        ItemFootprint footprint = ItemFootprint.One;
        if (view is not null && InventoryWindow.TryDragFootprint(view, spec.A, out ItemFootprint dragged))
        {
            footprint = dragged;
        }

        switch (spec.Verb)
        {
            case HudCaptureVerb.Hover:
                if (!TryCentre(in layout, spec.A, out Vector2 over))
                {
                    return false;
                }

                Rest(frames, over, HoverLeadFrames + HoldFrames);
                return true;
            case HudCaptureVerb.RightClick:
                if (!TryCentre(in layout, spec.A, out Vector2 target))
                {
                    return false;
                }

                RightClick(frames, target);
                return true;
            case HudCaptureVerb.Drag:
            case HudCaptureVerb.Dragging:
                if (!TryCentre(in layout, spec.A, out Vector2 from) || !TryDropPoint(in layout, spec.B, footprint, out Vector2 to))
                {
                    return false;
                }

                Drag(frames, from, to, release: spec.Verb == HudCaptureVerb.Drag);
                return true;
            case HudCaptureVerb.Burst:
                if (!TryCentre(in layout, spec.A, out Vector2 burstFrom)
                    || !TryCentre(in layout, spec.BurstSlotB, out Vector2 burstFromB)
                    || !TryDropPoint(in layout, spec.BurstSlotB, footprint, out Vector2 burstTo)
                    || !TryDropPoint(in layout, spec.A, footprint, out Vector2 burstBack))
                {
                    return false;
                }

                // The same item carried back and forth, as fast as the router allows, with no wait
                // for an answer in between: enough actions to meet the server's action budget.
                for (int move = 0; move < spec.BurstMoves; move++)
                {
                    bool forward = move % 2 == 0;
                    Drag(frames, forward ? burstFrom : burstFromB, forward ? burstTo : burstBack, release: true, lead: 1, steps: 2, hold: 1);
                }

                return true;
            case HudCaptureVerb.Release:
                Rest(frames, last, 1);
                return true;
            case HudCaptureVerb.Menu:
                if (menuRow is not UiRect row)
                {
                    return false;
                }

                Click(frames, new Vector2(row.X + (row.W * 0.5f), row.Y + (row.H * 0.5f)));
                return true;
            case HudCaptureVerb.Confirm:
                if (confirmButton is not UiRect button)
                {
                    return false;
                }

                Click(frames, new Vector2(button.X + (button.W * 0.5f), button.Y + (button.H * 0.5f)));
                return true;
            case HudCaptureVerb.Close:
                Click(frames, CloseButtonCentre(in layout));
                return true;
            default:
                return false;
        }
    }
}

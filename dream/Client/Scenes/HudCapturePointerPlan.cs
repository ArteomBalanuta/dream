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
    Fight
}

// A parsed capture step: the verb and up to two numbers (slots, a menu row, frames).
internal readonly record struct HudCaptureStepSpec(HudCaptureVerb Verb, int A, int B)
{
    public const int ConfirmDestroy = 0;
    public const int ConfirmCancel = 1;
    public const int MaxBurstMoves = 100;

    // Burst steps pack the second slot and the move count into B.
    public int BurstSlotB => B & 0xFFFF;

    public int BurstMoves => B >> 16;

    // Slot names for the script: bag slots are 0..15, the four wells 100..103.
    public static bool TryParseSlot(string text, out int slot)
    {
        slot = text switch
        {
            "main" => EquipmentSlots.FirstSlotIndex,
            "off" => EquipmentSlots.FirstSlotIndex + 1,
            "head" => EquipmentSlots.FirstSlotIndex + 2,
            "body" => EquipmentSlots.FirstSlotIndex + 3,
            _ => int.TryParse(text, out int n) ? n : -1
        };

        return (slot >= 0 && slot < 16) || (slot >= EquipmentSlots.FirstSlotIndex && slot < EquipmentSlots.FirstSlotIndex + 4);
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

    public static bool TryCentre(in UiLayout layout, int slot, out Vector2 centre)
    {
        UiRect r;
        if (slot >= 0 && slot < 16)
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
    // there). False when the step needs a rect that is not on screen.
    public static bool TryBuild(
        HudCaptureStepSpec spec,
        in UiLayout layout,
        Vector2 last,
        UiRect? menuRow,
        UiRect? confirmButton,
        List<ScriptedPointerFrame> frames)
    {
        frames.Clear();
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
                if (!TryCentre(in layout, spec.A, out Vector2 from) || !TryCentre(in layout, spec.B, out Vector2 to))
                {
                    return false;
                }

                Drag(frames, from, to, release: spec.Verb == HudCaptureVerb.Drag);
                return true;
            case HudCaptureVerb.Burst:
                if (!TryCentre(in layout, spec.A, out Vector2 burstFrom) || !TryCentre(in layout, spec.BurstSlotB, out Vector2 burstTo))
                {
                    return false;
                }

                // The same item carried back and forth, as fast as the router allows, with no wait
                // for an answer in between: enough actions to meet the server's action budget.
                for (int move = 0; move < spec.BurstMoves; move++)
                {
                    bool forward = move % 2 == 0;
                    Drag(frames, forward ? burstFrom : burstTo, forward ? burstTo : burstFrom, release: true, lead: 1, steps: 2, hold: 1);
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

using Dream.Application;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Rendering.Sky;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using ImGuiNET;

namespace Dream.Rendering.Core;

public partial class Render
{
    private HudInteractionRequest DrawHudInterface(
        GameSettings settings,
        HudStatusSnapshot status,
        string? rejectionMessage,
        string? connectionLabel,
        IReadOnlyList<PlayerRosterEntry> playerRoster,
        FramePerformanceSnapshot performance,
        WorldTransferHudState worldTransfer,
        PlayerInventorySnapshot? inventory)
    {
        float? fieldOfViewDegrees = null;
        float? mouseSensitivity = null;
        CameraViewMode? cameraViewMode = null;
        float? noclipSpeed = null;
        Vector2i? requestedResolution = null;
        int? stagedFloorSize = null;
        int? requestedFloorSize = null;
        bool? showCollisionGeometry = null;
        bool? showPerformanceCounters = null;
        int? strength = null;
        float? attackSpeed = null;
        int? vitality = null;
        FloatingCombatTextTuning? floatingCombatText = null;
        int? equipInventorySlotIndex = null;
        bool overlayVisible = settings.SettingsVisible || settings.InventoryVisible;

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 10));
        ImGui.Begin("HUD",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoMove |
            (overlayVisible ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoInputs) |
            ImGuiWindowFlags.NoFocusOnAppearing );

        Span<byte> hudText = stackalloc byte[HudText.StackBufferBytes];
        DrawWorldTransferIndicator(worldTransfer, hudText);
        ImGui.TextUnformatted(status.HealthLabel);
        ImGui.TextUnformatted(status.ModeLabel);
        ImGui.TextUnformatted(status.DamageLabel);
        ImGui.TextUnformatted(status.LevelLabel);
        ImGui.TextUnformatted(status.ExperiencePercentLabel);
        ImGui.TextUnformatted(status.ExperienceProgressLabel);
        if (!string.IsNullOrWhiteSpace(connectionLabel))
        {
            HudText.Text(hudText, $"Server: {connectionLabel}");
        }
        if (playerRoster.Count > 0)
        {
            HudText.Text(hudText, $"Players: {playerRoster.Count}");
        }
        if (!string.IsNullOrWhiteSpace(rejectionMessage))
        {
            // TextColored is a printf format, so the colour is pushed around unformatted text.
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.45f, 0.35f, 1f));
            ImGui.TextUnformatted(rejectionMessage);
            ImGui.PopStyleColor();
        }
        HudText.Text(hudText, $"Resolution: {settings.WindowWidth} x {settings.WindowHeight}");
        if (settings.ShowPerformanceCounters && performance.HasCounters)
        {
            for (int lineIndex = 0; lineIndex < FramePerformanceSnapshot.HudLineCount; lineIndex++)
            {
                if (performance.TryFormatHudLine(lineIndex, hudText, out int lineBytes))
                {
                    HudText.TextUnformatted(hudText[..lineBytes]);
                }
                else
                {
                    ImGui.TextUnformatted(performance.FormatHudLine(lineIndex));
                }
            }
        }
        ImGui.Text("Press Esc for settings");
        ImGui.Text("Press V for inventory");
        ImGui.End();

        if (settings.SettingsVisible)
        {
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(20, 70), ImGuiCond.FirstUseEver);
            ImGui.Begin("Settings", ImGuiWindowFlags.AlwaysAutoResize);

            if (!string.IsNullOrWhiteSpace(connectionLabel))
            {
                HudText.Text(hudText, $"Connected Server: {connectionLabel}");
            }

            if (playerRoster.Count > 0 && ImGui.CollapsingHeader("Connected Players", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (PlayerRosterEntry player in playerRoster)
                {
                    HudText.Text(hudText, $"{player.DisplayName} [{player.SessionId}]");
                }
            }

            float fieldOfView = settings.FieldOfViewDegrees;
            if (ImGui.SliderFloat("Field Of View", ref fieldOfView, 50f, 100f))
            {
                fieldOfViewDegrees = fieldOfView;
            }

            float sensitivity = settings.MouseSensitivity;
            if (ImGui.SliderFloat("Mouse Sensitivity", ref sensitivity, 0.5f, 8f))
            {
                mouseSensitivity = sensitivity;
            }

            bool thirdPersonEnabled = settings.CameraViewMode == CameraViewMode.ThirdPerson;
            if (ImGui.Checkbox("Third Person View", ref thirdPersonEnabled))
            {
                cameraViewMode = thirdPersonEnabled
                    ? CameraViewMode.ThirdPerson
                    : CameraViewMode.FirstPerson;
            }

            float currentNoclipSpeed = settings.NoclipSpeed;
            if (ImGui.SliderFloat("Fly Speed", ref currentNoclipSpeed, GameSettings.MinNoclipSpeed, GameSettings.MaxNoclipSpeed))
            {
                noclipSpeed = currentNoclipSpeed;
            }

            int currentStrength = status.Strength;
            if (ImGui.SliderInt("Strength", ref currentStrength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength))
            {
                strength = currentStrength;
            }

            float currentAttackSpeed = status.AttackSpeed;
            if (ImGui.SliderFloat("Attack Speed", ref currentAttackSpeed, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed))
            {
                attackSpeed = currentAttackSpeed;
            }

            int currentVitality = status.Vitality;
            if (ImGui.SliderInt("Vitality", ref currentVitality, PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality))
            {
                vitality = currentVitality;
            }

            FloatingCombatTextTuning textTuning = settings.FloatingCombatText;
            float damageTextSize = textTuning.SizeMultiplier;
            float damageTextSpeed = textTuning.MotionSpeedMultiplier;
            float criticalDepth = textTuning.CriticalDepthMultiplier;
            if (ImGui.CollapsingHeader("Floating Damage Text", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool changed = false;
                changed |= ImGui.SliderFloat(
                    "Damage Text Size",
                    ref damageTextSize,
                    FloatingCombatTextTuning.MinSizeMultiplier,
                    FloatingCombatTextTuning.MaxSizeMultiplier);
                changed |= ImGui.SliderFloat(
                    "Damage Text Speed",
                    ref damageTextSpeed,
                    FloatingCombatTextTuning.MinMotionSpeedMultiplier,
                    FloatingCombatTextTuning.MaxMotionSpeedMultiplier);
                changed |= ImGui.SliderFloat(
                    "Crit Depth",
                    ref criticalDepth,
                    FloatingCombatTextTuning.MinCriticalDepthMultiplier,
                    FloatingCombatTextTuning.MaxCriticalDepthMultiplier);

                if (changed)
                {
                    floatingCombatText = new FloatingCombatTextTuning(
                        damageTextSize,
                        damageTextSpeed,
                        criticalDepth);
                }
            }

            DrawViewModelControls(settings);
            DrawTimeOfDayControls(TimeOfDayOverride);

            bool showCollision = settings.ShowCollisionGeometry;
            if (ImGui.Checkbox("Show Collision Geometry", ref showCollision))
            {
                showCollisionGeometry = showCollision;
            }

            bool showPerformance = settings.ShowPerformanceCounters;
            if (ImGui.Checkbox("Show Performance Counters", ref showPerformance))
            {
                showPerformanceCounters = showPerformance;
            }

            int floorSize = settings.PendingFloorSize;
            if (ImGui.SliderInt("Floor Size", ref floorSize, GameSettings.MinFloorSize, GameSettings.MaxFloorSize))
            {
                stagedFloorSize = floorSize;
            }
            if (ImGui.Button("Apply Floor Size"))
            {
                requestedFloorSize = floorSize;
                stagedFloorSize = floorSize;
            }

            ImGui.Text("Resolution Presets");
            if (ImGui.Button("1280 x 720"))
            {
                requestedResolution = new Vector2i(1280, 720);
            }
            ImGui.SameLine();
            if (ImGui.Button("1600 x 900"))
            {
                requestedResolution = new Vector2i(1600, 900);
            }
            ImGui.SameLine();
            if (ImGui.Button("1920 x 1080"))
            {
                requestedResolution = new Vector2i(1920, 1080);
            }

            HudText.Text(hudText, $"Current: {settings.WindowWidth} x {settings.WindowHeight}");
            ImGui.End();
        }

        if (settings.InventoryVisible)
        {
            DrawInventoryOverlay(inventory, ref equipInventorySlotIndex);
        }

        return new HudInteractionRequest(
            fieldOfViewDegrees,
            mouseSensitivity,
            cameraViewMode,
            noclipSpeed,
            requestedResolution,
            stagedFloorSize,
            requestedFloorSize,
            showCollisionGeometry,
            showPerformanceCounters,
            strength,
            attackSpeed,
            vitality,
            floatingCombatText,
            equipInventorySlotIndex);
    }

    // The "receiving world" line at the top of the HUD panel while a world transfer has run past
    // WorldTransferHudState.ShowThreshold. It is plain text in the non-interactive HUD window, so
    // it blocks nothing; it formats into the caller's stack buffer, so it allocates nothing.
    private static void DrawWorldTransferIndicator(WorldTransferHudState worldTransfer, Span<byte> hudText)
    {
        if (!worldTransfer.IsVisible)
        {
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.82f, 0.3f, 1f));
        if (worldTransfer.TryFormat(hudText, out int lineBytes))
        {
            HudText.TextUnformatted(hudText[..lineBytes]);
        }
        else
        {
            ImGui.TextUnformatted(worldTransfer.Format());
        }

        ImGui.TextUnformatted(WorldTransferHudState.DetailLine);
        ImGui.PopStyleColor();
    }

    private static void DrawInventoryOverlay(
        PlayerInventorySnapshot? inventory,
        ref int? equipInventorySlotIndex)
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(20f, 420f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Inventory", ImGuiWindowFlags.AlwaysAutoResize);

        if (inventory is not PlayerInventorySnapshot snapshot)
        {
            ImGui.TextDisabled("Waiting for authoritative inventory...");
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted($"Inventory: {snapshot.PlayerName}");
        ImGui.SeparatorText("Equipped Tool");
        DrawInventorySlotButton(snapshot.EquippedSlot, isEquippedPanel: true, ref equipInventorySlotIndex);

        ImGui.SeparatorText("Backpack");
        for (int row = 0; row < snapshot.Rows; row++)
        {
            for (int column = 0; column < snapshot.Columns; column++)
            {
                int slotIndex = (row * snapshot.Columns) + column;
                InventorySlotSnapshot slot = FindInventorySlot(snapshot.Slots, slotIndex);
                DrawInventorySlotButton(slot, isEquippedPanel: false, ref equipInventorySlotIndex);
                if (column < snapshot.Columns - 1)
                {
                    ImGui.SameLine();
                }
            }
        }

        ImGui.End();
    }

    private static InventorySlotSnapshot FindInventorySlot(IReadOnlyList<InventorySlotSnapshot> slots, int slotIndex)
    {
        for (int index = 0; index < slots.Count; index++)
        {
            if (slots[index].SlotIndex == slotIndex)
            {
                return slots[index];
            }
        }

        return default;
    }

    private static void DrawInventorySlotButton(
        InventorySlotSnapshot? slotValue,
        bool isEquippedPanel,
        ref int? equipInventorySlotIndex)
    {
        InventorySlotSnapshot slot = slotValue ?? new InventorySlotSnapshot(-1, Item: null);
        bool occupied = slot.Item is InventoryItemSnapshot;
        InventoryItemSnapshot item = slot.Item.GetValueOrDefault();
        string label = occupied
            ? item.DisplayName
            : isEquippedPanel
                ? "Nothing Equipped"
                : $"Empty##slot-{slot.SlotIndex}";

        if (slot.IsEquipped)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.20f, 0.42f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.26f, 0.52f, 0.28f, 1f));
        }

        if (ImGui.Button($"{label}##inventory-{slot.SlotIndex}", isEquippedPanel
            ? new System.Numerics.Vector2(220f, 48f)
            : new System.Numerics.Vector2(120f, 60f)))
        {
            if (occupied && slot.SlotIndex >= 0 && slot.SlotIndex < InventoryConstants.BackpackSlotCount)
            {
                equipInventorySlotIndex = slot.SlotIndex;
            }
        }

        if (ImGui.IsItemHovered() && occupied)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(item.DisplayName);
            ImGui.Separator();
            // TextWrapped is a printf format; wrapping unformatted text keeps a "%" literal.
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(item.Description);
            ImGui.PopTextWrapPos();
            foreach (string detailLine in item.DetailLines)
            {
                ImGui.TextUnformatted(detailLine);
            }

            if (item.StackCount > 1)
            {
                ImGui.TextUnformatted($"Count: {item.StackCount}");
            }

            ImGui.EndTooltip();
        }

        if (slot.IsEquipped)
        {
            ImGui.PopStyleColor(2);
        }
    }

    // Client-local and debug only: it changes what this client renders and nothing else. Collapsed
    // by default so it stays out of the way.
    private static void DrawTimeOfDayControls(TimeOfDayOverride timeOfDay)
    {
        if (!ImGui.CollapsingHeader("Time Of Day (debug, this client only)"))
        {
            return;
        }

        bool enabled = timeOfDay.Enabled;
        if (ImGui.Checkbox("Override Time Of Day", ref enabled))
        {
            timeOfDay.Enabled = enabled;
        }

        float hours = timeOfDay.Hours;
        if (ImGui.SliderFloat("Hour", ref hours, 0f, TimeOfDayOverride.HoursPerDay, "%.2f h"))
        {
            timeOfDay.Hours = hours;
        }
    }

    private static void DrawViewModelControls(GameSettings settings)
    {
        if (!ImGui.CollapsingHeader("Viewmodel", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        ViewModelRigSettings rig = settings.ViewModelRig;

        float viewModelFov = rig.FieldOfViewDegrees;
        if (ImGui.SliderFloat("Viewmodel FOV", ref viewModelFov, 35f, 100f))
        {
            rig.FieldOfViewDegrees = viewModelFov;
        }

        DrawVector3Sliders("Shared Offset", rig.PositionOffset, -1.5f, 1.5f, value => rig.PositionOffset = value);
        DrawVector3Sliders("Shared Rotation", rig.RotationOffsetDegrees, -45f, 45f, value => rig.RotationOffsetDegrees = value);

        ImGui.SeparatorText("Body Anchor");

        float spread = rig.BodyAnchor.HorizontalSpread;
        if (ImGui.SliderFloat("Body Anchor Spread", ref spread, 0f, 1.25f))
        {
            rig.BodyAnchor.HorizontalSpread = spread;
        }

        float drop = rig.BodyAnchor.VerticalDrop;
        if (ImGui.SliderFloat("Body Anchor Drop", ref drop, 0f, 1f))
        {
            rig.BodyAnchor.VerticalDrop = drop;
        }

        float forwardPull = rig.BodyAnchor.ForwardPull;
        if (ImGui.SliderFloat("Body Anchor Forward Pull", ref forwardPull, 0f, 1f))
        {
            rig.BodyAnchor.ForwardPull = forwardPull;
        }

        DrawSlotTuningControls("Hands", rig.Hands);
        DrawSlotTuningControls("Held Item", rig.HeldItem);

        if (ImGui.Button("Reset Viewmodel Tuning"))
        {
            settings.ResetViewModelRig();
        }
    }

    private static void DrawSlotTuningControls(string label, ViewModelSlotTuning tuning)
    {
        ImGui.PushID(label);
        if (ImGui.TreeNode(label))
        {
            DrawVector3Sliders("Offset", tuning.PositionOffset, -1.5f, 1.5f, value => tuning.PositionOffset = value);
            DrawVector3Sliders("Rotation", tuning.RotationOffsetDegrees, -45f, 45f, value => tuning.RotationOffsetDegrees = value);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void DrawVector3Sliders(string label, Vector3 currentValue, float min, float max, Action<Vector3> apply)
    {
        float x = currentValue.X;
        float y = currentValue.Y;
        float z = currentValue.Z;
        bool changed = false;

        ImGui.PushID(label);
        changed |= ImGui.SliderFloat("X", ref x, min, max);
        changed |= ImGui.SliderFloat("Y", ref y, min, max);
        changed |= ImGui.SliderFloat("Z", ref z, min, max);
        ImGui.PopID();

        if (changed)
        {
            apply(new Vector3(x, y, z));
        }
    }
}

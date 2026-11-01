using Dream.Application;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Rendering.Sky;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;
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
        BuildMaterialChoice? buildMaterial = null;
        InventoryHudAction? inventoryAction = null;
        float? uiScale = null;
        bool uiScaleCommitted = false;
        bool overlayVisible = settings.SettingsVisible || settings.InventoryVisible;
        // The classic surface draws the status, inventory, loot lines and death text itself; ImGui
        // keeps only Settings and the Build panel then.
        bool imGuiSurface = !_classicSurfaceActive;
        // The developer controls show only where the admin setting is available (admin_hud or
        // DREAM_ADMIN_HUD). Display only: nothing is sent, and the server enforces nothing here.
        bool devControls = settings.AdminHudAvailable;
        Span<byte> hudText = stackalloc byte[HudText.StackBufferBytes];

        if (imGuiSurface)
        {
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 10));
            ImGui.Begin("HUD",
                ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoMove |
                (overlayVisible ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoInputs) |
                ImGuiWindowFlags.NoFocusOnAppearing );

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
        }

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

            if (devControls)
            {
                float currentNoclipSpeed = settings.NoclipSpeed;
                if (ImGui.SliderFloat("Fly Speed", ref currentNoclipSpeed, GameSettings.MinNoclipSpeed, GameSettings.MaxNoclipSpeed))
                {
                    noclipSpeed = currentNoclipSpeed;
                }

                // The sliders edit the base stats; the effective ones (gear included) are only shown.
                int currentStrength = status.BaseStrength;
                if (ImGui.SliderInt("Strength", ref currentStrength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength))
                {
                    strength = currentStrength;
                }

                float currentAttackSpeed = status.BaseAttackSpeed;
                if (ImGui.SliderFloat("Attack Speed", ref currentAttackSpeed, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MaxAttackSpeed))
                {
                    attackSpeed = currentAttackSpeed;
                }

                int currentVitality = status.BaseVitality;
                if (ImGui.SliderInt("Vitality", ref currentVitality, PlayerCombatStats.MinVitality, PlayerCombatStats.MaxVitality))
                {
                    vitality = currentVitality;
                }
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

            // HUD scale: 0 is auto (fit the window). Shown always; the classic HUD reads it.
            // A configured value above what fits the window is not in use, so the slider shows the scale in use.
            float uiScaleValue = settings.UiScaleSetting > 0f ? settings.ResolvedUiScale : 0f;
            if (ImGui.SliderFloat("UI Scale (0 = auto)", ref uiScaleValue, 0f, UiScaleSliderMax, "%.2f"))
            {
                uiScale = uiScaleValue;
            }

            // The scale applies while dragging; the config file is written once, on release.
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                uiScale = uiScaleValue;
                uiScaleCommitted = true;
            }

            if (devControls)
            {
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
                if (ImGui.SliderInt("New local world size", ref floorSize, GameSettings.MinFloorSize, GameSettings.MaxFloorSize))
                {
                    stagedFloorSize = floorSize;
                }
                if (ImGui.Button("Set new world size"))
                {
                    requestedFloorSize = floorSize;
                    stagedFloorSize = floorSize;
                }
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

        if (imGuiSurface && settings.InventoryVisible)
        {
            DrawInventoryOverlay(inventory, status, ref equipInventorySlotIndex, ref inventoryAction);
        }

        if (status.InteractionMode == PlayerInteractionMode.Building)
        {
            DrawBuildMaterialPanel(BuildMaterialSelection, overlayVisible, ref buildMaterial);
        }

        if (imGuiSurface)
        {
            DrawLootFeed(settings);
        }

        if (imGuiSurface && status.IsDead)
        {
            DrawDeathOverlay(settings, status);
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
            equipInventorySlotIndex,
            buildMaterial,
            inventoryAction,
            UiScale: uiScale,
            UiScaleCommitted: uiScaleCommitted);
    }

    private const float UiScaleSliderMax = 4f;

    // The "Build" window, shown in Building mode: Auto (the supporting cell's material) or one of
    // BuildMaterialCatalog.Placeable. It takes clicks only while an overlay has freed the mouse
    // (Esc or V), like the HUD panel; otherwise it just shows the current pick.
    private static void DrawBuildMaterialPanel(
        GeometryMaterialId? selection,
        bool interactive,
        ref BuildMaterialChoice? buildMaterial)
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10f, 300f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Build",
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            (interactive ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoInputs));

        DrawBuildMaterialButton(null, selection, ref buildMaterial);
        IReadOnlyList<GeometryMaterialId> placeable = BuildMaterialCatalog.Placeable;
        for (int i = 0; i < placeable.Count; i++)
        {
            ImGui.SameLine();
            DrawBuildMaterialButton(placeable[i], selection, ref buildMaterial);
        }

        if (!interactive)
        {
            ImGui.TextDisabled("Esc or V to choose");
        }

        ImGui.End();
    }

    private static void DrawBuildMaterialButton(
        GeometryMaterialId? material,
        GeometryMaterialId? selection,
        ref BuildMaterialChoice? buildMaterial)
    {
        bool selected = material == selection;
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.20f, 0.42f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.26f, 0.52f, 0.28f, 1f));
        }

        if (ImGui.Button(BuildMaterialCatalog.DisplayName(material), new System.Numerics.Vector2(64f, 32f)) && !selected)
        {
            buildMaterial = new BuildMaterialChoice(material);
        }

        if (selected)
        {
            ImGui.PopStyleColor(2);
        }
    }

    // The loot lines of the last inventory push (set by the play scene; null shows nothing). Four
    // lines at most, below the centre of the screen, in a window that takes no input.
    public LootFeedbackSystem? LootFeed { get; set; }

    private const ImGuiWindowFlags OverlayFlags =
        ImGuiWindowFlags.NoDecoration |
        ImGuiWindowFlags.NoInputs |
        ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.AlwaysAutoResize;

    private void DrawLootFeed(GameSettings settings)
    {
        LootFeedbackSystem? feed = LootFeed;
        if (feed is null || feed.Count == 0)
        {
            return;
        }

        ImGui.SetNextWindowPos(
            new System.Numerics.Vector2(settings.WindowWidth * 0.5f, settings.WindowHeight * 0.68f),
            ImGuiCond.Always,
            new System.Numerics.Vector2(0.5f, 0f));
        ImGui.Begin("LootFeed", OverlayFlags);
        // Item names come from the server: unformatted text only, so a '%' is shown as typed.
        for (int i = 0; i < feed.Count; i++)
        {
            ImGui.TextUnformatted(feed.GetLine(i));
        }

        ImGui.End();
    }

    // "You died" and the wait, centred over the world while the local player is dead.
    private void DrawDeathOverlay(GameSettings settings, HudStatusSnapshot status)
    {
        ImGui.SetNextWindowPos(
            new System.Numerics.Vector2(settings.WindowWidth * 0.5f, settings.WindowHeight * 0.4f),
            ImGuiCond.Always,
            new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.Begin("DeathOverlay", OverlayFlags);
        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.9f, 0.2f, 0.2f, 1f));
        ImGui.SetWindowFontScale(2.2f);
        ImGui.TextUnformatted(HudStatusSnapshot.DeathTitle);
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextUnformatted(status.RespawnLabel);
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();
        ImGui.End();
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

    private static readonly System.Numerics.Vector4 UncommonColor = new(0.35f, 0.90f, 0.40f, 1f);
    private static readonly System.Numerics.Vector4 RareColor = new(0.40f, 0.62f, 1.00f, 1f);
    private const string DestroyPopupId = "Destroy item?";

    // A destroy waits here for the confirm popup; the item's name is for the question only.
    private static InventoryHudAction _pendingDestroy;
    private static string _pendingDestroyName = string.Empty;
    private static bool _openDestroyPopup;

    private static void DrawInventoryOverlay(
        PlayerInventorySnapshot? inventory,
        HudStatusSnapshot status,
        ref int? equipInventorySlotIndex,
        ref InventoryHudAction? inventoryAction)
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
        DrawCharacterSection(snapshot, status, ref inventoryAction);

        DrawBackpackSection(snapshot, ref equipInventorySlotIndex, ref inventoryAction);

        if (_openDestroyPopup)
        {
            _openDestroyPopup = false;
            ImGui.OpenPopup(DestroyPopupId);
        }

        if (ImGui.BeginPopupModal(DestroyPopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Destroy {_pendingDestroyName}?");
            ImGui.TextUnformatted("This cannot be undone.");
            if (ImGui.Button("Destroy"))
            {
                inventoryAction = _pendingDestroy;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        ImGui.End();
    }

    // Stats (base + bonus = effective) and the four equipment slots. Every string is the server's
    // or built here and goes through TextUnformatted, so a "%" stays literal.
    private static void DrawCharacterSection(
        PlayerInventorySnapshot snapshot,
        HudStatusSnapshot status,
        ref InventoryHudAction? inventoryAction)
    {
        ImGui.SeparatorText("Character");
        foreach (CharacterStatRow row in CharacterPanelModel.BuildStatRows(status))
        {
            string bonus = row.BonusText.Length == 0 ? string.Empty : $" {row.BonusText}";
            string note = row.Note is null ? string.Empty : $"  ({row.Note})";
            ImGui.TextUnformatted($"{row.Label}: {row.BaseText}{bonus} = {row.EffectiveText}{note}");
        }

        ImGui.SeparatorText("Equipment");
        CharacterSlotRow[] slots = CharacterPanelModel.BuildSlotRows(snapshot);
        for (int i = 0; i < slots.Length; i++)
        {
            CharacterSlotRow row = slots[i];
            PushRarityColor(row.Color);
            ImGui.Button($"{row.SlotLabel}: {row.ItemLabel}##equipment-{row.SlotIndex}", new System.Numerics.Vector2(220f, 32f));
            ImGui.PopStyleColor();
            if (row.Item is InventoryItemSnapshot item)
            {
                DrawItemTooltip(item);
                if (ImGui.BeginPopupContextItem($"equipment-menu-{row.SlotIndex}"))
                {
                    foreach (CharacterMenuEntry entry in CharacterPanelModel.EquipmentMenu(row.Slot))
                    {
                        if (ImGui.MenuItem(entry.Label))
                        {
                            inventoryAction = entry.Action;
                        }
                    }

                    ImGui.EndPopup();
                }
            }
        }
    }

    private static void PushRarityColor(RarityColorKey key)
    {
        // Common keeps the default text colour: the pushed value is the style's own.
        System.Numerics.Vector4 color = key switch
        {
            RarityColorKey.Uncommon => UncommonColor,
            RarityColorKey.Rare => RareColor,
            _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text]
        };
        ImGui.PushStyleColor(ImGuiCol.Text, color);
    }

    // Buttons per row of the backpack flow.
    private const int BackpackFlowColumns = 4;

    // The bag is sparse: only the cells an item is anchored on appear in Slots, so the panel is a flow of
    // one button per item (row-major by anchor, however the snapshot orders them), then a line for the
    // items the server holds back because the board has no room for them.
    private static void DrawBackpackSection(
        PlayerInventorySnapshot snapshot,
        ref int? equipInventorySlotIndex,
        ref InventoryHudAction? inventoryAction)
    {
        ImGui.SeparatorText("Backpack");
        IReadOnlyList<InventorySlotSnapshot> slots = snapshot.Slots;
        int heldBack = 0;
        int shown = 0;
        for (int anchor = 0; anchor < BagGrid.CellCount; anchor++)
        {
            for (int index = 0; index < slots.Count; index++)
            {
                InventorySlotSnapshot slot = slots[index];
                if (slot.SlotIndex != anchor || slot.Item is not InventoryItemSnapshot)
                {
                    continue;
                }

                if (shown++ % BackpackFlowColumns != 0)
                {
                    ImGui.SameLine();
                }

                DrawInventorySlotButton(snapshot, slot, ref equipInventorySlotIndex, ref inventoryAction);
                break;
            }
        }

        for (int index = 0; index < slots.Count; index++)
        {
            int held = slots[index].SlotIndex - CharacterPanelModel.HeldBackFirstSlot;
            if ((uint)held < CharacterPanelModel.HeldBackSlots && slots[index].Item is InventoryItemSnapshot)
            {
                heldBack++;
            }
        }

        if (shown == 0)
        {
            ImGui.TextDisabled("Empty");
        }

        if (heldBack > 0)
        {
            ImGui.TextUnformatted(heldBack + CharacterPanelModel.HeldBackSuffix(heldBack));
        }
    }

    private static void DrawInventorySlotButton(
        PlayerInventorySnapshot snapshot,
        InventorySlotSnapshot slot,
        ref int? equipInventorySlotIndex,
        ref InventoryHudAction? inventoryAction)
    {
        InventoryItemSnapshot item = slot.Item.GetValueOrDefault();
        ItemFootprint footprint = BagGrid.FootprintOf(item.ItemKind);
        int row = slot.SlotIndex / BagGrid.Columns;
        int column = slot.SlotIndex % BagGrid.Columns;
        string label = $"{item.DisplayName}{(char)10}{footprint.Cols}x{footprint.Rows} at row {row + 1}, col {column + 1}##inventory-{slot.SlotIndex}";

        PushRarityColor(CharacterPanelModel.ColorKey(item.Rarity));
        if (ImGui.Button(label, new System.Numerics.Vector2(160f, 44f)))
        {
            equipInventorySlotIndex = slot.SlotIndex;
        }

        ImGui.PopStyleColor();

        DrawItemTooltip(item);
        DrawBackpackContextMenu(snapshot, slot.SlotIndex, item, ref inventoryAction);
    }

    private static void DrawBackpackContextMenu(
        PlayerInventorySnapshot snapshot,
        int slotIndex,
        InventoryItemSnapshot item,
        ref InventoryHudAction? inventoryAction)
    {
        if (!ImGui.BeginPopupContextItem($"inventory-menu-{slotIndex}"))
        {
            return;
        }

        foreach (CharacterMenuEntry entry in CharacterPanelModel.BackpackMenu(snapshot, slotIndex, item))
        {
            if (!ImGui.MenuItem(entry.Label))
            {
                continue;
            }

            if (entry.RequiresConfirm)
            {
                _pendingDestroy = entry.Action;
                _pendingDestroyName = item.DisplayName ?? string.Empty;
                _openDestroyPopup = true;
            }
            else
            {
                inventoryAction = entry.Action;
            }
        }

        if (ImGui.BeginMenu("Move To"))
        {
            foreach (InventoryHudAction target in CharacterPanelModel.MoveTargets(snapshot, slotIndex))
            {
                if (ImGui.MenuItem($"Row {(target.Target / BagGrid.Columns) + 1}, col {(target.Target % BagGrid.Columns) + 1}"))
                {
                    inventoryAction = target;
                }
            }

            ImGui.EndMenu();
        }

        ImGui.EndPopup();
    }

    private static void DrawItemTooltip(InventoryItemSnapshot item)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        PushRarityColor(CharacterPanelModel.ColorKey(item.Rarity));
        ImGui.TextUnformatted(item.DisplayName ?? string.Empty);
        ImGui.PopStyleColor();
        ImGui.Separator();
        // TextWrapped is a printf format; wrapping unformatted text keeps a "%" literal.
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(item.Description ?? string.Empty);
        ImGui.PopTextWrapPos();
        if (item.DetailLines is not null)
        {
            foreach (string detailLine in item.DetailLines)
            {
                ImGui.TextUnformatted(detailLine ?? string.Empty);
            }
        }

        foreach (string line in CharacterPanelModel.TooltipLines(item))
        {
            ImGui.TextUnformatted(line);
        }

        if (item.StackCount > 1)
        {
            ImGui.TextUnformatted($"Count: {item.StackCount}");
        }

        ImGui.EndTooltip();
    }

    // Client-local and debug only: it changes what this client renders and nothing else. Collapsed
    // by default so it stays out of the way.
    // The slider stops one minute short of 24 h: 24 h wraps to 0 (midnight), so dragging to the
    // right end would jump the sky back to the start of the day.
    private const float SliderMaxHours = TimeOfDayOverride.HoursPerDay - 1f / 60f;

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
        if (ImGui.SliderFloat("Hour", ref hours, 0f, SliderMaxHours, "%.2f h"))
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

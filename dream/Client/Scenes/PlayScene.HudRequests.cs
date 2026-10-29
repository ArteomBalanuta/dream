using OpenTK.Mathematics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    private void ApplyHudRequest(HudInteractionRequest request, Action<Vector2i> applyClientSize)
    {
        bool persistDesktopConfig = false;

        if (request.FieldOfViewDegrees.HasValue)
        {
            _settings.FieldOfViewDegrees = request.FieldOfViewDegrees.Value;
            persistDesktopConfig = true;
        }

        if (request.MouseSensitivity.HasValue)
        {
            _settings.MouseSensitivity = request.MouseSensitivity.Value;
            persistDesktopConfig = true;
        }

        if (request.NoclipSpeed.HasValue)
        {
            _settings.NoclipSpeed = request.NoclipSpeed.Value;
            persistDesktopConfig = true;
        }

        if (request.CameraViewMode.HasValue)
        {
            _settings.SetCameraViewMode(request.CameraViewMode.Value);
            persistDesktopConfig = true;
        }

        if (request.StagedFloorSize.HasValue)
        {
            _settings.StageFloorSize(request.StagedFloorSize.Value);
        }

        if (request.ShowCollisionGeometry.HasValue)
        {
            _settings.SetShowCollisionGeometry(request.ShowCollisionGeometry.Value);
        }

        if (request.ShowPerformanceCounters.HasValue)
        {
            _settings.SetShowPerformanceCounters(request.ShowPerformanceCounters.Value);
        }

        if (request.Strength.HasValue || request.AttackSpeed.HasValue || request.Vitality.HasValue)
        {
            PlayerCombatStats updatedStats = new()
            {
                Strength = request.Strength ?? _runtime.Player.Combatant.BaseStats.Strength,
                AttackSpeed = request.AttackSpeed ?? _runtime.Player.Combatant.BaseStats.AttackSpeed,
                Vitality = request.Vitality ?? _runtime.Player.Combatant.BaseStats.Vitality
            };

            _remoteClient.QueuePlayerAction(new PlayerActionMessage(
                new PlayerSessionId(_remoteClient.SessionId),
                _actionSequences.NextActionSequence(),
                new PlayerStatsUpdateIntent(PlayerCombatStatsSnapshot.From(updatedStats))));

            persistDesktopConfig = true;
        }

        if (request.FloatingCombatText.HasValue)
        {
            _settings.ApplyFloatingCombatTextTuning(request.FloatingCombatText.Value);
            persistDesktopConfig = true;
        }

        if (request.EquipInventorySlotIndex.HasValue)
        {
            _remoteClient.QueuePlayerAction(new PlayerActionMessage(
                new PlayerSessionId(_remoteClient.SessionId),
                _actionSequences.NextActionSequence(),
                new InventoryEquipIntent(request.EquipInventorySlotIndex.Value)));
        }

        if (request.BuildMaterial is BuildMaterialChoice buildMaterial)
        {
            // The server keeps the pick per session and places it from the next build on; the HUD
            // shows what was sent.
            _remoteClient.QueuePlayerAction(new PlayerActionMessage(
                new PlayerSessionId(_remoteClient.SessionId),
                _actionSequences.NextActionSequence(),
                new BuildMaterialSelectIntent(buildMaterial.MaterialId)));
            _render.BuildMaterialSelection = buildMaterial.MaterialId;
        }

        if (request.RequestedResolution.HasValue)
        {
            applyClientSize(request.RequestedResolution.Value);
            _settings.ApplyResolution(request.RequestedResolution.Value);
        }

        if (request.RequestedFloorSize.HasValue && request.RequestedFloorSize.Value != _settings.FloorSize)
        {
            _settings.StageFloorSize(request.RequestedFloorSize.Value);
            // Only the size of the next local world. The live map is the server's world and keeps
            // the size its join-accepted descriptor gave it.
            _settings.ApplyPendingFloorSize();
        }

        if (persistDesktopConfig)
        {
            PersistDesktopClientConfig();
        }
    }

    private void PersistDesktopClientConfig()
    {
        if (_desktopClientConfig == null || _persistDesktopClientConfig == null)
        {
            return;
        }

        _desktopClientConfig.CaptureRuntime(_settings, _runtime.Player.Combatant.BaseStats);
        _persistDesktopClientConfig();
    }
}

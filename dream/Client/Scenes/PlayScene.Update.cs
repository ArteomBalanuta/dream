using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Players;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    public void Update(FrameEventArgs args, KeyboardState keyboardState, MouseState mouseState)
    {
        float delta = (float)args.Time;
        _clientPresentationTimeSeconds += args.Time;
        RenderFrameClock.Advance(_clientPresentationTimeSeconds);
        _serverTickClock.Advance(args.Time);
        DrainRemoteTransportMessages();
        _runtime.Player.NudgeWorldPosition(_predictionReconciler.TakeSmoothingStep(delta));

        // Viewmodel pose refresh is presentation-local and should remain scene/client owned.
        _viewModelController.RefreshPose();
        HandleGlobalInput(keyboardState);
        // A dead local player sends no movement, look or use. Commands keep flowing (and keep their
        // sequence), because the reconciler only compares on a new ack and the respawn snap needs one.
        bool allowGameplayInput = DeadInputPolicy.AllowGameplayInput(
            _settings.CurrentRuntimeState.AllowGameplayInput,
            _runtime.Player.Combatant.Health.IsAlive);
        HandleConsumableHotkeys(keyboardState, allowGameplayInput);
        // Clicks set the goal here; the pointer is last Render's hover state, read once per Update.
        _clickMove.OnFrame(_render.SampleWorldPointer(mouseState, _settings, _windowFocused), allowGameplayInput);
        // The frame only captures input; each prediction tick below turns it into one command
        // whose sequence is that tick.
        PlayerCommandFrame rawCommandFrame = PlayerController.CaptureCommandFrame(
            _runtime.LocalPlayerSessionId,
            0,
            key => keyboardState.IsKeyDown(key) || CaptureKeyHeld(key),
            allowGameplayInput);
        if (allowGameplayInput)
        {
            rawCommandFrame = ApplyCaptureFight(rawCommandFrame);
            rawCommandFrame = _clickMove.MarkFrame(rawCommandFrame);
        }

        rawCommandFrame = _gameplayInputResumeFilter.Filter(rawCommandFrame, allowGameplayInput);
        // Only the integrated host (Game.CreateLocalServerHandle) lets noclip and freeze through.
        _commandInputFilter.AllowDevToggles = _localServer is { IsReady: true } localServer
            && LoopbackHost.IsLoopback(_remoteClient.Options.Host)
            && _remoteClient.Options.Port == localServer.BoundPort;
        PlayerCommandFrame commandFrame = _commandInputFilter.Filter(rawCommandFrame);
        _localPlayerWasMoving = IsMoving(commandFrame.MoveIntent);
        PlayJumpSoundIfPressed(commandFrame.MoveIntent.JumpPressed);
        _commandTicks.CaptureInput(commandFrame);
        ApplyImmediateLocalLook(commandFrame, allowGameplayInput);
        // Predict with the server's movement values; a local NoclipSpeed the server does not use
        // makes every snapshot disagree with the prediction.
        _tickTuning = _settings.ToPlayerSimulationTuning().WithAuthoritativeMovement();
        _tickPrimaryUseTriggered = false;
        _commandTicks.Advance(
            args.Time,
            this,
            static (scene, command, fixedDeltaSeconds) => scene.RunPredictionTick(command, fixedDeltaSeconds));

        if (_tickPrimaryUseTriggered)
        {
            _viewModelController.TriggerPrimaryUse();
            PlaySwingSound();
        }

        ViewModelUpdateResult viewModelUpdateResult =
            _render.ViewModels.Update(_viewModelController.CurrentFrameState, delta);

        if (viewModelUpdateResult.HandsFinished)
        {
            _viewModelController.NotifyAnimationFinished(ViewModelSlot.Hands);
        }

        if (viewModelUpdateResult.HeldItemFinished)
        {
            _viewModelController.NotifyAnimationFinished(ViewModelSlot.HeldItem);
        }

        UpdateLoopbackRemotePlayerDebugState();
        _combatFeedback.Update(delta, _settings.FloatingCombatText);
        _actionFeedback.Update(delta);
        _lootFeedback.Update(delta);
    }

    // One prediction tick and its one command: the server gets every tick's command, in order,
    // and the prediction recorded for the same sequence is what its acknowledgement is compared to.
    private void RunPredictionTick(PlayerCommandFrame command, float fixedDeltaSeconds)
    {
        command = _clickMove.SteerTick(command);
        ulong tick = command.Sequence;
        _actionSequences.ObserveCommandSequence(tick);
        QueueRemoteCommandFrame(command);

        // The simulation frame has primary use stripped (the server owns the action), so the
        // local animation is driven from the held input instead. Before, it never played.
        PlayerCommandFrame simulationCommandFrame = ClientSimulationCommandPolicy.PrepareForLocalSimulation(
            command,
            useRemoteAuthority: true);
        bool playPrimaryUse = _runtime.AdvancePredictionTick(
            tick,
            fixedDeltaSeconds,
            simulationCommandFrame,
            _tickTuning,
            primaryUseHeld: command.PrimaryUsePressed);
        _predictionReconciler.RecordPrediction(tick, _runtime.Player.GetWorldPosition(), command.MoveIntent);
        if (playPrimaryUse)
        {
            _tickPrimaryUseTriggered = true;
        }
    }

    private void HandleGlobalInput(KeyboardState keyboardState)
    {
        bool settingsTogglePressed = keyboardState.IsKeyDown(Keys.Escape);
        if (settingsTogglePressed && !_wasSettingsTogglePressed)
        {
            PressEscape();
        }

        _wasSettingsTogglePressed = settingsTogglePressed;

        bool inventoryTogglePressed = keyboardState.IsKeyDown(Keys.V);
        if (inventoryTogglePressed && !_wasInventoryTogglePressed)
        {
            PressInventoryKey();
        }

        _wasInventoryTogglePressed = inventoryTogglePressed;

        // F3 swaps the classic HUD for the ImGui one and back. Display only: a no-op unless the
        // admin setting is available, in-session, never saved, and nothing is sent.
        bool adminHudTogglePressed = keyboardState.IsKeyDown(Keys.F3);
        if (adminHudTogglePressed && !_wasAdminHudTogglePressed)
        {
            _render.ToggleAdminHud(_settings);
        }

        _wasAdminHudTogglePressed = adminHudTogglePressed;
    }

    // Set by the host: steps an open pause-menu sub-screen back one screen and returns true, or returns
    // false when there is none (Esc then closes the pause menu as usual).
    internal Func<bool>? TryEscapeBack { get; set; }

    // Esc closes an open inventory first (a second Esc opens Settings); from play it opens Settings, and
    // in Settings it closes them (a pause-menu sub-screen goes back one screen first). The capture
    // script calls these same methods.
    internal void PressEscape()
    {
        if (TryEscapeBack?.Invoke() == true)
        {
            return;
        }

        if (_settings.InventoryVisible && !_settings.SettingsVisible)
        {
            _settings.ToggleInventory();
            return;
        }

        _settings.ToggleSettings();
    }

    // V opens and closes the inventory and character windows (not while Settings is open).
    internal void PressInventoryKey()
    {
        if (!_settings.SettingsVisible)
        {
            _settings.ToggleInventory();
        }
    }

    private void QueueRemoteCommandFrame(PlayerCommandFrame localCommandFrame)
    {
        if (!_remoteClient.IsConnected)
        {
            return;
        }

        PlayerCommandFrame remoteCommandFrame = RemotePlayerCommandRelay.CreateRelayCommand(
            localCommandFrame,
            new PlayerSessionId(_remoteClient.SessionId),
            _runtime.Player.Camera.Front);
        _remoteClient.QueueCommandFrame(remoteCommandFrame);
        if (NetworkDiagnosticsLog.IsEnabled)
        {
            RefreshDiagnosticsKeys();
            NetworkDiagnosticsLog.WriteEvery(
                _commandLogKey,
                DiagnosticsLogInterval,
                "client.scene.command",
                (Scene: this, Frame: remoteCommandFrame),
                static state => state.Scene.DescribeQueuedCommand(state.Frame));
        }
    }

    private string DescribeQueuedCommand(PlayerCommandFrame remoteCommandFrame)
    {
        return $"session={_remoteClient.SessionId} seq={remoteCommandFrame.Sequence} sent={_remoteClient.SentCommandCount} " +
            $"facing={NetworkDiagnosticsLog.FormatVector(remoteCommandFrame.LookIntent.Facing ?? _runtime.Player.Camera.Front)}";
    }

    private void ApplyImmediateLocalLook(PlayerCommandFrame commandFrame, bool allowGameplayInput)
    {
        if (!allowGameplayInput)
        {
            return;
        }

        _runtime.Player.ApplyImmediateLook(
            new Vector2(commandFrame.LookIntent.YawDelta, commandFrame.LookIntent.PitchDelta),
            _settings.MouseSensitivity,
            allowLook: true);
    }

    private static bool IsMoving(PlayerMoveIntent moveIntent)
    {
        return MathF.Abs(moveIntent.ForwardAxis) > 0.001f ||
            MathF.Abs(moveIntent.StrafeAxis) > 0.001f ||
            MathF.Abs(moveIntent.VerticalAxis) > 0.001f;
    }
}

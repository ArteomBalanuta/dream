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
    // Set by the capture script while a fight step drives facing and attacks itself: the cursor then
    // neither aims nor attacks.
    internal bool SuppressCursorAim { get; set; }

    // Z toggles walking. Client-side only: it scales the analog axes the tick emits, and the motor
    // reads the magnitude that already replicates, so no wire field is needed. 0.35 of the run
    // cruise lands under the 1.6 m/s walk floor, which is what the motor then holds.
    private const float WalkAxisScale = 0.35f;
    private bool _walkToggleEnabled;
    private bool _wasWalkTogglePressed;

    internal bool WalkToggleEnabled => _walkToggleEnabled;

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
        HandleGlobalInputUnlessTyping(keyboardState);
        // A dead local player sends no movement, look or use. Commands keep flowing (and keep their
        // sequence), because the reconciler only compares on a new ack and the respawn snap needs one.
        bool allowGameplayInput = DeadInputPolicy.AllowGameplayInput(
            _settings.CurrentRuntimeState.AllowGameplayInput && !_chatInput.IsTyping,
            _runtime.Player.Combatant.Health.IsAlive);
        HandleConsumableHotkeys(keyboardState, allowGameplayInput);
        HandleWalkToggle(keyboardState, allowGameplayInput);
        // The pointer is last Render's hover state, read once per Update. The left button is the
        // cursor aim's (facing and the held attack), the right button sets click-to-move goals.
        WorldPointerState pointer = _render.SampleWorldPointer(mouseState, _settings, _windowFocused);
        _cursorAim.OnFrame(pointer, allowGameplayInput, SuppressCursorAim);
        _clickMove.OnFrame(pointer, allowGameplayInput);
        // The frame only captures input; each prediction tick below turns it into one command
        // whose sequence is that tick.
        PlayerCommandFrame rawCommandFrame = PlayerController.CaptureCommandFrame(
            _runtime.LocalPlayerSessionId,
            0,
            key => keyboardState.IsKeyDown(key) || CaptureKeyHeld(key),
            allowGameplayInput,
            _cursorAim.AttackHeld,
            out _tickCameraAxes);
        if (_tickCameraAxes != Vector2.Zero)
        {
            // Walking by hand ends a click goal, and marks the raw frame as walking (forward 1) so a
            // send stall that zeroes the axes can be told apart; the tick projects the real axes.
            _clickMove.Cancel();
            rawCommandFrame = rawCommandFrame with
            {
                MoveIntent = rawCommandFrame.MoveIntent with { ForwardAxis = 1f, StrafeAxis = 0f }
            };
        }

        rawCommandFrame = QuickbarInput.RemapModeKeys(
            rawCommandFrame,
            keyboardState.IsKeyDown(Keys.B) || CaptureKeyHeld(Keys.B),
            keyboardState.IsKeyDown(Keys.G) || CaptureKeyHeld(Keys.G),
            allowGameplayInput);
        if (allowGameplayInput)
        {
            rawCommandFrame = ApplyCaptureFight(rawCommandFrame);
            rawCommandFrame = _clickMove.MarkFrame(rawCommandFrame);
        }

        rawCommandFrame = _gameplayInputResumeFilter.Filter(rawCommandFrame, allowGameplayInput);
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
    //
    // Order matters: the facing the server resolves movement with is Camera.Front as read at
    // QueueRemoteCommandFrame, so the cursor aim sets it first, click steering may override it, and
    // the keyboard axes are projected against that same facing last.
    private void RunPredictionTick(PlayerCommandFrame command, float fixedDeltaSeconds)
    {
        if (_cursorAim.TryGetFacing(out Vector3 aimFacing))
        {
            _runtime.Player.SetFacing(aimFacing);
        }

        command = _clickMove.SteerTick(command);
        command = ProjectKeyboardAxes(command);
        command = ApplyWalkThrottle(command);
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

    // The raw frame carries forward 1 as the "walking" mark when W/A/S/D are held; a stalled send zeroed
    // it, and then the tick stays idle too.
    private PlayerCommandFrame ProjectKeyboardAxes(PlayerCommandFrame command)
    {
        if (_tickCameraAxes == Vector2.Zero || command.MoveIntent.ForwardAxis == 0f)
        {
            return command;
        }

        (float forward, float strafe) = PlayerController.Project(_tickCameraAxes, _runtime.Player.Camera.Front);
        return command with { MoveIntent = command.MoveIntent with { ForwardAxis = forward, StrafeAxis = strafe } };
    }

    // Edge-detected like the other toggles. The held-state is tracked even when gameplay input is
    // off (dead, typing, menus) so a key still down when input resumes is not read as a new press.
    private void HandleWalkToggle(KeyboardState keyboardState, bool allowGameplayInput)
    {
        bool pressed = keyboardState.IsKeyDown(Keys.Z) || CaptureKeyHeld(Keys.Z);
        if (pressed && !_wasWalkTogglePressed && allowGameplayInput)
        {
            _walkToggleEnabled = !_walkToggleEnabled;
        }

        _wasWalkTogglePressed = pressed;
    }

    // Last step of a tick's command assembly, after the click steering and the keyboard projection
    // have both read the axes. Scaling must never land on exactly zero: a zero ForwardAxis is the
    // "not walking / send stalled" mark that ProjectKeyboardAxes and ClickMoveController test.
    //
    // Click-move is exempt. The throttle runs after _clickMove.SteerTick, so it used to scale the
    // follower's axes too, which put the body at 1.6 m/s = 0.025 m/tick against
    // PathFollower.PinnedStepMetres 0.02: a 25% margin, so any mild slowdown while walk-toggled
    // read as pinned and fired the nudge - the exact fight the earlier step recalibration ended.
    // The walk toggle is a keyboard-movement affordance and a click goal carries its own speed
    // intent, so while a goal is active the toggle simply does not apply.
    private PlayerCommandFrame ApplyWalkThrottle(PlayerCommandFrame command)
    {
        if (!_walkToggleEnabled || _clickMove.Goal != ClickGoalKind.None)
        {
            return command;
        }

        PlayerMoveIntent move = command.MoveIntent;
        if (move.ForwardAxis == 0f && move.StrafeAxis == 0f)
        {
            return command;
        }

        return command with
        {
            MoveIntent = move with
            {
                ForwardAxis = move.ForwardAxis == 0f ? 0f : move.ForwardAxis * WalkAxisScale,
                StrafeAxis = move.StrafeAxis == 0f ? 0f : move.StrafeAxis * WalkAxisScale
            }
        };
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

    // Esc, in order: an open chat line already took it (HandleChatInput); a pause-menu sub-screen goes
    // back one screen; an open vendor window, NPC dialogue or trade closes (the trade is cancelled); an
    // open inventory closes (a second Esc opens the pause menu); from play it opens the pause menu, and in
    // the menu it closes it. The capture script calls these same methods.
    internal void PressEscape()
    {
        if (TryEscapeBack?.Invoke() == true)
        {
            return;
        }

        if (!_settings.SettingsVisible && TryCloseNpcWindows())
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

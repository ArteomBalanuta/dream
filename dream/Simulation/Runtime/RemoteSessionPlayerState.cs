using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Rules;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Server-side state of a session that does not own the runtime's primary Player seat.
// It wraps a full physics Player (Bullet body, PlayerMotor, gravity, jump, noclip, collisions,
// cube pushing) so every session is simulated the same way. Earlier secondary sessions were
// kinematic ghosts: Position += direction * speed, no gravity or collision, and they only
// moved on ticks where a command arrived.
//
// It is also the session's authoritative player target: its members read live state, so the
// runtime lists the same instance every tick instead of allocating a delegate-backed wrapper.
public sealed class RemoteSessionPlayerState : IAuthoritativePlayerTarget, IDisposable
{
    private readonly PhysicsIntegrator _physics;
    private readonly Player _player;
    private PlayerCommandFrame? _lastCommand;
    // Ticks simulated since the last accepted command; past WorldSessionRuntime.MaxCommandRepeatTicks
    // the seat simulates an idle frame instead of repeating the last one.
    private int _ticksSinceCommand;
    private bool _disposed;

    public RemoteSessionPlayerState(
        int actorId,
        PlayerSessionId sessionId,
        Vector3 position,
        Map map,
        PhysicsIntegrator physics,
        int currentHealth = 100,
        int maxHealth = 100,
        bool isAlive = true,
        CombatFormulaService? combatFormulaService = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        ActorId = actorId;
        SessionId = sessionId;
        var body = new PhysicsFactory().CreatePlayerBody(position.ToBullet());
        _physics.AddSessionPlayerBody(body);
        _player = new Player(map, _physics, body, position, eventHub: null, combatFormulaService);
        Combatant = new PlayerCombatant(
            () => Position,
            actorId: actorId,
            anchorHeightOffset: 1.2f,
            combatFormulaService: combatFormulaService);
        if (maxHealth != Combatant.Health.MaxHealth)
        {
            Combatant.Health.SetMaxHealthPreservingRatio(maxHealth);
        }

        if (currentHealth < Combatant.Health.CurrentHealth)
        {
            Combatant.Health.ApplyDamage(Combatant.Health.CurrentHealth - currentHealth);
        }
    }

    public int ActorId { get; }

    public PlayerSessionId SessionId { get; }

    public Vector3 Position => _player.GetWorldPosition();

    public Vector3 Facing => _player.Camera.Front;

    public Vector3 EyePosition => _player.Camera.Position;

    public PlayerCombatant Combatant { get; }

    public int CurrentHealth => Combatant.Health.CurrentHealth;

    public int MaxHealth => Combatant.Health.MaxHealth;

    public bool IsAlive => Combatant.Health.IsAlive;

    public PlayerInteractionMode InteractionMode { get; private set; } = PlayerInteractionMode.Building;

    // The runtime disposes this state when the session detaches and lists only spawned sessions.
    bool IAuthoritativePlayerTarget.IsSpawned => !_disposed;

    public Player PhysicsPlayer => _player;

    // Whether the last Simulate triggered this seat's primary use, after the same attack-interval
    // repeat gate the primary seat goes through. The runtime collects triggered seats each tick and
    // the host resolves them, with the primary seat's use, in one ordinal session-id pass.
    public bool PrimaryUseTriggered { get; private set; }

    // The frame the last Simulate ran (the last command, or its starved idle form).
    public PlayerCommandFrame SimulatedCommand { get; private set; }

    public int TicksSinceLastCommand => _ticksSinceCommand;

    public void SetInteractionMode(PlayerInteractionMode interactionMode)
    {
        InteractionMode = interactionMode;
    }

    // Records the session's latest command. Mode and facing apply immediately (they gate action
    // validation within the same tick); movement is simulated by Simulate before the physics step.
    public void ApplyCommand(PlayerCommandFrame commandFrame)
    {
        if (commandFrame.RequestedMode.Mode.HasValue)
        {
            InteractionMode = commandFrame.RequestedMode.Mode.Value;
        }

        if (commandFrame.LookIntent.Facing.HasValue)
        {
            _player.SetFacing(commandFrame.LookIntent.Facing.Value);
        }

        _lastCommand = commandFrame;
        _ticksSinceCommand = 0;
    }

    // Runs before Physics.StepSimulationFixed. The last command keeps applying until a newer one
    // arrives, like the primary seat, so movement no longer depends on command arrival rate. After
    // MaxCommandRepeatTicks starved ticks it decays to idle, so a stalled client stops walking and
    // using. One-shot inputs are edge-detected by PlayerMotor, so repeating a frame does not
    // re-trigger them. Primary use is gated every tick by the combatant's attack interval.
    public void Simulate(float deltaTime, PlayerSimulationTuning tuning)
    {
        PlayerCommandFrame command = _lastCommand ?? PlayerCommandFrame.CreateIdle(SessionId, 0);
        if (_ticksSinceCommand > WorldSessionRuntime.MaxCommandRepeatTicks || !Combatant.Health.IsAlive)
        {
            // Starved, or dead (wave I): idle input; facing and the requested mode are kept.
            command = WorldSessionRuntime.CreateStarvedCommandFrame(command);
        }

        if (_ticksSinceCommand < int.MaxValue)
        {
            _ticksSinceCommand++;
        }

        SimulatedCommand = command;
        PrimaryUseTriggered = _player.ConsumePrimaryUsePressed(
            command.ToInteractionCommand(),
            Combatant.DerivedStats.AttackIntervalSeconds,
            deltaTime);
        Vector2 lookDelta = command.LookIntent.Facing.HasValue
            ? Vector2.Zero
            : new Vector2(command.LookIntent.YawDelta, command.LookIntent.PitchDelta);
        _player.Update(
            deltaTime,
            new PlayerSimulationInput(
                PlayerMovementResolver.CreateMovementCommand(_player.Camera, _player.IsNoclipEnabled, command.MoveIntent),
                lookDelta,
                AllowGameplayInput: true,
                AllowLook: true),
            tuning);

        // Look deltas are relative; apply them only once.
        if (_lastCommand is { } last && !last.LookIntent.Facing.HasValue)
        {
            _lastCommand = last with
            {
                LookIntent = new PlayerLookIntent(0f, 0f, last.LookIntent.Facing)
            };
        }
    }

    public void SyncAfterPhysicsStep(PlayerSimulationTuning tuning)
    {
        _player.SyncCameraFromBody(tuning);
    }

    public void AdvanceCombat(float deltaTime)
    {
        Combatant.AdvanceCombat(deltaTime);
    }

    public void SetWorldPosition(Vector3 position)
    {
        _player.SetWorldPosition(position);
    }

    public void SetFacing(Vector3 facing)
    {
        _player.SetFacing(facing);
    }

    public void SetHealth(int currentHealth, int maxHealth)
    {
        Combatant.Health.SetAuthoritativeState(currentHealth, maxHealth);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The player's contact callback tests against this body in the physics world, so dispose
        // it before the body leaves the world. The body itself is removed and disposed once, here.
        _player.Dispose();
        _physics.RemoveSessionPlayerBody(_player.PhysicsBody);
    }
}

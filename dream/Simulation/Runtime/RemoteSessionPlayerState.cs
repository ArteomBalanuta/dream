using OpenTK.Mathematics;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Server.Rules;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// Server-side state of a session that does not own the runtime's primary Player seat.
// It wraps a full physics Player (Bullet body, PlayerMotor, gravity, jump, noclip, collisions,
// cube pushing) so every session is simulated the same way. Earlier secondary sessions were
// kinematic ghosts: Position += direction * speed, no gravity or collision, and they only
// moved on ticks where a command arrived.
public sealed class RemoteSessionPlayerState : IDisposable
{
    private readonly PhysicsIntegrator _physics;
    private readonly Player _player;
    private PlayerCommandFrame? _lastCommand;
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

    public Player PhysicsPlayer => _player;

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
    }

    // Runs before Physics.StepSimulationFixed. The last command keeps applying until a newer one
    // arrives, like the primary seat, so movement no longer depends on command arrival rate.
    // One-shot inputs are edge-detected by PlayerMotor, so repeating a frame does not re-trigger them.
    public void Simulate(float deltaTime, PlayerSimulationTuning tuning)
    {
        PlayerCommandFrame command = _lastCommand ?? PlayerCommandFrame.CreateIdle(SessionId, 0);
        Vector2 lookDelta = command.LookIntent.Facing.HasValue
            ? Vector2.Zero
            : new Vector2(command.LookIntent.YawDelta, command.LookIntent.PitchDelta);
        _player.Update(
            deltaTime,
            new PlayerSimulationInput(
                PlayerController.CreateMovementCommand(_player.Camera, _player.IsNoclipEnabled, command.MoveIntent),
                lookDelta,
                AllowGameplayInput: true,
                AllowLook: true),
            tuning);

        // Look deltas are relative; apply them only once.
        if (_lastCommand.HasValue && !command.LookIntent.Facing.HasValue)
        {
            _lastCommand = command with
            {
                LookIntent = new PlayerLookIntent(0f, 0f, command.LookIntent.Facing)
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
        _physics.RemoveSessionPlayerBody(_player.PhysicsBody);
    }
}

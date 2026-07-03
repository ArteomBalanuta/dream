using OpenTK.Mathematics;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;


public sealed class RemoteSessionPlayerState
{
    private readonly Camera _camera;

    public RemoteSessionPlayerState(
        int actorId,
        PlayerSessionId sessionId,
        Vector3 position,
        int currentHealth = 100,
        int maxHealth = 100,
        bool isAlive = true)
    {
        ActorId = actorId;
        SessionId = sessionId;
        Position = position;
        _camera = new Camera(position + new Vector3(0f, 0.35f, 0f));
        Combatant = new PlayerCombatant(
            () => Position,
            actorId: actorId,
            anchorHeightOffset: 1.2f);

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

    public Vector3 Position { get; private set; }

    public Vector3 Facing => _camera.Front;

    public Vector3 EyePosition => _camera.Position;

    public PlayerCombatant Combatant { get; }

    public int CurrentHealth => Combatant.Health.CurrentHealth;

    public int MaxHealth => Combatant.Health.MaxHealth;

    public bool IsAlive => Combatant.Health.IsAlive;

    public PlayerInteractionMode InteractionMode { get; private set; } = PlayerInteractionMode.Building;

    public void SetInteractionMode(PlayerInteractionMode interactionMode)
    {
        InteractionMode = interactionMode;
    }

    public void ApplyCommand(
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning tuning,
        float deltaTime)
    {
        if (commandFrame.RequestedMode.Mode.HasValue)
        {
            InteractionMode = commandFrame.RequestedMode.Mode.Value;
        }

        _camera.Position = Position + new Vector3(0f, tuning.EyeHeightOffset, 0f);
        _camera.MouseSensitivity = tuning.MouseSensitivity;
        if (commandFrame.LookIntent.Facing.HasValue)
        {
            _camera.SetFacing(commandFrame.LookIntent.Facing.Value);
        }
        else
        {
            _camera.Update(deltaTime, new Vector2(
                commandFrame.LookIntent.YawDelta,
                commandFrame.LookIntent.PitchDelta));
        }

        Vector3 movementDirection = PlayerController.CreateMovementCommand(
            _camera.Front,
            _camera.Right,
            moveForward: commandFrame.MoveIntent.ForwardAxis > 0f,
            moveBackward: commandFrame.MoveIntent.ForwardAxis < 0f,
            moveLeft: commandFrame.MoveIntent.StrafeAxis < 0f,
            moveRight: commandFrame.MoveIntent.StrafeAxis > 0f,
            jumpPressed: false,
            freezePressed: false,
            allowGameplayInput: true).WorldDirection;
        Position += movementDirection * tuning.MoveSpeed * deltaTime;
        _camera.Position = Position + new Vector3(0f, tuning.EyeHeightOffset, 0f);
    }

    public void AdvanceCombat(float deltaTime)
    {
        Combatant.AdvanceCombat(deltaTime);
    }

    public void ClampToMap(Map map)
    {
        Position = new Vector3(
            Math.Clamp(Position.X, 0.5f, map.Width - 0.5f),
            Math.Clamp(Position.Y, 1.5f, map.Height - 0.5f),
            Math.Clamp(Position.Z, 0.5f, map.Depth - 0.5f));
        _camera.Position = Position + new Vector3(0f, 0.35f, 0f);
    }

    public void SetWorldPosition(Vector3 position)
    {
        Position = position;
        _camera.Position = Position + new Vector3(0f, 0.35f, 0f);
    }

    public void SetFacing(Vector3 facing)
    {
        _camera.SetFacing(facing);
    }

    public void SetHealth(int currentHealth, int maxHealth)
    {
        Combatant.Health.SetAuthoritativeState(currentHealth, maxHealth);
    }
}

using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Simulation.Runtime;


public sealed class AuthoritativePlayerTarget : IAuthoritativePlayerTarget
{
    private readonly Func<Vector3> _positionProvider;
    private readonly Func<Vector3> _facingProvider;
    private readonly Func<Vector3> _eyePositionProvider;
    private readonly Func<PlayerInteractionMode> _interactionModeProvider;
    private readonly Action<Vector3> _setWorldPosition;

    public AuthoritativePlayerTarget(
        PlayerSessionId sessionId,
        int actorId,
        Func<Vector3> positionProvider,
        Func<Vector3> facingProvider,
        PlayerCombatant combatant,
        PlayerInteractionMode interactionMode,
        Action<Vector3> setWorldPosition,
        Func<Vector3>? eyePositionProvider = null,
        bool isSpawned = true)
        : this(
            sessionId,
            actorId,
            positionProvider,
            facingProvider,
            combatant,
            () => interactionMode,
            setWorldPosition,
            eyePositionProvider,
            isSpawned)
    {
    }

    public AuthoritativePlayerTarget(
        PlayerSessionId sessionId,
        int actorId,
        Func<Vector3> positionProvider,
        Func<Vector3> facingProvider,
        PlayerCombatant combatant,
        Func<PlayerInteractionMode> interactionModeProvider,
        Action<Vector3> setWorldPosition,
        Func<Vector3>? eyePositionProvider = null,
        bool isSpawned = true)
    {
        ArgumentNullException.ThrowIfNull(positionProvider);
        ArgumentNullException.ThrowIfNull(facingProvider);
        ArgumentNullException.ThrowIfNull(combatant);
        ArgumentNullException.ThrowIfNull(interactionModeProvider);
        ArgumentNullException.ThrowIfNull(setWorldPosition);

        SessionId = sessionId;
        ActorId = actorId;
        _positionProvider = positionProvider;
        _facingProvider = facingProvider;
        Combatant = combatant;
        _interactionModeProvider = interactionModeProvider;
        _setWorldPosition = setWorldPosition;
        _eyePositionProvider = eyePositionProvider ?? (() => _positionProvider() + new Vector3(0f, 0.35f, 0f));
        IsSpawned = isSpawned;
    }

    public PlayerSessionId SessionId { get; }

    public int ActorId { get; }

    public Vector3 Position => _positionProvider();

    public Vector3 Facing => _facingProvider();

    public Vector3 EyePosition => _eyePositionProvider();

    public PlayerCombatant Combatant { get; }

    public PlayerInteractionMode InteractionMode => _interactionModeProvider();

    public bool IsAlive => Combatant.Health.IsAlive;

    public bool IsSpawned { get; }

    public void SetWorldPosition(Vector3 position)
    {
        _setWorldPosition(position);
    }
}

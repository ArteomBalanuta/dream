using OpenTK.Mathematics;

public sealed class PlayerCombatant : IDamageable, ICombatAnchorProvider
{
    private readonly Func<Vector3> _positionProvider;
    private readonly float _anchorHeightOffset;
    private readonly SceneEventHub? _eventHub;

    public HealthState Health { get; }

    public CombatActorHandle Actor { get; }

    public PlayerCombatant(
        Func<Vector3> positionProvider,
        SceneEventHub? eventHub = null,
        int actorId = 1,
        int maxHealth = 100,
        float anchorHeightOffset = 1.2f)
    {
        _positionProvider = positionProvider;
        _eventHub = eventHub;
        _anchorHeightOffset = anchorHeightOffset;
        Health = new HealthState(maxHealth);
        Actor = new CombatActorHandle(actorId, CreatureKind.Player, Health, this);
    }

    public DamageReceipt ApplyDamage(int amount)
    {
        DamageReceipt receipt = Health.ApplyDamage(amount);
        if (receipt.WasFatal)
        {
            _eventHub?.Publish(new ActorDiedEvent(Actor));
        }

        return receipt;
    }

    public Vector3 GetCombatAnchorWorldPosition()
    {
        return _positionProvider() + new Vector3(0f, _anchorHeightOffset, 0f);
    }
}

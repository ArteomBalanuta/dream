using OpenTK.Mathematics;

public sealed class PlayerCombatant : IDamageable, ICombatAnchorProvider
{
    private readonly Func<Vector3> _positionProvider;
    private readonly float _anchorHeightOffset;

    public HealthState Health { get; }

    public CombatActorHandle Actor { get; }

    public PlayerCombatant(
        Func<Vector3> positionProvider,
        int actorId = 1,
        int maxHealth = 100,
        float anchorHeightOffset = 1.2f)
    {
        _positionProvider = positionProvider;
        _anchorHeightOffset = anchorHeightOffset;
        Health = new HealthState(maxHealth);
        Actor = new CombatActorHandle(actorId, CreatureKind.Player, Health, this);
    }

    public DamageReceipt ApplyDamage(int amount)
    {
        return Health.ApplyDamage(amount);
    }

    public Vector3 GetCombatAnchorWorldPosition()
    {
        return _positionProvider() + new Vector3(0f, _anchorHeightOffset, 0f);
    }
}

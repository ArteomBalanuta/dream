using OpenTK.Mathematics;

public readonly record struct CombatActorHandle(
    int Id,
    CreatureKind Kind,
    IDamageable Damageable,
    ICombatAnchorProvider AnchorProvider)
{
    public Vector3 GetWorldAnchor()
    {
        return AnchorProvider.GetCombatAnchorWorldPosition();
    }
}

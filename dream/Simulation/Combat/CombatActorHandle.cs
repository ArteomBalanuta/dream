using OpenTK.Mathematics;

namespace Dream.Simulation.Combat;


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

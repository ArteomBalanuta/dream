namespace Dream.Simulation.Combat;

public interface IDamageable
{
    HealthState Health { get; }

    DamageReceipt ApplyDamage(int amount);
}

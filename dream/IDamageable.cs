public interface IDamageable
{
    HealthState Health { get; }

    DamageReceipt ApplyDamage(int amount);
}

public sealed class HealthState : IDamageable
{
    public int MaxHealth { get; }

    public int CurrentHealth { get; private set; }

    public bool IsAlive => CurrentHealth > 0;

    public HealthState Health => this;

    public HealthState(int maxHealth)
    {
        MaxHealth = Math.Max(1, maxHealth);
        CurrentHealth = MaxHealth;
    }

    public DamageReceipt ApplyDamage(int amount)
    {
        int requestedAmount = Math.Max(0, amount);
        int previousHealth = CurrentHealth;
        CurrentHealth = Math.Max(0, CurrentHealth - requestedAmount);
        int appliedAmount = previousHealth - CurrentHealth;

        return new DamageReceipt(
            requestedAmount,
            appliedAmount,
            previousHealth,
            CurrentHealth,
            WasFatal: previousHealth > 0 && CurrentHealth == 0);
    }
}

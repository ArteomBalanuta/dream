namespace Dream.Simulation.Combat;

public sealed class HealthState : IDamageable
{
    public int MaxHealth { get; private set; }

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

    // Heals a living actor up to MaxHealth and returns the amount actually restored. The dead stay dead.
    public int Restore(int amount)
    {
        if (CurrentHealth <= 0 || amount <= 0)
        {
            return 0;
        }

        int previous = CurrentHealth;
        CurrentHealth = (int)Math.Min((long)CurrentHealth + amount, MaxHealth);
        return CurrentHealth - previous;
    }

    public void SetMaxHealthPreservingRatio(int maxHealth)
    {
        int clampedMaxHealth = Math.Max(1, maxHealth);
        float ratio = MaxHealth <= 0 ? 1f : CurrentHealth / (float)MaxHealth;
        MaxHealth = clampedMaxHealth;
        int scaled = Math.Clamp((int)MathF.Round(clampedMaxHealth * ratio), 0, clampedMaxHealth);
        // A living actor never rounds down to 0 (that would be a death with no event); the dead stay dead.
        CurrentHealth = CurrentHealth > 0 ? Math.Max(1, scaled) : scaled;
    }

    public void SetAuthoritativeState(int currentHealth, int maxHealth)
    {
        MaxHealth = Math.Max(1, maxHealth);
        CurrentHealth = Math.Clamp(currentHealth, 0, MaxHealth);
    }
}

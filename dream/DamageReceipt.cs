public readonly record struct DamageReceipt(
    int RequestedAmount,
    int AppliedAmount,
    int PreviousHealth,
    int CurrentHealth,
    bool WasFatal);

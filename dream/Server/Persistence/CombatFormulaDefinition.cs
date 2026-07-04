namespace Dream.Server.Persistence;

public readonly record struct CombatFormulaDefinition(
    string FormulaKey,
    string ExpressionText,
    string Description,
    bool Enabled,
    DateTime UpdatedUtc);

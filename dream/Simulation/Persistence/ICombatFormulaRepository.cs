namespace Dream.Simulation.Persistence;

public interface ICombatFormulaRepository
{
    IReadOnlyList<CombatFormulaDefinition> LoadAllEnabled();

    CombatFormulaDefinition? TryGet(string formulaKey);

    void Upsert(CombatFormulaDefinition definition);
}

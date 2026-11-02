using Dream.Rendering.Hud;

namespace Dream.Rendering.Ui;

// Where panels send what the player picked. The surface turns these into a HudInteractionRequest.
public interface IHudActions
{
    void Inventory(InventoryHudAction action);

    void CloseInventory();

    // Spend one attribute point on Strength, Agility, Vitality or Energy. A default method so the
    // fakes that predate the character sheet keep compiling.
    void SpendAttribute(Dream.Simulation.Stats.StatId attribute)
    {
    }
}

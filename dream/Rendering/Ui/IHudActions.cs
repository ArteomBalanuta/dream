using Dream.Rendering.Hud;

namespace Dream.Rendering.Ui;

// Where panels send what the player picked. The surface turns these into a HudInteractionRequest.
public interface IHudActions
{
    void Inventory(InventoryHudAction action);

    void CloseInventory();
}

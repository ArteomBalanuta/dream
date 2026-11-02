using Dream.Rendering.Hud;

namespace Dream.Rendering.Ui;

// Where panels send what the player picked. The surface turns these into a HudInteractionRequest.
public interface IHudActions
{
    void Inventory(InventoryHudAction action);

    void CloseInventory();

    // M3. Default members so a panel test's own actions fake keeps compiling; the surface implements them.

    // A ground-item label was clicked.
    void PickUpGround(long itemInstanceId)
    {
    }

    // An NPC label was clicked.
    void InteractNpc(int npcActorId)
    {
    }

    // A buy or sell pick in the vendor window.
    void Vendor(VendorHudAction action)
    {
    }

    void CloseVendor()
    {
    }
}

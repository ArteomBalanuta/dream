using System.Globalization;

namespace Dream.Rendering.Ui.Menu;

public enum MultiplayerField : byte
{
    None,
    PlayerName,
    Address,
    JoinPort,
    HostPort
}

// What the Multiplayer screen's fields hold and which one has the keyboard.
public sealed class MultiplayerFormState
{
    public const int MaxPlayerNameLength = 32;
    public const int MaxAddressLength = 253;

    public MultiplayerFormState(string playerName, string address, int joinPort, int hostPort)
    {
        PlayerName.SetText(playerName);
        Address.SetText(address);
        JoinPort.SetText(joinPort.ToString(CultureInfo.InvariantCulture));
        HostPort.SetText(hostPort.ToString(CultureInfo.InvariantCulture));
    }

    public MenuTextField PlayerName { get; } = new(MaxPlayerNameLength);

    public MenuTextField Address { get; } = new(MaxAddressLength);

    public MenuTextField JoinPort { get; } = new(5, digitsOnly: true);

    public MenuTextField HostPort { get; } = new(5, digitsOnly: true);

    public MultiplayerField Focused { get; set; } = MultiplayerField.Address;

    public MenuTextField? FocusedField => Focused switch
    {
        MultiplayerField.PlayerName => PlayerName,
        MultiplayerField.Address => Address,
        MultiplayerField.JoinPort => JoinPort,
        MultiplayerField.HostPort => HostPort,
        _ => null
    };

    // To the focused field.
    public void TypeCharacter(uint unicode) => FocusedField?.TypeCharacter(unicode);

    // The editing keys act on the focused field: Backspace/Delete/Left/Right/Home/End (Shift extends the
    // selection, Control moves or deletes by word), SelectAll, and Copy/Cut/Paste through clipboard (ignored
    // when it is null). Tab cycles PlayerName -> Address -> JoinPort -> HostPort -> PlayerName (Shift+Tab
    // backwards) and selects the new field's text. Enter returns Join (PlayerName, Address, JoinPort) or
    // Host (HostPort); Escape returns Back; everything else None.
    // CONTRACT-STUB: WS-A extends this to the new keys and modifiers.
    public MenuAction HandleKey(MenuKey key, MenuKeyModifiers modifiers, IMenuClipboard? clipboard) => HandleKey(key);

    public MenuAction HandleKey(MenuKey key)
    {
        switch (key)
        {
            case MenuKey.Backspace:
                FocusedField?.Backspace();
                return MenuAction.None;
            case MenuKey.Tab:
                Focused = Focused switch
                {
                    MultiplayerField.PlayerName => MultiplayerField.Address,
                    MultiplayerField.Address => MultiplayerField.JoinPort,
                    MultiplayerField.JoinPort => MultiplayerField.HostPort,
                    _ => MultiplayerField.PlayerName
                };
                return MenuAction.None;
            case MenuKey.Enter:
                return Focused switch
                {
                    MultiplayerField.PlayerName or MultiplayerField.Address or MultiplayerField.JoinPort => MenuAction.Join,
                    MultiplayerField.HostPort => MenuAction.Host,
                    _ => MenuAction.None
                };
            case MenuKey.Escape:
                return MenuAction.Back;
            default:
                return MenuAction.None;
        }
    }
}

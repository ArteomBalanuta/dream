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
// CONTRACT-STUB: WS-A replaces the bodies.
public sealed class MultiplayerFormState
{
    public const int MaxPlayerNameLength = 32;
    public const int MaxAddressLength = 253;

    public MultiplayerFormState(string playerName, string address, int joinPort, int hostPort)
    {
    }

    public MenuTextField PlayerName { get; } = new(MaxPlayerNameLength);

    public MenuTextField Address { get; } = new(MaxAddressLength);

    public MenuTextField JoinPort { get; } = new(5, digitsOnly: true);

    public MenuTextField HostPort { get; } = new(5, digitsOnly: true);

    public MultiplayerField Focused { get; set; } = MultiplayerField.Address;

    public MenuTextField? FocusedField => throw new NotImplementedException();

    // To the focused field.
    public void TypeCharacter(uint unicode) => throw new NotImplementedException();

    // Backspace edits the focused field; Tab cycles PlayerName -> Address -> JoinPort -> HostPort -> PlayerName;
    // Enter returns Join when focus is Address or JoinPort, Host when focus is HostPort, else None;
    // Escape returns Back.
    public MenuAction HandleKey(MenuKey key) => throw new NotImplementedException();
}

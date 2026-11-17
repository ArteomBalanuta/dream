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
    public MenuAction HandleKey(MenuKey key) => HandleKey(key, MenuKeyModifiers.None, null);

    // A "host:port" pasted into Address (exactly one ':' and 1-5 digits after it) splits into Address and
    // JoinPort; anything else, IPv6 included, is pasted as typed.
    private void PasteInto(MenuTextField? field, string? text)
    {
        if (field == Address && text != null)
        {
            string line = text.Split(['\r', '\n']).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            int colon = line.IndexOf(':');
            if (colon > 0 && colon == line.LastIndexOf(':'))
            {
                string digits = line[(colon + 1)..];
                if (digits.Length is >= 1 and <= 5 && digits.All(char.IsAsciiDigit))
                {
                    // The port follows only a host that actually went in (a full field takes nothing).
                    if (Address.Insert(line[..colon]) > 0)
                    {
                        JoinPort.SetText(digits);
                    }

                    return;
                }
            }
        }

        field?.Insert(text);
    }

    public MenuAction HandleKey(MenuKey key, MenuKeyModifiers modifiers, IMenuClipboard? clipboard)
    {
        bool shift = (modifiers & MenuKeyModifiers.Shift) != 0;
        bool control = (modifiers & MenuKeyModifiers.Control) != 0;
        MenuTextField? field = FocusedField;
        switch (key)
        {
            case MenuKey.Backspace:
                field?.Backspace(control);
                return MenuAction.None;
            case MenuKey.Delete:
                field?.Delete(control);
                return MenuAction.None;
            case MenuKey.Left:
                field?.MoveLeft(shift, control);
                return MenuAction.None;
            case MenuKey.Right:
                field?.MoveRight(shift, control);
                return MenuAction.None;
            case MenuKey.Home:
                field?.MoveHome(shift);
                return MenuAction.None;
            case MenuKey.End:
                field?.MoveEnd(shift);
                return MenuAction.None;
            case MenuKey.SelectAll:
                field?.SelectAll();
                return MenuAction.None;
            case MenuKey.Copy:
                if (field is { HasSelection: true })
                {
                    clipboard?.SetText(field.SelectedText);
                }

                return MenuAction.None;
            case MenuKey.Cut:
                if (field is { HasSelection: true })
                {
                    if (clipboard != null && clipboard.SetText(field.SelectedText))
                    {
                        field.CutSelection();
                    }
                }

                return MenuAction.None;
            case MenuKey.Paste:
                PasteInto(field, clipboard?.GetText());
                return MenuAction.None;
            case MenuKey.Tab:
                Focused = shift
                    ? Focused switch
                    {
                        MultiplayerField.Address => MultiplayerField.PlayerName,
                        MultiplayerField.JoinPort => MultiplayerField.Address,
                        MultiplayerField.HostPort => MultiplayerField.JoinPort,
                        _ => MultiplayerField.HostPort
                    }
                    : Focused switch
                    {
                        MultiplayerField.PlayerName => MultiplayerField.Address,
                        MultiplayerField.Address => MultiplayerField.JoinPort,
                        MultiplayerField.JoinPort => MultiplayerField.HostPort,
                        _ => MultiplayerField.PlayerName
                    };
                FocusedField?.SelectAll();
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

namespace Dream.Multiplayer.Protocol;

// M1 (protocol v14). One character of the requesting account, for the picker.
public readonly record struct CharacterListEntry(string DisplayName, int Level);

// M1 (protocol v14). The answer to ClientCharacterListRequest: the account's characters, at most
// MaxCharacters.
public sealed record ServerCharacterList(IReadOnlyList<CharacterListEntry> Characters) : IServerToClientMessage
{
    public const string Kind = "server.character-list";
    public const int MaxCharacters = 16;

    public string MessageKind => Kind;
}

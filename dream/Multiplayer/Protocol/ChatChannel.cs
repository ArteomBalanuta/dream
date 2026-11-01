namespace Dream.Multiplayer.Protocol;

// M1 (protocol v14). Which audience a chat line is for. Travels as its numeric value; the server
// refuses a value outside this list.
public enum ChatChannel : byte
{
    World,
    Say,
    Whisper,
    System
}

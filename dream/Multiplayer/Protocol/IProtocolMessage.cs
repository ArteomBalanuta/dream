using System.Text.Json.Serialization;

namespace Dream.Multiplayer.Protocol;


public interface IProtocolMessage
{
    [JsonIgnore]
    string MessageKind { get; }
}

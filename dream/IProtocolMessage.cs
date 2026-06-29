using System.Text.Json.Serialization;

public interface IProtocolMessage
{
    [JsonIgnore]
    string MessageKind { get; }
}

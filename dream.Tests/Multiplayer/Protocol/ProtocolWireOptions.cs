using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dream.Multiplayer.Protocol;

namespace Dream.Tests.Multiplayer.Protocol;

// Serializer options built independently of TransportJsonCodec, so tests can compare its output
// against a separate reference for each protocol version.
internal static class ProtocolWireOptions
{
    public static readonly JsonSerializerOptions V1 = CreateV1();

    public static readonly JsonSerializerOptions V2 = CreateV2();

    // Protocol v1: every property is written, nulls and derived properties included.
    private static JsonSerializerOptions CreateV1()
    {
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new Vector3JsonConverter());
        return options;
    }

    // Protocol v2: nulls are left out, and so are MessageKind on every message and the two
    // Effective* properties of JoinAcceptedMessage.
    private static JsonSerializerOptions CreateV2()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    typeInfo =>
                    {
                        if (!typeof(IProtocolMessage).IsAssignableFrom(typeInfo.Type))
                        {
                            return;
                        }

                        string[] dropped = typeInfo.Type == typeof(JoinAcceptedMessage)
                            ? ["MessageKind", "EffectiveRoster", "EffectiveInitialActors"]
                            : ["MessageKind"];
                        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
                        {
                            if (typeInfo.Properties[i].AttributeProvider is MemberInfo member
                                && Array.IndexOf(dropped, member.Name) >= 0)
                            {
                                typeInfo.Properties.RemoveAt(i);
                            }
                        }
                    }
                }
            }
        };
        options.Converters.Add(new Vector3JsonConverter());
        return options;
    }
}

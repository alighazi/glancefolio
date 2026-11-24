using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioWeb;

/// <summary>
/// One set of JSON options for the whole app: camelCase on the wire (so the
/// TypeScript client sees idiomatic names), enums as strings (so a payload is
/// readable and survives a reordering of an enum), and unknown members ignored
/// (so a provider adding a field cannot break deserialisation).
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}

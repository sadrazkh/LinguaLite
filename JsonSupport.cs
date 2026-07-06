using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class AppJsonOptions
{
    public static JsonSerializerOptions CreateIndented() => Create(true);
    public static JsonSerializerOptions CreateCompact() => Create(false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}

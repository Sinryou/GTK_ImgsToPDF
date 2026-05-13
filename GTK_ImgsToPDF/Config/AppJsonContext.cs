using System.Text.Json.Serialization;

namespace GTK_ImgsToPDF.Config {
    [JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AppConfig))]
    internal partial class AppJsonContext : JsonSerializerContext {
    }
}

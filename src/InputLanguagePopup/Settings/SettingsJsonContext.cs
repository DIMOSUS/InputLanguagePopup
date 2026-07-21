using System.Text.Json.Serialization;

namespace InputLanguagePopup.Settings;

/// <summary>
/// Source-generated JSON metadata for <see cref="AppSettings"/>. Reflection-based
/// serialization does not work under Native AOT, so the settings file is read and
/// written through this generated context instead.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}

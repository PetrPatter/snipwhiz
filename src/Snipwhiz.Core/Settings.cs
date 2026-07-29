using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.Core;

/// <summary>
/// Three booleans and, since the editor's style pill, the last style used with each
/// drawing tool.
/// </summary>
public sealed class Settings
{
    public bool Autostart { get; set; }
    public bool PrintScreenPromptAnswered { get; set; }
    public bool PrintScreenTakenOver { get; set; }

    /// <summary>
    /// The style each tool draws with next time, keyed by
    /// <c>ProjectStore.TagOf</c>.
    ///
    /// <para>A tool with no entry falls back to whatever its own type constructs
    /// itself with, so a new tool needs no migration and a settings file written by
    /// an older build simply has fewer keys.</para>
    /// </summary>
    public Dictionary<string, AnnotationStyle> ToolStyles { get; set; } = [];

    [JsonIgnore] private static string FileName => "settings.json";

    /// <summary>
    /// <see cref="Color"/> is a struct of a dozen properties to the reflection
    /// serializer; left alone it writes something unreadable and reads back
    /// something wrong. The converter routes it through the same hex the project
    /// format uses.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new ColourJsonConverter() },
    };

    public static Settings Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
        }
        // A corrupt settings file must never stop the app starting — and neither must
        // an unreadable one. Antivirus and file-sync clients hold this file open often
        // enough that the IOException escaped to OnStartup's catch and showed
        // "Snipwhiz could not start", which is the exact outcome this catch exists
        // to prevent. Defaulted preferences are always recoverable; startup is not.
        //
        // FormatException joins the list now that styles are in here: a hand-edited
        // colour is a bad string, not bad JSON, and it must not be fatal either.
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new Settings();
        }
    }

    /// <summary>
    /// Atomic. This file used to hold three booleans, where a torn write cost
    /// nothing anyone would notice; it now holds every remembered tool style, and
    /// <see cref="File.WriteAllText(string, string)"/> truncates before it writes.
    /// </summary>
    public void Save(string root) =>
        AtomicFile.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(this, Json));

    private sealed class ColourJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            ColourHex.Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
            writer.WriteStringValue(ColourHex.Write(value));
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snipwhiz.Core;

/// <summary>
/// Three fields and no UI. If this needs a fourth before spec 2, that is the
/// signal the settings window is overdue.
/// </summary>
public sealed class Settings
{
    public bool Autostart { get; set; }
    public bool PrintScreenPromptAnswered { get; set; }
    public bool PrintScreenTakenOver { get; set; }

    [JsonIgnore] private static string FileName => "settings.json";

    public static Settings Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        // A corrupt settings file must never stop the app starting — and neither must
        // an unreadable one. Antivirus and file-sync clients hold this file open often
        // enough that the IOException escaped to OnStartup's catch and showed
        // "Snipwhiz could not start", which is the exact outcome this catch exists
        // to prevent. Three defaulted booleans are always recoverable; startup is not.
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, FileName),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

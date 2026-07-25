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
        catch (JsonException)
        {
            return new Settings();   // a corrupt settings file must never stop the app starting
        }
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, FileName),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

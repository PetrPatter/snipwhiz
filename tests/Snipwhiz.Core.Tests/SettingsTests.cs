using Snipwhiz.Core;
using Xunit;

namespace Snipwhiz.Core.Tests;

/// <summary>
/// Load's whole job is that nothing about settings.json can stop the app starting.
/// Three defaulted booleans are always recoverable; a failed startup is not.
/// </summary>
public class SettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "snipwhiz-settings", Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_root, "settings.json");

    public SettingsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Settings_round_trip()
    {
        new Settings { Autostart = true, PrintScreenTakenOver = true }.Save(_root);

        var loaded = Settings.Load(_root);

        Assert.True(loaded.Autostart);
        Assert.True(loaded.PrintScreenTakenOver);
        Assert.False(loaded.PrintScreenPromptAnswered);
    }

    [Fact]
    public void A_missing_file_gives_defaults() =>
        Assert.False(Settings.Load(_root).Autostart);

    [Fact]
    public void A_corrupt_file_gives_defaults()
    {
        File.WriteAllText(Path_, "{ not json at all");

        Assert.False(Settings.Load(_root).Autostart);
    }

    [Fact]
    public void A_file_locked_by_another_process_gives_defaults()
    {
        new Settings { Autostart = true }.Save(_root);

        // Antivirus and file-sync clients hold this open. FileShare.None reproduces
        // it exactly: File.ReadAllText throws IOException, which used to escape Load
        // and surface as "Snipwhiz could not start."
        using var locked = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.None);

        var loaded = Settings.Load(_root);

        Assert.False(loaded.Autostart);   // defaults, not a thrown exception
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

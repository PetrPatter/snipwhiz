using System.Windows.Media;
using Snipwhiz.Core;
using Snipwhiz.Core.Annotations;
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

    // ---- tool styles ------------------------------------------------------

    [Fact]
    public void A_tool_style_survives_a_round_trip_with_its_colours()
    {
        var settings = new Settings();
        settings.ToolStyles["highlight"] = new AnnotationStyle
        {
            Stroke = Color.FromRgb(0xE5, 0x48, 0x4D),
            StrokeWidth = 6,
            Fill = Color.FromArgb(0x80, 0x2F, 0xB3, 0x44),
            Opacity = 0.35,
        };
        settings.Save(_root);

        var style = Settings.Load(_root).ToolStyles["highlight"];

        // Colour is the part that needs saying. Left to the reflection serializer a
        // Color goes out as its dozen properties and comes back as something else
        // entirely — and it would come back *silently*, as a black shape.
        Assert.Equal(Color.FromRgb(0xE5, 0x48, 0x4D), style.Stroke);
        Assert.Equal(Color.FromArgb(0x80, 0x2F, 0xB3, 0x44), style.Fill);
        Assert.Equal(6, style.StrokeWidth);
        Assert.Equal(0.35, style.Opacity);
    }

    [Fact]
    public void An_unfilled_style_round_trips_as_unfilled()
    {
        // Null fill is what "outline only" means. A converter that turns it into
        // transparent-black would make every outline shape reopen filled.
        var settings = new Settings();
        settings.ToolStyles["rectangle"] = AnnotationStyle.Default;
        settings.Save(_root);

        Assert.Null(Settings.Load(_root).ToolStyles["rectangle"].Fill);
    }

    [Fact]
    public void A_settings_file_written_before_tool_styles_existed_still_loads()
    {
        File.WriteAllText(Path_, """{ "Autostart": true }""");

        var loaded = Settings.Load(_root);

        Assert.True(loaded.Autostart);
        Assert.Empty(loaded.ToolStyles);
    }

    [Fact]
    public void A_hand_edited_colour_that_is_not_a_colour_gives_defaults()
    {
        // A bad colour is a FormatException from deep inside the converter, not a
        // JsonException. Before it was caught, this file stopped the app starting.
        File.WriteAllText(Path_, """
            { "Autostart": true,
              "ToolStyles": { "rectangle": { "Stroke": "not-a-colour", "StrokeWidth": 4,
                                             "Fill": null, "Opacity": 1 } } }
            """);

        Assert.False(Settings.Load(_root).Autostart);   // defaults, not a crash
    }

    // ---- the atomic write -------------------------------------------------

    /// <summary>
    /// Everything a save leaves behind, checked cheaply.
    ///
    /// <para>The property that actually matters — the file survives the process
    /// dying mid-write — is not reachable from here, and two attempts to reach it
    /// are worth recording so nobody repeats them. A reader racing a writer cannot
    /// fail: on Windows <c>File.ReadAllText</c> cannot even open a file that
    /// <c>File.WriteAllText</c> has in progress, so the control passed against the
    /// broken implementation. Holding the old file open to prove it was replaced
    /// rather than truncated cannot run either: <c>File.Move</c> refuses to replace
    /// a file with any handle on it, share-delete or not.</para>
    ///
    /// <para>The real control lives in <c>Diagnostics.SettingsWriteVerification</c>,
    /// which kills the process mid-write exactly as the plan specified.</para>
    /// </summary>
    [Fact]
    public void Saving_leaves_no_scratch_file_behind()
    {
        new Settings { Autostart = true }.Save(_root);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

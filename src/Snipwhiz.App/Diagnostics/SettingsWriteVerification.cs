using System.IO;
using System.Text.Json;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Proves a settings write survives the process being killed part-way through it.
///
/// <para>Settings used to hold three booleans, where a torn write cost nothing
/// anyone would notice. It now holds every remembered tool style, and
/// <c>File.WriteAllText</c> truncates the target <i>before</i> it writes: kill the
/// process in that window and what is left on disk is a valid, shorter, wrong file
/// — or an empty one. <see cref="AtomicFile"/> writes beside the target and moves
/// it into place, so the target only ever changes in one step.</para>
///
/// <para>This is a gate rather than a unit test because the property is about the
/// process dying, and two attempts to reach it from a test could not fail. A reader
/// racing a writer is unobservable on Windows — <c>File.ReadAllText</c> cannot open
/// a file <c>File.WriteAllText</c> holds — and proving the file was replaced rather
/// than truncated needs a handle on it, which is precisely what stops
/// <c>File.Move</c> replacing it.</para>
///
/// <para>Writes a probe file, not <c>settings.json</c>: this loop is killed dozens
/// of times and has no business anywhere near a real preferences file. It refuses
/// to run without <c>SNIPWHIZ_ROOT</c> for the same reason <c>LibrarySeed</c>
/// does.</para>
///
/// <code>
/// # driver, from the repo root:
/// scripts/verify-atomic-write.ps1            # positive
/// scripts/verify-atomic-write.ps1 -Break     # NEGATIVE CONTROL
/// </code>
/// </summary>
internal static class SettingsWriteVerification
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_ATOMIC") == "1";

    /// <summary>Written last, so its presence means the whole file arrived.</summary>
    public const string Sentinel = "--end-of-file--";

    public static string ProbePath(string root) => Path.Combine(root, "atomic-probe.json");

    /// <summary>Never returns. The driver kills this process at a random moment.</summary>
    public static void RunIfRequested(string root)
    {
        if (!IsEnabled) return;

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNIPWHIZ_ROOT")))
        {
            throw new InvalidOperationException(
                "SNIPWHIZ_VERIFY_ATOMIC requires SNIPWHIZ_ROOT — refusing to write beside the real settings.");
        }

        var broken = Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_ATOMIC") == "1";
        var path = ProbePath(root);

        // Large enough that the write is not instantaneous, or the kill almost never
        // lands inside it and the control would look like a pass.
        var payload = new Dictionary<string, string>();
        for (var i = 0; i < 20_000; i++) payload[$"key{i}"] = $"value{i}-{new string('x', 40)}";
        payload["zz-last"] = Sentinel;
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(root);
        while (true)
        {
            if (broken) File.WriteAllText(path, json);
            else AtomicFile.WriteAllText(path, json);
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.App.Editor;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Measures whether editing many captures in a row holds on to any of them.
///
/// <para>The library reached a 708 MB working set retaining 320 px thumbnails. The
/// editor holds a decoded <b>full-resolution</b> bitmap — 33 MB for a 4K capture —
/// so the same mistake costs twenty times as much per item. Spec §4.19 allows
/// exactly one source alive at a time.</para>
///
/// <para><b>Counted, not eyeballed, and specifically not by heap size.</b> That
/// lesson is already paid for: the managed heap read 4.3 MB while a thousand live
/// bitmaps sat in unmanaged WIC memory, and the planned <c>dotnet-counters</c> check
/// would have shown a small heap and been read as exoneration. This holds a
/// <see cref="WeakReference"/> to every bitmap handed to the editor and asks, after
/// a forced collection, how many are still alive. Liveness, not bookkeeping.</para>
///
/// <code>
/// $env:SNIPWHIZ_ROOT = "$env:TEMP\snipwhiz-verify"
/// $env:SNIPWHIZ_SEED = "10"
/// $env:SNIPWHIZ_VERIFY_EDITORMEMORY = "1"
/// # result: %TEMP%\snipwhiz-editor-memory.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — has the editor keep every source it is ever
/// given. The alive count must climb to the number opened:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_BREAK_EDITORMEMORY = "1"
/// </code>
/// </summary>
internal static class EditorMemoryVerification
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_EDITORMEMORY") == "1";

    public static bool BreakRelease =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_EDITORMEMORY") == "1";

    /// <summary>The control's leak. Only ever populated when <see cref="BreakRelease"/> is set.</summary>
    private static readonly List<BitmapSource> Retained = [];

    public static void Retain(BitmapSource source) => Retained.Add(source);

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-editor-memory.txt");

    public static void RunIfRequested(CaptureStore store)
    {
        if (!IsEnabled) return;

        var records = store.Recent(10);
        var editor = new EditorView(store);
        var window = new Window
        {
            Title = "Snipwhiz editor memory gate",
            Width = 900,
            Height = 620,
            Background = Brushes.Black,
            Content = editor,
        };

        window.ContentRendered += (_, _) =>
        {
            var watched = new List<WeakReference>();

            foreach (var record in records)
            {
                var source = Decode(store.Assets.Original(record));
                if (source is null) continue;

                watched.Add(new WeakReference(source));
                editor.Open(record, source);
            }

            // Let anything the editor dropped actually go, including finalisable
            // WIC wrappers, before asking what survived.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var alive = watched.Count(w => w.IsAlive);
            using var self = System.Diagnostics.Process.GetCurrentProcess();

            Write(watched.Count, alive, GC.GetTotalMemory(true), self.WorkingSet64, self.PrivateMemorySize64);

            window.Close();
            Application.Current.Shutdown();
        };

        window.Show();
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static void Write(int opened, int alive, long heap, long workingSet, long priv)
    {
        var mode = BreakRelease
            ? "NEGATIVE CONTROL (editor keeps every source it is given)"
            : "POSITIVE (one document at a time)";

        File.WriteAllText(ResultPath,
            $"mode={mode}\n" +
            $"capturesOpened={opened}\n" +
            "\n" +
            $"sourceBitmapsStillAlive={alive}\n" +
            $"managedHeapMB={heap / 1024.0 / 1024:F1}\n" +
            $"workingSetMB={workingSet / 1024.0 / 1024:F1}\n" +
            $"privateMB={priv / 1024.0 / 1024:F1}\n" +
            "\n" +
            "Expected: sourceBitmapsStillAlive=1, the one currently open. Anything\n" +
            "higher means editing a capture is enough to retain it, and memory grows\n" +
            "with how many were edited rather than with what is on screen.\n" +
            "\n" +
            "managedHeapMB is reported for contrast, not as the gate. Decoded pixels\n" +
            "live in unmanaged WIC memory, so the heap stays small whether or not the\n" +
            "bitmaps are retained - which is exactly how the library's 708 MB hid.\n");
    }
}

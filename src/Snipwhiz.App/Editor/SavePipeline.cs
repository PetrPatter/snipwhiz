using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Project;
using Snipwhiz.Core.Scene;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Editor;

/// <summary>
/// Turns an edited scene into files on disk and a row in the database.
///
/// <para>Ordered so that the thing that cannot be recreated is written first. The
/// project holds the user's work; the flattened render is derived from it and can
/// always be produced again. <b>A failed render therefore still commits the
/// project</b>, and the library falls back to showing the original capture — the
/// difference between a render bug and a lost-work bug.</para>
///
/// <para><b>Threading.</b> A 4K <c>RenderTargetBitmap</c> is tens of milliseconds,
/// and autosave makes it happen exactly when a window is going away, which is the
/// worst possible moment for a hitch. Writing and rendering run on a dedicated STA
/// thread — STA because the WPF render stack requires it. The database update
/// marshals back, because spec 2a put SQLite on the UI thread deliberately and that
/// is not being reversed for one caller.</para>
/// </summary>
internal sealed class SavePipeline(CaptureStore store, ThumbnailCache thumbnails, Dispatcher dispatcher)
{
    /// <summary>Serialises saves so two overlapping ones cannot land out of order.</summary>
    private static readonly Lock Gate = new();

    /// <summary>The capture as it now stands, with its project and render recorded.</summary>
    public event Action<CaptureRecord>? Saved;

    /// <summary>The render failed. The annotations are safe; the tile will show the original.</summary>
    public event Action<CaptureRecord, Exception>? RenderFailed;

    /// <summary>
    /// Env-gated control for the failure path, which is otherwise unreachable
    /// without corrupting something.
    /// </summary>
    public static bool BreakFlatten =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_FLATTEN") == "1";

    public void Save(CaptureRecord record, SceneDocument document, BitmapSource source)
    {
        // Snapshot on the thread that owns the scene. Everything after this point
        // works from an immutable string and a frozen bitmap, so the user can keep
        // editing while the save runs.
        var json = ProjectStore.Serialize(document);

        var thread = new Thread(() => Run(record, json, source)) { IsBackground = true, Name = "Snipwhiz save" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Writes the project on the calling thread and skips the render.
    ///
    /// <para>For shutdown, where the normal path cannot be used: its worker is a
    /// background thread, so the process exits out from under it and the
    /// annotations are lost. The render is skipped rather than waited for — it is
    /// derived from the project, and <c>Display</c> falls back to the original
    /// until the next save produces one.</para>
    /// </summary>
    public void SaveProjectNow(CaptureRecord record, SceneDocument document)
    {
        try
        {
            var projectPath = store.Assets.Project(record.Id);
            ProjectStore.Save(projectPath, document);
            store.SetEditPaths(
                record.Id, Relative(projectPath), record.FlatPath,
                record.FlatWidth, record.FlatHeight, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Shutting down. There is nowhere useful to report this, and throwing
            // would take the process down harder than the failure warrants.
        }
    }

    private void Run(CaptureRecord record, string json, BitmapSource source)
    {
        lock (Gate)
        {
            var projectPath = store.Assets.Project(record.Id);
            var flatPath = store.Assets.Flat(record.Id);

            try
            {
                ProjectStore.SaveText(projectPath, json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nothing was recorded and nothing was destroyed. The in-memory
                // scene is still intact, so the next save can succeed.
                dispatcher.BeginInvoke(() => RenderFailed?.Invoke(record, e));
                return;
            }

            int? width = null;
            int? height = null;
            string? recordedFlat = null;

            try
            {
                if (BreakFlatten) throw new InvalidOperationException("SNIPWHIZ_VERIFY_BREAK_FLATTEN");

                // Parsed back rather than reusing the live document: an independent
                // object graph, so nothing here races the UI thread.
                var snapshot = ProjectStore.Parse(json);
                var rendered = Flattener.Render(source, snapshot);
                Flattener.Save(flatPath, source, snapshot);

                width = rendered.PixelWidth;
                height = rendered.PixelHeight;
                recordedFlat = Relative(flatPath);
            }
            catch (Exception e)
            {
                dispatcher.BeginInvoke(() => RenderFailed?.Invoke(record, e));
            }

            var saved = record with
            {
                ProjectPath = Relative(projectPath),
                FlatPath = recordedFlat,
                FlatWidth = width,
                FlatHeight = height,
                EditedUtc = DateTimeOffset.UtcNow,
            };

            dispatcher.BeginInvoke(() => Commit(saved));
        }
    }

    /// <summary>The UI-thread half: the database, and the stale thumbnail.</summary>
    private void Commit(CaptureRecord saved)
    {
        store.SetEditPaths(
            saved.Id, saved.ProjectPath!, saved.FlatPath,
            saved.FlatWidth, saved.FlatHeight, saved.EditedUtc!.Value);

        // The cached JPEG is of the un-annotated capture. Leaving it is how "my
        // edits didn't save" gets reported for a save that worked perfectly.
        thumbnails.Remove(saved.Id);

        Saved?.Invoke(saved);
    }

    private string Relative(string absolute) => Path.GetRelativePath(store.Root, absolute);
}

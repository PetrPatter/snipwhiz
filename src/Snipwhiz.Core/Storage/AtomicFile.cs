namespace Snipwhiz.Core.Storage;

/// <summary>
/// Writes a file so that a reader — or the next launch, after a crash — sees either
/// the old contents or the new ones, never a half of each.
///
/// <para>Extracted from <c>ProjectStore.SaveText</c> when settings needed the same
/// guarantee. Two copies of a temp-and-move is how one of them quietly loses its
/// cleanup or its overwrite flag.</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Written beside the target and moved into place, because
    /// <see cref="File.WriteAllText(string, string)"/> truncates first: interrupt it
    /// and the file that is left is a valid, shorter, wrong file.
    /// </summary>
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text);
            Move(temp, path);
        }
        catch
        {
            // The target is untouched at this point, so the only thing to undo is the
            // scratch file. A failure to remove it must not mask the real error.
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Replacing a file that someone has open fails on Windows, because
    /// <c>File.ReadAllText</c> does not ask for share-delete. Antivirus and file-sync
    /// clients hold these files open constantly — <c>Settings.Load</c> already
    /// carries a comment about exactly that — and a settings save that throws
    /// because a scanner was mid-read is a visible error over nothing.
    ///
    /// <para>Bounded, so a genuinely locked file still reports rather than hanging.
    /// Same shape as <c>ThumbnailCache.Publish</c>, which lost a one-in-eight race
    /// to this before it retried.</para>
    /// </summary>
    private static void Move(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }
}

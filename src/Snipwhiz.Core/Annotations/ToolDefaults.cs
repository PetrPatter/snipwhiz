using Snipwhiz.Core.Project;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// What each drawing tool draws with next time.
///
/// <para>Wraps the <b>one</b> <see cref="Settings"/> instance the app loaded at
/// startup rather than loading its own. A second instance would go stale the moment
/// the tray toggled autostart, and whichever saved last would write its own stale
/// copy of everything else back over the file.</para>
/// </summary>
public sealed class ToolDefaults(Settings settings, string root)
{
    /// <summary>
    /// Applies the remembered style to a freshly created annotation.
    ///
    /// <para>Takes the object rather than a name so the fallback is the type's own
    /// constructor default — a highlight's yellow, a rectangle's accent red. A tool
    /// with nothing remembered therefore needs no entry anywhere.</para>
    /// </summary>
    public Annotation Apply(Annotation annotation)
    {
        if (settings.ToolStyles.TryGetValue(ProjectStore.TagOf(annotation), out var style))
            annotation.Style = style;
        return annotation;
    }

    /// <summary>Remembers in memory. Cheap, so it can run on every tick of a slider.</summary>
    public void Remember(Annotation annotation) =>
        settings.ToolStyles[ProjectStore.TagOf(annotation)] = annotation.Style;

    /// <summary>
    /// Writes to disk. Called at the end of a gesture, not on every change — a
    /// slider drag is twenty style changes and does not need twenty file writes.
    /// </summary>
    public void Persist() => settings.Save(root);
}

using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.Core.Project;

/// <summary>Thrown when a <c>.ssproj</c> cannot be read as one.</summary>
public sealed class ProjectFormatException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Reads and writes <c>.ssproj</c>, the sidecar holding a capture's annotations.
///
/// <para><b>JSON, and hand-written rather than attribute-driven.</b> These files
/// get read by a human the first time something is wrong. <c>[JsonPolymorphic]</c>
/// would cover the easy part and then throw on an unknown type discriminator,
/// which is exactly the case that must not throw (see <see cref="UnknownAnnotation"/>) —
/// and intercepting that needs a custom converter anyway, at which point the
/// attribute has bought nothing. Doing it explicitly also keeps the whole wire
/// format legible in one file.</para>
/// </summary>
public static class ProjectStore
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public static void Save(string path, SceneDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written beside the target and moved into place. A crash part-way must not
        // leave a truncated project that parses as an empty scene — losing the
        // annotations is the one failure this format exists to prevent.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                Write(writer, document);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    public static SceneDocument Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);
            return Read(json.RootElement);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new ProjectFormatException($"'{path}' is not a readable Snipwhiz project.", e);
        }
    }

    // ---- write ------------------------------------------------------------

    private static void Write(Utf8JsonWriter w, SceneDocument document)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", SceneDocument.CurrentSchema);
        w.WriteString("captureId", document.CaptureId);

        w.WriteStartObject("document");
        if (document.Crop is { } crop) WriteRect(w, "crop", crop);
        else w.WriteNull("crop");
        w.WriteEndObject();

        w.WriteStartArray("annotations");
        foreach (var annotation in document.Annotations) WriteAnnotation(w, annotation);
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private static void WriteAnnotation(Utf8JsonWriter w, Annotation a)
    {
        // Straight back out as it came in. Re-emitting it from parsed fields would
        // mean understanding it, which is the thing this build cannot do.
        if (a is UnknownAnnotation unknown)
        {
            unknown.Raw.WriteTo(w);
            return;
        }

        w.WriteStartObject();
        w.WriteString("type", TagOf(a));
        w.WriteString("id", a.Id);
        w.WriteNumber("z", a.ZIndex);

        var m = a.Transform;
        w.WriteStartArray("transform");
        foreach (var v in new[] { m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY }) w.WriteNumberValue(v);
        w.WriteEndArray();

        w.WriteStartObject("style");
        w.WriteString("stroke", Hex(a.Style.Stroke));
        w.WriteNumber("width", a.Style.StrokeWidth);
        if (a.Style.Fill is { } fill) w.WriteString("fill", Hex(fill)); else w.WriteNull("fill");
        w.WriteNumber("opacity", a.Style.Opacity);
        w.WriteEndObject();

        w.WriteStartObject("geometry");
        switch (a)
        {
            case RectangleAnnotation r:
                w.WriteNumber("width", r.Size.Width);
                w.WriteNumber("height", r.Size.Height);
                break;
            default:
                throw new ProjectFormatException($"No serializer for {a.GetType().Name}.");
        }
        w.WriteEndObject();

        w.WriteEndObject();
    }

    private static string TagOf(Annotation a) => a switch
    {
        RectangleAnnotation => "rectangle",
        _ => throw new ProjectFormatException($"No type tag for {a.GetType().Name}."),
    };

    // ---- read -------------------------------------------------------------

    private static SceneDocument Read(JsonElement root)
    {
        var document = new SceneDocument
        {
            CaptureId = root.GetProperty("captureId").GetGuid(),
            Schema = root.GetProperty("schema").GetInt32(),
        };

        if (root.TryGetProperty("document", out var doc)
            && doc.TryGetProperty("crop", out var crop)
            && crop.ValueKind is JsonValueKind.Array)
        {
            document.Crop = ReadRect(crop);
        }

        if (root.TryGetProperty("annotations", out var annotations))
        {
            foreach (var element in annotations.EnumerateArray())
                document.Annotations.Add(ReadAnnotation(element));
        }

        return document;
    }

    private static Annotation ReadAnnotation(JsonElement e)
    {
        var tag = e.GetProperty("type").GetString() ?? "";
        var transform = ReadMatrix(e.GetProperty("transform"));
        var style = ReadStyle(e.GetProperty("style"));
        var id = e.GetProperty("id").GetGuid();
        var z = e.GetProperty("z").GetInt32();

        if (tag != "rectangle")
        {
            // Clone: the JsonDocument this came from is disposed before the caller
            // ever touches it.
            return new UnknownAnnotation { Raw = e.Clone(), TypeTag = tag, Id = id, ZIndex = z };
        }

        var geometry = e.GetProperty("geometry");
        return new RectangleAnnotation
        {
            Id = id,
            ZIndex = z,
            Transform = transform,
            Style = style,
            Size = new Size(geometry.GetProperty("width").GetDouble(),
                            geometry.GetProperty("height").GetDouble()),
        };
    }

    private static Matrix ReadMatrix(JsonElement e)
    {
        var v = new double[6];
        var i = 0;
        foreach (var n in e.EnumerateArray())
        {
            if (i == 6) throw new ProjectFormatException("A transform must have exactly six numbers.");
            v[i++] = n.GetDouble();
        }
        if (i != 6) throw new ProjectFormatException("A transform must have exactly six numbers.");
        return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    private static AnnotationStyle ReadStyle(JsonElement e) => new()
    {
        Stroke = ParseHex(e.GetProperty("stroke").GetString()),
        StrokeWidth = e.GetProperty("width").GetDouble(),
        Fill = e.TryGetProperty("fill", out var fill) && fill.ValueKind is JsonValueKind.String
            ? ParseHex(fill.GetString())
            : null,
        Opacity = e.TryGetProperty("opacity", out var o) ? o.GetDouble() : 1,
    };

    // ---- primitives -------------------------------------------------------

    private static void WriteRect(Utf8JsonWriter w, string name, Rect r)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(r.X);
        w.WriteNumberValue(r.Y);
        w.WriteNumberValue(r.Width);
        w.WriteNumberValue(r.Height);
        w.WriteEndArray();
    }

    private static Rect ReadRect(JsonElement e)
    {
        var v = new double[4];
        var i = 0;
        foreach (var n in e.EnumerateArray())
        {
            if (i == 4) throw new ProjectFormatException("A rect must have exactly four numbers.");
            v[i++] = n.GetDouble();
        }
        if (i != 4) throw new ProjectFormatException("A rect must have exactly four numbers.");
        return new Rect(v[0], v[1], v[2], v[3]);
    }

    /// <summary>Alpha is omitted when opaque, which is nearly always, so the common case reads as a familiar CSS colour.</summary>
    private static string Hex(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseHex(string? value)
    {
        var s = (value ?? "").TrimStart('#');
        if (s.Length is not (6 or 8)) throw new ProjectFormatException($"'{value}' is not a colour.");

        var n = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return s.Length == 6
            ? Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n)
            : Color.FromArgb((byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);
    }
}

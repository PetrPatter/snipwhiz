using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;
using Snipwhiz.Core.Storage;

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

    public static void Save(string path, SceneDocument document) =>
        SaveText(path, Serialize(document));

    /// <summary>
    /// The document as it would be written, without writing it.
    ///
    /// <para>Exists so the save pipeline can take a snapshot on the UI thread —
    /// where the scene is only ever touched — and hand a string to the background
    /// thread that writes and renders it. Passing the live
    /// <see cref="SceneDocument"/> instead would let a flatten read a scene the
    /// user is still editing.</para>
    /// </summary>
    public static string Serialize(SceneDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) Write(writer, document);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads back what <see cref="Serialize"/> produced, as an independent object graph.</summary>
    public static SceneDocument Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new ProjectFormatException("Not a readable Snipwhiz project.", e);
        }
    }

    /// <summary>
    /// Atomic, because a crash part-way must not leave a truncated project that
    /// parses as an empty scene — losing the annotations is the one failure this
    /// format exists to prevent.
    /// </summary>
    public static void SaveText(string path, string json) => AtomicFile.WriteAllText(path, json);

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
            // Before the rectangle arm: a pixelate is one, and carries a block size
            // a rectangle knows nothing about. Placed after, this does not compile.
            case PixelateAnnotation p:
                w.WriteNumber("width", p.Size.Width);
                w.WriteNumber("height", p.Size.Height);
                w.WriteNumber("blockSize", p.BlockSize);
                break;
            // Diameter only. The number it reads is its position among the steps and
            // is deliberately not written: a file that carried both would have two
            // answers, and the stored one would win on reopen.
            case StepAnnotation step:
                w.WriteNumber("diameter", step.Diameter);
                break;
            case MagnifyAnnotation magnify:
                w.WriteNumber("width", magnify.Size.Width);
                w.WriteNumber("height", magnify.Size.Height);
                w.WriteNumber("zoom", magnify.Zoom);
                // Absolute, so a magnifier dragged away from its subject reopens
                // still pointing at the subject rather than at itself.
                w.WriteNumber("sourceX", magnify.SourceCentre.X);
                w.WriteNumber("sourceY", magnify.SourceCentre.Y);
                break;
            case BlurAnnotation b:
                w.WriteNumber("width", b.Size.Width);
                w.WriteNumber("height", b.Size.Height);
                w.WriteNumber("radius", b.Radius);
                break;
            // Covers HighlightAnnotation too — it is a rectangle, same geometry.
            case RectangleAnnotation r:
                w.WriteNumber("width", r.Size.Width);
                w.WriteNumber("height", r.Size.Height);
                break;
            case EllipseAnnotation e:
                w.WriteNumber("width", e.Size.Width);
                w.WriteNumber("height", e.Size.Height);
                break;
            // Covers ArrowAnnotation too — it is a line, and stores the same vector.
            case LineAnnotation line:
                w.WriteNumber("dx", line.Delta.X);
                w.WriteNumber("dy", line.Delta.Y);
                break;
            case CalloutAnnotation callout:
                w.WriteString("text", callout.Text);
                w.WriteNumber("fontSize", callout.FontSize);
                w.WriteNumber("tailX", callout.Tail.X);
                w.WriteNumber("tailY", callout.Tail.Y);
                break;
            case TextAnnotation text:
                w.WriteString("text", text.Text);
                w.WriteNumber("fontSize", text.FontSize);
                break;
            default:
                throw new ProjectFormatException($"No serializer for {a.GetType().Name}.");
        }
        w.WriteEndObject();

        w.WriteEndObject();
    }

    /// <summary>
    /// The name a type is stored under — and, because it is the same identity, the
    /// key its remembered tool defaults live under in settings. A second name table
    /// would be a second thing to keep in step.
    /// </summary>
    public static string TagOf(Annotation a) => a switch
    {
        HighlightAnnotation => "highlight",
        // Before the text arm: a callout is one, and carries a tail that text knows
        // nothing about. Placed after, this does not compile.
        CalloutAnnotation => "callout",
        TextAnnotation => "text",
        // Also before the rectangle arm, and for the same reason as arrow below.
        PixelateAnnotation => "pixelate",
        BlurAnnotation => "blur",
        // Its geometry really is a rectangle's — the dim strength lives in the
        // style's opacity — so it needs no arm in the geometry switch below. Only
        // the tag has to come first, or a spotlight reopens as an opaque black box
        // over the whole picture.
        SpotlightAnnotation => "spotlight",
        MagnifyAnnotation => "magnify",
        StepAnnotation => "step",
        RectangleAnnotation => "rectangle",
        EllipseAnnotation => "ellipse",
        // Before the line arm, not after: an arrow is a LineAnnotation and the first
        // matching pattern wins. Putting it second does not save arrows as lines —
        // it does not compile (CS8510, unreachable pattern), which is the right
        // place for this to be caught.
        ArrowAnnotation => "arrow",
        LineAnnotation => "line",
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

        if (tag is not ("rectangle" or "highlight" or "ellipse" or "line" or "arrow" or "text"
            or "pixelate" or "blur" or "spotlight" or "magnify" or "step" or "callout"))
        {
            // Clone: the JsonDocument this came from is disposed before the caller
            // ever touches it.
            return new UnknownAnnotation { Raw = e.Clone(), TypeTag = tag, Id = id, ZIndex = z };
        }

        var geometry = e.GetProperty("geometry");

        return tag switch
        {
            "rectangle" => new RectangleAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Size = ReadSize(geometry),
            },
            // Style comes from the file, not from HighlightAnnotation's default — a
            // highlight the user recoloured must reopen the colour they chose.
            "highlight" => new HighlightAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Size = ReadSize(geometry),
            },
            "pixelate" => new PixelateAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Size = ReadSize(geometry),
                BlockSize = geometry.GetProperty("blockSize").GetDouble(),
            },
            // Style from the file, not from DefaultStyle: a spotlight the user
            // dimmed further must reopen at the strength they chose.
            "spotlight" => new SpotlightAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Size = ReadSize(geometry),
            },
            "step" => new StepAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style,
                Diameter = geometry.GetProperty("diameter").GetDouble(),
            },
            "magnify" => new MagnifyAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Size = ReadSize(geometry),
                Zoom = geometry.GetProperty("zoom").GetDouble(),
                SourceCentre = new Point(
                    geometry.GetProperty("sourceX").GetDouble(),
                    geometry.GetProperty("sourceY").GetDouble()),
            },
            "blur" => new BlurAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Size = ReadSize(geometry),
                Radius = geometry.GetProperty("radius").GetDouble(),
            },
            "ellipse" => new EllipseAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Size = ReadSize(geometry),
            },
            "callout" => new CalloutAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style,
                Text = geometry.GetProperty("text").GetString() ?? "",
                FontSize = geometry.GetProperty("fontSize").GetDouble(),
                Tail = new Vector(
                    geometry.GetProperty("tailX").GetDouble(),
                    geometry.GetProperty("tailY").GetDouble()),
            },
            "text" => new TextAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style,
                Text = geometry.GetProperty("text").GetString() ?? "",
                FontSize = geometry.GetProperty("fontSize").GetDouble(),
            },
            "line" => new LineAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Delta = ReadDelta(geometry),
            },
            _ => new ArrowAnnotation
            {
                Id = id, ZIndex = z, Transform = transform, Style = style, Delta = ReadDelta(geometry),
            },
        };
    }

    private static Vector ReadDelta(JsonElement geometry) =>
        new(geometry.GetProperty("dx").GetDouble(), geometry.GetProperty("dy").GetDouble());

    private static Size ReadSize(JsonElement geometry) =>
        new(geometry.GetProperty("width").GetDouble(), geometry.GetProperty("height").GetDouble());

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

    // Colour text lives in ColourHex, shared with settings. FormatException from a
    // bad colour is already wrapped as a ProjectFormatException by Load and Parse.
    private static string Hex(Color c) => ColourHex.Write(c);

    private static Color ParseHex(string? value) => ColourHex.Parse(value);
}

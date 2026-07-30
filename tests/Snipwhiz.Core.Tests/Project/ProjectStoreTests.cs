using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Project;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Project;

public class ProjectStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    public ProjectStoreTests() => Directory.CreateDirectory(_dir);

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    private static string Fixture(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static SceneDocument Scene(params Annotation[] annotations) => new()
    {
        CaptureId = Guid.Parse("0198f2c1-4a5b-7c6d-8e9f-a0b1c2d3e4f5"),
        Annotations = [.. annotations],
    };

    // ---- round trip -------------------------------------------------------

    [Fact]
    public void A_rectangle_survives_a_round_trip_with_its_transform_and_style()
    {
        var transform = Matrix.Identity;
        transform.Rotate(30);
        transform.Translate(400, 250);

        var original = new RectangleAnnotation
        {
            ZIndex = 3,
            Size = new Size(200, 60),
            Transform = transform,
            Style = new AnnotationStyle
            {
                Stroke = Color.FromRgb(0x3B, 0x82, 0xF6),
                StrokeWidth = 2.5,
                Fill = Color.FromArgb(0x80, 0x2F, 0xB3, 0x44),
                Opacity = 0.5,
            },
        };

        var path = Path_("round-trip.ssproj");
        ProjectStore.Save(path, Scene(original));
        var loaded = Assert.IsType<RectangleAnnotation>(ProjectStore.Load(path).Annotations.Single());

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(3, loaded.ZIndex);
        Assert.Equal(new Size(200, 60), loaded.Size);
        Assert.Equal(original.Transform, loaded.Transform);
        Assert.Equal(original.Style, loaded.Style);
    }

    [Fact]
    public void The_capture_id_crop_and_schema_survive_a_round_trip()
    {
        var scene = Scene();
        scene.Crop = new Rect(10, 20, 800, 600);

        var path = Path_("document.ssproj");
        ProjectStore.Save(path, scene);
        var loaded = ProjectStore.Load(path);

        Assert.Equal(scene.CaptureId, loaded.CaptureId);
        Assert.Equal(new Rect(10, 20, 800, 600), loaded.Crop);
        Assert.Equal(SceneDocument.CurrentSchema, loaded.Schema);
    }

    [Fact]
    public void An_uncropped_document_round_trips_as_uncropped()
    {
        var path = Path_("no-crop.ssproj");
        ProjectStore.Save(path, Scene());
        Assert.Null(ProjectStore.Load(path).Crop);
    }

    [Fact]
    public void An_ellipse_and_a_line_survive_a_round_trip()
    {
        var ellipse = new EllipseAnnotation
        {
            ZIndex = 1,
            Size = new Size(180, 90),
            Transform = new Matrix(1, 0, 0, 1, 200, 150),
            Style = AnnotationStyle.Default with { Fill = Colors.Teal },
        };

        var line = new LineAnnotation
        {
            ZIndex = 2,
            // Negative both ways: a format that stored bounds rather than a vector
            // would come back pointing the other way.
            Delta = new Vector(-140, -60),
            Transform = new Matrix(1, 0, 0, 1, 400, 300),
        };

        var path = Path_("shapes.ssproj");
        ProjectStore.Save(path, Scene(ellipse, line));
        var loaded = ProjectStore.Load(path);

        var loadedEllipse = Assert.IsType<EllipseAnnotation>(loaded.Annotations[0]);
        Assert.Equal(ellipse.Id, loadedEllipse.Id);
        Assert.Equal(new Size(180, 90), loadedEllipse.Size);
        Assert.Equal(Colors.Teal, loadedEllipse.Style.Fill);

        var loadedLine = Assert.IsType<LineAnnotation>(loaded.Annotations[1]);
        Assert.Equal(line.Id, loadedLine.Id);
        Assert.Equal(new Vector(-140, -60), loadedLine.Delta);
        Assert.Equal(2, loadedLine.ZIndex);
    }

    /// <summary>
    /// An arrow is a <see cref="LineAnnotation"/>, so a type switch that tests for a
    /// line first matches it and writes it out as one. It would reopen without its
    /// head and nothing would throw. <c>Assert.IsType</c> is exact-type, which is
    /// what catches it.
    /// </summary>
    [Fact]
    public void An_arrow_round_trips_as_an_arrow_and_not_as_a_line()
    {
        var arrow = new ArrowAnnotation
        {
            Delta = new Vector(-140, -60),
            Transform = new Matrix(1, 0, 0, 1, 400, 300),
            Style = AnnotationStyle.Default with { StrokeWidth = 8 },
        };

        var path = Path_("arrow.ssproj");
        ProjectStore.Save(path, Scene(arrow));

        Assert.Contains("\"arrow\"", File.ReadAllText(path));

        var loaded = Assert.IsType<ArrowAnnotation>(ProjectStore.Load(path).Annotations.Single());
        Assert.Equal(arrow.Id, loaded.Id);
        Assert.Equal(new Vector(-140, -60), loaded.Delta);
        Assert.Equal(8, loaded.Style.StrokeWidth);
    }

    /// <summary>
    /// A pixelate is a rectangle plus a block size, so it can fail in both of the
    /// ways a subclass can: coming back as its base type, and coming back as itself
    /// with the extra number lost. A redaction reopened at the default block size is
    /// a redaction that has changed how much it hides.
    /// </summary>
    [Fact]
    public void A_pixelate_round_trips_with_its_block_size()
    {
        var pixelate = new PixelateAnnotation
        {
            Size = new Size(180, 60),
            BlockSize = 21,
            Transform = new Matrix(1, 0, 0, 1, 250, 150),
        };

        var path = Path_("pixelate.ssproj");
        ProjectStore.Save(path, Scene(pixelate));

        Assert.Contains("\"pixelate\"", File.ReadAllText(path));

        var loaded = Assert.IsType<PixelateAnnotation>(ProjectStore.Load(path).Annotations.Single());
        Assert.Equal(new Size(180, 60), loaded.Size);
        Assert.Equal(21, loaded.BlockSize);
    }

    [Fact]
    public void A_blur_round_trips_with_its_radius()
    {
        var blur = new BlurAnnotation
        {
            Size = new Size(200, 40),
            Radius = 19,
            Transform = new Matrix(1, 0, 0, 1, 120, 90),
        };

        var path = Path_("blur.ssproj");
        ProjectStore.Save(path, Scene(blur));

        Assert.Contains("\"blur\"", File.ReadAllText(path));

        var loaded = Assert.IsType<BlurAnnotation>(ProjectStore.Load(path).Annotations.Single());
        Assert.Equal(new Size(200, 40), loaded.Size);
        Assert.Equal(19, loaded.Radius);
    }

    /// <summary>
    /// A spotlight's geometry really is a rectangle's, so the only thing standing
    /// between it and reopening as an opaque black box over the whole picture is its
    /// type tag.
    /// </summary>
    [Fact]
    public void A_spotlight_round_trips_as_a_spotlight_and_not_as_a_rectangle()
    {
        var spotlight = new SpotlightAnnotation { Size = new Size(160, 120) };
        spotlight.SizeControl = 75;

        var path = Path_("spotlight.ssproj");
        ProjectStore.Save(path, Scene(spotlight));

        var loaded = Assert.IsType<SpotlightAnnotation>(ProjectStore.Load(path).Annotations.Single());
        Assert.Equal(new Size(160, 120), loaded.Size);
        Assert.Equal(75, loaded.SizeControl);
    }

    /// <summary>
    /// The whole reason <see cref="HighlightAnnotation"/> exists as a type: it draws
    /// exactly like a rectangle, so nothing else in the suite would notice it coming
    /// back as one — and a highlight saved as a rectangle can never be recovered.
    /// </summary>
    [Fact]
    public void A_highlight_round_trips_as_a_highlight_and_not_as_a_rectangle()
    {
        var highlight = new HighlightAnnotation
        {
            Size = new Size(220, 40),
            Transform = new Matrix(1, 0, 0, 1, 300, 200),
        };

        var path = Path_("highlight.ssproj");
        ProjectStore.Save(path, Scene(highlight));

        var loaded = Assert.IsType<HighlightAnnotation>(ProjectStore.Load(path).Annotations.Single());
        Assert.Equal(new Size(220, 40), loaded.Size);
    }

    [Fact]
    public void A_recoloured_highlight_reopens_the_colour_it_was_given()
    {
        // Not HighlightAnnotation.DefaultStyle: the constructor's default must not
        // win over what is in the file, or every edit to a highlight is discarded.
        var highlight = new HighlightAnnotation
        {
            Size = new Size(100, 30),
            Style = HighlightAnnotation.DefaultStyle with { Fill = Colors.LimeGreen, Opacity = 0.6 },
        };

        var path = Path_("green-highlight.ssproj");
        ProjectStore.Save(path, Scene(highlight));
        var loaded = (HighlightAnnotation)ProjectStore.Load(path).Annotations.Single();

        Assert.Equal(Colors.LimeGreen, loaded.Style.Fill);
        Assert.Equal(0.6, loaded.Style.Opacity);
    }

    [Fact]
    public void Text_and_its_font_size_survive_a_round_trip()
    {
        var text = new TextAnnotation
        {
            Text = "Line one\nLine two — an em dash, \"quotes\" and a \\ backslash",
            FontSize = 31.5,
            Transform = new Matrix(1, 0, 0, 1, 120, 90),
        };

        var path = Path_("text.ssproj");
        ProjectStore.Save(path, Scene(text));
        var loaded = Assert.IsType<TextAnnotation>(ProjectStore.Load(path).Annotations.Single());

        // The string is the geometry here, so anything the JSON writer mangles is a
        // caption that comes back wrong rather than a shape that comes back wonky.
        Assert.Equal(text.Text, loaded.Text);
        Assert.Equal(31.5, loaded.FontSize);
    }

    [Fact]
    public void A_text_annotation_being_edited_still_saves_its_words()
    {
        // IsBeingEdited suppresses drawing, not storing. If it ever reached the
        // writer, saving while a caption was open would persist an empty plate.
        var text = new TextAnnotation { Text = "still here", IsBeingEdited = true };

        var path = Path_("editing.ssproj");
        ProjectStore.Save(path, Scene(text));

        Assert.Equal("still here", ((TextAnnotation)ProjectStore.Load(path).Annotations.Single()).Text);
    }

    // ---- the golden file --------------------------------------------------

    /// <summary>
    /// Asserts scene equality against known values, not merely that loading did not
    /// throw. A no-throw assertion passes against a loader that returns an empty
    /// scene, which is the failure this is meant to catch.
    /// </summary>
    [Fact]
    public void The_committed_golden_file_still_loads_with_the_values_it_was_written_with()
    {
        var loaded = ProjectStore.Load(Fixture("golden-v1.ssproj"));

        Assert.Equal(1, loaded.Schema);
        Assert.Equal(Guid.Parse("0198f2c1-4a5b-7c6d-8e9f-a0b1c2d3e4f5"), loaded.CaptureId);
        Assert.Equal(new Rect(10, 20, 800, 600), loaded.Crop);
        Assert.Equal(2, loaded.Annotations.Count);

        var first = Assert.IsType<RectangleAnnotation>(loaded.Annotations[0]);
        Assert.Equal(Guid.Parse("0198f2c1-0000-7000-8000-000000000001"), first.Id);
        Assert.Equal(0, first.ZIndex);
        Assert.Equal(new Size(180, 90), first.Size);
        Assert.Equal(new Matrix(1, 0, 0, 1, 120, 80), first.Transform);
        Assert.Equal(Color.FromRgb(0xE5, 0x48, 0x4D), first.Style.Stroke);
        Assert.Equal(4, first.Style.StrokeWidth);
        Assert.Null(first.Style.Fill);

        var second = Assert.IsType<RectangleAnnotation>(loaded.Annotations[1]);
        Assert.Equal(1, second.ZIndex);
        Assert.Equal(new Size(200, 60), second.Size);
        Assert.Equal(Color.FromArgb(0x80, 0x2F, 0xB3, 0x44), second.Style.Fill);
        Assert.Equal(0.5, second.Style.Opacity);
        // A rotation, not an axis-aligned placement — the golden file covers the
        // matrix path that a hand-written identity would not.
        Assert.Equal(0.8660254, second.Transform.M11, 6);
    }

    [Fact]
    public void The_golden_file_still_says_what_it_said_after_a_save()
    {
        var path = Path_("golden-resaved.ssproj");
        ProjectStore.Save(path, ProjectStore.Load(Fixture("golden-v1.ssproj")));

        var reloaded = ProjectStore.Load(path);
        var original = ProjectStore.Load(Fixture("golden-v1.ssproj"));

        Assert.Equal(original.CaptureId, reloaded.CaptureId);
        Assert.Equal(original.Crop, reloaded.Crop);
        Assert.Equal(original.Annotations.Count, reloaded.Annotations.Count);
        for (var i = 0; i < original.Annotations.Count; i++)
        {
            var a = (RectangleAnnotation)original.Annotations[i];
            var b = (RectangleAnnotation)reloaded.Annotations[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Size, b.Size);
            Assert.Equal(a.Transform, b.Transform);
            Assert.Equal(a.Style, b.Style);
        }
    }

    // ---- forward compatibility -------------------------------------------

    [Fact]
    public void An_annotation_type_this_build_does_not_know_survives_load_and_save()
    {
        var loaded = ProjectStore.Load(Fixture("unknown-type.ssproj"));

        Assert.Equal(2, loaded.Annotations.Count);
        var unknown = Assert.IsType<UnknownAnnotation>(loaded.Annotations[1]);
        Assert.Equal("hologram", unknown.TypeTag);

        var path = Path_("unknown-resaved.ssproj");
        ProjectStore.Save(path, loaded);

        // Re-read as raw JSON: the point is what landed on disk, not what the
        // object model believes.
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var written = json.RootElement.GetProperty("annotations")[1];

        Assert.Equal("hologram", written.GetProperty("type").GetString());
        Assert.Equal(42, written.GetProperty("geometry").GetProperty("radius").GetInt32());
        Assert.Equal("iridescent", written.GetProperty("geometry").GetProperty("shimmer").GetString());
        Assert.Equal(3, written.GetProperty("geometry").GetProperty("nested").GetProperty("keep")[2].GetInt32());
    }

    [Fact]
    public void An_unknown_annotation_is_inert_rather_than_selectable()
    {
        var unknown = ProjectStore.Load(Fixture("unknown-type.ssproj")).Annotations[1];

        Assert.False(unknown.HitTest(new Point(300, 200), 50));
        Assert.True(unknown.Bounds.IsEmpty);
    }

    // ---- failure paths ----------------------------------------------------

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        var path = Path_("clean.ssproj");
        ProjectStore.Save(path, Scene(new RectangleAnnotation { Size = new Size(10, 10) }));

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_file_that_is_not_a_project_fails_as_a_format_error()
    {
        var path = Path_("garbage.ssproj");
        File.WriteAllText(path, "this is not json");

        Assert.Throws<ProjectFormatException>(() => ProjectStore.Load(path));
    }

    [Fact]
    public void A_truncated_project_fails_rather_than_loading_as_an_empty_scene()
    {
        // The failure the atomic write exists to prevent. If this ever returns a
        // scene with no annotations instead of throwing, a half-written file looks
        // exactly like a project the user emptied.
        var path = Path_("truncated.ssproj");
        File.WriteAllText(path, """{ "schema": 1, "captureId": "0198f2c1-4a5b-7c6d-8e9f-a0b1c2d3e4f5", "annota""");

        Assert.Throws<ProjectFormatException>(() => ProjectStore.Load(path));
    }

    [Fact]
    public void A_transform_with_the_wrong_number_of_components_is_rejected()
    {
        var path = Path_("short-transform.ssproj");
        File.WriteAllText(path, """
            { "schema": 1, "captureId": "0198f2c1-4a5b-7c6d-8e9f-a0b1c2d3e4f5",
              "annotations": [ { "type": "rectangle", "id": "0198f2c1-0000-7000-8000-000000000001",
                "z": 0, "transform": [1, 0, 0],
                "style": { "stroke": "#FFFFFF", "width": 1, "fill": null, "opacity": 1 },
                "geometry": { "width": 1, "height": 1 } } ] }
            """);

        Assert.Throws<ProjectFormatException>(() => ProjectStore.Load(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

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

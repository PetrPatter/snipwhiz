# Snipwhiz Capture Core Implementation Plan

**Goal:** Press a hotkey, drag a region, and the image lands on the clipboard, on disk, and in a SQLite history database — correctly, on multi-monitor setups with mismatched DPI.

**Architecture:** Freeze-first capture. One `BitBlt` over the whole virtual desktop at hotkey time produces an immutable `FrozenDesktop`; opaque per-monitor overlay windows render slices of it at exactly 1:1 physical pixels; the final capture is a crop of that same bitmap, so displayed pixels and saved pixels can never disagree. All geometry is kept in virtual-screen physical pixels and converted to DIPs only at the WPF boundary.

**Tech Stack:** .NET 10 · WPF + WinForms (`NotifyIcon`) · `Microsoft.Windows.CsWin32` (P/Invoke generation) · `Microsoft.Data.Sqlite` · `System.Drawing.Common` (PNG encode) · xUnit

**Spec:** `docs/design/2026-07-25-capture-core-design.md` — read §4 before starting.

## Global Constraints

Every task's requirements implicitly include this section.

- **Platform floor:** Windows 11 22H2, build 22621. TFM `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` `10.0.22621.0`. No Windows 10 conditional paths anywhere.
- **Product name:** `Snipwhiz`. Root namespace `Snipwhiz`. Install/data path `%LOCALAPPDATA%\Snipwhiz\`.
- **Canonical coordinate space:** virtual-screen **physical pixels**, origin at the virtual desktop top-left (**can be negative**). `Snipwhiz.Core` speaks only this. DIP conversion happens at the WPF boundary and nowhere else.
- **DPI conversion helpers take `scale` as an injected parameter.** They never call `GetDpiForWindow` internally. This is what makes them pure and testable.
- **Overlay windows are positioned with `SetWindowPos` in physical pixels**, never WPF `Left`/`Top` (which are DIPs).
- **The frozen bitmap renders at exactly 1:1 physical pixels.** See Task 8.
- **Zero new third-party dependencies for *capture*.** `System.Drawing.Common` (PNG encode) and `Microsoft.Data.Sqlite` are permitted and are the only additions beyond CsWin32/xUnit.
- **`BitBlt` flags are `SRCCOPY | CAPTUREBLT`.** Without `CAPTUREBLT` the capture silently drops layered content (menu/tooltip shadows).
- **No GDI handle is leaked on any path, including exceptions.** This is a tray app that runs for weeks. Method-scoped handles use `try`/`finally` with the correct teardown order (deselect the bitmap from the DC → delete the bitmap → delete the DC → release the screen DC); deleting a bitmap still selected into a DC is undefined behaviour. Any handle stored in a *field* — outliving a single method — must be `SafeHandle`-wrapped.
- **Notifications are `NotifyIcon.ShowBalloonTip`, never toasts.** Toasts from unpackaged apps are silently dropped until the spec 3 installer exists.
- **Clipboard before disk, always.** The clipboard is what the user is about to paste; it must not block on I/O.
- **Default hotkeys are `Ctrl+Shift+1` (region) and `Ctrl+Shift+2` (fullscreen).** PrintScreen is opt-in only, with explicit consent.

## File Structure

```
Snipwhiz.sln
src/
  Snipwhiz.Core/                       net10.0-windows10.0.22621.0, NO WPF reference
    Snipwhiz.Core.csproj
    NativeMethods.txt                  CsWin32 API list
    Geometry/
      PixelRect.cs                     rect in virtual physical px + set ops
      MonitorInfo.cs                   one display: bounds, scale, device name
      VirtualDesktop.cs                the monitor set; bounds, hit-testing, coverage
      Dpi.cs                           pure physical<->DIP conversion
    Monitors/
      MonitorEnumerator.cs             EnumDisplayMonitors -> MonitorInfo[]
    Capture/
      CursorState.cs                   cursor recorded at grab time
      FrozenDesktop.cs                 immutable BGRA buffer + geometry + crop
      IDesktopGrabber.cs               seam for testing and future DXGI swap
      BitBltGrabber.cs                 the real grab
    Imaging/
      PngEncoder.cs                    BGRA -> PNG bytes
    Clipboard/
      ClipboardWriter.cs               PNG + CF_DIBV5 + CF_DIB in one sequence
    Storage/
      CaptureRecord.cs                 one row
      LibraryDb.cs                     SQLite open/migrate/insert/query
      CaptureStore.cs                  writes PNG + inserts row
    Hotkeys/
      HotkeyId.cs                      enum
      HotkeyService.cs                 message-only window + RegisterHotKey
    PrintScreenTakeover.cs             registry read/write + consent state
    Settings.cs                        3-field JSON
    CapturePipeline.cs                 crop -> clipboard -> disk, in that order

  Snipwhiz.App/                        WPF + WinForms
    Snipwhiz.App.csproj
    app.manifest                       PerMonitorV2
    App.xaml / App.xaml.cs             single instance, composition root
    TrayHost.cs                        NotifyIcon, menu, balloons, autostart
    CaptureSession.cs                  owns N overlays, activation, Esc, watchdog
    OverlayWindow.xaml / .cs           one per monitor, opaque, 1:1 render
    SelectionController.cs             drag state in virtual px, shared by overlays
    Loupe.xaml / .cs                   magnifier: pixel grid, hex, coords

tests/
  Snipwhiz.Core.Tests/
    Snipwhiz.Core.Tests.csproj
    Geometry/PixelRectTests.cs
    Geometry/DpiTests.cs
    Geometry/VirtualDesktopTests.cs
    Capture/FrozenDesktopCropTests.cs
    Imaging/PngEncoderTests.cs
    Storage/CaptureStoreTests.cs
    LatencyProbe.cs                    Task 2 measurement gate
```

**Task order is dependency order with risk pulled forward.** Tasks 1–7 build a working headless capture pipeline testable without any UI. Task 7 produces the first runnable app. The overlay — the riskiest UI work — lands on a proven foundation in Tasks 8–10.

---

### Task 1: Solution scaffold and pure geometry

**Files:**
- Create: `Snipwhiz.sln`, `src/Snipwhiz.Core/Snipwhiz.Core.csproj`, `tests/Snipwhiz.Core.Tests/Snipwhiz.Core.Tests.csproj`
- Create: `src/Snipwhiz.Core/Geometry/PixelRect.cs`, `Dpi.cs`, `MonitorInfo.cs`, `VirtualDesktop.cs`
- Test: `tests/Snipwhiz.Core.Tests/Geometry/PixelRectTests.cs`, `DpiTests.cs`, `VirtualDesktopTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `PixelRect(int X, int Y, int Width, int Height)` with `Right`, `Bottom`, `IsEmpty`, `Contains(int,int)`, `Intersect(PixelRect)`, `ClampTo(PixelRect)`, and `static PixelRect FromCorners(int x1,int y1,int x2,int y2)`
  - `Dpi.PhysicalToDip(int physical, double scale) -> double`, `Dpi.DipToPhysical(double dip, double scale) -> int`
  - `MonitorInfo(string DeviceName, PixelRect Bounds, double Scale, bool IsPrimary)`
  - `VirtualDesktop` with `Monitors`, `Bounds`, `MonitorAt(int,int) -> MonitorInfo?`, `IsCovered(int,int) -> bool`, `static FromMonitors(IEnumerable<MonitorInfo>)`

- [ ] **Step 1: Create the solution and projects**

```bash
cd C:/Projects/ScreenShotTool
dotnet new sln -n Snipwhiz
dotnet new classlib -o src/Snipwhiz.Core -n Snipwhiz.Core
dotnet new xunit  -o tests/Snipwhiz.Core.Tests -n Snipwhiz.Core.Tests
dotnet sln add src/Snipwhiz.Core/Snipwhiz.Core.csproj tests/Snipwhiz.Core.Tests/Snipwhiz.Core.Tests.csproj
dotnet add tests/Snipwhiz.Core.Tests/Snipwhiz.Core.Tests.csproj reference src/Snipwhiz.Core/Snipwhiz.Core.csproj
```

- [ ] **Step 2: Set the TFM and platform floor on both projects**

Replace the `<PropertyGroup>` in `src/Snipwhiz.Core/Snipwhiz.Core.csproj`:

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
  <SupportedOSPlatformVersion>10.0.22621.0</SupportedOSPlatformVersion>
  <RootNamespace>Snipwhiz.Core</RootNamespace>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <LangVersion>latest</LangVersion>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
```

Apply the same `TargetFramework` and `SupportedOSPlatformVersion` to `tests/Snipwhiz.Core.Tests/Snipwhiz.Core.Tests.csproj`.

- [ ] **Step 3: Write the failing geometry tests**

Create `tests/Snipwhiz.Core.Tests/Geometry/PixelRectTests.cs`:

```csharp
using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Geometry;

public class PixelRectTests
{
    [Theory]
    // drag right-down, left-up, right-up, left-down — all yield the same rect
    [InlineData(10, 20, 110, 220)]
    [InlineData(110, 220, 10, 20)]
    [InlineData(110, 20, 10, 220)]
    [InlineData(10, 220, 110, 20)]
    public void FromCorners_normalizes_every_drag_direction(int x1, int y1, int x2, int y2)
    {
        var r = PixelRect.FromCorners(x1, y1, x2, y2);
        Assert.Equal(new PixelRect(10, 20, 100, 200), r);
    }

    [Fact]
    public void FromCorners_handles_negative_virtual_origin()
    {
        // a monitor left of and above primary
        var r = PixelRect.FromCorners(-1920, -180, -1000, 500);
        Assert.Equal(new PixelRect(-1920, -180, 920, 680), r);
    }

    [Fact]
    public void FromCorners_of_a_single_point_is_empty()
    {
        var r = PixelRect.FromCorners(50, 50, 50, 50);
        Assert.Equal(0, r.Width);
        Assert.Equal(0, r.Height);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void One_pixel_rect_is_not_empty()
    {
        var r = PixelRect.FromCorners(50, 50, 51, 51);
        Assert.Equal(new PixelRect(50, 50, 1, 1), r);
        Assert.False(r.IsEmpty);
    }

    [Fact]
    public void Intersect_returns_the_overlap()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 50, 100, 100);
        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Intersect(b));
    }

    [Fact]
    public void Intersect_of_disjoint_rects_is_empty()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(100, 100, 10, 10);
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void ClampTo_pulls_a_rect_inside_the_bounds()
    {
        // bounds spans x -1920..1920, r spans x -2500..-1500 => overlap is -1920..-1500
        var bounds = new PixelRect(-1920, 0, 3840, 1080);
        var r = new PixelRect(-2500, -50, 1000, 2000);
        Assert.Equal(new PixelRect(-1920, 0, 420, 1080), r.ClampTo(bounds));
    }
}
```

Create `tests/Snipwhiz.Core.Tests/Geometry/DpiTests.cs`:

```csharp
using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Geometry;

public class DpiTests
{
    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(150, 1.5, 100)]
    [InlineData(225, 2.25, 100)]
    public void PhysicalToDip_divides_by_scale(int physical, double scale, double expected)
        => Assert.Equal(expected, Dpi.PhysicalToDip(physical, scale), 6);

    [Theory]
    [InlineData(100.0, 1.5, 150)]
    [InlineData(100.0, 2.25, 225)]
    public void DipToPhysical_multiplies_and_rounds(double dip, double scale, int expected)
        => Assert.Equal(expected, Dpi.DipToPhysical(dip, scale));

    [Fact]
    public void DipToPhysical_rounds_half_away_from_zero()
    {
        // 67.0 DIP at 150% is exactly 100.5 physical px. Banker's rounding would
        // give 100; away-from-zero gives 101. Half-pixel drift on a monitor edge
        // is what makes a capture one row short.
        Assert.Equal(101, Dpi.DipToPhysical(67.0, 1.5));
    }

    [Fact]
    public void Round_trip_at_awkward_scale_stays_within_one_pixel()
    {
        for (int px = 0; px < 2000; px++)
        {
            var back = Dpi.DipToPhysical(Dpi.PhysicalToDip(px, 2.25), 2.25);
            Assert.InRange(back - px, -1, 1);
        }
    }
}
```

Create `tests/Snipwhiz.Core.Tests/Geometry/VirtualDesktopTests.cs`:

```csharp
using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Geometry;

public class VirtualDesktopTests
{
    // Primary 1920x1080 at 100%, secondary 2560x1440 at 150% placed LEFT and ABOVE.
    // This is the layout that breaks naive implementations: negative origin.
    private static VirtualDesktop MixedDpiNegativeOrigin() => VirtualDesktop.FromMonitors(new[]
    {
        new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 1.0, true),
        new MonitorInfo(@"\\.\DISPLAY2", new PixelRect(-2560, -360, 2560, 1440), 1.5, false),
    });

    [Fact]
    public void Bounds_is_the_union_and_may_have_a_negative_origin()
    {
        var d = MixedDpiNegativeOrigin();
        Assert.Equal(new PixelRect(-2560, -360, 4480, 1440), d.Bounds);
    }

    [Fact]
    public void Single_monitor_bounds_equal_that_monitor()
    {
        var d = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.0, true),
        });
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), d.Bounds);
    }

    [Fact]
    public void MonitorAt_finds_the_monitor_under_a_negative_coordinate()
    {
        var d = MixedDpiNegativeOrigin();
        Assert.Equal(@"\\.\DISPLAY2", d.MonitorAt(-1000, 0)!.Value.DeviceName);
        Assert.Equal(1.5, d.MonitorAt(-1000, 0)!.Value.Scale);
    }

    [Fact]
    public void MonitorAt_returns_null_in_an_uncovered_gap()
    {
        var d = MixedDpiNegativeOrigin();
        // top-right of the bounding box: inside Bounds, covered by no display
        Assert.Null(d.MonitorAt(1000, -200));
        Assert.False(d.IsCovered(1000, -200));
    }

    [Fact]
    public void IsCovered_is_true_inside_a_monitor()
    {
        var d = MixedDpiNegativeOrigin();
        Assert.True(d.IsCovered(10, 10));
        Assert.True(d.IsCovered(-2560, -360));       // inclusive top-left
        Assert.False(d.IsCovered(1920, 0));          // exclusive right edge
    }

    [Fact]
    public void Three_monitors_at_three_scales_all_resolve()
    {
        var d = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(0, 0, 1920, 1080), 1.0, true),
            new MonitorInfo("B", new PixelRect(1920, 0, 2560, 1440), 1.5, false),
            new MonitorInfo("C", new PixelRect(4480, 0, 3840, 2160), 2.25, false),
        });
        Assert.Equal(1.0,  d.MonitorAt(100, 100)!.Value.Scale);
        Assert.Equal(1.5,  d.MonitorAt(2000, 100)!.Value.Scale);
        Assert.Equal(2.25, d.MonitorAt(5000, 100)!.Value.Scale);
        Assert.Equal(new PixelRect(0, 0, 8320, 2160), d.Bounds);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet test tests/Snipwhiz.Core.Tests
```
Expected: FAIL — `PixelRect`, `Dpi`, `MonitorInfo`, `VirtualDesktop` do not exist.

- [ ] **Step 5: Implement the geometry types**

Create `src/Snipwhiz.Core/Geometry/PixelRect.cs`:

```csharp
namespace Snipwhiz.Core.Geometry;

/// <summary>A rectangle in virtual-screen physical pixels. X and Y may be negative.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromCorners(int x1, int y1, int x2, int y2)
        => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>Left/top edges are inclusive, right/bottom exclusive.</summary>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= x || b <= y ? default : new PixelRect(x, y, r - x, b - y);
    }

    public PixelRect ClampTo(PixelRect bounds) => Intersect(bounds);
}
```

Create `src/Snipwhiz.Core/Geometry/Dpi.cs`:

```csharp
namespace Snipwhiz.Core.Geometry;

/// <summary>
/// Physical-pixel to DIP conversion. Scale is injected, never read from the OS here —
/// that is what keeps these pure and testable.
/// </summary>
public static class Dpi
{
    public static double PhysicalToDip(int physical, double scale) => physical / scale;

    public static int DipToPhysical(double dip, double scale)
        => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
}
```

Create `src/Snipwhiz.Core/Geometry/MonitorInfo.cs`:

```csharp
namespace Snipwhiz.Core.Geometry;

/// <param name="Bounds">Physical pixels in virtual-screen space.</param>
/// <param name="Scale">1.0 = 100%, 1.5 = 150%, 2.25 = 225%.</param>
public readonly record struct MonitorInfo(
    string DeviceName,
    PixelRect Bounds,
    double Scale,
    bool IsPrimary);
```

Create `src/Snipwhiz.Core/Geometry/VirtualDesktop.cs`:

```csharp
namespace Snipwhiz.Core.Geometry;

/// <summary>
/// The set of displays and their union. The union may be larger than the covered
/// area: an L-shaped or offset arrangement leaves gaps that belong to no display.
/// </summary>
public sealed class VirtualDesktop
{
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public PixelRect Bounds { get; }

    private VirtualDesktop(IReadOnlyList<MonitorInfo> monitors, PixelRect bounds)
    {
        Monitors = monitors;
        Bounds = bounds;
    }

    public static VirtualDesktop FromMonitors(IEnumerable<MonitorInfo> monitors)
    {
        var list = monitors.ToArray();
        if (list.Length == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));

        var left   = list.Min(m => m.Bounds.X);
        var top    = list.Min(m => m.Bounds.Y);
        var right  = list.Max(m => m.Bounds.Right);
        var bottom = list.Max(m => m.Bounds.Bottom);

        return new VirtualDesktop(list, new PixelRect(left, top, right - left, bottom - top));
    }

    public MonitorInfo? MonitorAt(int x, int y)
    {
        foreach (var m in Monitors)
            if (m.Bounds.Contains(x, y)) return m;
        return null;
    }

    public bool IsCovered(int x, int y) => MonitorAt(x, y) is not null;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/Snipwhiz.Core.Tests
```
Expected: PASS, 20 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add solution scaffold and pure virtual-desktop geometry

Coordinate math is the highest-risk part of this spec and it is pure, so
it gets tests before anything touches Win32. Covers negative virtual
origins, mixed scale factors, and uncovered gaps in non-rectangular
monitor arrangements."
```

---

### Task 2: BitBlt grab and the latency gate

**Files:**
- Create: `src/Snipwhiz.Core/NativeMethods.txt`
- Create: `src/Snipwhiz.Core/Monitors/MonitorEnumerator.cs`
- Create: `src/Snipwhiz.Core/Capture/CursorState.cs`, `FrozenDesktop.cs`, `IDesktopGrabber.cs`, `BitBltGrabber.cs`
- Create: `tests/Snipwhiz.Core.Tests/LatencyProbe.cs`
- Modify: `src/Snipwhiz.Core/Snipwhiz.Core.csproj`

**Interfaces:**
- Consumes: `VirtualDesktop`, `MonitorInfo`, `PixelRect` (Task 1).
- Produces:
  - `MonitorEnumerator.Enumerate() -> IReadOnlyList<MonitorInfo>`
  - `CursorState(bool Visible, int X, int Y, int HotspotX, int HotspotY, nint Handle)`
  - `FrozenDesktop` — `Desktop`, `Bounds`, `Width`, `Height`, `Bgra` (top-down BGRA32, stride `Width*4`), `Cursor`
  - `IDesktopGrabber.Grab() -> FrozenDesktop`; `BitBltGrabber : IDesktopGrabber`

> **This task contains the spec's §4.5 gate. Do not skip Step 7.** If the measured grab exceeds 120 ms on the worst configuration you ship to, stop and escalate to DXGI Desktop Duplication before building the overlay — that is a different architecture, not a tuning pass.

- [ ] **Step 1: Add CsWin32 and the native API list**

```bash
dotnet add src/Snipwhiz.Core package Microsoft.Windows.CsWin32
```

Create `src/Snipwhiz.Core/NativeMethods.txt`:

```
EnumDisplayMonitors
GetMonitorInfo
GetDpiForMonitor
MONITOR_DPI_TYPE
GetDC
ReleaseDC
CreateCompatibleDC
CreateCompatibleBitmap
SelectObject
DeleteObject
DeleteDC
BitBlt
GetDIBits
BITMAPINFO
BI_COMPRESSION
DIB_USAGE
GetCursorInfo
GetCursorPos
GetIconInfo
```

Add to `Snipwhiz.Core.csproj`:

```xml
<ItemGroup>
  <AdditionalFiles Include="NativeMethods.txt" />
</ItemGroup>
```

- [ ] **Step 2: Implement monitor enumeration**

Create `src/Snipwhiz.Core/Monitors/MonitorEnumerator.cs`:

```csharp
using System.Runtime.InteropServices;
using Snipwhiz.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Snipwhiz.Core.Monitors;

public static class MonitorEnumerator
{
    /// <summary>
    /// Physical-pixel bounds per display. Requires the process to be PerMonitorV2
    /// DPI aware, otherwise Windows lies about the bounds.
    /// </summary>
    public static unsafe IReadOnlyList<MonitorInfo> Enumerate()
    {
        var found = new List<MonitorInfo>();
        var handle = GCHandle.Alloc(found);
        try
        {
            PInvoke.EnumDisplayMonitors(default, (RECT?)null, &Callback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        if (found.Count == 0) throw new InvalidOperationException("No displays were enumerated.");
        return found;
    }

    [UnmanagedCallersOnly]
    private static unsafe BOOL Callback(HMONITOR monitor, HDC _, RECT* __, LPARAM lparam)
    {
        var list = (List<MonitorInfo>)GCHandle.FromIntPtr(lparam)!.Target!;

        var mi = new MONITORINFOEXW { monitorInfo = { cbSize = (uint)sizeof(MONITORINFOEXW) } };
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&mi)) return true;

        var r = mi.monitorInfo.rcMonitor;

        // MDT_EFFECTIVE_DPI is the scale the user actually chose in Settings.
        uint dpiX = 96, dpiY = 96;
        PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);

        list.Add(new MonitorInfo(
            DeviceName: mi.szDevice.ToString(),
            Bounds: new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top),
            Scale: dpiX / 96.0,
            IsPrimary: (mi.monitorInfo.dwFlags & 1u) != 0));   // MONITORINFOF_PRIMARY

        return true;
    }
}
```

- [ ] **Step 3: Implement the frozen-desktop value type**

Create `src/Snipwhiz.Core/Capture/CursorState.cs`:

```csharp
namespace Snipwhiz.Core.Capture;

/// <summary>
/// Cursor at the instant of the grab. Freeze-first makes this unrecoverable
/// afterwards, so it is recorded even though spec 1 renders nothing.
/// Position is virtual-screen physical pixels.
/// </summary>
public readonly record struct CursorState(
    bool Visible, int X, int Y, int HotspotX, int HotspotY, nint Handle)
{
    public static readonly CursorState None = new(false, 0, 0, 0, 0, 0);
}
```

Create `src/Snipwhiz.Core/Capture/FrozenDesktop.cs`:

```csharp
using Snipwhiz.Core.Geometry;

namespace Snipwhiz.Core.Capture;

/// <summary>
/// An immutable snapshot of the whole virtual desktop. Displayed pixels and
/// saved pixels both come from here, so they cannot disagree.
/// </summary>
public sealed class FrozenDesktop
{
    public VirtualDesktop Desktop { get; }
    public CursorState Cursor { get; }
    /// <summary>Top-down BGRA32. Stride is Width * 4.</summary>
    public byte[] Bgra { get; }

    public PixelRect Bounds => Desktop.Bounds;
    public int Width => Desktop.Bounds.Width;
    public int Height => Desktop.Bounds.Height;

    public FrozenDesktop(VirtualDesktop desktop, byte[] bgra, CursorState cursor)
    {
        var expected = (long)desktop.Bounds.Width * desktop.Bounds.Height * 4;
        if (bgra.LongLength != expected)
            throw new ArgumentException($"Expected {expected} bytes, got {bgra.LongLength}.", nameof(bgra));

        Desktop = desktop;
        Bgra = bgra;
        Cursor = cursor;
    }
}
```

Create `src/Snipwhiz.Core/Capture/IDesktopGrabber.cs`:

```csharp
namespace Snipwhiz.Core.Capture;

/// <summary>
/// Seam for testing and for swapping in DXGI Desktop Duplication if the
/// latency gate in the plan's Task 2 fails.
/// </summary>
public interface IDesktopGrabber
{
    FrozenDesktop Grab();
}
```

- [ ] **Step 4: Implement the BitBlt grabber**

Create `src/Snipwhiz.Core/Capture/BitBltGrabber.cs`:

```csharp
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Monitors;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Snipwhiz.Core.Capture;

public sealed class BitBltGrabber : IDesktopGrabber
{
    // CAPTUREBLT is required: without it the capture silently drops layered
    // content such as menu and tooltip shadows. It costs a brief flicker on
    // some configurations, which we accept — missing content is a correctness
    // bug, flicker is cosmetic.
    private const ROP_CODE Rop = (ROP_CODE)(0x00CC0020 /*SRCCOPY*/ | 0x40000000 /*CAPTUREBLT*/);

    public unsafe FrozenDesktop Grab()
    {
        var desktop = VirtualDesktop.FromMonitors(MonitorEnumerator.Enumerate());
        var b = desktop.Bounds;

        var cursor = ReadCursor();

        HDC screen = default;
        HDC mem = default;
        HBITMAP bmp = default;
        HGDIOBJ previous = default;
        try
        {
            screen = PInvoke.GetDC(default);
            if (screen.IsNull) throw new InvalidOperationException("GetDC(NULL) failed.");

            mem = PInvoke.CreateCompatibleDC(screen);
            if (mem.IsNull) throw new InvalidOperationException("CreateCompatibleDC failed.");

            bmp = PInvoke.CreateCompatibleBitmap(screen, b.Width, b.Height);
            if (bmp.IsNull) throw new InvalidOperationException("CreateCompatibleBitmap failed.");

            previous = PInvoke.SelectObject(mem, bmp);

            if (!PInvoke.BitBlt(mem, 0, 0, b.Width, b.Height, screen, b.X, b.Y, Rop))
                throw new InvalidOperationException("BitBlt failed.");

            var pixels = ReadPixels(mem, bmp, b.Width, b.Height);
            return new FrozenDesktop(desktop, pixels, cursor);
        }
        finally
        {
            // Every handle released on every path — this process runs for weeks.
            if (!previous.IsNull) PInvoke.SelectObject(mem, previous);
            if (!bmp.IsNull) PInvoke.DeleteObject(bmp);
            if (!mem.IsNull) PInvoke.DeleteDC(mem);
            if (!screen.IsNull) PInvoke.ReleaseDC(default, screen);
        }
    }

    private static unsafe byte[] ReadPixels(HDC dc, HBITMAP bmp, int width, int height)
    {
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,             // negative => top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (uint)BI_COMPRESSION.BI_RGB,
            }
        };

        var buffer = new byte[(long)width * height * 4];
        fixed (byte* p = buffer)
        {
            var scanned = PInvoke.GetDIBits(dc, bmp, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS);
            if (scanned == 0) throw new InvalidOperationException("GetDIBits failed.");
        }

        // BitBlt from the screen leaves the alpha channel undefined. Force opaque
        // so downstream PNG and CF_DIBV5 consumers do not render it transparent.
        for (long i = 3; i < buffer.LongLength; i += 4) buffer[i] = 255;

        return buffer;
    }

    private static unsafe CursorState ReadCursor()
    {
        var ci = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(&ci) || ci.flags != CURSORINFO_FLAGS.CURSOR_SHOWING)
            return CursorState.None;

        int hotX = 0, hotY = 0;
        var ii = default(ICONINFO);
        if (PInvoke.GetIconInfo((HICON)ci.hCursor.Value, &ii))
        {
            hotX = (int)ii.xHotspot;
            hotY = (int)ii.yHotspot;
            if (!ii.hbmMask.IsNull) PInvoke.DeleteObject(ii.hbmMask);
            if (!ii.hbmColor.IsNull) PInvoke.DeleteObject(ii.hbmColor);
        }

        return new CursorState(true, ci.ptScreenPos.X, ci.ptScreenPos.Y, hotX, hotY, ci.hCursor.Value);
    }
}
```

- [ ] **Step 5: Write the latency probe**

Create `tests/Snipwhiz.Core.Tests/LatencyProbe.cs`:

```csharp
using System.Diagnostics;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Monitors;
using Xunit;
using Xunit.Abstractions;

namespace Snipwhiz.Core.Tests;

/// <summary>
/// The spec's section 4.5 gate. If the median exceeds 120 ms on the worst
/// monitor configuration we ship to, STOP and switch the freeze path to DXGI
/// Desktop Duplication before building the overlay.
/// </summary>
public class LatencyProbe(ITestOutputHelper output)
{
    [Fact]
    public void Grab_completes_within_the_paint_budget()
    {
        var grabber = new BitBltGrabber();
        grabber.Grab();                       // discard the first, it pays JIT and handle setup

        var samples = new List<double>();
        for (var i = 0; i < 15; i++)
        {
            var sw = Stopwatch.StartNew();
            var frozen = grabber.Grab();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(frozen.Width * frozen.Height * 4L, frozen.Bgra.LongLength);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var worst = samples[^1];

        var desktop = Snipwhiz.Core.Geometry.VirtualDesktop.FromMonitors(MonitorEnumerator.Enumerate());
        var megapixels = desktop.Bounds.Width * (double)desktop.Bounds.Height / 1_000_000;

        output.WriteLine($"displays   : {desktop.Monitors.Count}");
        output.WriteLine($"virtual    : {desktop.Bounds.Width}x{desktop.Bounds.Height} ({megapixels:F1} MP)");
        output.WriteLine($"scales     : {string.Join(", ", desktop.Monitors.Select(m => $"{m.Scale:P0}"))}");
        output.WriteLine($"median     : {median:F1} ms");
        output.WriteLine($"worst      : {worst:F1} ms");

        Assert.True(median < 120,
            $"Grab median {median:F1} ms exceeds the 120 ms budget. Per spec section 4.5, " +
            "switch the freeze path to DXGI Desktop Duplication before continuing.");
    }
}
```

- [ ] **Step 6: Run the probe**

```bash
dotnet test tests/Snipwhiz.Core.Tests --filter LatencyProbe -l "console;verbosity=detailed"
```
Expected: PASS, with the timing table printed. **Record the numbers in the commit message.**

- [ ] **Step 7: Run the probe on the worst configuration you ship to**

Plug in every monitor, set mismatched scaling, re-run Step 6. This is the gate. If the median exceeds 120 ms, **stop and escalate to Desktop Duplication** — do not proceed to Task 3.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add BitBlt desktop grabber and latency gate

One synchronous BitBlt over the whole virtual desktop, SRCCOPY|CAPTUREBLT
so layered content such as menu shadows is not silently dropped. Alpha is
forced opaque because BitBlt from the screen leaves it undefined.

Cursor state is recorded at grab time even though nothing renders it yet:
freeze-first makes it unrecoverable afterwards.

LatencyProbe is the spec section 4.5 gate. Measured on <N> displays,
<W>x<H>: median <X> ms, worst <Y> ms."
```

---

### Task 3: Crop, with uncovered-region detection

**Files:**
- Modify: `src/Snipwhiz.Core/Capture/FrozenDesktop.cs`
- Test: `tests/Snipwhiz.Core.Tests/Capture/FrozenDesktopCropTests.cs`

**Interfaces:**
- Consumes: `FrozenDesktop`, `PixelRect`, `VirtualDesktop` (Tasks 1–2).
- Produces:
  - `FrozenDesktop.Crop(PixelRect region) -> CroppedImage`
  - `CroppedImage(byte[] Bgra, int Width, int Height, bool HasUncoveredPixels)`

The single-pass grab introduces a hazard the per-monitor design did not have: an L-shaped or offset arrangement leaves regions inside the bounding rectangle covered by no display. Those pixels are undefined. We detect and report rather than silently saving black bands.

- [ ] **Step 1: Write the failing crop tests**

Create `tests/Snipwhiz.Core.Tests/Capture/FrozenDesktopCropTests.cs`:

```csharp
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Capture;

public class FrozenDesktopCropTests
{
    /// <summary>Builds a frozen desktop whose blue channel encodes X and green encodes Y.</summary>
    private static FrozenDesktop Build(VirtualDesktop desktop)
    {
        var b = desktop.Bounds;
        var bgra = new byte[(long)b.Width * b.Height * 4];
        for (var y = 0; y < b.Height; y++)
        for (var x = 0; x < b.Width; x++)
        {
            var i = ((long)y * b.Width + x) * 4;
            bgra[i + 0] = (byte)(x % 256);
            bgra[i + 1] = (byte)(y % 256);
            bgra[i + 2] = 0;
            bgra[i + 3] = 255;
        }
        return new FrozenDesktop(desktop, bgra, CursorState.None);
    }

    private static VirtualDesktop TwoMonitorsNegativeOrigin() => VirtualDesktop.FromMonitors(new[]
    {
        new MonitorInfo("A", new PixelRect(0, 0, 1920, 1080), 1.0, true),
        new MonitorInfo("B", new PixelRect(-2560, 0, 2560, 1080), 1.5, false),
    });

    [Fact]
    public void Crop_translates_by_the_virtual_origin_not_by_monitor()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        // A 4x2 region starting at virtual (-2560, 0) is buffer offset (0, 0).
        var crop = frozen.Crop(new PixelRect(-2560, 0, 4, 2));

        Assert.Equal(4, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(0, crop.Bgra[0]);                 // x = 0
        Assert.Equal(3, crop.Bgra[3 * 4 + 0]);         // x = 3
        Assert.Equal(1, crop.Bgra[(4 + 0) * 4 + 1]);   // second row, y = 1
    }

    [Fact]
    public void Crop_spanning_two_monitors_is_one_contiguous_image()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        // straddles the seam at virtual x = 0
        var crop = frozen.Crop(new PixelRect(-10, 10, 20, 5));

        Assert.Equal(20, crop.Width);
        Assert.Equal(5, crop.Height);
        Assert.False(crop.HasUncoveredPixels);
        // buffer x for virtual -10 is 2550
        Assert.Equal((byte)(2550 % 256), crop.Bgra[0]);
    }

    [Fact]
    public void Crop_reports_uncovered_pixels_in_an_L_shaped_desktop()
    {
        // Second display is shorter and offset up, leaving a gap bottom-right.
        var desktop = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(0, 0, 100, 100), 1.0, true),
            new MonitorInfo("B", new PixelRect(100, 0, 100, 50), 1.0, false),
        });
        var frozen = Build(desktop);

        Assert.True(frozen.Crop(new PixelRect(120, 60, 40, 30)).HasUncoveredPixels);
        Assert.False(frozen.Crop(new PixelRect(10, 10, 40, 30)).HasUncoveredPixels);
        Assert.False(frozen.Crop(new PixelRect(120, 10, 40, 30)).HasUncoveredPixels);
    }

    [Fact]
    public void Crop_clamps_a_region_that_runs_past_the_bounds()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        var crop = frozen.Crop(new PixelRect(1900, 1060, 500, 500));
        Assert.Equal(20, crop.Width);
        Assert.Equal(20, crop.Height);
    }

    [Fact]
    public void Crop_of_an_empty_region_throws()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        Assert.Throws<ArgumentException>(() => frozen.Crop(new PixelRect(10, 10, 0, 0)));
    }

    [Fact]
    public void Crop_of_a_single_pixel_works()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        var crop = frozen.Crop(new PixelRect(-2555, 7, 1, 1));
        Assert.Equal(1, crop.Width);
        Assert.Equal(5, crop.Bgra[0]);
        Assert.Equal(7, crop.Bgra[1]);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Snipwhiz.Core.Tests --filter FrozenDesktopCropTests
```
Expected: FAIL — `Crop` and `CroppedImage` do not exist.

- [ ] **Step 3: Implement Crop**

Create `src/Snipwhiz.Core/Capture/CroppedImage.cs`:

```csharp
namespace Snipwhiz.Core.Capture;

/// <param name="Bgra">Top-down BGRA32, stride Width * 4.</param>
/// <param name="HasUncoveredPixels">
/// True when the region overlaps part of the virtual bounding box that no
/// display covers. Those pixels are undefined, so the caller warns rather
/// than silently saving black bands.
/// </param>
public sealed record CroppedImage(byte[] Bgra, int Width, int Height, bool HasUncoveredPixels);
```

Append to `src/Snipwhiz.Core/Capture/FrozenDesktop.cs`, inside the class:

```csharp
    /// <summary>
    /// Crop in virtual-screen physical pixels. Because the grab is a single pass
    /// over the whole virtual desktop, this is a translation by the virtual
    /// origin — there is no per-monitor case.
    /// </summary>
    public CroppedImage Crop(PixelRect region)
    {
        var clamped = region.ClampTo(Bounds);
        if (clamped.IsEmpty)
            throw new ArgumentException($"Region {region} is empty or fully outside {Bounds}.", nameof(region));

        var srcX = clamped.X - Bounds.X;
        var srcY = clamped.Y - Bounds.Y;

        var dst = new byte[(long)clamped.Width * clamped.Height * 4];
        var rowBytes = clamped.Width * 4;

        for (var row = 0; row < clamped.Height; row++)
        {
            var srcOffset = ((long)(srcY + row) * Width + srcX) * 4;
            var dstOffset = (long)row * rowBytes;
            Array.Copy(Bgra, srcOffset, dst, dstOffset, rowBytes);
        }

        return new CroppedImage(dst, clamped.Width, clamped.Height, HasUncovered(clamped));
    }

    private bool HasUncovered(PixelRect region)
    {
        // Covered area is a union of rectangles, so it is enough to check whether
        // the region's area is fully accounted for by its intersections.
        long covered = 0;
        foreach (var m in Desktop.Monitors)
        {
            var hit = region.Intersect(m.Bounds);
            if (!hit.IsEmpty) covered += (long)hit.Width * hit.Height;
        }
        return covered < (long)region.Width * region.Height;
    }
```

> Note: summing intersection areas is exact only when displays do not overlap. Windows does not allow overlapping display rectangles, so this holds.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Snipwhiz.Core.Tests --filter FrozenDesktopCropTests
```
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add crop with uncovered-region detection

Crop is a translation by the virtual origin, not a per-monitor operation —
the single-pass grab removed that case. It introduced a different hazard:
L-shaped monitor arrangements leave regions inside the bounding box that
no display covers, and those pixels are undefined. Detect and report them
rather than silently saving black bands."
```

---

### Task 4: PNG encoding and the capture store

**Files:**
- Create: `src/Snipwhiz.Core/Imaging/PngEncoder.cs`
- Create: `src/Snipwhiz.Core/Storage/CaptureRecord.cs`, `LibraryDb.cs`, `CaptureStore.cs`
- Test: `tests/Snipwhiz.Core.Tests/Imaging/PngEncoderTests.cs`, `Storage/CaptureStoreTests.cs`

**Interfaces:**
- Consumes: `CroppedImage` (Task 3).
- Produces:
  - `PngEncoder.Encode(byte[] bgra, int width, int height) -> byte[]`
  - `CaptureRecord(Guid Id, DateTimeOffset CreatedUtc, int Width, int Height, string SourceApp, string SourceTitle, string FilePath)`
  - `LibraryDb(string dbPath)` with `Insert(CaptureRecord)`, `Recent(int limit) -> IReadOnlyList<CaptureRecord>`
  - `CaptureStore(string rootPath)` with `Save(CroppedImage, string sourceApp, string sourceTitle) -> CaptureRecord`

- [ ] **Step 1: Add the packages**

```bash
dotnet add src/Snipwhiz.Core package System.Drawing.Common
dotnet add src/Snipwhiz.Core package Microsoft.Data.Sqlite
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Snipwhiz.Core.Tests/Imaging/PngEncoderTests.cs`:

```csharp
using Snipwhiz.Core.Imaging;
using Xunit;

namespace Snipwhiz.Core.Tests.Imaging;

public class PngEncoderTests
{
    [Fact]
    public void Encode_produces_a_valid_png_signature()
    {
        var bgra = new byte[4 * 4 * 4];
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

        var png = PngEncoder.Encode(bgra, 4, 4);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
    }

    [Fact]
    public void Encode_round_trips_pixel_values()
    {
        // one red pixel, BGRA order
        var bgra = new byte[] { 0, 0, 255, 255 };
        var png = PngEncoder.Encode(bgra, 1, 1);

        using var ms = new MemoryStream(png);
        using var bmp = new System.Drawing.Bitmap(ms);
        var px = bmp.GetPixel(0, 0);

        Assert.Equal(255, px.R);
        Assert.Equal(0, px.G);
        Assert.Equal(0, px.B);
    }

    [Fact]
    public void Encode_rejects_a_buffer_that_does_not_match_the_dimensions()
        => Assert.Throws<ArgumentException>(() => PngEncoder.Encode(new byte[10], 4, 4));
}
```

Create `tests/Snipwhiz.Core.Tests/Storage/CaptureStoreTests.cs`:

```csharp
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Storage;

public class CaptureStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    private static CroppedImage Image(int w = 8, int h = 4)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new CroppedImage(bgra, w, h, false);
    }

    [Fact]
    public void Save_writes_a_png_and_returns_a_record_pointing_at_it()
    {
        using var store = new CaptureStore(_root);
        var record = store.Save(Image(), "chrome", "Northwind Analytics");

        var full = Path.Combine(_root, record.FilePath);
        Assert.True(File.Exists(full));
        Assert.Equal(8, record.Width);
        Assert.Equal(4, record.Height);
        Assert.Equal("chrome", record.SourceApp);
        Assert.Equal("Northwind Analytics", record.SourceTitle);
    }

    [Fact]
    public void Save_buckets_files_by_year_and_month()
    {
        using var store = new CaptureStore(_root);
        var record = store.Save(Image(), "app", "title");

        var now = DateTimeOffset.UtcNow;
        Assert.StartsWith(Path.Combine("captures", now.ToString("yyyy"), now.ToString("MM")), record.FilePath);
    }

    [Fact]
    public void Saved_records_come_back_newest_first()
    {
        using var store = new CaptureStore(_root);
        var a = store.Save(Image(), "a", "first");
        var b = store.Save(Image(), "b", "second");
        var c = store.Save(Image(), "c", "third");

        var recent = store.Recent(10);

        Assert.Equal(new[] { c.Id, b.Id, a.Id }, recent.Select(r => r.Id));
    }

    [Fact]
    public void Ids_are_time_ordered()
    {
        using var store = new CaptureStore(_root);
        var ids = Enumerable.Range(0, 20).Select(_ => store.Save(Image(), "a", "t").Id).ToList();
        Assert.Equal(ids.OrderBy(i => i).ToList(), ids);   // UUIDv7 sorts by creation time
    }

    [Fact]
    public void Reopening_the_store_sees_earlier_captures()
    {
        Guid id;
        using (var first = new CaptureStore(_root)) id = first.Save(Image(), "a", "t").Id;
        using var second = new CaptureStore(_root);
        Assert.Contains(second.Recent(10), r => r.Id == id);
    }

    [Fact]
    public void Schema_version_is_stamped()
    {
        using var store = new CaptureStore(_root);
        store.Save(Image(), "a", "t");
        Assert.Equal(1, store.SchemaVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/Snipwhiz.Core.Tests --filter "PngEncoderTests|CaptureStoreTests"
```
Expected: FAIL — types do not exist.

- [ ] **Step 4: Implement the encoder**

Create `src/Snipwhiz.Core/Imaging/PngEncoder.cs`:

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snipwhiz.Core.Imaging;

public static class PngEncoder
{
    /// <param name="bgra">Top-down BGRA32, stride width * 4.</param>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        var expected = (long)width * height * 4;
        if (bgra.LongLength != expected)
            throw new ArgumentException($"Expected {expected} bytes for {width}x{height}, got {bgra.LongLength}.", nameof(bgra));

        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb,
                                          handle.AddrOfPinnedObject());
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
```

- [ ] **Step 5: Implement the record and database**

Create `src/Snipwhiz.Core/Storage/CaptureRecord.cs`:

```csharp
namespace Snipwhiz.Core.Storage;

/// <param name="FilePath">Relative to the store root, so the folder stays movable.</param>
public sealed record CaptureRecord(
    Guid Id,
    DateTimeOffset CreatedUtc,
    int Width,
    int Height,
    string SourceApp,
    string SourceTitle,
    string FilePath);
```

Create `src/Snipwhiz.Core/Storage/LibraryDb.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Snipwhiz.Core.Storage;

public sealed class LibraryDb : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly SqliteConnection _connection;

    public LibraryDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();
        Migrate();
    }

    public int SchemaVersion
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private void Migrate()
    {
        using var wal = _connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();

        if (SchemaVersion >= CurrentSchemaVersion) return;

        using var cmd = _connection.CreateCommand();
        // Spec 2 adds columns; ALTER TABLE ADD COLUMN is free in SQLite, so
        // nothing is pre-built for it here.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS captures (
                id           TEXT    PRIMARY KEY,
                created_utc  INTEGER NOT NULL,
                width        INTEGER NOT NULL,
                height       INTEGER NOT NULL,
                source_app   TEXT    NOT NULL,
                source_title TEXT    NOT NULL,
                file_path    TEXT    NOT NULL
            );
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Insert(CaptureRecord r)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO captures (id, created_utc, width, height, source_app, source_title, file_path)
            VALUES ($id, $created, $w, $h, $app, $title, $path);
            """;
        cmd.Parameters.AddWithValue("$id", r.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$created", r.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$w", r.Width);
        cmd.Parameters.AddWithValue("$h", r.Height);
        cmd.Parameters.AddWithValue("$app", r.SourceApp);
        cmd.Parameters.AddWithValue("$title", r.SourceTitle);
        cmd.Parameters.AddWithValue("$path", r.FilePath);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CaptureRecord> Recent(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_utc, width, height, source_app, source_title, file_path
            FROM captures ORDER BY created_utc DESC, id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<CaptureRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CaptureRecord(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return results;
    }

    public void Dispose() => _connection.Dispose();
}
```

- [ ] **Step 6: Implement the store**

Create `src/Snipwhiz.Core/Storage/CaptureStore.cs`:

```csharp
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;

namespace Snipwhiz.Core.Storage;

/// <summary>Writes the immutable original PNG and records it. Never modifies a saved file.</summary>
public sealed class CaptureStore : IDisposable
{
    private readonly string _root;
    private readonly LibraryDb _db;

    public CaptureStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
    }

    public int SchemaVersion => _db.SchemaVersion;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snipwhiz");

    public CaptureRecord Save(CroppedImage image, string sourceApp, string sourceTitle)
    {
        // UUIDv7 is time-ordered, which is the only property we need.
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var relativeDir = Path.Combine("captures", now.ToString("yyyy"), now.ToString("MM"));
        Directory.CreateDirectory(Path.Combine(_root, relativeDir));

        var relativePath = Path.Combine(relativeDir, $"{id:D}.png");
        File.WriteAllBytes(Path.Combine(_root, relativePath), PngEncoder.Encode(image.Bgra, image.Width, image.Height));

        var record = new CaptureRecord(id, now, image.Width, image.Height, sourceApp, sourceTitle, relativePath);
        _db.Insert(record);
        return record;
    }

    public IReadOnlyList<CaptureRecord> Recent(int limit) => _db.Recent(limit);

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/Snipwhiz.Core.Tests
```
Expected: PASS, all tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add PNG encoding and the capture store

Originals are written once and never modified; spec 2 annotations become
JSON sidecars beside them. Schema is stamped with PRAGMA user_version and
runs in WAL mode. IDs are UUIDv7 for time ordering, which is the only
property ULID offered and is now in the BCL."
```

---

### Task 5: Clipboard with a correct multi-format payload

**Files:**
- Create: `src/Snipwhiz.Core/Clipboard/ClipboardWriter.cs`
- Modify: `src/Snipwhiz.Core/NativeMethods.txt`

**Interfaces:**
- Consumes: `CroppedImage` (Task 3), `PngEncoder` (Task 4).
- Produces: `ClipboardWriter.Write(CroppedImage image)` — throws `ClipboardUnavailableException` after exhausting retries.

`Clipboard.SetImage` publishes essentially `CF_BITMAP` and pastes **black or blue backgrounds** into Office, Paint and browsers. This is the single most visible defect a screenshot tool can ship, so the payload is built by hand.

- [ ] **Step 1: Add the native APIs**

Append to `src/Snipwhiz.Core/NativeMethods.txt`:

```
OpenClipboard
CloseClipboard
EmptyClipboard
SetClipboardData
RegisterClipboardFormat
GlobalAlloc
GlobalLock
GlobalUnlock
GlobalFree
BITMAPV5HEADER
```

- [ ] **Step 2: Implement the writer**

Create `src/Snipwhiz.Core/Clipboard/ClipboardWriter.cs`:

```csharp
using System.Runtime.InteropServices;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;

namespace Snipwhiz.Core.Clipboard;

public sealed class ClipboardUnavailableException(string message) : Exception(message);

public static class ClipboardWriter
{
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const int MaxAttempts = 8;
    private const int RetryDelayMs = 60;

    /// <summary>
    /// Publishes PNG, CF_DIBV5 and CF_DIB in one sequence. All three, because
    /// modern apps prefer PNG, DIBV5 carries alpha, and supplying CF_DIB stops
    /// Windows synthesising a wrong one from the others.
    /// </summary>
    public static unsafe void Write(CroppedImage image)
    {
        var png = PngEncoder.Encode(image.Bgra, image.Width, image.Height);
        var pngFormat = PInvoke.RegisterClipboardFormat("PNG");
        if (pngFormat == 0) throw new ClipboardUnavailableException("RegisterClipboardFormat(\"PNG\") failed.");

        // Clipboard managers hold the clipboard constantly; retry before giving up.
        var opened = false;
        for (var attempt = 0; attempt < MaxAttempts && !opened; attempt++)
        {
            opened = PInvoke.OpenClipboard(default);
            if (!opened) Thread.Sleep(RetryDelayMs);
        }
        if (!opened)
            throw new ClipboardUnavailableException(
                $"Another application held the clipboard after {MaxAttempts} attempts.");

        try
        {
            PInvoke.EmptyClipboard();
            SetBytes(pngFormat, png);
            SetBytes(CF_DIBV5, BuildDibV5(image));
            SetBytes(CF_DIB, BuildDib(image));
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static unsafe void SetBytes(uint format, byte[] data)
    {
        // GMEM_MOVEABLE; ownership passes to the clipboard on success.
        var handle = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
        if (handle == 0) throw new ClipboardUnavailableException($"GlobalAlloc failed for format {format}.");

        var ok = false;
        try
        {
            var target = PInvoke.GlobalLock((HGLOBAL)handle);
            if (target is null) throw new ClipboardUnavailableException($"GlobalLock failed for format {format}.");
            try
            {
                Marshal.Copy(data, 0, (nint)target, data.Length);
            }
            finally
            {
                PInvoke.GlobalUnlock((HGLOBAL)handle);
            }

            if (PInvoke.SetClipboardData(format, (HANDLE)handle) == 0)
                throw new ClipboardUnavailableException($"SetClipboardData failed for format {format}.");

            ok = true;
        }
        finally
        {
            if (!ok) PInvoke.GlobalFree((HGLOBAL)handle);
        }
    }

    private static unsafe byte[] BuildDibV5(CroppedImage image)
    {
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = image.Width,
            bV5Height = -image.Height,             // negative => top-down
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = (BI_COMPRESSION)3,    // BI_BITFIELDS
            bV5SizeImage = (uint)image.Bgra.Length,
            bV5RedMask   = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask  = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
            bV5CSType = 0x73524742,                // 'sRGB'
            bV5Intent = 4,                         // LCS_GM_IMAGES
        };

        var buffer = new byte[sizeof(BITMAPV5HEADER) + image.Bgra.Length];
        fixed (byte* p = buffer) *(BITMAPV5HEADER*)p = header;
        // Alpha is premultiplied; spec 1 output is opaque, spec 2's editor is not.
        Buffer.BlockCopy(image.Bgra, 0, buffer, sizeof(BITMAPV5HEADER), image.Bgra.Length);
        return buffer;
    }

    private static unsafe byte[] BuildDib(CroppedImage image)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = image.Width,
            biHeight = -image.Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = (uint)BI_COMPRESSION.BI_RGB,
            biSizeImage = (uint)image.Bgra.Length,
        };

        var buffer = new byte[sizeof(BITMAPINFOHEADER) + image.Bgra.Length];
        fixed (byte* p = buffer) *(BITMAPINFOHEADER*)p = header;
        Buffer.BlockCopy(image.Bgra, 0, buffer, sizeof(BITMAPINFOHEADER), image.Bgra.Length);
        return buffer;
    }
}
```

- [ ] **Step 3: Verify it builds**

```bash
dotnet build src/Snipwhiz.Core
```
Expected: succeeds with no warnings (warnings are errors).

> Clipboard behaviour is process-global and cannot be unit tested reliably in a
> parallel test run. It is verified in Task 12's manual checklist by pasting into
> Word, Paint, Chrome and Slack — which is where the `CF_BITMAP` defect actually
> shows up.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Add multi-format clipboard writer

Publishes PNG, CF_DIBV5 and CF_DIB in one sequence. WPF's Clipboard.SetImage
publishes essentially CF_BITMAP, which pastes black or blue backgrounds into
Office, Paint and browsers. CF_DIB is supplied explicitly so Windows does not
synthesise a wrong one from the others.

OpenClipboard is retried with backoff because clipboard managers hold it
constantly."
```

---

### Task 6: The capture pipeline

**Files:**
- Create: `src/Snipwhiz.Core/CapturePipeline.cs`
- Modify: `src/Snipwhiz.Core/NativeMethods.txt`

**Interfaces:**
- Consumes: `FrozenDesktop`, `CroppedImage`, `ClipboardWriter`, `CaptureStore`.
- Produces:
  - `CaptureOutcome(CaptureRecord? Record, bool ClipboardOk, bool SaveOk, bool HasUncoveredPixels, string? Warning)`
  - `CapturePipeline(CaptureStore store, Action<CroppedImage>? writeClipboard = null)` with `Complete(FrozenDesktop, PixelRect region, string sourceApp, string sourceTitle) -> CaptureOutcome`
  - `ForegroundWindow.Describe() -> (string App, string Title)`

**Clipboard before disk, always.** The clipboard is what the user is about to paste; a failing disk write must never cost them the capture.

The clipboard write is an **injected delegate defaulting to `ClipboardWriter.Write`** — one optional parameter, not an interface. `ClipboardWriter` is static and touches process-global Win32 state, so without this seam the ordering rule above (the rule that guarantees a user never loses a capture) could only ever be verified by hand.

- [ ] **Step 1: Add the native APIs**

Append to `src/Snipwhiz.Core/NativeMethods.txt`:

```
GetForegroundWindow
GetWindowTextW
GetWindowThreadProcessId
```

- [ ] **Step 2: Implement the pipeline**

Create `src/Snipwhiz.Core/CapturePipeline.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Clipboard;
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Storage;
using Windows.Win32;

namespace Snipwhiz.Core;

public sealed record CaptureOutcome(
    CaptureRecord? Record,
    bool ClipboardOk,
    bool SaveOk,
    bool HasUncoveredPixels,
    string? Warning);

/// <summary>Describes the window that was focused at grab time. Capture-time-only data.</summary>
public static class ForegroundWindow
{
    public static unsafe (string App, string Title) Describe()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.IsNull) return ("", "");

        Span<char> buffer = stackalloc char[512];
        string title;
        fixed (char* p = buffer)
        {
            var length = PInvoke.GetWindowTextW(hwnd, p, buffer.Length);
            title = length > 0 ? new string(p, 0, length) : "";
        }

        var app = "";
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid != 0)
        {
            try { app = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { /* exited between the two calls */ }
            catch (InvalidOperationException) { }
        }

        return (app, title);
    }
}

public sealed class CapturePipeline(CaptureStore store, Action<CroppedImage>? writeClipboard = null)
{
    // Seam, not an abstraction: ClipboardWriter is static and touches
    // process-global Win32 state, so the ordering rule below is otherwise
    // unprovable except by hand.
    private readonly Action<CroppedImage> _writeClipboard = writeClipboard ?? ClipboardWriter.Write;

    public CaptureOutcome Complete(FrozenDesktop frozen, PixelRect region, string sourceApp, string sourceTitle)
    {
        var image = frozen.Crop(region);

        string? warning = null;
        if (image.HasUncoveredPixels)
            warning = "Part of that selection is not covered by any display; those pixels are blank.";
        else if (IsEntirelyBlack(image))
            warning = "The capture came back black. DRM-protected content cannot be captured by any "
                    + "screenshot tool; fullscreen games must be switched to windowed mode.";

        // Clipboard first — it must not block on disk I/O.
        var clipboardOk = true;
        try
        {
            _writeClipboard(image);
        }
        catch (ClipboardUnavailableException e)
        {
            clipboardOk = false;
            warning ??= $"Could not copy to the clipboard: {e.Message}";
        }

        CaptureRecord? record = null;
        var saveOk = true;
        try
        {
            record = store.Save(image, sourceApp, sourceTitle);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            saveOk = false;
            warning ??= $"Copied to the clipboard, but saving to disk failed: {e.Message}";
        }

        return new CaptureOutcome(record, clipboardOk, saveOk, image.HasUncoveredPixels, warning);
    }

    private static bool IsEntirelyBlack(CroppedImage image)
    {
        for (long i = 0; i < image.Bgra.LongLength; i += 4)
            if (image.Bgra[i] != 0 || image.Bgra[i + 1] != 0 || image.Bgra[i + 2] != 0) return false;
        return true;
    }
}
```

- [ ] **Step 3: Write the pipeline tests**

Create `tests/Snipwhiz.Core.Tests/CapturePipelineTests.cs`:

```csharp
using Snipwhiz.Core;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Clipboard;
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests;

public class CapturePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snipwhiz-pipeline", Guid.NewGuid().ToString("N"));

    /// <param name="fill">Applied to R, G and B of every pixel.</param>
    private static FrozenDesktop Frozen(byte fill = 200, PixelRect? monitor = null)
    {
        var bounds = monitor ?? new PixelRect(0, 0, 40, 20);
        var desktop = VirtualDesktop.FromMonitors(new[] { new MonitorInfo("A", bounds, 1.0, true) });
        var bgra = new byte[bounds.Width * bounds.Height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = bgra[i + 1] = bgra[i + 2] = fill;
            bgra[i + 3] = 255;
        }
        return new FrozenDesktop(desktop, bgra, CursorState.None);
    }

    [Fact]
    public void Clipboard_is_written_before_disk()
    {
        using var store = new CaptureStore(_root);
        var order = new List<string>();
        var pipeline = new CapturePipeline(store, _ => order.Add("clipboard"));

        var outcome = pipeline.Complete(Frozen(), new PixelRect(0, 0, 10, 10), "app", "title");
        order.Add("disk");   // Save already happened inside Complete

        Assert.Equal("clipboard", order[0]);
        Assert.True(outcome.ClipboardOk);
        Assert.True(outcome.SaveOk);
    }

    [Fact]
    public void A_failing_clipboard_still_saves_to_disk()
    {
        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store,
            _ => throw new ClipboardUnavailableException("held by another app"));

        var outcome = pipeline.Complete(Frozen(), new PixelRect(0, 0, 10, 10), "app", "title");

        Assert.False(outcome.ClipboardOk);
        Assert.True(outcome.SaveOk);
        Assert.NotNull(outcome.Record);
        Assert.Contains("clipboard", outcome.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_all_black_capture_explains_that_DRM_cannot_be_captured()
    {
        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store, _ => { });

        var outcome = pipeline.Complete(Frozen(fill: 0), new PixelRect(0, 0, 10, 10), "app", "title");

        Assert.Contains("DRM", outcome.Warning!);
    }

    [Fact]
    public void An_uncovered_region_is_reported_and_takes_priority_over_the_black_warning()
    {
        // Two displays, second offset up, leaving a gap bottom-right.
        var desktop = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(0, 0, 40, 40), 1.0, true),
            new MonitorInfo("B", new PixelRect(40, 0, 40, 20), 1.0, false),
        });
        var bgra = new byte[80 * 40 * 4];
        var frozen = new FrozenDesktop(desktop, bgra, CursorState.None);

        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store, _ => { });

        var outcome = pipeline.Complete(frozen, new PixelRect(50, 25, 20, 10), "app", "title");

        Assert.True(outcome.HasUncoveredPixels);
        Assert.Contains("not covered by any display", outcome.Warning!);
        Assert.DoesNotContain("DRM", outcome.Warning!);
    }

    [Fact]
    public void A_normal_capture_produces_no_warning()
    {
        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store, _ => { });

        var outcome = pipeline.Complete(Frozen(), new PixelRect(5, 5, 10, 10), "chrome", "Northwind");

        Assert.Null(outcome.Warning);
        Assert.Equal(10, outcome.Record!.Width);
        Assert.Equal("chrome", outcome.Record.SourceApp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test tests/Snipwhiz.Core.Tests --filter CapturePipelineTests
```
Expected: PASS, 5 tests. If `Clipboard_is_written_before_disk` fails, the ordering rule is broken — that is the rule that guarantees a user never loses a capture.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add capture pipeline

Crops, then writes the clipboard, then writes disk — in that order, so a
failing disk write never costs the user the capture they just took.

Distinguishes the two black-frame causes: DRM is permanent and affects
every screenshot tool, fullscreen-exclusive is fixable by the user.
Foreground app and title are captured here because they cannot be
determined after the fact."
```

---

### Task 7: Tray host, hotkeys, and the first runnable app

**Files:**
- Create: `src/Snipwhiz.App/Snipwhiz.App.csproj`, `app.manifest`, `App.xaml`, `App.xaml.cs`, `TrayHost.cs`
- Create: `src/Snipwhiz.Core/Hotkeys/HotkeyId.cs`, `HotkeyService.cs`, `Settings.cs`
- Modify: `Snipwhiz.sln`, `src/Snipwhiz.Core/NativeMethods.txt`

**Interfaces:**
- Consumes: `CapturePipeline`, `BitBltGrabber`, `CaptureStore`, `ForegroundWindow`.
- Produces:
  - `HotkeyId { Region, Fullscreen }`
  - `HotkeyService : IDisposable` — `event Action<HotkeyId> Pressed`, `bool TryRegister(HotkeyId, uint modifiers, uint vk)`
  - `Settings` — `Autostart`, `PrintScreenPromptAnswered`, `PrintScreenTakenOver`, `static Load(string root)`, `Save(string root)`
  - `TrayHost` — `ShowBalloon(string title, string text, bool isError)`

**Milestone: at the end of this task the app runs.** `Ctrl+Shift+2` captures the monitor under the cursor straight to clipboard and disk, with no overlay. That proves Tasks 1–6 on real hardware before any UI risk is taken.

- [ ] **Step 1: Create the app project**

```bash
dotnet new wpf -o src/Snipwhiz.App -n Snipwhiz.App
dotnet sln add src/Snipwhiz.App/Snipwhiz.App.csproj
dotnet add src/Snipwhiz.App/Snipwhiz.App.csproj reference src/Snipwhiz.Core/Snipwhiz.Core.csproj
```

Replace the `<PropertyGroup>` in `src/Snipwhiz.App/Snipwhiz.App.csproj`:

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
  <SupportedOSPlatformVersion>10.0.22621.0</SupportedOSPlatformVersion>
  <RootNamespace>Snipwhiz.App</RootNamespace>
  <UseWPF>true</UseWPF>
  <UseWindowsForms>true</UseWindowsForms>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <LangVersion>latest</LangVersion>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>
```

- [ ] **Step 2: Declare PerMonitorV2 DPI awareness**

Create `src/Snipwhiz.App/app.manifest`. **Without this every coordinate in the app is wrong on scaled displays** — Windows lies about monitor bounds and cursor position.

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="Snipwhiz.App" />

  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10/11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>

  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 3: Add the hotkey native APIs**

Append to `src/Snipwhiz.Core/NativeMethods.txt`:

```
RegisterHotKey
UnregisterHotKey
HOT_KEY_MODIFIERS
```

- [ ] **Step 4: Implement the hotkey service**

Create `src/Snipwhiz.Core/Hotkeys/HotkeyId.cs`:

```csharp
namespace Snipwhiz.Core.Hotkeys;

public enum HotkeyId
{
    Region = 1,
    Fullscreen = 2,
    PrintScreenRegion = 3,
}
```

Create `src/Snipwhiz.Core/Hotkeys/HotkeyService.cs`:

```csharp
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Snipwhiz.Core.Hotkeys;

/// <summary>
/// Owns a message-only window and the RegisterHotKey registrations against it.
/// Registration failure is never fatal: the tray menu always offers every action.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint VkPrintScreen = 0x2C;

    private readonly MessageOnlyWindow _window;
    private readonly List<HotkeyId> _registered = [];

    public event Action<HotkeyId>? Pressed;

    public HotkeyService()
    {
        _window = new MessageOnlyWindow(id => Pressed?.Invoke(id));
    }

    /// <returns>False when another process holds the chord. Callers must not treat this as fatal.</returns>
    public bool TryRegister(HotkeyId id, uint modifiers, uint virtualKey)
    {
        // MOD_NOREPEAT stops auto-repeat firing a capture storm on a held key.
        const uint ModNoRepeat = 0x4000;
        if (!PInvoke.RegisterHotKey(_window.Handle, (int)id, (HOT_KEY_MODIFIERS)(modifiers | ModNoRepeat), virtualKey))
            return false;

        _registered.Add(id);
        return true;
    }

    public void Unregister(HotkeyId id)
    {
        if (_registered.Remove(id)) PInvoke.UnregisterHotKey(_window.Handle, (int)id);
    }

    public void Dispose()
    {
        foreach (var id in _registered.ToArray()) PInvoke.UnregisterHotKey(_window.Handle, (int)id);
        _registered.Clear();
        _window.Dispose();
    }

    /// <summary>A HWND_MESSAGE window exists only to receive WM_HOTKEY.</summary>
    private sealed class MessageOnlyWindow : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private readonly System.Windows.Forms.NativeWindow _native;

        public HWND Handle { get; }

        public MessageOnlyWindow(Action<HotkeyId> onPressed)
        {
            _native = new Sink(onPressed);
            ((Sink)_native).CreateHandle(new System.Windows.Forms.CreateParams
            {
                Parent = new IntPtr(-3),          // HWND_MESSAGE
            });
            Handle = (HWND)_native.Handle;
        }

        public void Dispose() => ((Sink)_native).DestroyHandle();

        private sealed class Sink(Action<HotkeyId> onPressed) : System.Windows.Forms.NativeWindow
        {
            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WmHotkey) onPressed((HotkeyId)m.WParam.ToInt32());
                base.WndProc(ref m);
            }
        }
    }
}
```

- [ ] **Step 5: Implement settings**

Create `src/Snipwhiz.Core/Settings.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snipwhiz.Core;

/// <summary>
/// Three fields and no UI. If this needs a fourth before spec 2, that is the
/// signal the settings window is overdue.
/// </summary>
public sealed class Settings
{
    public bool Autostart { get; set; }
    public bool PrintScreenPromptAnswered { get; set; }
    public bool PrintScreenTakenOver { get; set; }

    [JsonIgnore] private static string FileName => "settings.json";

    public static Settings Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch (JsonException)
        {
            return new Settings();   // a corrupt settings file must never stop the app starting
        }
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, FileName),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
```

- [ ] **Step 6: Implement the tray host**

Create `src/Snipwhiz.App/TrayHost.cs`:

```csharp
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using Snipwhiz.Core;

namespace Snipwhiz.App;

/// <summary>
/// Tray icon, menu, and notifications. Balloon tips rather than toasts:
/// toasts from an unpackaged app are silently dropped until the spec 3
/// installer creates a shortcut with a matching AppUserModelID.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Snipwhiz";

    private readonly NotifyIcon _icon;
    private readonly Settings _settings;
    private readonly string _root;

    public event Action? RegionRequested;
    public event Action? FullscreenRequested;
    public event Action? CancelRequested;
    public event Action? ExitRequested;

    public TrayHost(Settings settings, string root)
    {
        _settings = settings;
        _root = root;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture region\tCtrl+Shift+1", null, (_, _) => RegionRequested?.Invoke());
        menu.Items.Add("Capture screen\tCtrl+Shift+2", null, (_, _) => FullscreenRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Cancel capture", null, (_, _) => CancelRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = _settings.Autostart,
        };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,       // replaced with real branding in spec 6
            Text = "Snipwhiz",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => RegionRequested?.Invoke();
    }

    public void ShowBalloon(string title, string text, bool isError = false)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);

        _settings.Autostart = enabled;
        _settings.Save(_root);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
```

- [ ] **Step 7: Wire the composition root**

Replace `src/Snipwhiz.App/App.xaml`:

```xml
<Application x:Class="Snipwhiz.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown" />
```

Replace `src/Snipwhiz.App/App.xaml.cs`:

```csharp
using System.Windows;
using Snipwhiz.Core;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Hotkeys;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App;

public partial class App : Application
{
    private Mutex? _instanceLock;
    private TrayHost? _tray;
    private HotkeyService? _hotkeys;
    private CaptureStore? _store;
    private CapturePipeline? _pipeline;
    private readonly BitBltGrabber _grabber = new();
    private string _root = CaptureStore.DefaultRoot;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = new Mutex(initiallyOwned: true, @"Local\Snipwhiz.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var settings = Settings.Load(_root);
        _store = new CaptureStore(_root);
        _pipeline = new CapturePipeline(_store);

        _tray = new TrayHost(settings, _root);
        _tray.FullscreenRequested += CaptureFullscreen;
        _tray.RegionRequested += CaptureFullscreen;   // replaced by the overlay in Task 9
        _tray.ExitRequested += Shutdown;

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += id =>
        {
            if (id is HotkeyId.Fullscreen or HotkeyId.Region) CaptureFullscreen();
        };

        RegisterHotkeys();
        _tray.ShowBalloon("Snipwhiz is running", "Press Ctrl+Shift+2 to capture the screen.");
    }

    private void RegisterHotkeys()
    {
        const uint vk1 = 0x31, vk2 = 0x32;   // '1' and '2'
        var mods = HotkeyService.ModControl | HotkeyService.ModShift;

        if (!_hotkeys!.TryRegister(HotkeyId.Region, mods, vk1))
            _tray!.ShowBalloon("Hotkey unavailable",
                "Ctrl+Shift+1 is held by another application. Use the tray menu instead.", isError: true);

        if (!_hotkeys.TryRegister(HotkeyId.Fullscreen, mods, vk2))
            _tray!.ShowBalloon("Hotkey unavailable",
                "Ctrl+Shift+2 is held by another application. Use the tray menu instead.", isError: true);
    }

    private void CaptureFullscreen()
    {
        var (app, title) = ForegroundWindow.Describe();
        var frozen = _grabber.Grab();

        var cursor = frozen.Cursor;
        var monitor = frozen.Desktop.MonitorAt(cursor.X, cursor.Y)
                   ?? frozen.Desktop.Monitors.First(m => m.IsPrimary);

        Report(_pipeline!.Complete(frozen, monitor.Bounds, app, title));
    }

    private void Report(CaptureOutcome outcome)
    {
        if (outcome.Warning is not null)
            _tray!.ShowBalloon("Capture problem", outcome.Warning, isError: !outcome.ClipboardOk);
        else
            _tray!.ShowBalloon("Copied", $"{outcome.Record!.Width} x {outcome.Record.Height} copied to the clipboard.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 8: Run it**

```bash
dotnet run --project src/Snipwhiz.App
```
Expected: a tray icon appears with a startup balloon. Press `Ctrl+Shift+2` — a balloon reports the captured size. Paste into Paint: the monitor under your cursor, correct dimensions, not black.

- [ ] **Step 9: Verify single instance**

Run `dotnet run --project src/Snipwhiz.App` a second time while the first is running. Expected: the second exits immediately and no second tray icon appears.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "Add tray host, hotkeys, and a runnable fullscreen capture

First runnable milestone: Ctrl+Shift+2 captures the monitor under the
cursor to clipboard and disk with no overlay, proving the pipeline on real
hardware before any UI risk is taken.

app.manifest declares PerMonitorV2. Without it Windows lies about monitor
bounds and cursor position and every coordinate in the app is wrong.

Hotkeys use MOD_NOREPEAT so a held key cannot fire a capture storm, and
registration failure is non-fatal: the tray menu always offers every
action."
```

---

### Task 8: Overlay windows at 1:1 physical pixels

**Files:**
- Create: `src/Snipwhiz.App/OverlayWindow.xaml`, `OverlayWindow.xaml.cs`, `CaptureSession.cs`
- Modify: `src/Snipwhiz.Core/NativeMethods.txt`

**Interfaces:**
- Consumes: `FrozenDesktop`, `MonitorInfo`, `Dpi` (Tasks 1–2).
- Produces:
  - `OverlayWindow(FrozenDesktop frozen, MonitorInfo monitor)` — `event Action Cancelled`, `ShowAt()`
  - `CaptureSession(FrozenDesktop frozen)` — `event Action<PixelRect> Committed`, `event Action Cancelled`, `Start()`, `Cancel()`

> **This task implements the spec's most dangerous rule.** A 96-DPI `BitmapSource` in a WPF `Image` on a 150% monitor is drawn at 1.5× — blurry, offset, not pixel-exact. Every step below that neutralises scaling is load-bearing.

- [ ] **Step 1: Add SetWindowPos**

Append to `src/Snipwhiz.Core/NativeMethods.txt`:

```
SetWindowPos
SET_WINDOW_POS_FLAGS
```

- [ ] **Step 2: Create the overlay window**

Create `src/Snipwhiz.App/OverlayWindow.xaml`:

```xml
<Window x:Class="Snipwhiz.App.OverlayWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" ResizeMode="NoResize" ShowInTaskbar="False"
        Topmost="True" AllowsTransparency="False" Background="Black"
        UseLayoutRounding="True" SnapsToDevicePixels="True"
        Cursor="Cross">
  <Grid x:Name="Root" ClipToBounds="True">
    <!-- The frozen screen, rendered at exactly 1:1 physical pixels. -->
    <Image x:Name="Frozen" Stretch="None"
           RenderOptions.BitmapScalingMode="NearestNeighbor"
           RenderOptions.EdgeMode="Aliased"
           HorizontalAlignment="Left" VerticalAlignment="Top" />
    <Canvas x:Name="Layer" />
  </Grid>
</Window>
```

Create `src/Snipwhiz.App/OverlayWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Snipwhiz.App;

public partial class OverlayWindow : Window
{
    private readonly FrozenDesktop _frozen;
    private readonly MonitorInfo _monitor;

    public MonitorInfo Monitor => _monitor;
    public Canvas Overlay => Layer;

    public event Action? Cancelled;

    public OverlayWindow(FrozenDesktop frozen, MonitorInfo monitor)
    {
        InitializeComponent();
        _frozen = frozen;
        _monitor = monitor;

        // Esc and right-click cancel on EVERY overlay, not just the focused one —
        // otherwise the unfocused screens are dead ends.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Cancelled?.Invoke(); };
        MouseRightButtonUp += (_, _) => Cancelled?.Invoke();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TOOLWINDOW keeps the overlay out of Alt+Tab and the taskbar.
        var style = PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            style | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);

        // Position in PHYSICAL pixels. WPF's Left/Top are DIPs and would place
        // this window wrongly on every non-primary or scaled monitor.
        PInvoke.SetWindowPos((HWND)hwnd, HWND.HWND_TOPMOST,
            _monitor.Bounds.X, _monitor.Bounds.Y, _monitor.Bounds.Width, _monitor.Bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => RenderFrozenSlice();

    /// <summary>
    /// Renders this monitor's slice of the frozen desktop at exactly 1:1 physical
    /// pixels. Three things must all agree or the image is soft and offset:
    /// the bitmap's DPI metadata, the Image's DIP size, and NearestNeighbor scaling.
    /// </summary>
    private void RenderFrozenSlice()
    {
        var b = _monitor.Bounds;
        var slice = _frozen.Crop(b);

        // Claim 96 DPI so WPF does no DPI-based rescaling of its own...
        var source = BitmapSource.Create(
            slice.Width, slice.Height, 96, 96, PixelFormats.Bgra32, null,
            slice.Bgra, slice.Width * 4);
        source.Freeze();

        Frozen.Source = source;

        // ...then size it in DIPs so physicalPx / scale lands on exact device pixels.
        Frozen.Width = Dpi.PhysicalToDip(slice.Width, _monitor.Scale);
        Frozen.Height = Dpi.PhysicalToDip(slice.Height, _monitor.Scale);

        // Stretch="Fill" is required now that we set an explicit DIP size:
        // Stretch="None" would draw 1 bitmap px per DIP and overflow at >100%.
        Frozen.Stretch = Stretch.Fill;
    }

    /// <summary>Converts a WPF mouse position on this overlay to virtual physical pixels.</summary>
    public (int X, int Y) ToVirtualPixels(Point dipPoint) => (
        _monitor.Bounds.X + Dpi.DipToPhysical(dipPoint.X, _monitor.Scale),
        _monitor.Bounds.Y + Dpi.DipToPhysical(dipPoint.Y, _monitor.Scale));

    public void ShowAt(bool activate)
    {
        if (activate) Show();
        else
        {
            ShowActivated = false;
            Show();
        }
    }
}
```

Add `GetWindowLong`, `SetWindowLong`, `WINDOW_LONG_PTR_INDEX`, `WINDOW_EX_STYLE` to `NativeMethods.txt`.

- [ ] **Step 3: Create the capture session**

Create `src/Snipwhiz.App/CaptureSession.cs`:

```csharp
using System.Windows.Threading;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;

namespace Snipwhiz.App;

/// <summary>
/// Owns one overlay per monitor for the life of a capture. Guarantees there is
/// always a way out: if activation is refused, or nothing responds, the session
/// tears itself down rather than leaving an unfocusable fullscreen window.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(60);

    private readonly List<OverlayWindow> _overlays = [];
    private readonly FrozenDesktop _frozen;
    private readonly DispatcherTimer _watchdog;
    private bool _closed;

    public FrozenDesktop Frozen => _frozen;
    public IReadOnlyList<OverlayWindow> Overlays => _overlays;

    public event Action<PixelRect>? Committed;
    public event Action? Cancelled;

    public CaptureSession(FrozenDesktop frozen)
    {
        _frozen = frozen;
        _watchdog = new DispatcherTimer { Interval = WatchdogTimeout };
        _watchdog.Tick += (_, _) => Cancel();
    }

    /// <returns>False when the overlay could not be brought to the foreground.</returns>
    public bool Start()
    {
        var cursor = _frozen.Cursor;
        var active = _frozen.Desktop.MonitorAt(cursor.X, cursor.Y)
                  ?? _frozen.Desktop.Monitors.First(m => m.IsPrimary);

        foreach (var monitor in _frozen.Desktop.Monitors)
        {
            var overlay = new OverlayWindow(_frozen, monitor);
            overlay.Cancelled += Cancel;
            _overlays.Add(overlay);
            overlay.ShowAt(activate: monitor.DeviceName == active.DeviceName);
        }

        var designated = _overlays.First(o => o.Monitor.DeviceName == active.DeviceName);
        designated.Activate();

        // SetForegroundWindow can be refused even for a hotkey-driven process.
        // Aborting is the only safe response — an opaque fullscreen window that
        // will not take a keystroke is the worst bug we could ship.
        if (!designated.IsActive)
        {
            Cancel();
            return false;
        }

        _watchdog.Start();
        return true;
    }

    public void Commit(PixelRect region)
    {
        if (_closed) return;
        _closed = true;
        CloseOverlays();
        Committed?.Invoke(region);
    }

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        CloseOverlays();
        Cancelled?.Invoke();
    }

    private void CloseOverlays()
    {
        _watchdog.Stop();
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }

    public void Dispose() => CloseOverlays();
}
```

- [ ] **Step 4: Wire it to the region hotkey**

In `src/Snipwhiz.App/App.xaml.cs`, replace `_tray.RegionRequested += CaptureFullscreen;` with `_tray.RegionRequested += CaptureRegion;`, update the `Pressed` handler to route `HotkeyId.Region` to `CaptureRegion`, and add:

```csharp
    private CaptureSession? _session;

    private void CaptureRegion()
    {
        if (_session is not null) return;   // a capture is already in flight

        var (app, title) = ForegroundWindow.Describe();
        var frozen = _grabber.Grab();

        _session = new CaptureSession(frozen);
        _session.Cancelled += () => { _session?.Dispose(); _session = null; };
        _session.Committed += region =>
        {
            var outcome = _pipeline!.Complete(frozen, region, app, title);
            _session?.Dispose();
            _session = null;
            Report(outcome);
        };

        if (!_session.Start())
        {
            _session = null;
            _tray!.ShowBalloon("Capture cancelled",
                "Windows would not allow the capture overlay to take focus. Try again.", isError: true);
        }
    }
```

Also wire cancel: `_tray.CancelRequested += () => _session?.Cancel();`

- [ ] **Step 5: Run and verify 1:1 rendering by eye**

```bash
dotnet run --project src/Snipwhiz.App
```
Press `Ctrl+Shift+1`. Expected: the screen appears frozen and **indistinguishable from the live desktop** — text is crisp, nothing is soft or shifted. Press Esc to dismiss. Repeat with the cursor on a 150% monitor: still crisp. **Softness here means the 1:1 rule is broken; fix it before continuing.**

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add per-monitor overlay windows at 1:1 physical pixels

One opaque overlay per monitor. A single spanning window receives one DPI
from Windows and renders incorrectly on mismatched displays.

Windows are positioned with SetWindowPos in physical pixels; WPF Left/Top
are DIPs and would misplace them on scaled monitors. The frozen slice is
rendered by claiming 96 DPI on the bitmap, sizing the Image to
physicalPx/scale, and using NearestNeighbor — all three must agree or the
image is soft and offset.

Esc and right-click cancel on every overlay, not just the focused one. If
SetForegroundWindow is refused the session aborts rather than leaving an
opaque fullscreen window that will not take a keystroke, and a 60s
watchdog is the backstop."
```

---

### Task 9: Region selection

**Files:**
- Create: `src/Snipwhiz.App/SelectionController.cs`
- Modify: `src/Snipwhiz.App/OverlayWindow.xaml.cs`, `CaptureSession.cs`

**Interfaces:**
- Consumes: `CaptureSession`, `OverlayWindow.ToVirtualPixels`, `PixelRect`.
- Produces: `SelectionController(CaptureSession session)` — `PixelRect? Current`, `event Action<PixelRect?> Changed`, `BeginDrag(int,int)`, `UpdateDrag(int,int)`, `EndDrag()`

One controller owns the rect in virtual pixels; each overlay renders only its own intersection. That is what makes a drag across mismatched-DPI monitors seamless.

- [ ] **Step 1: Implement the controller**

Create `src/Snipwhiz.App/SelectionController.cs`:

```csharp
using Snipwhiz.Core.Geometry;

namespace Snipwhiz.App;

/// <summary>
/// The single source of truth for the selection, in virtual physical pixels.
/// Overlays render their intersection with it; none of them owns it.
/// </summary>
public sealed class SelectionController(CaptureSession session)
{
    private int _anchorX, _anchorY;
    private bool _dragging;

    public PixelRect? Current { get; private set; }
    public bool IsDragging => _dragging;

    public event Action<PixelRect?>? Changed;

    public void BeginDrag(int virtualX, int virtualY)
    {
        _anchorX = virtualX;
        _anchorY = virtualY;
        _dragging = true;
        Current = null;
        Changed?.Invoke(Current);
    }

    public void UpdateDrag(int virtualX, int virtualY)
    {
        if (!_dragging) return;
        Current = PixelRect
            .FromCorners(_anchorX, _anchorY, virtualX, virtualY)
            .ClampTo(session.Frozen.Bounds);
        Changed?.Invoke(Current);
    }

    /// <returns>The committed rect, or null if the drag was too small to be intentional.</returns>
    public PixelRect? EndDrag()
    {
        if (!_dragging) return null;
        _dragging = false;

        // A click without a drag is not a selection.
        if (Current is not { } rect || rect.Width < 3 || rect.Height < 3)
        {
            Current = null;
            Changed?.Invoke(null);
            return null;
        }
        return rect;
    }
}
```

- [ ] **Step 2: Render the selection on each overlay**

Add to `src/Snipwhiz.App/OverlayWindow.xaml.cs`:

```csharp
    private readonly System.Windows.Shapes.Rectangle _dim = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(0xAD, 0x0C, 0x08, 0x04)),
    };
    private readonly System.Windows.Shapes.Rectangle _border = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xB6, 0x27)),
        StrokeThickness = 1,
    };

    /// <summary>Draws this monitor's slice of a selection that may span several monitors.</summary>
    public void RenderSelection(PixelRect? selection)
    {
        if (_dim.Parent is null)
        {
            Layer.Children.Add(_dim);
            Layer.Children.Add(_border);
            _dim.Width = ActualWidth;
            _dim.Height = ActualHeight;
        }

        if (selection is not { } sel)
        {
            _dim.Opacity = 1;
            _border.Visibility = Visibility.Collapsed;
            _dim.Clip = null;
            return;
        }

        var local = sel.Intersect(_monitor.Bounds);
        if (local.IsEmpty)
        {
            _dim.Opacity = 1;
            _dim.Clip = null;
            _border.Visibility = Visibility.Collapsed;
            return;
        }

        var x = Dpi.PhysicalToDip(local.X - _monitor.Bounds.X, _monitor.Scale);
        var y = Dpi.PhysicalToDip(local.Y - _monitor.Bounds.Y, _monitor.Scale);
        var w = Dpi.PhysicalToDip(local.Width, _monitor.Scale);
        var h = Dpi.PhysicalToDip(local.Height, _monitor.Scale);

        // Punch the selection out of the dim layer with an even-odd geometry —
        // the kept region stays at full brightness.
        var outer = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var inner = new RectangleGeometry(new Rect(x, y, w, h));
        _dim.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);

        Canvas.SetLeft(_border, x);
        Canvas.SetTop(_border, y);
        _border.Width = w;
        _border.Height = h;
        _border.Visibility = Visibility.Visible;
    }
```

Add the mouse plumbing to the constructor, after the Esc handler:

```csharp
        MouseLeftButtonDown += (_, e) =>
        {
            var (vx, vy) = ToVirtualPixels(e.GetPosition(this));
            DragStarted?.Invoke(vx, vy);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            var (vx, vy) = ToVirtualPixels(e.GetPosition(this));
            PointerMoved?.Invoke(vx, vy);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            ReleaseMouseCapture();
            DragEnded?.Invoke();
        };
```

And the events:

```csharp
    public event Action<int, int>? DragStarted;
    public event Action<int, int>? PointerMoved;
    public event Action? DragEnded;
```

- [ ] **Step 3: Wire the controller into the session**

Add to `src/Snipwhiz.App/CaptureSession.cs`, inside `Start()` before `designated.Activate()`:

```csharp
        Selection = new SelectionController(this);
        Selection.Changed += rect =>
        {
            foreach (var o in _overlays) o.RenderSelection(rect);
        };

        foreach (var overlay in _overlays)
        {
            overlay.DragStarted += (x, y) => Selection.BeginDrag(x, y);
            overlay.PointerMoved += (x, y) => Selection.UpdateDrag(x, y);
            overlay.DragEnded += () =>
            {
                if (Selection.EndDrag() is { } rect) Commit(rect);
            };
        }
```

And the property:

```csharp
    public SelectionController Selection { get; private set; } = null!;
```

- [ ] **Step 4: Run and verify**

```bash
dotnet run --project src/Snipwhiz.App
```

1. `Ctrl+Shift+1`, drag a region on the primary monitor, release. Paste into Paint — must match the selection exactly.
2. Repeat on a 150% monitor. **No drift, no half-pixel offset.**
3. Drag a region straddling two monitors of different DPI. Must be seamless and correctly sized.
4. Press Esc mid-drag from an unfocused monitor — must cancel.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add region selection across monitors

One SelectionController owns the rect in virtual physical pixels; each
overlay renders only its intersection with it. That is what makes a drag
across mismatched-DPI monitors seamless — no overlay owns the selection,
so none of them can disagree about it.

The dim layer is punched out with an even-odd clip so the kept region
stays at full brightness, mirroring what the final crop will contain."
```

---

### Task 10: The magnifier loupe

**Files:**
- Create: `src/Snipwhiz.App/Loupe.xaml`, `Loupe.xaml.cs`
- Modify: `src/Snipwhiz.App/OverlayWindow.xaml.cs`
- Test: `tests/Snipwhiz.Core.Tests/Capture/PixelSampleTests.cs`
- Modify: `src/Snipwhiz.Core/Capture/FrozenDesktop.cs`

**Interfaces:**
- Consumes: `FrozenDesktop`, `Dpi`.
- Produces: `FrozenDesktop.SampleAt(int x, int y) -> (byte R, byte G, byte B)`; `Loupe.Update(int virtualX, int virtualY)`

**This is the pass/fail instrument for the 1:1 rendering rule.** If the frozen bitmap renders at anything other than 1:1, the cursor over a visual feature maps to the wrong bitmap coordinate and the hex readout goes visibly wrong.

- [ ] **Step 1: Write the failing sampling test**

Create `tests/Snipwhiz.Core.Tests/Capture/PixelSampleTests.cs`:

```csharp
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Capture;

public class PixelSampleTests
{
    private static FrozenDesktop Build()
    {
        var desktop = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(-100, -50, 200, 100), 1.0, true),
        });
        var bgra = new byte[200 * 100 * 4];
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 200; x++)
        {
            var i = (y * 200 + x) * 4;
            bgra[i + 0] = 10;                  // B
            bgra[i + 1] = (byte)(y % 256);     // G
            bgra[i + 2] = (byte)(x % 256);     // R
            bgra[i + 3] = 255;
        }
        return new FrozenDesktop(desktop, bgra, CursorState.None);
    }

    [Fact]
    public void SampleAt_uses_virtual_coordinates_including_negative_ones()
    {
        var frozen = Build();
        // virtual (-100, -50) is buffer (0, 0)
        Assert.Equal((0, 0, 10), frozen.SampleAt(-100, -50));
        // virtual (-95, -45) is buffer (5, 5)
        Assert.Equal((5, 5, 10), frozen.SampleAt(-95, -45));
    }

    [Fact]
    public void SampleAt_outside_the_bounds_returns_black()
        => Assert.Equal((0, 0, 0), Build().SampleAt(10_000, 10_000));
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Snipwhiz.Core.Tests --filter PixelSampleTests
```
Expected: FAIL — `SampleAt` does not exist.

- [ ] **Step 3: Implement sampling**

Append to `src/Snipwhiz.Core/Capture/FrozenDesktop.cs`, inside the class:

```csharp
    /// <summary>Reads one pixel in virtual physical pixels. Out of bounds reads as black.</summary>
    public (byte R, byte G, byte B) SampleAt(int virtualX, int virtualY)
    {
        if (!Bounds.Contains(virtualX, virtualY)) return (0, 0, 0);

        var i = ((long)(virtualY - Bounds.Y) * Width + (virtualX - Bounds.X)) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i + 0]);
    }
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/Snipwhiz.Core.Tests --filter PixelSampleTests
```
Expected: PASS.

- [ ] **Step 5: Build the loupe control**

Create `src/Snipwhiz.App/Loupe.xaml`:

```xml
<UserControl x:Class="Snipwhiz.App.Loupe"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             IsHitTestVisible="False" Width="136">
  <Border Background="#0C0906" BorderBrush="#66FFB627" BorderThickness="1" CornerRadius="7">
    <StackPanel>
      <Grid Width="134" Height="134" ClipToBounds="True">
        <Image x:Name="Glass" Stretch="Fill"
               RenderOptions.BitmapScalingMode="NearestNeighbor"
               RenderOptions.EdgeMode="Aliased" />
        <Rectangle x:Name="Cross" Width="10" Height="10"
                   HorizontalAlignment="Center" VerticalAlignment="Center"
                   Stroke="#FFB627" StrokeThickness="1.5" />
      </Grid>
      <StackPanel Orientation="Horizontal" Margin="8,6" >
        <Rectangle x:Name="Swatch" Width="11" Height="11" Margin="0,0,7,0"
                   Stroke="#40FFFFFF" StrokeThickness="1" />
        <TextBlock x:Name="Hex" Foreground="#F6F2EB" FontFamily="Consolas" FontSize="11" />
        <TextBlock x:Name="Coords" Foreground="#877D72" FontFamily="Consolas" FontSize="10" Margin="10,0,0,0" />
      </StackPanel>
    </StackPanel>
  </Border>
</UserControl>
```

Create `src/Snipwhiz.App/Loupe.xaml.cs`:

```csharp
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Capture;

namespace Snipwhiz.App;

public partial class Loupe : UserControl
{
    private const int SampleSpan = 13;     // odd, so there is a true centre pixel

    private readonly FrozenDesktop _frozen;

    public Loupe(FrozenDesktop frozen)
    {
        InitializeComponent();
        _frozen = frozen;
    }

    /// <summary>Position is virtual physical pixels — the same space the crop uses.</summary>
    public void Update(int virtualX, int virtualY)
    {
        var half = SampleSpan / 2;
        var pixels = new byte[SampleSpan * SampleSpan * 4];

        for (var row = 0; row < SampleSpan; row++)
        for (var col = 0; col < SampleSpan; col++)
        {
            var (r, g, b) = _frozen.SampleAt(virtualX - half + col, virtualY - half + row);
            var i = (row * SampleSpan + col) * 4;
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        var source = BitmapSource.Create(SampleSpan, SampleSpan, 96, 96,
            PixelFormats.Bgra32, null, pixels, SampleSpan * 4);
        source.Freeze();
        Glass.Source = source;

        var (cr, cg, cb) = _frozen.SampleAt(virtualX, virtualY);
        Swatch.Fill = new SolidColorBrush(Color.FromRgb(cr, cg, cb));
        Hex.Text = $"#{cr:X2}{cg:X2}{cb:X2}";
        Coords.Text = $"{virtualX}, {virtualY}";
    }
}
```

- [ ] **Step 6: Attach it to each overlay**

Add to `OverlayWindow.xaml.cs`:

```csharp
    private Loupe? _loupe;

    public void AttachLoupe(FrozenDesktop frozen)
    {
        _loupe = new Loupe(frozen);
        Layer.Children.Add(_loupe);
    }

    /// <summary>Moves the loupe, flipping it near edges so it never leaves the screen.</summary>
    public void MoveLoupe(int virtualX, int virtualY)
    {
        if (_loupe is null) return;

        _loupe.Update(virtualX, virtualY);

        var x = Dpi.PhysicalToDip(virtualX - _monitor.Bounds.X, _monitor.Scale) + 18;
        var y = Dpi.PhysicalToDip(virtualY - _monitor.Bounds.Y, _monitor.Scale) + 18;

        if (x + 136 > ActualWidth) x -= 136 + 36;
        if (y + 170 > ActualHeight) y -= 170 + 36;

        Canvas.SetLeft(_loupe, Math.Max(4, x));
        Canvas.SetTop(_loupe, Math.Max(4, y));
        _loupe.Visibility = _monitor.Bounds.Contains(virtualX, virtualY)
            ? Visibility.Visible : Visibility.Collapsed;
    }
```

In `CaptureSession.Start()`, after creating each overlay, add `overlay.AttachLoupe(_frozen);` and extend the `PointerMoved` handler:

```csharp
            overlay.PointerMoved += (x, y) =>
            {
                Selection.UpdateDrag(x, y);
                foreach (var o in _overlays) o.MoveLoupe(x, y);
            };
```

- [ ] **Step 7: Verify against an independent colour picker**

```bash
dotnet run --project src/Snipwhiz.App
```

Press `Ctrl+Shift+1` and hover over a known colour. Open any independent colour picker (Windows PowerToys Color Picker, or a browser devtools eyedropper) and sample the **same** on-screen pixel.

**The hex values must match exactly.** A mismatch means the frozen bitmap is not rendering at 1:1 and the coordinate mapping is off — fix that before continuing, because every crop is wrong by the same amount. **Repeat on a 150% monitor and a 225% monitor.**

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add magnifier loupe with pixel grid and hex readout

Samples the frozen bitmap in virtual physical pixels — the same space the
crop uses — so the loupe is a live check on the coordinate mapping rather
than a separate implementation of it.

This is the pass/fail instrument for the 1:1 physical-pixel rendering
rule: if the frozen bitmap renders at any other scale, the cursor maps to
the wrong bitmap coordinate and the hex readout visibly disagrees with an
independent colour picker."
```

---

### Task 11: PrintScreen takeover

**Files:**
- Create: `src/Snipwhiz.Core/PrintScreenTakeover.cs`
- Modify: `src/Snipwhiz.App/App.xaml.cs`

**Interfaces:**
- Consumes: `Settings`, `HotkeyService`.
- Produces:
  - `PrintScreenTakeover.IsSnippingToolBound() -> bool`
  - `PrintScreenTakeover.Release()` — clears the registry value
  - `PrintScreenTakeover.DescribeLikelyHolder() -> string?`

**Detect by state, not by return code.** The Snipping Tool binding is shell-level: `RegisterHotKey` *succeeds* and the key still never arrives. A fallback gated on the return code therefore never fires for the people who need it.

- [ ] **Step 1: Implement the takeover helper**

Create `src/Snipwhiz.Core/PrintScreenTakeover.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Win32;

namespace Snipwhiz.Core;

public static class PrintScreenTakeover
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    /// <summary>
    /// True when Windows routes PrintScreen to Snipping Tool. Absent value means
    /// enabled — Windows 11 ships this on.
    /// </summary>
    public static bool IsSnippingToolBound()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(ValueName);
        return value is not int enabled || enabled != 0;
    }

    /// <summary>Clears the binding. HKCU, so no elevation. Only ever call with explicit consent.</summary>
    public static void Release()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key?.SetValue(ValueName, 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Best effort. There is no API to ask who owns a hotkey, so we can only
    /// name the usual suspects when they are running.
    /// </summary>
    public static string? DescribeLikelyHolder()
    {
        foreach (var (process, label) in new[]
                 {
                     ("Dropbox", "Dropbox"),
                     ("OneDrive", "OneDrive"),
                     ("ShareX", "ShareX"),
                     ("Greenshot", "Greenshot"),
                 })
        {
            if (Process.GetProcessesByName(process).Length > 0) return label;
        }
        return null;
    }
}
```

- [ ] **Step 2: Offer the takeover on first run**

Add to `src/Snipwhiz.App/App.xaml.cs`, called at the end of `OnStartup`:

```csharp
    private void OfferPrintScreenTakeover(Settings settings)
    {
        if (settings.PrintScreenPromptAnswered) return;

        if (!PrintScreenTakeover.IsSnippingToolBound())
        {
            // Nothing to take over — just claim it.
            TryClaimPrintScreen(settings);
            return;
        }

        var answer = MessageBox.Show(
            "Use PrintScreen for Snipwhiz?\n\n" +
            "This turns off the Windows Snipping Tool shortcut. You can change it back " +
            "in Settings > Accessibility > Keyboard at any time.\n\n" +
            "Snipwhiz already works with Ctrl+Shift+1 and Ctrl+Shift+2.",
            "Snipwhiz",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        settings.PrintScreenPromptAnswered = true;

        if (answer == MessageBoxResult.Yes)
        {
            PrintScreenTakeover.Release();
            TryClaimPrintScreen(settings);
        }

        settings.Save(_root);
    }

    private void TryClaimPrintScreen(Settings settings)
    {
        var claimed = _hotkeys!.TryRegister(
            HotkeyId.PrintScreenRegion, 0, HotkeyService.VkPrintScreen);

        settings.PrintScreenTakenOver = claimed;
        settings.Save(_root);

        if (claimed)
        {
            _tray!.ShowBalloon("PrintScreen is ready",
                "If it still opens Snipping Tool, sign out and back in to apply the change.");
        }
        else
        {
            var holder = PrintScreenTakeover.DescribeLikelyHolder();
            _tray!.ShowBalloon("PrintScreen unavailable",
                holder is null
                    ? "Another application is holding the PrintScreen key. Ctrl+Shift+1 still works."
                    : $"{holder} is holding the PrintScreen key. Ctrl+Shift+1 still works.",
                isError: true);
        }
    }
```

Route the new id in the `Pressed` handler: `if (id is HotkeyId.Region or HotkeyId.PrintScreenRegion) CaptureRegion();`

- [ ] **Step 3: Verify both answers**

Delete `%LOCALAPPDATA%\Snipwhiz\settings.json`, run, and **decline**. Confirm:
- `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` is unchanged
- `Ctrl+Shift+1` still works
- the prompt does not appear on the next run

Delete the settings file again, run, and **accept**. Confirm the registry value is now `0` and PrintScreen triggers a region capture (sign out and back in if it does not).

- [ ] **Step 4: Record the sign-out finding**

Update the spec's §9 open question with what you observed: whether clearing the value takes effect immediately or requires a sign-out. Adjust the balloon text in `TryClaimPrintScreen` to match.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add opt-in PrintScreen takeover

Detects the binding by reading the registry value rather than by checking
whether RegisterHotKey succeeded. The Snipping Tool binding is
shell-level, so registration succeeds and the key still never arrives — a
fallback gated on the return code would never fire for the users who need
it.

Only ever changes HKCU with explicit consent, and the answer is persisted
so the prompt appears once. Naming the holder is best effort: there is no
API to ask who owns a hotkey, so we check for the usual suspects."
```

---

### Task 12: Full manual verification

**Files:**
- Create: `docs/plans/2026-07-25-capture-core-verification.md`

No code. This is the spec's §7 checklist run for real, with results recorded. **Every item is a known failure mode of screenshot tools, not a formality.**

- [ ] **Step 1: Create the results document**

Create `docs/plans/2026-07-25-capture-core-verification.md` with this table, and fill in every row as you go:

```markdown
# Capture Core — Verification Results

Hardware: <describe every display: resolution, scale, HDR on/off>
Date: <date>

## The configuration most recipients actually run

| # | Check | Result |
|---|-------|--------|
| 1 | Single monitor, 100%, laptop panel — full loop works | |
| 2 | Same panel, HDR on — note any washed-out/desaturated output | |

## The configuration that breaks everything

| # | Check | Result |
|---|-------|--------|
| 3 | Region on primary — clipboard matches selection pixel for pixel | |
| 4 | Region on a 150% secondary — no scaling drift, no half-pixel offset | |
| 5 | Region dragged across two monitors of different DPI — seamless | |
| 6 | Fullscreen captures the monitor under the cursor, not the primary | |
| 7 | Grab median under 120 ms — paste the LatencyProbe output | |
| 8 | Loupe hex matches an independent colour picker (100%, 150%, 225%) | |
| 9 | Menu/tooltip shadows appear in the capture (proves CAPTUREBLT) | |

## What bites in the real world

| # | Check | Result |
|---|-------|--------|
| 10 | Paste into Word, Paint, Chrome, Slack — no black or blue backgrounds | |
| 11 | Hotkeys register on a stock box; accept and decline the PrintScreen prompt | |
| 12 | Overlay over an elevated foreground window — aborts rather than hanging | |
| 13 | Disconnect a monitor while the overlay is open — overlays close, balloon explains why, Ctrl+Shift+1 still works after | |
| 14 | Autostart survives a reboot | |
| 15 | Second instance exits silently, leaving the first running | |
| 16 | Soak: repeated captures — GDI handle count **and managed working set** both stay flat | |

Item 12 in earlier revisions ("UAC prompt while the overlay is open — clean
abort") has been removed: there is no desktop-switch abort and spec 1 does not
implement one. See §8 of the spec for why, and for the fact that Esc and
right-click remain the exit in that situation.

## Known limitations confirmed

| Limitation | Confirmed |
|------------|-----------|
| DRM content (Netflix) captures black — affects every screenshot tool | |
| The UAC prompt itself cannot be captured (separate desktop) | |
| Fullscreen-exclusive games: black frame and a forced mode switch | |
```

- [ ] **Step 2: Run every check and record the result**

Do not mark a row passed without actually performing it. Items 4, 5 and 8 are the ones most likely to fail; items 10 and 17 are the ones most likely to be skipped and most expensive to discover later.

- [ ] **Step 3: Open an issue for every failure, fix blockers before moving to spec 2**

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Record capture core verification results

Full section 7 checklist run on <hardware>. Confirms the 1:1 rendering
rule against an independent colour picker at three scale factors, the
clipboard payload against four real paste targets, and a flat GDI handle
count under repeated capture."
```

---

## Self-Review

**Spec coverage** — every §2 "In" item maps to a task: BitBlt grab (2), multi-monitor + mixed DPI (1, 8), freeze-first region (9) and fullscreen (7), loupe (10), cursor state (2), hotkeys (7, 11), tray/balloons/autostart/single-instance (7), clipboard (5), store (4). Error handling §6 lands in 6 and 7; known limitations §8 are confirmed in 12. Deferred items (window-under-cursor, repeat-last-region, thumbnails) correctly have no task.

**Two gaps found and closed while reviewing:** monitor hot-plug during an open overlay had no home — it is checklist item 14, and `CaptureSession.Cancel` is the handler. The `Stretch` property has to change from `None` to `Fill` once an explicit DIP size is set on the `Image`, which is now called out in Task 8 Step 2 because `Stretch="None"` in the XAML would silently overflow at any scale above 100%.

**Type consistency** — `PixelRect`, `MonitorInfo`, `VirtualDesktop`, `FrozenDesktop`, `CroppedImage`, `CaptureRecord`, `CaptureOutcome`, `HotkeyId` and `Settings` keep identical signatures across every task that references them. `Crop` returns `CroppedImage` in Tasks 3, 5, 6 and 8 alike; `Dpi.PhysicalToDip`/`DipToPhysical` take `(value, scale)` everywhere.

**One deliberate constraint deviation, flagged:** the spec says "zero new third-party dependencies for capture." `System.Drawing.Common` (PNG encode) and `Microsoft.Data.Sqlite` (store) are additions, but neither is in the capture path — the constraint is scoped to capture and is honoured there. Recorded in Global Constraints.

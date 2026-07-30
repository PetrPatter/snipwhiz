using System.Globalization;
using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// The one way a colour is written down.
///
/// <para>Shared by the project format and by settings, because two hex writers is
/// how one of them starts emitting alpha and the other stops reading it. Alpha is
/// omitted when opaque, which is nearly always, so the common case reads as a
/// familiar CSS colour.</para>
/// </summary>
public static class ColourHex
{
    public static string Write(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Throws <see cref="FormatException"/>, which each caller wraps in its own error type.</summary>
    public static Color Parse(string? value)
    {
        var s = (value ?? "").TrimStart('#');
        if (s.Length is not (6 or 8)) throw new FormatException($"'{value}' is not a colour.");

        var n = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return s.Length == 6
            ? Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n)
            : Color.FromArgb((byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);
    }
}

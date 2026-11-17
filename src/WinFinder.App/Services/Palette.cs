using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinFinder.App.Services;

/// <summary>Cached brushes for tag colours.</summary>
public static class Palette
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Brush(string hex)
    {
        if (Brushes.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush(Parse(hex));
        Brushes[hex] = b;
        return b;
    }

    public static Color Parse(string hex)
    {
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
                return Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
            if (hex.Length == 8)
                return Color.FromArgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16));
        }
        catch { }
        return Colors.Gray;
    }
}

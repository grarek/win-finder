using System.Globalization;

namespace WinFinder.Core.FileSystem;

public static class Format
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(long bytes)
    {
        if (bytes < 0) return "--";
        if (bytes == 0) return "Zero bytes";
        if (bytes == 1) return "1 byte";
        if (bytes < 1024) return $"{bytes} bytes";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string number = value < 10
            ? value.ToString("0.#", CultureInfo.CurrentCulture)
            : value.ToString("0", CultureInfo.CurrentCulture);
        return $"{number} {Units[unit]}";
    }

    /// <summary>Finder-style relative dates: "Today at 3:41 PM", "Yesterday at 9:02 AM", "Nov 3, 2026 at 9:12 AM".</summary>
    public static string Date(DateTime value, DateTime now)
    {
        if (value == DateTime.MinValue || value.Year < 1700) return "--";
        string time = value.ToString("t", CultureInfo.CurrentCulture);
        if (value.Date == now.Date) return $"Today at {time}";
        if (value.Date == now.Date.AddDays(-1)) return $"Yesterday at {time}";
        return $"{value.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)} at {time}";
    }

    public static string ItemCount(int count) => count == 1 ? "1 item" : $"{count:N0} items";
}

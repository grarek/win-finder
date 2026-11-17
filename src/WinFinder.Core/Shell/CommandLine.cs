using System.Runtime.InteropServices;

namespace WinFinder.Core.Shell;

public static class CommandLine
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Splits a command line exactly the way Windows does for a program's arguments.</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return Array.Empty<string>();
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>
    /// The folder/file argument from a forwarded launch: the first argument that isn't the
    /// program itself. Null when the launch had no arguments.
    /// </summary>
    public static string? FirstPathArgument(string? commandLine)
    {
        var args = Split(commandLine).ToList();
        if (args.Count > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) args.RemoveAt(0);
        return args.Select(CleanPath).FirstOrDefault(a => a.Length > 0);
    }

    /// <summary>
    /// Repairs a path argument. Explorer opens a drive with <c>"C:\"</c>, and Windows' argument
    /// rules read <c>\"</c> as an escaped quote, giving <c>C:"</c>; this turns that back into <c>C:\</c>.
    /// </summary>
    public static string CleanPath(string arg)
    {
        string s = arg.Trim().Trim('"').Trim();
        if (s.Length == 2 && s[1] == ':' && char.IsAsciiLetter(s[0])) s += "\\";
        return s;
    }
}

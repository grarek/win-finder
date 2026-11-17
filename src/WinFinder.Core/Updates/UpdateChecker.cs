using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinFinder.Core.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>What the update helper reports after it runs (written to update-result.txt).</summary>
public sealed record UpdateResult(bool Succeeded, string Version, string Message)
{
    /// <summary>Lines: "ok" or "failed", then the version, then (on failure) the reason.</summary>
    public static UpdateResult? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", "").Split('\n');
        string status = lines[0].Trim().TrimStart('﻿');
        if (status is not ("ok" or "failed")) return null;
        string version = lines.Length > 1 ? lines[1].Trim() : "";
        string message = lines.Length > 2 ? string.Join(" ", lines.Skip(2).Select(l => l.Trim()).Where(l => l.Length > 0)) : "";
        return new UpdateResult(status == "ok", version, message);
    }
}

public sealed record ReleaseInfo(Version Version, string Tag, string Name, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The zip built for this machine's architecture (named <c>*-win-x64.zip</c> / <c>*-win-arm64.zip</c>).</summary>
    public ReleaseAsset? AssetFor(Architecture architecture)
    {
        string rid = architecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        return Assets.FirstOrDefault(a => a.Name.EndsWith($"-{rid}.zip", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Reads the latest GitHub Release of a repository and compares versions.</summary>
public static class UpdateChecker
{
    /// <summary>"v1.0.12" or "1.0.12" → 1.0.12. Anything else → null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>How often a running app checks for a new release.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    /// <summary>True when the last check is older than <paramref name="interval"/> (or never happened).</summary>
    public static bool IsCheckDue(DateTime? lastCheckUtc, DateTime nowUtc, TimeSpan interval) =>
        lastCheckUtc is not { } last || nowUtc - last >= interval || last > nowUtc; // clock moved back: check

    /// <summary>Compares major.minor.build only (assembly versions carry a .0 revision).</summary>
    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>Parses GitHub's "latest release" JSON. Returns null for drafts, pre-releases or unparsable tags.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = ParseTag(tag);
        if (version is null) return null;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                long size = a.TryGetProperty("size", out var z) && z.TryGetInt64(out long sz) ? sz : 0;
                if (name.Length > 0 && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    assets.Add(new ReleaseAsset(name, url, size));
            }
        }

        string title = root.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } s ? s : tag;
        string html = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new ReleaseInfo(version, tag, title, html, assets);
    }

    /// <summary>Fetches the latest release of <paramref name="repository"/> ("owner/name"). Null if there is none yet.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(string repository, HttpClient http, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases published yet
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

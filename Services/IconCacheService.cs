using System.IO;
using System.Net.Http;

namespace GreenLuma_Manager.Services;

public class IconCacheService
{
    private static readonly string IconCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLM_Manager",
        "icons");

    private static readonly HttpClient _client = new();

    static IconCacheService()
    {
        _client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _client.Timeout = TimeSpan.FromSeconds(30);
    }

    public static async Task<string?> DownloadAndCacheIconAsync(string appId, string iconUrl)
    {
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(iconUrl))
            return null;
        if (!iconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            EnsureIconCacheDirectoryExists();
            var candidates = BuildCandidateUrls(appId, iconUrl).Distinct().ToList();
            foreach (var url in candidates)
            {
                var extension = GetImageExtension(url);
                var filePath = Path.Combine(IconCacheDir, appId + extension);
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                    return filePath;
                var data = await TryDownloadWithRetries(url, 3, TimeSpan.FromMilliseconds(400));
                if (data != null && data.Length > 0)
                {
                    File.WriteAllBytes(filePath, data);
                    return filePath;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static IEnumerable<string> BuildCandidateUrls(string appId, string primary)
    {
        yield return primary;
        var base1 = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}";
        var base2 = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}";
        var base3 = $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}";
        yield return base1 + "/header.jpg";
        yield return base2 + "/header.jpg";
        yield return base3 + "/header.jpg";
        yield return base1 + "/capsule_231x87.jpg";
        yield return base1 + "/capsule_616x353.jpg";
        yield return base1 + "/library_600x900.jpg";
    }

    private static async Task<byte[]?> TryDownloadWithRetries(string url, int maxAttempts, TimeSpan delay)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var resp = await _client.GetAsync(url, cts.Token);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException();
                var data = await resp.Content.ReadAsByteArrayAsync();
                if (data != null && data.Length > 256)
                    return data;
            }
            catch
            {
                if (attempt == maxAttempts)
                    break;
            }

            await Task.Delay(delay);
        }

        return null;
    }

    public static string? GetCachedIconPath(string appId)
    {
        if (string.IsNullOrEmpty(appId))
            return null;

        try
        {
            if (!Directory.Exists(IconCacheDir))
                return null;

            string[] extensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

            foreach (var ext in extensions)
            {
                var filePath = Path.Combine(IconCacheDir, $"{appId}{ext}");
                if (File.Exists(filePath))
                    return filePath;
            }
        }
        catch
        {
        }

        return null;
    }

    public static void ClearCache()
    {
        try
        {
            if (Directory.Exists(IconCacheDir)) Directory.Delete(IconCacheDir, true);
        }
        catch
        {
        }
    }

    public static void DeleteCachedIcon(string appId)
    {
        if (string.IsNullOrEmpty(appId))
            return;

        try
        {
            if (!Directory.Exists(IconCacheDir))
                return;

            string[] extensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

            foreach (var ext in extensions)
            {
                var filePath = Path.Combine(IconCacheDir, $"{appId}{ext}");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    public static void DeleteUnusedIcons(HashSet<string> validAppIds)
    {
        try
        {
            if (!Directory.Exists(IconCacheDir))
                return;
            var files = Directory.GetFiles(IconCacheDir);
            foreach (var file in files)
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!validAppIds.Contains(name))
                        File.Delete(file);
                }
                catch
                {
                }
        }
        catch
        {
        }
    }

    private static void EnsureIconCacheDirectoryExists()
    {
        if (!Directory.Exists(IconCacheDir)) Directory.CreateDirectory(IconCacheDir);
    }

    private static string GetImageExtension(string url)
    {
        try
        {
            var lower = url.ToLower();

            if (lower.Contains(".jpg") || lower.Contains("jpeg"))
                return ".jpg";
            if (lower.Contains(".png"))
                return ".png";
            if (lower.Contains(".gif"))
                return ".gif";
            if (lower.Contains(".webp"))
                return ".webp";

            return ".jpg";
        }
        catch
        {
            return ".jpg";
        }
    }
}
using System.Collections.Concurrent;
using System.Net.Http;
using GreenLuma_Manager.Models;
using Newtonsoft.Json.Linq;

namespace GreenLuma_Manager.Services;

public class CacheEntry<T>
{
    public DateTime Expiry { get; set; }
    public T Data { get; set; } = default!;
}

public static class SteamApiCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry<object>> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<object>> TaskCache = new();
    private static readonly TimeSpan CacheDurationLocal = TimeSpan.FromMinutes(30);

    public static async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> fetchFunc)
    {
        if (Cache.TryGetValue(key, out var entry))
            if (DateTime.Now < entry.Expiry && entry.Data is T cachedVal)
                return cachedVal;

        var task = TaskCache.GetOrAdd(key, _ => FetchAndCacheAsync(key, fetchFunc));
        try
        {
            var result = await task;
            return (T)result;
        }
        finally
        {
            TaskCache.TryRemove(key, out _);
        }
    }

    private static async Task<object> FetchAndCacheAsync<T>(string key, Func<Task<T>> fetchFunc)
    {
        var data = await fetchFunc();
        Cache[key] = new CacheEntry<object>
        {
            Expiry = DateTime.Now.Add(CacheDurationLocal),
            Data = data!
        };

        return data!;
    }
}

public class SearchService
{
    private const string SteamAppListUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
    private const string SteamStoreApiUrl = "https://api.steampowered.com/IStoreService/GetAppList/v1/";
    private const string SteamApiKey = "1DD0450A99F573693CD031EBB160907D";
    private const string SteamAppDetailsUrl = "https://store.steampowered.com/api/appdetails?appids={0}&l=english";
    private const int MaxConcurrentRequests = 8;
    private static readonly HttpClient Client = new();
    private static List<SteamApp>? _appListCache;
    private static readonly Dictionary<string, GameDetails> DetailsCache = [];
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static bool _useV2Api = true;

    static SearchService()
    {
        Client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        Client.Timeout = TimeSpan.FromSeconds(30);
    }

    private static async Task<List<SteamApp>> GetAppListAsync()
    {
        if (_appListCache != null && DateTime.Now < _cacheExpiry)
            return _appListCache;

        if (_useV2Api)
            try
            {
                var response = await Client.GetStringAsync(SteamAppListUrl);
                var json = JObject.Parse(response);
                var apps = json["applist"]?["apps"];

                if (apps != null)
                {
                    _appListCache =
                    [
                        .. apps
                            .Select(app => new SteamApp(
                                app["appid"]?.ToString() ?? string.Empty,
                                app["name"]?.ToString() ?? string.Empty))
                            .Where(app => !string.IsNullOrWhiteSpace(app.AppId) && !string.IsNullOrWhiteSpace(app.Name))
                    ];

                    _cacheExpiry = DateTime.Now.Add(CacheDuration);
                    return _appListCache;
                }
            }
            catch
            {
                _useV2Api = false;
            }

        if (!_useV2Api)
            try
            {
                _appListCache = [];
                uint lastAppId = 0;
                const int maxResults = 50000;

                while (true)
                {
                    var url =
                        $"{SteamStoreApiUrl}?key={SteamApiKey}&include_games=true&include_dlc=true&include_software=true&include_videos=true&include_hardware=true&max_results={maxResults}&last_appid={lastAppId}";

                    var response = await Client.GetStringAsync(url);
                    var json = JObject.Parse(response);
                    var apps = json["response"]?["apps"];

                    if (apps == null || !apps.Any())
                        break;

                    foreach (var app in apps)
                    {
                        var appId = app["appid"]?.ToString() ?? string.Empty;
                        var name = app["name"]?.ToString() ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(name))
                            _appListCache.Add(new SteamApp(appId, name));
                    }

                    var haveMore = json["response"]?["have_more_results"]?.Value<bool>() ?? false;
                    if (!haveMore)
                        break;

                    var lastAppIdFromResponse = json["response"]?["last_appid"]?.Value<uint>();
                    if (lastAppIdFromResponse.HasValue)
                        lastAppId = lastAppIdFromResponse.Value;
                    else
                        break;
                }

                _cacheExpiry = DateTime.Now.Add(CacheDuration);
                return _appListCache;
            }
            catch
            {
                // ignored
            }

        return _appListCache ?? [];
    }

    public static async Task<List<Game>> SearchAsync(string query, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        try
        {
            var appList = await GetAppListAsync();
            if (appList.Count == 0)
                return [];

            var queryLower = query.ToLower();
            var cacheKey = $"search:{queryLower}:{maxResults}";

            var cached = await SteamApiCache.GetOrAddAsync(cacheKey, async () =>
            {
                if (uint.TryParse(query, out _))
                {
                    var url = $"https://store.steampowered.com/api/appdetails?appids={query}";
                    var response = await Client.GetStringAsync(url);
                    var json = JObject.Parse(response)[query];
                    if (json?["success"]?.Value<bool>() == true && json["data"] != null)
                    {
                        var details = json["data"];
                        var appName = details!["name"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(appName))
                            return
                            [
                                new Game
                                {
                                    AppId = query,
                                    Name = appName,
                                    Type = MapSteamTypeToDisplayType(details["type"]?.ToString() ?? "game")
                                }
                            ];
                    }
                }

                return appList
                    .Select(app => (app, score: CalculateScore(app.Name, queryLower)))
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .Take(maxResults)
                    .Select(x => new Game
                    {
                        AppId = x.app.AppId,
                        Name = x.app.Name,
                        Type = "Game"
                    })
                    .ToList();
            });

            return
            [
                .. cached.Select(g => new Game { AppId = g.AppId, Name = g.Name, Type = g.Type, IconUrl = g.IconUrl })
            ];
        }
        catch
        {
            return [];
        }
    }

    private static int CalculateScore(string appName, string query)
    {
        if (string.IsNullOrEmpty(appName))
            return 0;

        var nameLower = appName.ToLower();
        var score = 0;

        if (nameLower == query)
            return 10000;

        if (nameLower.StartsWith(query))
            score += 5000;

        var nameWords = nameLower.Split([' ', '-', ':', '_', '™', '®'], StringSplitOptions.RemoveEmptyEntries);
        var queryWords = query.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        if (nameWords.Length > 0 && nameWords[0].StartsWith(query))
            score += 3000;

        var matchingWords = queryWords.Count(queryWord => nameWords.Any(w => w.StartsWith(queryWord)));

        if (queryWords.Length > 1 && matchingWords == queryWords.Length)
            score += 2000;

        if (nameLower.Contains(query))
            score += 1000;

        var lengthPenalty = Math.Max(0, (appName.Length - query.Length * 2) / 10);
        score -= lengthPenalty;

        if (HasWordBoundaryMatch(nameLower, query))
            score += 500;

        if (ContainsAllCharsInOrder(nameLower, query))
            score += 100;

        return Math.Max(0, score);
    }

    private static bool HasWordBoundaryMatch(string name, string query)
    {
        var words = name.Split([' ', '-', ':', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => w.StartsWith(query));
    }

    private static bool ContainsAllCharsInOrder(string text, string chars)
    {
        var charIndex = 0;
        foreach (var c in text)
            if (charIndex < chars.Length && c == chars[charIndex])
                charIndex++;
        return charIndex == chars.Length;
    }

    private static async Task FetchBatchDetailsAsync(List<Game> games)
    {
        var gamesNeedingDetails = games
            .Where(g => !DetailsCache.ContainsKey(g.AppId))
            .ToList();

        if (gamesNeedingDetails.Count == 0)
        {
            ApplyCachedDetails(games);
            return;
        }

        var syncContext = SynchronizationContext.Current;
        var semaphore = new SemaphoreSlim(MaxConcurrentRequests);

        try
        {
            var tasks = gamesNeedingDetails.Select(async game =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var details = await FetchGameDetailsAsync(game.AppId);
                    DetailsCache[game.AppId] = details;

                    if (syncContext != null)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        syncContext.Post(_ =>
                        {
                            try
                            {
                                if (details.Name != $"App {game.AppId}")
                                    game.Name = details.Name;

                                game.Type = details.Type;
                                game.IconUrl = details.IconUrl;
                                tcs.SetResult(true);
                            }
                            catch (Exception ex)
                            {
                                tcs.SetException(ex);
                            }
                        }, null);
                        await tcs.Task;
                    }
                    else
                    {
                        if (details.Name != $"App {game.AppId}")
                            game.Name = details.Name;

                        game.Type = details.Type;
                        game.IconUrl = details.IconUrl;
                    }
                }
                catch
                {
                    DetailsCache[game.AppId] = new GameDetails("Game", string.Empty, $"App {game.AppId}");
                    if (syncContext != null)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        syncContext.Post(_ =>
                        {
                            try
                            {
                                game.Type = "Game";
                                tcs.SetResult(true);
                            }
                            catch (Exception ex)
                            {
                                tcs.SetException(ex);
                            }
                        }, null);
                        await tcs.Task;
                    }
                    else
                    {
                        game.Type = "Game";
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
            ApplyCachedDetails(games);

            if (gamesNeedingDetails.Count > 0)
                _cacheExpiry = DateTime.Now.Add(CacheDuration);
        }
        finally
        {
            semaphore.Dispose();
        }
    }

    private static void ApplyCachedDetails(List<Game> games)
    {
        foreach (var game in games)
            if (DetailsCache.TryGetValue(game.AppId, out var details))
            {
                if (details.Name != $"App {game.AppId}")
                    game.Name = details.Name;

                game.Type = details.Type;
                if (!string.IsNullOrEmpty(details.IconUrl))
                    game.IconUrl = details.IconUrl;
            }
    }

    private static async Task<GameDetails> FetchGameDetailsAsync(string appId)
    {
        var key = $"details:{appId}";
        return await SteamApiCache.GetOrAddAsync(key, async () =>
        {
            try
            {
                var url = string.Format(SteamAppDetailsUrl, appId);
                var response = await Client.GetStringAsync(url);
                var json = JObject.Parse(response)[appId];

                if (json?["success"]?.Value<bool>() == true)
                {
                    var data = json["data"];
                    if (data != null)
                    {
                        var rawType = data["type"]?.ToString().ToLower() ?? "game";
                        var type = MapSteamTypeToDisplayType(rawType);
                        var name = data["name"]?.ToString() ?? $"App {appId}";

                        var headerImage = data["header_image"]?.ToString();
                        var capsuleImage = data["capsule_image"]?.ToString();
                        var iconUrl = !string.IsNullOrEmpty(headerImage) ? headerImage : capsuleImage ?? string.Empty;

                        if (!string.IsNullOrEmpty(iconUrl))
                            return new GameDetails(type, iconUrl, name);
                    }
                }

                var fallbackIconUrl = await TryGetCdnImageAsync(appId);
                return new GameDetails("Game", fallbackIconUrl ?? string.Empty, $"App {appId}");
            }
            catch
            {
                var fallbackIconUrl = await TryGetCdnImageAsync(appId);
                return new GameDetails("Game", fallbackIconUrl ?? string.Empty, $"App {appId}");
            }
        });
    }

    private static async Task<string?> TryGetCdnImageAsync(string appId)
    {
        string[] cdnUrls =
        [
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_231x87.jpg"
        ];

        foreach (var url in cdnUrls)
            try
            {
                var head = new HttpRequestMessage(HttpMethod.Head, url);
                var headResp = await Client.SendAsync(head);
                if (headResp.IsSuccessStatusCode)
                    return url;
            }
            catch
            {
                // ignored
            }

        return null;
    }

    private static string MapSteamTypeToDisplayType(string steamType)
    {
        return steamType switch
        {
            "game" => "Game",
            "dlc" => "DLC",
            "demo" => "Demo",
            "mod" => "Mod",
            "video" => "Video",
            "music" => "Soundtrack",
            "bundle" => "Bundle",
            "episode" => "Episode",
            "tool" or "advertising" => "Software",
            _ => "Game"
        };
    }

    public static async Task PopulateGameDetailsAsync(Game game)
    {
        var details = await FetchGameDetailsAsync(game.AppId);
        if (details.Name != $"App {game.AppId}")
            game.Name = details.Name;
        game.Type = details.Type;
        game.IconUrl = details.IconUrl;
    }

    public static async Task FetchIconUrlAsync(Game game)
    {
        if (!string.IsNullOrEmpty(game.IconUrl))
            return;

        await PopulateGameDetailsAsync(game);
    }

    public static async Task FetchIconUrlsAsync(List<Game> games)
    {
        await FetchBatchDetailsAsync(games);
    }

    private class SteamApp(string appId, string name)
    {
        public string AppId { get; } = appId;
        public string Name { get; } = name;
    }

    private class GameDetails(string type, string iconUrl, string name)
    {
        public string Type { get; } = type;
        public string IconUrl { get; } = iconUrl;
        public string Name { get; } = name;
    }
}
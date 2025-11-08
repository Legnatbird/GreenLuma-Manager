using GreenLuma_Manager.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GreenLuma_Manager.Services
{
    public class SearchService
    {
        private static readonly HttpClient _client = new();
        private static List<SteamApp>? _appListCache;
        private static readonly Dictionary<string, GameDetails> _detailsCache = [];
        private static DateTime _cacheExpiry = DateTime.MinValue;
        private static DateTime _detailsCacheExpiry = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

        private const string SteamAppListUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
        private const string SteamAppDetailsUrl = "https://store.steampowered.com/api/appdetails?appids={0}&l=english";
        private const int MaxConcurrentRequests = 8;
        private static readonly char[] NameSeparators = [' ', '-', ':', '_', '™', '®'];
        private static readonly char[] SpaceSeparator = [' '];
        private static readonly char[] WordSeparators = [' ', '-', ':', '_'];

        static SearchService()
        {
            _client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        private static async Task<List<SteamApp>> GetAppListAsync()
        {
            if (_appListCache != null && DateTime.Now < _cacheExpiry)
                return _appListCache;

            try
            {
                string response = await _client.GetStringAsync(SteamAppListUrl);
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
                            .Where(app => !string.IsNullOrWhiteSpace(app.AppId) &&
                                          !string.IsNullOrWhiteSpace(app.Name))
                    ];

                    _cacheExpiry = DateTime.Now.Add(CacheDuration);
                    return _appListCache;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading app list: {ex.Message}");
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
                string queryLower = query.ToLower();

                var matches = appList
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

                await FetchTypesForGamesAsync(matches);

                return matches;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Search error: {ex.Message}");
                return [];
            }
        }

        private static int CalculateScore(string appName, string query)
        {
            if (string.IsNullOrEmpty(appName))
                return 0;

            string nameLower = appName.ToLower();
            int score = 0;

            if (nameLower == query)
                return 10000;

            if (nameLower.StartsWith(query))
                score += 5000;

            var nameWords = nameLower.Split(NameSeparators,
                StringSplitOptions.RemoveEmptyEntries);
            var queryWords = query.Split(SpaceSeparator,
                StringSplitOptions.RemoveEmptyEntries);

            if (nameWords.Length > 0 && nameWords[0].StartsWith(query))
                score += 3000;

            int matchingWords = queryWords.Count(queryWord =>
                nameWords.Any(w => w.StartsWith(queryWord)));

            if (queryWords.Length > 1 && matchingWords == queryWords.Length)
                score += 2000;

            if (nameLower.Contains(query))
                score += 1000;

            int lengthPenalty = Math.Max(0, (appName.Length - query.Length * 2) / 10);
            score -= lengthPenalty;

            if (HasWordBoundaryMatch(nameLower, query))
                score += 500;

            if (ContainsAllCharsInOrder(nameLower, query))
                score += 100;

            return Math.Max(0, score);
        }

        private static bool HasWordBoundaryMatch(string name, string query)
        {
            var words = name.Split(WordSeparators,
                StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => w.StartsWith(query));
        }

        private static bool ContainsAllCharsInOrder(string text, string chars)
        {
            int charIndex = 0;

            foreach (char c in text)
            {
                if (charIndex < chars.Length && c == chars[charIndex])
                {
                    charIndex++;
                }
            }

            return charIndex == chars.Length;
        }

        private static async Task FetchBatchDetailsAsync(List<Game> games)
        {
            var gamesNeedingDetails = games
                .Where(g => !_detailsCache.ContainsKey(g.AppId))
                .ToList();

            if (gamesNeedingDetails.Count == 0)
            {
                ApplyCachedDetails(games);
                return;
            }

            var syncContext = System.Threading.SynchronizationContext.Current;
            var semaphore = new SemaphoreSlim(MaxConcurrentRequests);
            try
            {
                await Task.WhenAll(gamesNeedingDetails.Select(async game =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var details = await FetchGameDetailsAsync(game.AppId);
                        _detailsCache[game.AppId] = details;

                        if (syncContext != null)
                        {
                            var tcs = new TaskCompletionSource<bool>();
                            syncContext.Post(_ =>
                            {
                                try
                                {
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
                            game.Type = details.Type;
                            game.IconUrl = details.IconUrl;
                        }
                    }
                    catch
                    {
                        _detailsCache[game.AppId] = new GameDetails("Game", string.Empty, string.Empty, true);
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
                }));

                if (gamesNeedingDetails.Count > 0)
                {
                    _detailsCacheExpiry = DateTime.Now.Add(CacheDuration);
                }
            }
            finally
            {
                semaphore.Dispose();
            }
        }

        private static void ApplyCachedDetails(List<Game> games)
        {
            foreach (var game in games)
            {
                if (_detailsCache.TryGetValue(game.AppId, out var details))
                {
                    game.Type = details.Type;
                    if (!string.IsNullOrEmpty(details.IconUrl))
                    {
                        game.IconUrl = details.IconUrl;
                    }
                }
            }
        }

        private static async Task FetchTypesForGamesAsync(List<Game> games)
        {
            var semaphore = new SemaphoreSlim(MaxConcurrentRequests);
            var syncContext = System.Threading.SynchronizationContext.Current;
            try
            {
                await Task.WhenAll(games.Select(async game =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        try
                        {
                            string url = string.Format(SteamAppDetailsUrl, game.AppId);
                            string response = await _client.GetStringAsync(url);
                            var json = JObject.Parse(response)[game.AppId];
                            if (json?["success"]?.Value<bool>() == true)
                            {
                                var data = json["data"];
                                if (data != null)
                                {
                                    string rawType = data["type"]?.ToString().ToLower() ?? "game";
                                    string type = MapSteamTypeToDisplayType(rawType);
                                    if (syncContext != null)
                                    {
                                        var tcs = new TaskCompletionSource<bool>();
                                        syncContext.Post(_ =>
                                        {
                                            try
                                            {
                                                game.Type = type;
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
                                        game.Type = type;
                                    }

                                    return;
                                }
                            }
                        }
                        catch
                        {
                        }

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
                }));
            }
            finally
            {
                semaphore.Dispose();
            }
        }

        private static async Task<GameDetails> FetchGameDetailsAsync(string appId)
        {
            try
            {
                string url = string.Format(SteamAppDetailsUrl, appId);
                string response = await _client.GetStringAsync(url);
                var json = JObject.Parse(response)[appId];

                if (json?["success"]?.Value<bool>() == true)
                {
                    var data = json["data"];
                    if (data != null)
                    {
                        string rawType = data["type"]?.ToString().ToLower() ?? "game";
                        string type = MapSteamTypeToDisplayType(rawType);

                        string? headerImage = data["header_image"]?.ToString();
                        string? capsuleImage = data["capsule_image"]?.ToString();
                        string iconUrl = !string.IsNullOrEmpty(headerImage)
                            ? headerImage
                            : capsuleImage ?? string.Empty;

                        if (!string.IsNullOrEmpty(iconUrl))
                        {
                            return new GameDetails(type, iconUrl, string.Empty, type == "Game");
                        }
                    }
                }

                string? fallbackIconUrl = await TryGetCdnImageAsync(appId);
                return new GameDetails("Game", fallbackIconUrl ?? string.Empty, string.Empty, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching details for {appId}: {ex.Message}");
                string? fallbackIconUrl = await TryGetCdnImageAsync(appId);
                return new GameDetails("Game", fallbackIconUrl ?? string.Empty, string.Empty, true);
            }
        }

        private static async Task<string?> TryGetCdnImageAsync(string appId)
        {
            string[] cdnUrls =
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
                $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/header.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_231x87.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg"
            ];
            foreach (var url in cdnUrls.Distinct())
            {
                try
                {
                    var head = new HttpRequestMessage(HttpMethod.Head, url);
                    var headResp = await _client.SendAsync(head);
                    if (headResp.IsSuccessStatusCode)
                        return url;
                    var getResp = await _client.GetAsync(url);
                    if (getResp.IsSuccessStatusCode)
                        return url;
                }
                catch
                {
                }
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

        public static async Task FetchIconUrlAsync(Game game)
        {
            if (!string.IsNullOrEmpty(game.IconUrl))
                return;

            var details = await FetchGameDetailsAsync(game.AppId);
            game.IconUrl = details.IconUrl;
            game.Type = details.Type;
        }

        public static async Task FetchIconUrlsAsync(List<Game> games)
        {
            var gamesWithoutIcons = games.Where(g => string.IsNullOrEmpty(g.IconUrl)).ToList();

            if (gamesWithoutIcons.Count > 0)
            {
                await FetchBatchDetailsAsync(gamesWithoutIcons);
            }
        }

        public static void ClearCache()
        {
            _appListCache = null;
            _detailsCache?.Clear();
            _cacheExpiry = DateTime.MinValue;
            _detailsCacheExpiry = DateTime.MinValue;
        }

        private class SteamApp(string appId, string name)
        {
            public string AppId { get; } = appId;
            public string Name { get; } = name;
        }

        private class GameDetails(string type, string iconUrl, string logoUrl, bool isGame)
        {
            public string Type { get; } = type;
            public string IconUrl { get; } = iconUrl;
            public string LogoUrl { get; } = logoUrl;
            public bool IsGame { get; } = isGame;
        }
    }
}

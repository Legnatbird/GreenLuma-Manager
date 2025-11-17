using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace GreenLuma_Manager.Services;

public class AppPackageInfo
{
    public string AppId { get; set; } = string.Empty;
    public List<string> Depots { get; set; } = [];
    public List<string> DlcAppIds { get; set; } = [];
    public Dictionary<string, List<string>> DlcDepots { get; set; } = new();
}

public static class DepotService
{
    private const string SteamCmdApiUrl = "https://api.steamcmd.net/v1/info/{0}";
    private static readonly HttpClient Client = new();

    static DepotService()
    {
        Client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        Client.Timeout = TimeSpan.FromSeconds(15);
    }

    public static async Task<AppPackageInfo?> FetchAppPackageInfoAsync(string appId)
    {
        var cacheKey = $"packageinfo:{appId}";
        try
        {
            return await SteamApiCache.GetOrAddAsync(cacheKey, async () =>
            {
                var info = new AppPackageInfo { AppId = appId };
                var url = string.Format(SteamCmdApiUrl, appId);
                var response = await Client.GetStringAsync(url);
                var json = JObject.Parse(response);

                if (json["status"]?.ToString() != "success")
                    return info;

                var data = json["data"]?[appId];
                if (data == null)
                    return info;

                if (data["extended"]?["listofdlc"] is JValue { Value: string dlcString })
                    info.DlcAppIds = dlcString.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                foreach (var dlcAppId in info.DlcAppIds) info.DlcDepots[dlcAppId] = [];

                if (data["depots"] is not JObject depots)
                    return info;

                foreach (var depot in depots.Properties())
                {
                    if (!int.TryParse(depot.Name, out _))
                        continue;

                    if (depot.Name == appId)
                        continue;

                    if (depot.Value is not JObject depotData)
                        continue;

                    if (depotData["manifests"] == null && depotData["depotfromapp"] == null)
                        continue;

                    var dlcAppId = depotData["dlcappid"]?.ToString();

                    if (string.IsNullOrEmpty(dlcAppId))
                    {
                        info.Depots.Add(depot.Name);
                    }
                    else
                    {
                        if (!info.DlcDepots.ContainsKey(dlcAppId)) info.DlcDepots[dlcAppId] = [];

                        if (depot.Name != dlcAppId) info.DlcDepots[dlcAppId].Add(depot.Name);
                    }
                }

                return info;
            });
        }
        catch
        {
            return null;
        }
    }
}
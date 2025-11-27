using SteamKit2;

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
    private static readonly SteamManager Manager = new();

    public static async Task<AppPackageInfo?> FetchAppPackageInfoAsync(string appId)
    {
        if (!uint.TryParse(appId, out var id))
            return null;

        return await Manager.GetAppPackageInfoAsync(id);
    }

    private class SteamManager : IDisposable
    {
        private readonly Task _callbackLoop;
        private readonly CallbackManager _callbackManager;
        private readonly TaskCompletionSource _connectedTcs;
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource _loggedOnTcs;
        private readonly SteamApps _steamApps;
        private readonly SteamClient _steamClient;
        private readonly SteamUser _steamUser;
        private bool _isConnected;
        private bool _isLoggedOn;

        private bool _isRunning;

        public SteamManager()
        {
            _steamClient = new SteamClient();
            _callbackManager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>()!;
            _steamApps = _steamClient.GetHandler<SteamApps>()!;

            _cts = new CancellationTokenSource();
            _connectedTcs = new TaskCompletionSource();
            _loggedOnTcs = new TaskCompletionSource();

            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

            _isRunning = true;
            _callbackLoop = Task.Run(CallbackLoop);

            _steamClient.Connect();
        }

        public void Dispose()
        {
            _isRunning = false;
            _cts.Cancel();
            _steamClient.Disconnect();
            _callbackLoop.Wait(1000);
            _cts.Dispose();
        }

        public async Task<AppPackageInfo?> GetAppPackageInfoAsync(uint appId)
        {
            try
            {
                await EnsureReadyAsync();

                var request = new SteamApps.PICSRequest { ID = appId, AccessToken = 0 };
                var job = _steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, []);

                var result = await job.ToTask();

                if (result.Failed || result.Results == null)
                    return null;

                foreach (var callback in result.Results)
                    if (callback.Apps.TryGetValue(appId, out var appData))
                        return ParseAppInfo(appId, appData);

                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task EnsureReadyAsync()
        {
            if (!_isConnected)
                await _connectedTcs.Task;

            if (!_isLoggedOn)
                await _loggedOnTcs.Task;
        }

        private static AppPackageInfo? ParseAppInfo(uint appId,
            SteamApps.PICSProductInfoCallback.PICSProductInfo appData)
        {
            var kv = appData.KeyValues;

            var type = kv["common"]["type"].Value;
            if (string.Equals(type, "depot", StringComparison.OrdinalIgnoreCase))
                return null;

            var info = new AppPackageInfo
            {
                AppId = appId.ToString()
            };

            var dlcList = kv["common"]["extended"]["listofdlc"].Value;
            if (!string.IsNullOrEmpty(dlcList))
                info.DlcAppIds = dlcList.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var dlcId in info.DlcAppIds) info.DlcDepots[dlcId] = [];

            var depotsNode = kv["depots"];
            foreach (var child in depotsNode.Children)
            {
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                if (depotId == appId)
                    continue;

                if (child["manifests"] == KeyValue.Invalid && child["depotfromapp"] == KeyValue.Invalid)
                    continue;

                var dlcAppId = child["dlcappid"].Value;

                if (!string.IsNullOrEmpty(dlcAppId) && info.DlcDepots.TryGetValue(dlcAppId, out var dlcDepotList))
                    dlcDepotList.Add(depotId.ToString());
                else
                    info.Depots.Add(depotId.ToString());
            }

            return info;
        }

        private async Task CallbackLoop()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                await Task.Delay(100);
            }
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            _isConnected = true;
            _connectedTcs.TrySetResult();
            _steamUser.LogOnAnonymous();
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _isConnected = false;
            _isLoggedOn = false;
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                _isLoggedOn = true;
                _loggedOnTcs.TrySetResult();
            }
        }
    }
}
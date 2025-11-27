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
        private readonly SteamClient _steamClient;
        private readonly CallbackManager _callbackManager;
        private readonly SteamUser _steamUser;
        private readonly SteamApps _steamApps;

        private bool _isRunning;
        private bool _isConnected;
        private bool _isLoggedOn;
        private readonly Task _callbackLoop;
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource _connectedTcs;
        private readonly TaskCompletionSource _loggedOnTcs;

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

        public async Task<AppPackageInfo?> GetAppPackageInfoAsync(uint appId)
        {
            try
            {
                await EnsureReadyAsync();

                var job = _steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest>
                {
                    new() { ID = appId, AccessToken = 0 }
                }, []);

                var result = await job.ToTask();

                if (result.Failed || result.Results == null || !result.Results.Any())
                    return null;

                if (!result.Results[0].Apps.TryGetValue(appId, out var appData))
                    return null;

                return ParseAppInfo(appId, appData);
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

        private static AppPackageInfo ParseAppInfo(uint appId,
            SteamApps.PICSProductInfoCallback.PICSProductInfo appData)
        {
            var info = new AppPackageInfo
            {
                AppId = appId.ToString()
            };

            var kv = appData.KeyValues;

            var dlcList = kv["common"]["extended"]["listofdlc"].Value;
            if (!string.IsNullOrEmpty(dlcList))
            {
                info.DlcAppIds = dlcList.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            foreach (var dlcId in info.DlcAppIds)
            {
                info.DlcDepots[dlcId] = [];
            }

            var depotsNode = kv["depots"];
            foreach (var child in depotsNode.Children)
            {
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                if (depotId == appId)
                    continue;

                var dlcAppId = child["dlcappid"].Value;

                if (!string.IsNullOrEmpty(dlcAppId) && info.DlcDepots.TryGetValue(dlcAppId, out var dlcDepotList))
                {
                    dlcDepotList.Add(depotId.ToString());
                }
                else
                {
                    info.Depots.Add(depotId.ToString());
                }
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

        public void Dispose()
        {
            _isRunning = false;
            _cts.Cancel();
            _steamClient.Disconnect();
            _callbackLoop.Wait(1000);
            _cts.Dispose();
        }
    }
}
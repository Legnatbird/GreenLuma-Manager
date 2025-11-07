using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GreenLuma_Manager
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public const string Version = "RC1";

        private readonly ConfigService _configService;
        private readonly GreenLumaService _greenLumaService;
        private readonly UpdateService _updateService;

        private readonly ObservableCollection<Game> _searchResults;
        private readonly ObservableCollection<Game> _games;
        private readonly ObservableCollection<string> _profiles;

        private Config? _config;
        private Profile? _currentProfile;
        private CancellationTokenSource? _searchCts;
        private DispatcherTimer? _loadingDotsTimer;

        public ICommand FocusSearchCommand { get; }
        public ICommand GenerateApplistCommand { get; }
        public ICommand LaunchGreenlumaCommand { get; }
        public ICommand ToggleStealthCommand { get; }

        public MainWindow()
        {
            InitializeComponent();

            _configService = new ConfigService();
            _greenLumaService = new GreenLumaService();
            _updateService = new UpdateService();

            _searchResults = [];
            _games = [];
            _profiles = [];

            FocusSearchCommand = new RelayCommand(_ => txtSearchInput.Focus());
            GenerateApplistCommand =
                new RelayCommand(_ => GenerateApplistButton_Click(btnGenerateApplist, new RoutedEventArgs()));
            LaunchGreenlumaCommand =
                new RelayCommand(_ => LaunchGreenlumaButton_Click(btnLaunchGreenluma, new RoutedEventArgs()));
            ToggleStealthCommand =
                new RelayCommand(_ => tglStealthMode.IsChecked = !tglStealthMode.IsChecked.GetValueOrDefault());

            DataContext = this;
            dgResults.ItemsSource = _searchResults;
            lstGames.ItemsSource = _games;
            cmbProfile.ItemsSource = _profiles;

            InitializeLoadingTimer();
            LoadConfig();
            LoadProfiles();
            CheckForUpdates();
            CheckPathsOnStartup();
            UpdateGameListState();
        }

        private void InitializeLoadingTimer()
        {
            _loadingDotsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _loadingDotsTimer.Tick += LoadingDotsTimer_Tick;
        }

        private void LoadingDotsTimer_Tick(object? sender, EventArgs e)
        {
            string text = txtLoadingDots.Text;
            txtLoadingDots.Text = text.Length >= 3 ? "." : text + ".";
        }

        private void LoadConfig()
        {
            _config = ConfigService.Load();
            if (_config != null)
            {
                tglStealthMode.IsChecked = _config.NoHook;
            }
        }

        private void LoadProfiles()
        {
            _profiles.Clear();

            foreach (var profile in ProfileService.LoadAll())
            {
                if (profile.Name != "__empty__")
                {
                    _profiles.Add(profile.Name);
                }
            }

            if (_profiles.Count == 0)
            {
                _profiles.Add("default");
            }

            string lastProfile = _config?.LastProfile ?? "default";

            if (_profiles.Contains(lastProfile))
            {
                cmbProfile.SelectedItem = lastProfile;
            }
            else
            {
                cmbProfile.SelectedIndex = 0;
            }

            if (_profiles.Count == 1)
            {
                _profiles.Add("__empty__");
            }
        }

        private async void CheckForUpdates()
        {
            if (_config?.DisableUpdateCheck == true)
                return;

            try
            {
                var updateInfo = await UpdateService.CheckForUpdatesAsync();

                if (updateInfo?.UpdateAvailable == true)
                {
                    await HandleUpdateAvailable(updateInfo);
                }
            }
            catch
            {
            }
        }

        private async Task HandleUpdateAvailable(UpdateInfo updateInfo)
        {
            if (_config?.AutoUpdate == true && !string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
            {
                var result = CustomMessageBox.Show(
                    $"Current Version: {updateInfo.CurrentVersion}\nLatest Version: {updateInfo.LatestVersion}\n\n" +
                    "Auto-update is enabled. The update will be downloaded and installed automatically.\n\n" +
                    "The application will restart to complete the update.",
                    "Update Available",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Asterisk);

                if (result == MessageBoxResult.OK)
                {
                    if (await UpdateService.PerformAutoUpdateAsync(updateInfo.DownloadUrl))
                    {
                        Application.Current.Shutdown();
                    }
                    else
                    {
                        ShowToast("Auto-update failed. Please download manually.", false);
                        LaunchBrowser(updateInfo.DownloadUrl);
                    }
                }
            }
            else
            {
                var result = CustomMessageBox.Show(
                    $"Current Version: {updateInfo.CurrentVersion}\nLatest Version: {updateInfo.LatestVersion}\n\n" +
                    "Would you like to download the update now?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Asterisk);

                if (result == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
                {
                    LaunchBrowser(updateInfo.DownloadUrl);
                }
            }
        }

        private void CheckPathsOnStartup()
        {
            if (_config == null)
                return;

            if (!_config.FirstRun ||
                (!string.IsNullOrWhiteSpace(_config.SteamPath) && !string.IsNullOrWhiteSpace(_config.GreenLumaPath)))
                return;

            _config.FirstRun = false;
            ConfigService.Save(_config);

            Dispatcher.BeginInvoke((Action)(() =>
            {
                var result = CustomMessageBox.Show(
                    "Steam and GreenLuma paths could not be detected automatically.\n\n" +
                    "Please configure them in Settings to use all features.",
                    "Setup Required",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Asterisk);

                if (result == MessageBoxResult.OK)
                {
                    SettingsButton_Click(null, null!);
                }
            }), DispatcherPriority.Loaded);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        private void GitHubButton_Click(object sender, RoutedEventArgs e)
        {
            LaunchBrowser("https://github.com/3vil3vo/GreenLuma-Manager");
        }

        private void SettingsButton_Click(object? sender, RoutedEventArgs? e)
        {
            if (_config == null)
                return;

            var dialog = new SettingsDialog(_config);

            if (dialog.ShowDialog() == true)
            {
                LoadConfig();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SearchGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            txtSearchInput.Focus();
        }

        private void SearchInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (txtSearchPlaceholder.Visibility != Visibility.Visible)
                return;

            AnimatePlaceholder(0.5, 0.0, () => txtSearchPlaceholder.Visibility = Visibility.Collapsed);
        }

        private void SearchInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(txtSearchInput.Text))
                return;

            txtSearchPlaceholder.Visibility = Visibility.Visible;
            txtSearchPlaceholder.Opacity = 0.0;
            AnimatePlaceholder(0.0, 0.5);
        }

        private void AnimatePlaceholder(double from, double to, Action? onComplete = null)
        {
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(150));

            Storyboard.SetTarget(animation, txtSearchPlaceholder);
            Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));

            storyboard.Children.Add(animation);

            if (onComplete != null)
            {
                storyboard.Completed += (_, args) => onComplete();
            }

            storyboard.Begin();
        }

        private void SearchInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return)
                return;

            Keyboard.ClearFocus();
            e.Handled = true;
            Dispatcher.BeginInvoke((Action)(() => btnSearch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))));
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            string query = txtSearchInput.Text.Trim();

            if (!ValidateSearchQuery(query))
                return;

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                await PerformSearch(query, token);
            }
            catch (OperationCanceledException)
            {
                _loadingDotsTimer?.Stop();
            }
            catch (Exception ex)
            {
                _loadingDotsTimer?.Stop();
                ShowToast("Search failed: " + ex.Message, false);
            }
        }

        private bool ValidateSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                ShowToast("Enter a search term", false);
                return false;
            }

            if (query.Length < 3)
            {
                ShowToast("Search term must be at least 3 characters", false);
                return false;
            }

            if (query.Length > 200)
            {
                ShowToast("Search term is too long (max 200 characters)", false);
                return false;
            }

            return true;
        }

        private async Task PerformSearch(string query, CancellationToken token)
        {
            Keyboard.ClearFocus();

            ShowSearchLoading();

            var results = await Task.Run(() => SearchService.SearchAsync(query), token);

            if (token.IsCancellationRequested)
                return;

            DisplaySearchResults(results);

            await SearchService.FetchIconUrlsAsync(results);
        }

        private void ShowSearchLoading()
        {
            dgResults.Visibility = Visibility.Collapsed;
            pnlEmptyResults.Visibility = Visibility.Collapsed;
            pnlSearchLoading.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var scaleIn = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            pnlSearchLoading.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            var transform = (ScaleTransform)pnlSearchLoading.RenderTransform;
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

            txtLoadingDots.Text = ".";
            _loadingDotsTimer?.Start();
        }

        private void DisplaySearchResults(List<Game> results)
        {
            _searchResults.Clear();

            foreach (var game in results)
            {
                _searchResults.Add(game);
            }

            SortResultsByName();
            txtResultCount.Text = _searchResults.Count.ToString();
            _loadingDotsTimer?.Stop();

            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += async (_, args) =>
            {
                pnlSearchLoading.Visibility = Visibility.Collapsed;
                await Task.Delay(50);

                if (_searchResults.Count > 0)
                {
                    ShowResultsGrid();
                }
                else
                {
                    ShowEmptyResults();
                }
            };

            pnlSearchLoading.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void SortResultsByName()
        {
            dgResults.Items.SortDescriptions.Clear();
            dgResults.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));

            foreach (var column in dgResults.Columns)
            {
                column.SortDirection = null;
            }

            var nameColumn = dgResults.Columns.FirstOrDefault(c => c.Header?.ToString() == "NAME");
            if (nameColumn != null)
            {
                nameColumn.SortDirection = ListSortDirection.Ascending;
            }
        }

        private void ShowResultsGrid()
        {
            dgResults.Opacity = 0.0;
            dgResults.Visibility = Visibility.Visible;
            pnlEmptyResults.Visibility = Visibility.Collapsed;

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            dgResults.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ShowToast($"Found {_searchResults.Count} results");
        }

        private void ShowEmptyResults()
        {
            dgResults.Visibility = Visibility.Collapsed;
            pnlEmptyResults.Visibility = Visibility.Visible;
            ShowToast("No results found", false);
        }

        private void ResultRow_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (dgResults.SelectedItem is Game selectedGame)
            {
                var fakeButton = new Button { Tag = selectedGame };
                AddGameButton_Click(fakeButton, new RoutedEventArgs());
            }
        }

        private void AddGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Game result)
                return;

            if (_games.Any(g => g.AppId == result.AppId))
            {
                ShowToast(result.Name + " already in profile", false);
                return;
            }

            var newGame = new Game
            {
                AppId = result.AppId,
                Name = result.Name,
                Type = result.Type,
                IconUrl = result.IconUrl
            };

            _games.Add(newGame);
            ShowToast("Added " + result.Name);
            SaveCurrentProfile();
            UpdateGameListState();

            if (!string.IsNullOrEmpty(newGame.IconUrl))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? cachedPath =
                            await IconCacheService.DownloadAndCacheIconAsync(newGame.AppId, newGame.IconUrl);
                        if (string.IsNullOrEmpty(cachedPath))
                        {
                            await SearchService.FetchIconUrlAsync(newGame);
                            if (!string.IsNullOrWhiteSpace(newGame.IconUrl))
                            {
                                cachedPath =
                                    await IconCacheService.DownloadAndCacheIconAsync(newGame.AppId, newGame.IconUrl);
                            }
                        }

                        if (!string.IsNullOrEmpty(cachedPath))
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                newGame.IconUrl = cachedPath;
                                SaveCurrentProfile();
                            });
                        }
                    }
                    catch
                    {
                    }
                });
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SearchService.FetchIconUrlAsync(newGame);
                        if (!string.IsNullOrWhiteSpace(newGame.IconUrl))
                        {
                            var cachedPath =
                                await IconCacheService.DownloadAndCacheIconAsync(newGame.AppId, newGame.IconUrl);
                            if (!string.IsNullOrEmpty(cachedPath))
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    newGame.IconUrl = cachedPath;
                                    SaveCurrentProfile();
                                });
                            }
                        }
                    }
                    catch
                    {
                    }
                });
            }
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbProfile.SelectedItem == null)
                return;

            string? profileName = cmbProfile.SelectedItem.ToString();

            if (profileName == "__empty__")
            {
                RestorePreviousProfile(e);
                return;
            }

            if (profileName != null)
            {
                LoadProfile(profileName);
            }
        }

        private void RestorePreviousProfile(SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is string removedItem && removedItem != "__empty__")
            {
                cmbProfile.SelectedItem = removedItem;
            }
            else
            {
                foreach (var profile in _profiles)
                {
                    if (profile != "__empty__")
                    {
                        cmbProfile.SelectedItem = profile;
                        break;
                    }
                }
            }
        }

        private async void LoadProfile(string profileName)
        {
            _currentProfile = ProfileService.Load(profileName);

            if (_currentProfile == null)
            {
                _currentProfile = new Profile { Name = profileName };
                ProfileService.Save(_currentProfile);
            }

            var gamesToProcess = _currentProfile.Games.ToList();
            var semaphore = new SemaphoreSlim(6);
            var tasks = new List<Task>();
            bool changed = false;

            foreach (var game in gamesToProcess)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        string? cachedPath = IconCacheService.GetCachedIconPath(game.AppId);
                        if (!string.IsNullOrEmpty(cachedPath))
                        {
                            game.IconUrl = cachedPath;
                        }
                        else
                        {
                            string? path = null;
                            if (!string.IsNullOrWhiteSpace(game.IconUrl) &&
                                game.IconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId, game.IconUrl);
                            }

                            if (string.IsNullOrEmpty(path))
                            {
                                await SearchService.FetchIconUrlAsync(game);
                                if (!string.IsNullOrWhiteSpace(game.IconUrl))
                                {
                                    path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId, game.IconUrl);
                                }
                            }

                            if (!string.IsNullOrEmpty(path))
                            {
                                game.IconUrl = path;
                                changed = true;
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            _games.Clear();
            foreach (var game in gamesToProcess)
            {
                _games.Add(game);
            }

            if (_config != null)
            {
                _config.LastProfile = profileName;
                ConfigService.Save(_config);
            }

            if (changed && _currentProfile != null)
            {
                ProfileService.Save(_currentProfile);
            }

            UpdateGameListState();
        }

        private void SaveCurrentProfile()
        {
            if (_currentProfile == null)
                return;

            _currentProfile.Games.Clear();

            foreach (var game in _games)
            {
                _currentProfile.Games.Add(game);
            }

            ProfileService.Save(_currentProfile);
        }

        private void ProfileOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton button || button.ContextMenu == null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
            button.ContextMenu.Closed += (_, args) => button.IsChecked = false;
        }

        private void AddProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CreateProfileDialog();

            if (dialog.ShowDialog() != true || dialog.Result == null)
                return;

            var newProfile = dialog.Result;

            if (_profiles.Any(p => p.Equals(newProfile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast("Profile name already exists", false);
                return;
            }

            _profiles.Remove("__empty__");

            ProfileService.Save(newProfile);
            _profiles.Add(newProfile.Name);
            cmbProfile.SelectedItem = newProfile.Name;

            ShowToast($"Created profile '{newProfile.Name}'");
        }

        private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON files|*.json|All files|*.*",
                Title = "Import Profile"
            };

            if (dialog.ShowDialog() != true)
                return;

            var profile = ProfileService.Import(dialog.FileName);

            if (profile == null)
            {
                ShowToast("Failed to import profile", false);
                return;
            }

            if (!ValidateProfileName(profile.Name))
                return;

            if (_profiles.Contains(profile.Name))
            {
                var result = CustomMessageBox.Show(
                    $"Profile '{profile.Name}' already exists. Overwrite?",
                    "Import Profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;
            }
            else
            {
                _profiles.Remove("__empty__");
                _profiles.Add(profile.Name);
            }

            var semaphore = new SemaphoreSlim(6);
            var tasks = new List<Task>();
            foreach (var game in profile.Games)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        game.IconUrl = string.Empty;
                        await SearchService.FetchIconUrlAsync(game);
                        if (!string.IsNullOrWhiteSpace(game.IconUrl))
                        {
                            var path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId, game.IconUrl);
                            if (!string.IsNullOrEmpty(path))
                            {
                                game.IconUrl = path;
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);

            ProfileService.Save(profile);
            cmbProfile.SelectedItem = profile.Name;
            LoadProfile(profile.Name);
            ShowToast($"Imported profile '{profile.Name}'");
        }

        private bool ValidateProfileName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName) || profileName.Length > 50)
            {
                ShowToast("Invalid profile name in imported file", false);
                return false;
            }

            if (profileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                profileName.Contains('/') || profileName.Contains('\\'))
            {
                ShowToast("Profile name contains invalid characters", false);
                return false;
            }

            return true;
        }

        private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProfile == null)
                return;

            var dialog = new SaveFileDialog
            {
                Filter = "JSON files|*.json|All files|*.*",
                FileName = _currentProfile.Name + ".json",
                Title = "Export Profile"
            };

            if (dialog.ShowDialog() != true)
                return;

            ProfileService.Export(_currentProfile, dialog.FileName);
            ShowToast($"Exported '{_currentProfile.Name}'");
        }

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProfile == null || _currentProfile.Name.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("Cannot delete default profile", false);
                return;
            }

            var result = CustomMessageBox.Show(
                $"Delete profile '{_currentProfile.Name}'?",
                "Delete Profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Exclamation);

            if (result != MessageBoxResult.Yes)
                return;

            string deletedName = _currentProfile.Name;

            ProfileService.Delete(_currentProfile.Name);
            _profiles.Remove(_currentProfile.Name);

            if (_profiles.Count == 1 && !_profiles.Contains("__empty__"))
            {
                _profiles.Add("__empty__");
            }

            cmbProfile.SelectedIndex = 0;
            ShowToast($"Deleted profile '{deletedName}'");
        }

        private void RemoveGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Game result)
                return;

            var game = _games.FirstOrDefault(g => g.AppId == result.AppId);

            if (game == null)
                return;

            IconCacheService.DeleteCachedIcon(game.AppId);

            _games.Remove(game);
            ShowToast("Game removed from profile");
            SaveCurrentProfile();
            UpdateGameListState();
        }

        private void UpdateGameListState()
        {
            txtGameCount.Text = _games.Count.ToString();
            pnlEmptyGames.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StealthMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null || sender is not ToggleButton toggleButton)
                return;

            _config.NoHook = toggleButton.IsChecked.GetValueOrDefault();
            ConfigService.Save(_config);

            string status = toggleButton.IsChecked.GetValueOrDefault() ? "enabled" : "disabled";
            ShowToast($"Stealth mode {status}");
        }

        private async void GenerateApplistButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidatePathsForGeneration())
                return;

            if (_currentProfile == null)
            {
                ShowToast("No profile selected", false);
                return;
            }

            if (_games.Count == 0)
            {
                var result = CustomMessageBox.Show(
                    "This profile contains no games. Clear the existing AppList?",
                    "Clear AppList",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            btnGenerateApplist.IsEnabled = false;

            try
            {
                SaveCurrentProfile();

                if (_config != null && await GreenLumaService.GenerateAppListAsync(_currentProfile, _config))
                {
                    string gameWord = _games.Count == 1 ? "game" : "games";
                    ShowToast($"Generated AppList with {_games.Count} {gameWord}");
                }
                else
                {
                    ShowToast("Failed to generate AppList - check paths in settings", false);
                }
            }
            catch (Exception ex)
            {
                ShowToast("Error: " + ex.Message, false);
            }
            finally
            {
                btnGenerateApplist.IsEnabled = true;
            }
        }

        private async void LaunchGreenlumaButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidatePathsForLaunch())
                return;

            if (!await CheckAndGenerateAppList())
                return;

            var result = CustomMessageBox.Show(
                "This will close Steam and launch GreenLuma. Continue?",
                "Launch GreenLuma",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            btnLaunchGreenluma.IsEnabled = false;

            try
            {
                if (_config != null && await GreenLumaService.LaunchGreenLumaAsync(_config))
                {
                    ShowToast("GreenLuma launched successfully");
                }
                else
                {
                    ShowToast("Failed to launch GreenLuma. Check settings.", false);
                }
            }
            catch (Exception ex)
            {
                ShowToast("Error: " + ex.Message, false);
            }
            finally
            {
                btnLaunchGreenluma.IsEnabled = true;
            }
        }

        private bool ValidatePathsForGeneration()
        {
            if (_config == null || string.IsNullOrWhiteSpace(_config.GreenLumaPath) ||
                string.IsNullOrWhiteSpace(_config.SteamPath))
            {
                var result = CustomMessageBox.Show(
                    "Steam and GreenLuma paths must be configured first.\n\nOpen settings now?",
                    "Paths Not Set",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Exclamation);

                if (result == MessageBoxResult.Yes)
                {
                    SettingsButton_Click(null, null!);
                }

                return false;
            }

            return true;
        }

        private bool ValidatePathsForLaunch()
        {
            return ValidatePathsForGeneration();
        }

        private async Task<bool> CheckAndGenerateAppList()
        {
            if (_config != null && GreenLumaService.IsAppListGenerated(_config))
                return true;

            var result = CustomMessageBox.Show(
                "AppList has not been generated.\n\nGenerate AppList now, or continue without it?",
                "AppList Not Generated",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            switch (result)
            {
                case MessageBoxResult.Cancel:
                    return false;

                case MessageBoxResult.Yes:
                    GenerateApplistButton_Click(btnGenerateApplist, new RoutedEventArgs());
                    await Task.Delay(500);
                    break;
            }

            return true;
        }

        private void ShowToast(string message, bool isSuccess = true)
        {
            ToastMessage.Text = message;

            if (ToastIcon != null)
            {
                ToastIcon.Fill = isSuccess
                    ? (Brush)Resources["Success"]
                    : (Brush)Resources["Danger"];
            }

            Toast.Visibility = Visibility.Visible;
            Toast.Opacity = 0.0;

            var storyboard = new Storyboard();

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200));
            Storyboard.SetTarget(fadeIn, Toast);
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(fadeIn);

            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(3500)
            };
            Storyboard.SetTarget(fadeOut, Toast);
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(fadeOut);

            storyboard.Completed += (_, args) =>
            {
                Toast.Visibility = Visibility.Collapsed;
                Toast.Opacity = 1.0;
            };

            storyboard.Begin();
        }

        private static void LaunchBrowser(string url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
        {
            private readonly Action<object?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            private readonly Func<object?, bool>? _canExecute = canExecute;

            public bool CanExecute(object? parameter)
            {
                return _canExecute?.Invoke(parameter) ?? true;
            }

            public void Execute(object? parameter)
            {
                _execute(parameter);
            }

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }
        }
    }
}

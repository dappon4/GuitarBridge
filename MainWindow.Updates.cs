using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GuitarBridge;

public partial class MainWindow
{
    private readonly UpdateService updates = new();
    private readonly CancellationTokenSource updateCancellation = new();
    private DispatcherTimer? updateTimer;
    private AppRelease? availableUpdate;
    private PreparedUpdate? preparedUpdate;
    private bool checkingUpdate, downloadingUpdate, applyingUpdate;

    internal void StartUpdateChecks()
    {
        UpdateVersionText.Text = "Installed version " + UpdateService.DisplayVersion;
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updateTimer.Tick += async (_, _) => { if (AutomaticUpdates.IsChecked == true) await CheckUpdatesAsync(false); };
        updateTimer.Start();
        if (AutomaticUpdates.IsChecked == true) _ = CheckUpdatesAsync(false);
        _ = Task.Run(() =>
        {
            try
            {
                string cache = Path.Combine(Settings.Folder, "Updates");
                if (!Directory.Exists(cache)) return;
                foreach (string directory in Directory.GetDirectories(cache))
                    if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 &&
                        Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-7))
                        try { Directory.Delete(directory, recursive: true); } catch { }
            }
            catch { }
        });
    }

    internal void CloseUpdateChecks()
    {
        updateTimer?.Stop();
        updateCancellation.Cancel();
        updates.Dispose();
    }

    internal async Task OpenUpdatesAsync()
    {
        UpdateVersionText.Text = "Installed version " + UpdateService.DisplayVersion;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        UpdatePopup.IsOpen = true;
        UpdateInstallState();
        await CheckUpdatesAsync(true);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdate || downloadingUpdate) return;
        checkingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        if (manual) UpdateStatusText.Text = "Checking GitHub Releases…";
        try
        {
            var release = await updates.CheckAsync(updateCancellation.Token);
            availableUpdate = release;
            UpdateBadge.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
            InstallUpdateButton.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateButton.ToolTip = release == null ? "App updates" : $"Version {release.Version.ToString(3)} is available";
            UpdateStatusText.Text = release == null ? "You’re up to date. No newer stable release is available." : $"Version {release.Version.ToString(3)} is available. Update when you’re ready.";
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Could not check for updates. " + ex.Message + " Try again later.";
        }
        finally
        {
            checkingUpdate = false;
            CheckUpdateButton.IsEnabled = true;
            UpdateInstallState();
        }
    }

    internal void UpdateInstallState()
    {
        bool routing = IsRouting || changingRoute;
        InstallUpdateButton.IsEnabled = !routing && !checkingUpdate && !downloadingUpdate;
        InstallUpdateButton.Content = routing ? "Stop routing to update" : "Update and restart";
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate == null || downloadingUpdate || IsRouting || changingRoute) { UpdateInstallState(); return; }
        downloadingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateInstallState();
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            UpdateStatusText.Text = "Downloading and verifying the update…";
            bool installer = UpdateService.IsMsiInstallation(Environment.ProcessPath!);
            var progress = new Progress<int>(value => UpdateProgress.Value = value);
            if (preparedUpdate == null || preparedUpdate.Version != availableUpdate.Version || preparedUpdate.Installer != installer || !File.Exists(preparedUpdate.File))
                preparedUpdate = await updates.DownloadAsync(availableUpdate, installer, Path.Combine(Settings.Folder, "Updates"), progress, updateCancellation.Token);
            else await UpdateService.VerifyAsync(preparedUpdate.File, preparedUpdate.Sha256, updateCancellation.Token);
            if (IsRouting || changingRoute) { UpdateStatusText.Text = "Download verified. Stop routing, then click Update and restart."; return; }
            IsEnabled = false;
            applyingUpdate = true;
            await UpdateWorker.StartAsync(preparedUpdate, updateCancellation.Token);
            await ExitAsync();
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception ex) { UpdateStatusText.Text = "Update did not start. " + ex.Message; }
        finally
        {
            IsEnabled = true;
            applyingUpdate = false;
            downloadingUpdate = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
            UpdateInstallState();
        }
    }

    private async void Updates_Click(object sender, RoutedEventArgs e) => await OpenUpdatesAsync();
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);
    private void AutomaticUpdates_Click(object sender, RoutedEventArgs e)
    {
        try { if (ready) SaveSettings(); } catch (Exception ex) { UpdateStatusText.Text = "Could not save the update preference: " + ex.Message; }
    }
}

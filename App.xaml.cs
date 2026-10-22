using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;

namespace GuitarBridge;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? tray;
    private MainWindow? window;
    private Mutex? mutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private Icon? icon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && (e.Args[0] == "--default-output-tests" || e.Args[0] == "--default-output-probe"))
        {
            RunDefaultOutputTests(e.Args[1], e.Args[0] == "--default-output-probe");
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            RunUpdateWorker(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--update-tests")
        {
            RunUpdateTests(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--write-licenses")
        {
            try { File.WriteAllText(e.Args[1], LicenseNotices.ReadAll()); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--buffer-benchmark")
        {
            try { File.WriteAllText(e.Args[1], SelfTests.BenchmarkBuffer()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--native-output-probe")
        {
            try { StartupProbe.TestNativeOutput(e.Args[1]); Shutdown(0); }
            catch (Exception ex) { File.AppendAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--startup-probe")
        {
            RunStartupProbe(e.Args[1], Array.IndexOf(e.Args, "--shared") >= 0, Array.IndexOf(e.Args, "--48000") >= 0 ? 48000 : null, Array.IndexOf(e.Args, "--default-output") >= 0);
            return;
        }
        if (e.Args.Length >= 2 && (e.Args[0] == "--audio-host" || e.Args[0] == "--audio-host-test" || e.Args[0] == "--audio-host-start-test" || e.Args[0] == "--audio-host-reset-test"))
        {
            RunAudioHost(e.Args[1], e.Args[0] == "--audio-host-test", e.Args[0] == "--audio-host-start-test",
                e.Args[0] == "--audio-host-reset-test" && e.Args.Length >= 3 ? e.Args[2] : null);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--host-tests")
        {
            RunHostTests(e.Args[1]);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--interactive-test")
        {
            window = new MainWindow(false);
            window.ShowInTaskbar = true;
            MainWindow = window;
            window.Show();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--audio-probe")
        {
            File.WriteAllText(e.Args[1], HardwareProbe.Run());
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            try { File.WriteAllText(e.Args[1], SelfTests.Run()); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--smoke-test")
        {
            try
            {
                window = new MainWindow(false);
                window.Show();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        window.UpdateLayout();
                        if (window.WindowStyle != WindowStyle.None || window.RouteButton.Content != null)
                            throw new Exception("Custom chrome or text-free routing switch check failed.");
                        const string attention = "Verification message: enable the required inputs in your ASIO driver's control panel, then scan channels and select an input.\n\nThis complete message must remain readable in both the hover tooltip and the click-to-open details panel, without being truncated in the status strip.";
                        window.ShowError(attention);
                        if (window.WarningButton.Visibility != Visibility.Visible || window.WarningTipText.Text != attention || window.WarningPopupText.Text != attention)
                            throw new Exception("Full attention message was not exposed by both warning views.");
                        window.Warning_Click(window.WarningButton, new RoutedEventArgs());
                        if (!window.WarningPopup.IsOpen) throw new Exception("Warning click did not open details.");
                        window.Warning_Click(window.WarningButton, new RoutedEventArgs());
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(e.Args[1])) encoder.Save(file);
                        window.UpdatePopup.IsOpen = true;
                        window.UpdatePopup.Child.UpdateLayout();
                        var updatePanel = (FrameworkElement)window.UpdatePopup.Child;
                        var updateBitmap = new RenderTargetBitmap((int)Math.Ceiling(updatePanel.ActualWidth), (int)Math.Ceiling(updatePanel.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        updateBitmap.Render(updatePanel);
                        var updateEncoder = new PngBitmapEncoder(); updateEncoder.Frames.Add(BitmapFrame.Create(updateBitmap));
                        using (var file = File.Create(e.Args[1] + ".updates.png")) updateEncoder.Save(file);
                        window.UpdatePopup.IsOpen = false;
                        // Exercise the close-to-tray contract without saving test settings.
                        window.CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        if (window.IsVisible || window.ShowInTaskbar) throw new Exception("Tray window visibility contract failed.");
                        File.WriteAllText(e.Args[1] + ".txt", "PASS: no native title bar; text-free routing switch; full warning tooltip and click popup; custom close button hides to tray.\nPASS: Material 3 window loaded, rendered, and hidden; ShowInTaskbar=false.\nDrivers: " + window.Drivers.Items.Count + "\nOutputs: " + window.Outputs.Items.Count + "\nSelected output: " + window.Outputs.SelectedItem);
                        Shutdown(0);
                    }
                    catch (Exception ex) { File.WriteAllText(e.Args[1] + ".txt", ex.ToString()); Shutdown(1); }
                }));
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1] + ".txt", ex.ToString()); Shutdown(1); }
            return;
        }
        mutex = new Mutex(true, "Local\\GuitarBridge.App.v1", out bool first);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GuitarBridge.Show.v1");
        if (!first) { showEvent.Set(); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try { window?.StopRouting(); } catch { }
            tray?.ShowBalloonTip(5000, "Guitar Bridge needs attention", args.Exception.Message, Forms.ToolTipIcon.Error);
        };
        window = new MainWindow(!(e.Args.Length > 0 && e.Args[0] == "--tray-test"));
        MainWindow = window;
        icon = CreateTrayIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Guitar Bridge", null, (_, _) => ShowSettings());
        var route = menu.Items.Add("Start routing", null, (_, _) => window.ToggleRouting());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Check for updates", null, async (_, _) => await window.OpenUpdatesAsync());
        menu.Items.Add("Open-source licenses", null, (_, _) =>
        {
            try { LicenseNotices.Show(); }
            catch (Exception ex) { Forms.MessageBox.Show(ex.Message, "Could not open licenses"); }
        });
        menu.Items.Add("Quit", null, async (_, _) => { try { await window.ExitAsync(); } finally { Shutdown(); } });
        tray = new Forms.NotifyIcon { Icon = icon, Text = "Guitar Bridge · stopped", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowSettings(); };
        window.RoutingChanged += running => { route.Text = running ? "Stop routing" : "Start routing"; tray.Text = running ? "Guitar Bridge · routing" : "Guitar Bridge · stopped"; };
        window.RoutingError += message => tray.ShowBalloonTip(5000, "Guitar Bridge needs attention", message, Forms.ToolTipIcon.Warning);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings)), null, Timeout.Infinite, false);
        tray.ShowBalloonTip(4000, "Guitar Bridge is in your tray", "Click the purple icon to choose an audio input and output device.", Forms.ToolTipIcon.Info);
        if (e.Args.Length >= 2 && e.Args[0] == "--tray-test")
        {
            var checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            checkTimer.Tick += (_, _) =>
            {
                checkTimer.Stop();
                File.WriteAllText(e.Args[1], tray.Visible && !window.IsVisible && !window.ShowInTaskbar
                    ? "PASS: real tray icon created; startup window hidden; no taskbar window." : "FAIL: tray startup state");
                Shutdown();
            };
            checkTimer.Start();
        }
        else
        {
            window.StartUpdateChecks();
            window.StartSavedRoute(Array.IndexOf(e.Args, "--no-auto-start") < 0);
        }
    }

    private async void RunAudioHost(string pipeName, bool testStopHang, bool testStartHang, string? startupTest = null)
    {
        try { await AudioHostServer.RunAsync(pipeName, testStopHang, testStartHang, startupTest); Environment.Exit(0); }
        catch { Environment.Exit(1); }
    }

    private async void RunUpdateWorker(string planPath)
    {
        try { await UpdateWorker.ApplyAsync(planPath); Shutdown(0); }
        catch (Exception ex) { Forms.MessageBox.Show(ex.Message, "Guitar Bridge update failed", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning); Shutdown(1); }
    }

    private async void RunUpdateTests(string resultPath)
    {
        try { File.WriteAllText(resultPath, await UpdateTests.RunAsync()); Shutdown(0); }
        catch (Exception ex) { File.WriteAllText(resultPath, ex.ToString()); Shutdown(1); }
    }

    private async void RunDefaultOutputTests(string resultPath, bool hardware)
    {
        try { File.WriteAllText(resultPath, hardware ? await DefaultOutputTests.ProbeAsync(resultPath + ".restore.json") : await DefaultOutputTests.RunAsync()); Shutdown(0); }
        catch (Exception ex) { File.WriteAllText(resultPath, ex.ToString()); Shutdown(1); }
    }

    private async void RunStartupProbe(string resultPath, bool shared, int? rate, bool defaultOutput)
    {
        try { await StartupProbe.RunAsync(resultPath, shared, rate, defaultOutput); Shutdown(0); }
        catch (Exception ex) { File.WriteAllText(resultPath, ex.ToString()); Shutdown(1); }
    }

    private async void RunHostTests(string resultPath)
    {
        try { File.WriteAllText(resultPath, await SelfTests.RunHostAsync()); Shutdown(0); }
        catch (Exception ex) { File.WriteAllText(resultPath, ex.ToString()); Shutdown(1); }
    }

    private void ShowSettings()
    {
        if (window == null) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(208, 188, 255));
        using var pen = new Pen(Color.FromArgb(42, 30, 65), 3) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.FillEllipse(background, 1, 1, 30, 30);
        g.DrawLines(pen, new[] { new Point(7, 17), new Point(11, 17), new Point(14, 9), new Point(18, 24), new Point(21, 15), new Point(25, 15) });
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    protected override void OnExit(ExitEventArgs e)
    {
        window?.CloseUpdateChecks();
        showWait?.Unregister(null);
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        icon?.Dispose();
        showEvent?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}

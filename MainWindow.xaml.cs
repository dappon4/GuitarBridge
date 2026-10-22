using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GuitarBridge;

public record OutputChoice(string Id, string Name);
public record ChannelChoice(int Index, string Name);

public partial class MainWindow : Window
{
    private readonly Settings settings;
    private readonly AudioHostClient audio;
    private AudioHostClient bridge => audio;
    private readonly DispatcherTimer timer;
    private bool ready, exiting;
    private bool changingRoute;
    private bool startingRoute;
    private readonly bool persistSettings;
    public event Action<bool>? RoutingChanged;
    public event Action<string>? RoutingError;
    public bool IsRouting => bridge.Running;
    public MainWindow(bool persistSettings = true, AudioHostClient? audioClient = null)
    {
        audio = audioClient ?? new AudioHostClient();
        this.persistSettings = persistSettings;
        settings = persistSettings ? Settings.Load() : new Settings();
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        Rates.ItemsSource = new[] { 44100, 48000, 96000 };
        Rates.SelectedItem = settings.SampleRate;
        if (Rates.SelectedIndex < 0) Rates.SelectedItem = 48000;
        Buffers.ItemsSource = new[] { 2, 3, 5, 10, 15, 20, 30, 50 };
        Buffers.SelectedItem = settings.BufferMs;
        if (Buffers.SelectedIndex < 0) Buffers.SelectedItem = 10;
        Stereo.IsChecked = settings.Stereo;
        Exclusive.IsChecked = settings.Exclusive;
        Volume.Maximum = OutputLevel.MaximumPercent;
        Volume.Value = OutputLevel.Normalize((float)settings.Volume) * 100;
        AutoStart.IsChecked = settings.AutoStart;
        RefreshDevices();
        ready = true;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => UpdateMeter();
        timer.Start();
        Closing += OnClosing;
    }
    public void StartSavedRoute() { if (settings.AutoStart) StartRouting(); }
    private void RefreshDevices()
    {
        try
        {
            string wantedDriver = Drivers.SelectedItem as string ?? settings.Driver;
            string wantedOutput = Outputs.SelectedValue as string ?? settings.OutputId;
            Drivers.ItemsSource = AsioOut.GetDriverNames();
            Drivers.SelectedItem = wantedDriver;
            if (Drivers.SelectedIndex < 0 && Drivers.Items.Count > 0) Drivers.SelectedIndex = 0;
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var choices = devices.Select(d => { var c = new OutputChoice(d.ID, d.FriendlyName); d.Dispose(); return c; }).ToArray();
            Outputs.ItemsSource = choices;
            Outputs.SelectedValue = wantedOutput;
            if (Outputs.SelectedIndex < 0)
            {
                string? defaultId = null;
                try { using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); defaultId = defaultDevice.ID; }
                catch { /* No Windows default output is configured. */ }
                Outputs.SelectedItem = choices.FirstOrDefault(d => d.Id == defaultId) ?? choices.FirstOrDefault();
            }
            ResetChannels();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void ResetChannels()
    {
        Channels.ItemsSource = Enumerable.Range(0, 32).Select(i => new ChannelChoice(i, $"Input {i + 1} · scan to identify")).ToArray();
        Channels.SelectedValue = Math.Clamp(settings.FirstChannel, 0, 31);
    }
    private void Driver_Changed(object sender, SelectionChangedEventArgs e) { if (ready) ResetChannels(); }
    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var asio = new AsioOut(SelectedDriver());
            int selected = Channels.SelectedValue is int i ? i : 0;
            Channels.ItemsSource = Enumerable.Range(0, asio.DriverInputChannelCount).Select(c => new ChannelChoice(c, $"{c + 1} · {asio.AsioInputChannelName(c)}")).ToArray();
            Channels.SelectedValue = selected;
            if (Channels.SelectedIndex < 0 && Channels.Items.Count > 0) Channels.SelectedIndex = 0;
            ClearAttention();
            StatusLabel.Text = "Routing off";
            StatusDetail.Text = "Select an input channel. Stereo uses the next channel too.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private string SelectedDriver() => Drivers.SelectedItem as string ?? throw new InvalidOperationException("Install or select an ASIO driver first.");
    private void Panel_Click(object sender, RoutedEventArgs e)
    {
        try { using var asio = new AsioOut(SelectedDriver()); asio.ShowControlPanel(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void SaveSettings()
    {
        settings.Driver = Drivers.SelectedItem as string ?? settings.Driver;
        settings.OutputId = Outputs.SelectedValue as string ?? "";
        settings.FirstChannel = Channels.SelectedValue is int i ? i : 0;
        if (Channels.SelectedItem is ChannelChoice chosen && !chosen.Name.Contains("scan to identify"))
            settings.InputName = chosen.Name[(chosen.Name.IndexOf(" · ", StringComparison.Ordinal) + 3)..];
        settings.Stereo = Stereo.IsChecked == true;
        settings.Exclusive = Exclusive.IsChecked == true;
        settings.SampleRate = Rates.SelectedItem is int r ? r : 48000;
        settings.BufferMs = Buffers.SelectedItem is int b ? b : 10;
        settings.Volume = Volume.Value / 100;
        settings.AutoStart = AutoStart.IsChecked == true;
        if (persistSettings) settings.Save();
    }
    public void ToggleRouting() { if (changingRoute) return; if (bridge.Running) StopRouting(); else StartRouting(); }
    public async void StartRouting() => await StartRoutingAsync();
    public async Task StartRoutingAsync()
    {
        if (changingRoute) return;
        changingRoute = true;
        startingRoute = true;
        RouteButton.IsEnabled = false;
        RouteSettings.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            ClearAttention();
            SaveSettings();
            if (Channels.SelectedItem is not ChannelChoice channel || channel.Name.Contains("scan to identify") && string.IsNullOrEmpty(settings.InputName))
                throw new InvalidOperationException("Click Scan channels, then select the input you want to monitor. If it is missing, enable the input in your ASIO driver's control panel.");
            if (string.IsNullOrEmpty(settings.OutputId)) throw new InvalidOperationException("Select an output device first.");
            StatusLabel.Text = "Starting routing…";
            await audio.StartAsync(settings);
            if (bridge.Buffer is { } buffer) buffer.Gain = (float)Volume.Value / 100;
            RouteSettings.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            RouteButton.IsChecked = true;
            StatusLabel.Text = "Routing on";
            StatusDetail.Text = $"{bridge.AsioFrames} ASIO samples · {settings.SampleRate / 1000.0:0.#} kHz input\n{bridge.OutputFormat}";
            RoutingChanged?.Invoke(true);
        }
        catch (Exception ex) { RouteButton.IsChecked = false; ShowError(ex.Message); }
        finally
        {
            startingRoute = false;
            changingRoute = false;
            RouteButton.IsEnabled = true;
            RouteSettings.IsEnabled = !bridge.Running;
            RefreshButton.IsEnabled = !bridge.Running;
        }
    }
    public async void StopRouting() => await StopRoutingAsync();
    public async Task StopRoutingAsync(string? originalFault = null)
    {
        if (changingRoute) return;
        changingRoute = true;
        RouteButton.IsEnabled = false;
        RouteButton.IsChecked = false;
        if (originalFault == null) ClearAttention();
        StatusLabel.Text = originalFault == null ? "Stopping routing…" : "Needs attention";
        try { await audio.StopAsync(); }
        catch (Exception ex) { ShowError(originalFault == null ? ex.Message : originalFault + "\n\n" + ex.Message); }
        finally
        {
            changingRoute = false;
            RouteButton.IsEnabled = true;
            RouteSettings.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            RouteButton.IsChecked = false;
            if (WarningButton.Visibility != Visibility.Visible)
            {
                StatusLabel.Text = "Routing off";
                StatusDetail.Text = "Your route is saved. Start again whenever you’re ready.";
            }
            Meter.Value = 0;
            Stats.Text = "Input level · waiting for audio";
            RoutingChanged?.Invoke(false);
        }
    }
    private async void UpdateMeter()
    {
        if (changingRoute)
        {
            if (startingRoute) StatusDetail.Text = bridge.StartupStage;
            return;
        }
        if (bridge.Fault is string fault && !RouteSettings.IsEnabled)
        {
            // Reveal the original error before waiting for native cleanup.
            ShowError(fault);
            await StopRoutingAsync(fault);
            return;
        }
        var buffer = bridge.Buffer;
        if (buffer == null) return;
        Meter.Value = Math.Min(1, buffer.Peak);
        Stats.Text = $"Input {(buffer.Peak > 0 ? 20 * Math.Log10(buffer.Peak) : -90):0} dBFS · queue {buffer.BufferedMs:0.0} ms · gaps {buffer.Underruns} · resyncs {buffer.Overruns}";
        if (buffer.LimiterReductionDb > 0.5f) Stats.Text += $" · limiter −{buffer.LimiterReductionDb:0.0} dB";
    }
    internal void ShowError(string message)
    {
        StatusLabel.Text = "Needs attention";
        StatusDetail.Text = "Hover over or click the warning icon for details.";
        WarningTipText.Text = message;
        WarningPopupText.Text = message;
        WarningButton.Visibility = Visibility.Visible;
        if (!IsVisible) RoutingError?.Invoke(message);
    }
    private void ClearAttention()
    {
        WarningPopup.IsOpen = false;
        WarningButton.Visibility = Visibility.Collapsed;
        WarningTipText.Text = "";
        WarningPopupText.Text = "";
    }
    internal void Warning_Click(object sender, RoutedEventArgs e)
    {
        if (WarningButton.ToolTip is ToolTip tooltip) tooltip.IsOpen = false;
        WarningPopup.IsOpen = !WarningPopup.IsOpen;
    }
    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Route_Click(object sender, RoutedEventArgs e) => ToggleRouting();
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeLabel != null)
        {
            VolumeLabel.Text = $"{e.NewValue:0}%";
            VolumeLabel.ToolTip = e.NewValue > 100 ? $"Boost: +{20 * Math.Log10(e.NewValue / 100):0.0} dB" : "Normal output level";
        }
        var buffer = bridge.Buffer;
        if (buffer != null) buffer.Gain = (float)e.NewValue / 100;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        try { SaveSettings(); } catch (Exception ex) { ShowError("Could not save settings: " + ex.Message); }
        Hide();
    }
    public async Task ExitAsync()
    {
        exiting = true;
        timer.Stop();
        try { SaveSettings(); } catch { }
        // Do not keep the tray app stuck open if a native driver refuses to stop.
        try { await Task.WhenAny(audio.CloseAsync(), Task.Delay(2000)); } finally { Close(); }
    }
}

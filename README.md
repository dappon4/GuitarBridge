# Guitar Bridge

[![Windows build](https://github.com/dappon4/GuitarBridge/actions/workflows/build.yml/badge.svg)](https://github.com/dappon4/GuitarBridge/actions/workflows/build.yml)

A portable Windows x64 tray app for monitoring ASIO inputs through a separate Windows output device. Use it with an audio interface, instrument processor, microphone, or other source exposed by an installed ASIO driver. The executable includes .NET.

## Download

Download the MSI installer or portable EXE from [Releases](https://github.com/dappon4/GuitarBridge/releases). Before the first tagged release, open a successful [Windows build](https://github.com/dappon4/GuitarBridge/actions/workflows/build.yml) and download its `GuitarBridge-...-win-x64` artifact (GitHub sign-in required for Actions downloads).

The MSI installs to Program Files and adds a Start menu shortcut. The portable EXE runs from a folder you choose. Quit the tray app before upgrading or uninstalling. These builds are unsigned; Windows may display an unknown-publisher or reputation prompt.

Requires Windows 10/11 x64, an installed ASIO input driver, and a working Windows speaker or headphone output. Device drivers are not bundled.

## Getting started

1. Run GuitarBridge.exe, then click its purple icon in the notification area to open settings.
2. Select your ASIO driver. Open ASIO panel if you need to enable input channels or adjust the driver buffer size.
3. Click Scan channels and select the input you want to monitor. Stereo pair uses that channel and the next one; disable it to send one mono input to both output channels.
4. Select your speakers, headphones, or other Windows output. Your saved choice is preserved; on first use, the app selects the Windows default output when available.
5. Choose a sample rate supported by your interface and turn on routing. Start with a 10 ms output buffer target and, if supported, 128 ASIO samples. Reduce these values if playback remains stable; increase them if audio crackles or drops out.

Shared output is the default, so guitar routing can play alongside YouTube and other Windows apps. Leave Exclusive output off for this behavior. Enabling it reserves the selected output device for guitar routing. If your ASIO driver also opens Windows speaker devices, leave the selected output available to Windows.

**Switch Windows output while routing** temporarily makes the selected device the Windows playback default after routing starts successfully. Stopping routing, an audio fault, or Quit restores the previous playback defaults. Turn this checkbox off to leave Windows defaults alone. Communications devices and app-specific output overrides are unchanged. Apps that follow the Windows default can follow this switch; some apps may need playback restarted. If you manually change Windows output during routing, your new selection is preserved. An unavailable original device is reported, and recovery is retried on the next launch. Closing the window to the tray keeps routing and the temporary output active.

Shared output now uses IAudioClient3 when supported, selects a legal engine period near the requested buffer value, and queues only the next engine period. The status shows the selected period and output queue instead of treating the requested value as actual latency. Optional per-stream effects are bypassed where raw mode is supported; Windows' always-on speaker processing remains. Unsupported devices or formats automatically use the compatibility output path. The 2 and 3 ms choices are useful only when the output driver supports smaller periods.

## Window and tray controls

The switch position shows routing state. Refresh reloads devices while routing is stopped. Hover over the information icon for setup tips. When attention is needed, hover over the warning icon for the full message or click it to open a details panel.

Output level ranges from 0% to 500%. 100% is unity gain; 500% multiplies the guitar signal amplitude by five (about +14 dB). Boost is most useful for quiet signals. A stereo-linked peak limiter reduces gain when needed to contain boosted peaks, and the meter text shows limiter attenuation while it is active. Boost cannot recover detail already clipped at the source or guarantee five times the perceived loudness. The limiter uses no additional audio buffer. The saved level is preserved when updating.

Drag the app heading to move the window. The close button hides the window and keeps routing active. Right-click the tray icon to stop routing or quit. Windows controls whether the tray icon appears directly in the notification area or inside its overflow arrow.

Automatic routing starts the saved route when the app launches. It does not add the app to Windows startup.

## Updates

Open the download icon in the window header, or **Check for updates** in the tray menu. The app checks GitHub Releases on launch and every six hours; you can turn automatic checks off in that panel. A dot on the icon indicates a newer stable version.

Click **Update and restart** while routing is stopped. The download runs in the background and is checked against the release's SHA-256 checksum and GitHub asset digest when available. Installed copies use the MSI (Windows may ask for administrator permission); portable copies replace their EXE in place. The app restarts afterward and preserves your settings. Offline or failed checks leave audio routing available. Updates are downloaded only after you click the button.

## Latency and recovery

The buffer value is a request, not a guaranteed end-to-end latency. Total delay includes your source device, ASIO buffers, the bridge queue, Windows audio buffering, and output hardware. The queue time shown in the app measures only the bridge queue. Bluetooth output usually adds substantial delay.

The bridge follows the independent input and output clocks and limits stale audio after scheduling stalls. Capture publishes blocks without waiting for the renderer. Its shorter queue adds a bounded safety margin if input starvation occurs. Each audio session runs in a child process. If a native driver stalls, the app closes that session after a timeout and restores its controls. Quit also closes the session.

After routing settles, the bridge gradually removes excess queue headroom when it has enough reserve. If headroom runs low or audio starves, it backs off and stops trimming for that session. The audio worker also avoids repeated unchanged volume messages and uses .NET's sustained low-latency collection mode. A direct floating-point sample path avoids an intermediate conversion copy while resampling; matching input and output rates can bypass conversion adapters entirely.

After changing the ASIO buffer size while stopped, turn on routing once. The app automatically reopens the audio session if the driver requests a reset during startup. If the driver keeps requesting resets after reopening, the app shows an error and restores the controls.

If devices are unplugged or input channel mappings change, stop, refresh devices, scan channels, and select the intended input again. Another audio app may be holding a device. Saved input channel names are checked before reuse.

## Settings and privacy

Settings are stored in %LOCALAPPDATA%\GuitarBridge\settings.json. Audio is not recorded or sent over a network.

Update checks contact GitHub with the app version in the User-Agent. They do not send audio, device names, device identifiers, or saved settings. Downloads and temporary updater files use `%LOCALAPPDATA%\GuitarBridge\Updates`; old updater folders are cleaned up after seven days.

The app starts in the notification area, without a taskbar button. It does not start routing until you enable it, unless you opt into automatic routing. No device names or identifiers are preconfigured.

## Build from source

Install the .NET 8 SDK on Windows, clone this repository, and run PowerShell in its folder:

```powershell
./build.ps1
./scripts/test.ps1
./scripts/package.ps1
```

The published app is `artifacts/publish/GuitarBridge.exe`. Packaging creates the portable EXE, MSI, and SHA-256 checksums in `artifacts/release`. Build tools and dependencies restore automatically; no local SDK directory or audio hardware is required for the automated checks. See [development and release instructions](docs/DEVELOPING.md).

GitHub Actions runs the same build and checks on pushes and pull requests. A new project version on `main`, or a version tag, creates a GitHub Release containing both downloads. Existing releases are never overwritten. See [code-signing options](docs/SIGNING.md) for trusted Windows signatures.

The app uses .NET 8 WPF, NAudio 2.2.1, and MaterialDesignThemes 5.2.1 with Material Design 3 defaults. Dependency license notices are included in licenses.

## License

Guitar Bridge is available under the [MIT license](LICENSE). Dependency notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `licenses/`, and are embedded in the portable EXE under the tray menu's **Open-source licenses** item.

- [NAudio 2.2.1](https://github.com/naudio/NAudio/tree/v2.2.1)
- [Material Design in XAML 5.2.1](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/tree/v5.2.1)

# Development and release builds

Use Windows 10/11 x64, PowerShell, and the .NET 8 SDK. NuGet restores the
application dependencies. The SDK is not committed or required beside the app.

```powershell
./build.ps1
./scripts/test.ps1
./scripts/package.ps1
```

`build.ps1` accepts `-DotnetPath` to select an SDK and `-OutputDirectory` to
select an output folder. Defaults work with `dotnet` on PATH. `global.json`
allows current .NET 8 feature-band and security updates.
The default version comes from `GuitarBridge.csproj`; `-Version` overrides it.

The published EXE is `artifacts/publish/GuitarBridge.exe`. Packaging creates
`artifacts/release/GuitarBridge-<version>-win-x64.exe`, the matching `.msi`,
and `SHA256SUMS.txt`. WiX 5.0.2 is restored from the local tool manifest.
The portable EXE embeds the project and dependency licenses, accessible from
the tray menu. The MSI also installs readable notices beside the executable.

The MSI targets 64-bit Program Files, creates a Start menu shortcut, supports
major upgrades, and leaves per-user settings on uninstall. Quit the tray app
before installing, upgrading, or uninstalling. The installer does not install
ASIO drivers, change device drivers, choose audio devices, or add Windows
startup entries. The app itself runs without administrator privileges.

## Automated checks

`scripts/test.ps1` runs buffer, conversion, limiter, clock-drift, concurrency,
isolated-host, UI, and embedded-license checks. Host checks inject a simulated
ASIO driver and output; no physical input or ASIO driver is required.

`scripts/test-installer.ps1` is restricted to disposable GitHub Actions runners.
It installs the MSI, verifies the payload and shortcut, uninstalls it, and
checks preservation of per-user data. It is not run on a developer's computer.

The optional `tests/DriverProbe` console project queries available output
periods or plays an explicitly requested quiet tone. It contains no device IDs
or driver-installation code. Real audio-device and acoustic-latency testing
remains a manual task; simulated checks do not establish hardware latency.

## GitHub Actions

Pushes to `main`, pull requests, and manual dispatch build and test both Windows
downloads. Open a successful run and download its `GuitarBridge-...-win-x64`
artifact. Check results are uploaded separately, including logs after failures.

For a release, increase the three-part version in `GuitarBridge.csproj` and
push to `main`. After all checks pass, the workflow creates a matching version
tag and public GitHub Release with the EXE, MSI, and checksums. It skips releases
that already exist; never replace an existing release's downloads. You can
also push an explicit version tag. The release job uses the built-in GitHub
token; no personal access token or signing certificate is required.

```powershell
git tag v0.1.2
git push origin v0.1.2
```

Versions must have major/minor <=255 and patch <=65535 for MSI compatibility.
CI artifacts and releases are unsigned unless a maintainer adds a code-signing
step. Windows may show publisher/reputation prompts; a successful CI build does
not imply a trusted Authenticode signature. Do not disable Windows security
features as part of installation instructions.
See [signing options and build order](SIGNING.md).

## Update testing

`--default-output-tests <report>` exercises Windows playback-default switching
using simulated devices: per-role capture/restore, startup failure, partial
switch failure, manual overrides, disconnected originals, and crash recovery.
Host checks also verify switching on successful routing and restoration on
fault/stop and Quit. No real Windows defaults change in automated CI checks.

For an explicit manual Windows integration check, `--default-output-probe
<report>` briefly switches playback defaults to another available device and
restores them in a `finally` block. This is not run by the automated test script.
The implementation uses Windows' private IPolicyConfig COM interface; failures
are reported and routing is stopped if switching fails. Recovery data remains
in local application data, never in the source repository. Only Console and
Multimedia roles change; Communications and per-app routes are left alone.

`--update-tests <report>` uses synthetic HTTP responses and temporary files to
check release/version selection, download source validation, checksums,
corruption, cancellation, missing releases, and portable replacement. It never
downloads or executes a real update. The normal app and its audio child process
do not share update operations. Installation is disabled while routing starts,
runs, or stops. The updater waits for the GUI process to exit before modifying
the app and does not terminate an active audio process.

The app reads only stable releases from `dappon4/GuitarBridge`. Each must include
the versioned Windows EXE and MSI and `SHA256SUMS.txt`. The client compares the
three-part release version with the running assembly version, accepts downloads
only under this project's HTTPS release URL, and verifies file size and SHA-256
before starting a helper. MSI installs retain Windows' permission prompts;
portable updates require write access to the app's folder. Audio settings stay
under local application data across both update paths.

Actions are pinned to immutable upstream commits. Update their pins and the
WiX tool version deliberately. WiX is a build tool with its own license;
GuitarBridge's MIT license does not replace dependency/tool terms.

## Repository contents

Only source, generic tests, build/installer configuration, documentation,
artwork, and license notices belong in Git. A root allowlist excludes local
settings, hardware IDs in diagnostics, driver exports, SDK/package caches,
private development notes, build outputs, and signing keys.

Avoid committing recordings, device-specific logs, real endpoint identifiers,
absolute workstation paths, or credentials. Use synthetic device names and
temporary paths in tests. Per-user settings live under the OS-provided local
application-data directory and are created at runtime.

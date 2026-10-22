# Development and release builds

Use Windows 10/11 x64, PowerShell, and the .NET 8 SDK. NuGet restores the
application dependencies. The SDK is not committed or required beside the app.

```powershell
./build.ps1 -Version 0.1.0
./scripts/test.ps1
./scripts/package.ps1 -Version 0.1.0
```

`build.ps1` accepts `-DotnetPath` to select an SDK and `-OutputDirectory` to
select an output folder. Defaults work with `dotnet` on PATH. `global.json`
allows current .NET 8 feature-band and security updates.

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

For a release, choose a new three-part version, create a tag such as `v0.1.0`,
and push it. The same workflow builds/tests the tag and publishes a GitHub
Release with the EXE, MSI, and checksums. The release job uses the built-in
GitHub token; no personal access token or signing certificate is required.

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Versions must have major/minor <=255 and patch <=65535 for MSI compatibility.
CI artifacts and releases are unsigned unless a maintainer adds a code-signing
step. Windows may show publisher/reputation prompts; a successful CI build does
not imply a trusted Authenticode signature. Do not disable Windows security
features as part of installation instructions.

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

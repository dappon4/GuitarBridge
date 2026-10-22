[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath,[Parameter(Mandatory)][string]$PublishedExecutable)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This install/uninstall smoke test is restricted to disposable GitHub Actions runners.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$results = Join-Path $repoRoot 'artifacts\test-results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$installedExe = Join-Path $env:ProgramFiles 'GuitarBridge\GuitarBridge.exe'
$sentinel = Join-Path $env:LOCALAPPDATA 'GuitarBridge\installer-preservation-test.txt'
if (Test-Path -LiteralPath $installedExe) { throw 'A previous Guitar Bridge installation exists; do not alter it.' }
New-Item -ItemType Directory -Path (Split-Path $sentinel -Parent) -Force | Out-Null
Set-Content -LiteralPath $sentinel -Value 'Preserve per-user data across uninstall.'
$installed = $false
try {
    $log = Join-Path $results 'msi-install.log'
    $process = Start-Process msiexec.exe -ArgumentList @('/i',('"{0}"' -f $MsiPath),'/qn','/norestart','/l*v',('"{0}"' -f $log)) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -notin @(0,3010)) { throw "MSI install failed: $($process.ExitCode). See $log" }
    $installed = $true
    if ((Get-FileHash -LiteralPath $installedExe).Hash -ne (Get-FileHash -LiteralPath $PublishedExecutable).Hash) { throw 'Installed EXE does not match the published EXE.' }
    foreach ($relative in @('LICENSE','README.md','THIRD_PARTY_NOTICES.md','licenses\NAudio.txt','licenses\MaterialDesign.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $installedExe -Parent) $relative))) { throw "MSI omitted $relative" }
    }
    $shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Guitar Bridge\Guitar Bridge.lnk'
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut was not installed.' }
} finally {
    if ($installed) {
        $log = Join-Path $results 'msi-uninstall.log'
        $process = Start-Process msiexec.exe -ArgumentList @('/x',('"{0}"' -f $MsiPath),'/qn','/norestart','/l*v',('"{0}"' -f $log)) -PassThru -Wait -WindowStyle Hidden
        if ($process.ExitCode -notin @(0,3010)) { throw "MSI uninstall failed: $($process.ExitCode). See $log" }
    }
}
if (Test-Path -LiteralPath $installedExe) { throw 'Uninstall left the installed application executable behind.' }
if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed per-user app data.' }
Remove-Item -LiteralPath $sentinel
Write-Host 'PASS: MSI install, payload, shortcut, uninstall and user-data preservation.'

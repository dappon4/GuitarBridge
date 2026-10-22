[CmdletBinding()]
param([string]$Executable, [string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Executable) { $Executable = Join-Path $repoRoot 'artifacts\publish\GuitarBridge.exe' }
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repoRoot 'artifacts\test-results' }
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
function Invoke-AppCheck([string]$Mode,[string]$File,[int]$TimeoutSeconds=60) {
    $process = Start-Process -FilePath $Executable -ArgumentList @($Mode,('"{0}"' -f $File)) -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "$Mode timed out. This check only uses simulated audio devices." }
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $File) { Get-Content -LiteralPath $File }
        throw "$Mode failed with exit code $($process.ExitCode)."
    }
}
Invoke-AppCheck '--self-test' (Join-Path $ResultsDirectory 'buffer-tests.txt')
Invoke-AppCheck '--host-tests' (Join-Path $ResultsDirectory 'host-tests.txt')
Invoke-AppCheck '--update-tests' (Join-Path $ResultsDirectory 'update-tests.txt')
Invoke-AppCheck '--smoke-test' (Join-Path $ResultsDirectory 'window.png')
$notices = Join-Path $ResultsDirectory 'embedded-licenses.txt'
Invoke-AppCheck '--write-licenses' $notices
$licenseText = Get-Content -LiteralPath $notices -Raw
foreach ($expected in @('GuitarBridge contributors','Mark Heath','James Willock','Microsoft')) {
    if (-not $licenseText.Contains($expected)) { throw "Standalone EXE is missing license notice: $expected" }
}
Write-Host 'PASS: buffer, host, updater, window and standalone-license checks.'

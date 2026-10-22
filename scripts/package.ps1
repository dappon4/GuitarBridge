[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$DotnetPath = 'dotnet',
    [string]$PublishDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'GuitarBridge.csproj') -Raw)).Project.PropertyGroup.Version }
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot 'artifacts\publish' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\release' }
$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($parts[0] -gt 255 -or $parts[1] -gt 255 -or $parts[2] -gt 65535) { throw 'MSI versions require major/minor <=255 and patch <=65535.' }
foreach ($name in @('GuitarBridge.exe','LICENSE','README.md','THIRD_PARTY_NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $name))) { throw "Missing published file: $name. Run build.ps1 first." }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputBase = Join-Path $OutputDirectory "GuitarBridge-$Version-win-x64"
$previousRollForward = $env:DOTNET_ROLL_FORWARD
$previousDotnetRoot = $env:DOTNET_ROOT
Push-Location $repoRoot
try {
    # WiX 5 targets .NET 6; the supported .NET 8 SDK can host this build tool.
    $env:DOTNET_ROLL_FORWARD = 'Major'
    if ([IO.Path]::IsPathRooted($DotnetPath)) { $env:DOTNET_ROOT = Split-Path $DotnetPath -Parent }
    & $DotnetPath tool restore --tool-manifest (Join-Path $repoRoot '.config\dotnet-tools.json')
    if ($LASTEXITCODE -ne 0) { throw 'WiX tool restore failed.' }
    & $DotnetPath tool run wix -- build (Join-Path $repoRoot 'installer\Package.wxs') -arch x64 -d "Version=$Version" -d "PublishDir=$PublishDirectory" -d "SourceDir=$repoRoot" -o "$outputBase.msi"
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
    Copy-Item -LiteralPath (Join-Path $PublishDirectory 'GuitarBridge.exe') -Destination "$outputBase.exe" -Force
    @("$outputBase.exe", "$outputBase.msi") | ForEach-Object {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_
        '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_)
    } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
} finally {
    Pop-Location
    $env:DOTNET_ROLL_FORWARD = $previousRollForward
    $env:DOTNET_ROOT = $previousDotnetRoot
}
Write-Host "Created $outputBase.exe and $outputBase.msi"

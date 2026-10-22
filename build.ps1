[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\publish')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'GuitarBridge.csproj'
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version }
& $DotnetPath publish $project --configuration $Configuration --runtime win-x64 --self-contained true --output $OutputDirectory "-p:Version=$Version" -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
foreach ($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $OutputDirectory $name) -Force
}
$licenseDestination = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenseDestination $_.Name) -Force
}
Write-Host "Published Guitar Bridge $Version to $OutputDirectory"

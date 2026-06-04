param(
    [string]$Configuration = "Release",
    [string]$Runtime = "",
    [string]$Repository = "wawgame123/Minecraft",
    [string]$Branch = "main"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "ServerLauncher.csproj"
$publishDir = Join-Path $root "artifacts\publish\minivibe"
$launcherDir = Join-Path $root "launcher"

function Assert-UnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside repository: $fullPath"
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function ConvertFrom-Utf8Base64([string]$Value) {
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

[xml]$projectXml = Get-Content -Raw -LiteralPath $project
$version = $projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version is missing in ServerLauncher.csproj"
}

Assert-UnderRoot -Path $publishDir -Root $root
Assert-UnderRoot -Path $launcherDir -Root $root

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $launcherDir | Out-Null

if ([string]::IsNullOrWhiteSpace($Runtime)) {
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "--self-contained", "false", "--no-restore", "-o", $publishDir)
    $zipName = "Minivibe-$version-WIN-portable.zip"
}
else {
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "-r", $Runtime, "--self-contained", "false", "--no-restore", "-o", $publishDir)
    $zipName = "Minivibe-$version-$Runtime-portable.zip"
}

$zipPath = Join-Path $launcherDir $zipName
Assert-UnderRoot -Path $zipPath -Root $root
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$url = "https://raw.githubusercontent.com/$Repository/$Branch/launcher/$zipName"

$update = [ordered]@{
    version = $version
    url = $url
    sha256 = $hash
    mandatory = $false
    notes = @(
        ConvertFrom-Utf8Base64 "0JTQvtCx0LDQstC70LXQvdCwINC90LDRgdGC0YDQvtC50LrQsCDQtNC70Y8g0L/QvtC60LDQt9CwINC40LzQtdC9INGB0LrQsNGH0LjQstCw0LXQvNGL0YUg0YTQsNC50LvQvtCyLg=="
        ConvertFrom-Utf8Base64 "0KPQu9GD0YfRiNC10L0g0L/QvtC40YHQuiDRgdGD0YnQtdGB0YLQstGD0Y7RidC10LPQviBOZW9Gb3JnZSDQuCBNaW5lY3JhZnQgYXNzZXRzL2xpYnJhcmllcywg0L7RgdC+0LHQtdC90L3QviDQtdGB0LvQuCDQstGL0LHRgNCw0L3QsCDQv9Cw0L/QutCwIC5taW5lY3JhZnQvdmVyc2lvbnMvbmVvZm9yZ2UtKi4="
        ConvertFrom-Utf8Base64 "0K3QvNC+0YbQuNC4INGC0LXQv9C10YDRjCDRgdGC0LDQstGP0YLRgdGPINC40LcgZW1vdGVzLnJhciDQsdC10Lcg0LfQsNCy0LjRgdC40LzQvtGB0YLQuCDQvtGCIG1hbmlmZXN0INC4INCx0LXQtyDQv9GA0L7QstC10YDQutC4INGB0L7QtNC10YDQttC40LzQvtCz0L4g0LDRgNGF0LjQstCwLg=="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

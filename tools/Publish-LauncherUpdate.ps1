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
    dotnet restore $project --ignore-failed-sources
    dotnet publish $project -c $Configuration --self-contained false --no-restore -o $publishDir
    $packageSuffix = "framework"
}
else {
    dotnet restore $project -r $Runtime --ignore-failed-sources
    dotnet publish $project -c $Configuration -r $Runtime --self-contained false --no-restore -o $publishDir
    $packageSuffix = $Runtime
}

$zipName = "Minivibe-$version-$packageSuffix.zip"
$zipPath = Join-Path $launcherDir $zipName
Assert-UnderRoot -Path $zipPath -Root $root
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$url = "https://raw.githubusercontent.com/$Repository/$Branch/launcher/$zipName"
$utf8 = [System.Text.Encoding]::UTF8

$update = [ordered]@{
    version = $version
    url = $url
    sha256 = $hash
    mandatory = $false
    notes = @(
        $utf8.GetString([Convert]::FromBase64String("0JvQsNGD0L3Rh9C10YAg0YLQtdC/0LXRgNGMINC/0YDQvtCy0LXRgNGP0LXRgiDQstC10YDRgdC40Y4gSmF2YSDQuCDQuNCz0L3QvtGA0LjRgNGD0LXRgiBKYXZhIDgvMTcg0LTQu9GPIE1pbmVjcmFmdCAxLjIxLjEu")),
        $utf8.GetString([Convert]::FromBase64String("0JXRgdC70LggSmF2YSAyMSsg0L3QtSDQvdCw0LnQtNC10L3QsCwg0LvQsNGD0L3Rh9C10YAg0YHQutCw0YfQuNCy0LDQtdGCINC/0L7RgNGC0LDRgtC40LLQvdGD0Y4gVGVtdXJpbiBKUkUgMjEg0LIg0L/QsNC/0LrRgyDRgdCx0L7RgNC60Lgu")),
        $utf8.GetString([Convert]::FromBase64String("0J/QvtGB0LvQtSDRgdC60LDRh9C40LLQsNC90LjRjyBNaW5lY3JhZnQg0LfQsNC/0YPRgdC60LDQtdGC0YHRjyDRh9C10YDQtdC3INC70L7QutCw0LvRjNC90YPRjiBKYXZhINCx0LXQtyDRgNGD0YfQvdC+0LPQviDRg9C60LDQt9Cw0L3QuNGPINC/0YPRgtC4Lg=="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

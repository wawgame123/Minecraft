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
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvdC+INC+0LrQvdC+INCy0YvQsdC+0YDQsCDRhtCy0LXRgtCwINGBINC/0LDQu9C40YLRgNC+0Lkg0LTQu9GPINC/0L7Qu9GM0LfQvtCy0LDRgtC10LvRjNGB0LrQuNGFINGG0LLQtdGC0L7Qsi4=")),
        $utf8.GetString([Convert]::FromBase64String("0KLQtdC60YHRgiDQsNCy0YLQvtC80LDRgtC40YfQtdGB0LrQuCDQv9C+0LvRg9GH0LDQtdGCINC60L7QvdGC0YDQsNGB0YLQvdGL0Lkg0YbQstC10YIsINC10YHQu9C4INCy0YvQsdGA0LDQvdC90YvQuSDRhtCy0LXRgiDRgdC70LjQstCw0LXRgtGB0Y8g0YEg0YTQvtC90L7QvC4=")),
        $utf8.GetString([Convert]::FromBase64String("0KHQutCw0YfQuNCy0LDQvdC40LUg0YTQsNC50LvQvtCyINGD0YHQutC+0YDQtdC90L4g0L/QsNGA0LDQu9C70LXQu9GM0L3QvtC5INC30LDQs9GA0YPQt9C60L7QuSDQuCDQv9C+0LrQsNC30YvQstCw0LXRgiDQv9GA0L7RhtC10L3RgtGLINCx0LXQtyDQuNC80LXQvSDRhNCw0LnQu9C+0LIu")),
        $utf8.GetString([Convert]::FromBase64String("SmF2YSDQuNGJ0LXRgtGB0Y8g0LDQstGC0L7QvNCw0YLQuNGH0LXRgdC60Lg6IFBBVEgsIEpBVkFfSE9NRSwgcnVudGltZSDRgNGP0LTQvtC8INGBINC70LDRg9C90YfQtdGA0L7QvCDQuNC70Lgg0YHQsdC+0YDQutC+0LksINGC0LjQv9C+0LLRi9C1INGD0YHRgtCw0L3QvtCy0LrQuCBKYXZhLg=="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

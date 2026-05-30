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
        $utf8.GetString([Convert]::FromBase64String("0KPQtNCw0LvQtdC9INGA0LDQt9C00LXQuyDCq9Cc0L7QtNGLwrsg0LjQtyDQvdCw0LLQuNCz0LDRhtC40Lgg0LvQsNGD0L3Rh9C10YDQsC4=")),
        $utf8.GetString([Convert]::FromBase64String("0JrQsNGA0YLQsCBCbHVlTWFwINGC0LXQv9C10YDRjCDQvtGC0LrRgNGL0LLQsNC10YLRgdGPINCy0L4g0LLQvdC10YjQvdC10Lwg0LHRgNCw0YPQt9C10YDQtSDQstC80LXRgdGC0L4g0L3QtdGB0YLQsNCx0LjQu9GM0L3QvtCz0L4g0LLRgdGC0YDQvtC10L3QvdC+0LPQviBXZWJCcm93c2VyLg==")),
        $utf8.GetString([Convert]::FromBase64String("0JLRi9C/0LDQtNCw0Y7RidC40LUg0YHQv9C40YHQutC4INGC0LXQvNGLINC4INCw0LrRhtC10L3RgtCwINC/0L7Qu9GD0YfQuNC70Lgg0LrQvtC90YLRgNCw0YHRgtC90YvQtSDRhtCy0LXRgtCwLCDRh9GC0L7QsdGLINGC0LXQutGB0YIg0L3QtSDQv9GA0L7Qv9Cw0LTQsNC7INC90LAg0LHQtdC70L7QvCDRhNC+0L3QtS4=")),
        $utf8.GetString([Convert]::FromBase64String("0JIgbWFuaWZlc3QuanNvbiDQt9Cw0L/QvtC70L3QtdC9IGxhdW5jaC5tYWluQ2xhc3Mg0LggY2xhc3NwYXRoINC00LvRjyDQt9Cw0L/Rg9GB0LrQsCDRgdCx0L7RgNC60Lgu"))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

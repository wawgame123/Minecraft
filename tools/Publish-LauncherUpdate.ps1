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
        $utf8.GetString([Convert]::FromBase64String("0KHRgdGL0LvQutC4IG1hbmlmZXN0Lmpzb24g0LggdXBkYXRlLmpzb24g0LHQvtC70YzRiNC1INC90LXQu9GM0LfRjyDQvNC10L3Rj9GC0Ywg0LjQtyDQvdCw0YHRgtGA0L7QtdC6Lg==")),
        $utf8.GetString([Convert]::FromBase64String("0J3QuNC6INGC0LXQv9C10YDRjCDQstCy0L7QtNC40YLRgdGPINC/0YDRj9C80L4g0L3QsCDQs9C70LDQstC90L7QuSDRgdGC0YDQsNC90LjRhtC1INC4INGB0L7RhdGA0LDQvdGP0LXRgtGB0Y8g0LIg0L3QsNGB0YLRgNC+0LnQutC4Lg==")),
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvdGLINC/0L7Qu9GM0LfQvtCy0LDRgtC10LvRjNGB0LrQuNC1IEhFWC3RhtCy0LXRgtCwINC40L3RgtC10YDRhNC10LnRgdCwLg==")),
        $utf8.GetString([Convert]::FromBase64String("0JrQsNGA0YLQsCDQvNC40YDQsCDQv9C+0LTQutC70Y7Rh9C10L3QsCDQsiDRgNCw0LfQtNC10Lsg0JrQsNGA0YLQsC4="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

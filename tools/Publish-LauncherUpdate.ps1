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
        $utf8.GetString([Convert]::FromBase64String("0JjRgdC/0YDQsNCy0LvQtdC9INC60YDQsNGIINC/0YDQuCDQv9GA0L7QstC10YDQutC1IFNIQS0xOiDQstGA0LXQvNC10L3QvdGL0LkgLmRvd25sb2FkINGE0LDQudC7INGC0LXQv9C10YDRjCDQt9Cw0LrRgNGL0LLQsNC10YLRgdGPINC/0LXRgNC10LQg0L/RgNC+0LLQtdGA0LrQvtC5Lg==")),
        $utf8.GetString([Convert]::FromBase64String("0J/QvtC40YHQuiBKYXZhIDIxINGA0LDRgdGI0LjRgNC10L06IFBBVEgsIEpBVkFfSE9NRSwgUHJvZ3JhbSBGaWxlcywgTG9jYWxBcHBEYXRhLCBydW50aW1lINC+0YTQuNGG0LjQsNC70YzQvdC+0LPQviBNaW5lY3JhZnQgTGF1bmNoZXIg0Lgg0YDQtdC10YHRgtGAIFdpbmRvd3Mu")),
        $utf8.GetString([Convert]::FromBase64String("0JIg0YHRgtCw0YLRg9GB0LUg0LfQsNC/0YPRgdC60LAg0YLQtdC/0LXRgNGMINCy0LjQtNC90L4sINC60LDQutCw0Y8gSmF2YSDQvdCw0LnQtNC10L3QsCDQuCDQv9C+0YfQtdC80YMg0LvQsNGD0L3Rh9C10YAg0YHQutCw0YfQuNCy0LDQtdGCIHJ1bnRpbWUu"))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

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
    $packageSuffix = "framework"
}
else {
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "-r", $Runtime, "--self-contained", "false", "--no-restore", "-o", $publishDir)
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
        $utf8.GetString([Convert]::FromBase64String("0JfQsNCz0YDRg9C30LrQsCDRgdC60LjQvdC+0LIg0LIg0L7QsdGJ0LjQuSDQutCw0YLQsNC70L7QsyDRgtC10L/QtdGA0Ywg0YDQsNCx0L7RgtCw0LXRgiDQsdC10Lcg0LrQvtC00LAg0LTQvtGB0YLRg9C/0LAu")),
        $utf8.GetString([Convert]::FromBase64String("0JjQtyDQu9Cw0YPQvdGH0LXRgNCwINGD0LHRgNCw0L3QviDQv9C+0LvQtSDQutC+0LTQsCDQtNC70Y8g0LfQsNCz0YDRg9C30LrQuCDRgdC60LjQvdCwLg==")),
        $utf8.GetString([Convert]::FromBase64String("Q2xvdWRmbGFyZSBXb3JrZXIg0YLQtdC/0LXRgNGMINGC0YDQtdCx0YPQtdGCINGC0L7Qu9GM0LrQviBHSVRIVUJfVE9LRU4u")),
        $utf8.GetString([Convert]::FromBase64String("0J7RgdGC0LDRjtGC0YHRjyDQv9GA0L7QstC10YDQutC4INC90LjQutCwLCBQTkct0YTQvtGA0LzQsNGC0LAg0Lgg0YDQsNC30LzQtdGA0LAg0YTQsNC50LvQsC4="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

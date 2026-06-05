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
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "--self-contained", "false", "--no-restore", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", $publishDir)
    $zipName = "Minivibe-$version-WIN-portable.zip"
}
else {
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "-r", $Runtime, "--self-contained", "false", "--no-restore", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", $publishDir)
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
        ConvertFrom-Utf8Base64 "T2ZmbGluZVNraW5zINGC0LXQv9C10YDRjCDQsdC10YDQtdGCINGB0LrQuNC90Ysg0LjQtyBHaXRIdWIt0LrQsNGC0LDQu9C+0LPQsCBtaW5pdmliZSDQsdC10Lcg0L/RgNC40L7RgNC40YLQtdGC0LAgTW9qYW5nL0NyYWZhdGFyIGZhbGxiYWNrLg=="
        ConvertFrom-Utf8Base64 "0J/RgNC4INC30LDQs9GA0YPQt9C60LUg0YHQutC40L3QsCDRgdC+0YXRgNCw0L3Rj9C10YLRgdGPIGFsaWFzINC90LjQutCwINCyINC90LjQttC90LXQvCDRgNC10LPQuNGB0YLRgNC1LCDRh9GC0L7QsdGLINGA0LXQs9C40YHRgtGAINC/0YPRgtC4INC90LAgR2l0SHViINC90LUg0LvQvtC80LDQuyDRgdC60LjQvS4="
        ConvertFrom-Utf8Base64 "0J/RgNC4INGB0LzQtdC90LUg0LrQvtC90YTQuNCz0LAgT2ZmbGluZVNraW5zINC70LDRg9C90YfQtdGAINC+0YfQuNGJ0LDQtdGCINGB0YLQsNGA0YvQuSDQutGN0Ygg0YHQutC40L3QvtCyLCDQvtGB0YLQsNCy0LvRj9GPINC70L7QutCw0LvRjNC90YvQuSDRgdC60LjQvSDRgtC10LrRg9GJ0LXQs9C+INC40LPRgNC+0LrQsC4="
        ConvertFrom-Utf8Base64 "0KPQsdGA0LDQvdGLINCw0LLRgtC+0LzQsNGC0LjRh9C10YHQutC40LUgRzFHQy9KVk0t0L/QsNGA0LDQvNC10YLRgNGLINC30LDQv9GD0YHQutCwOyBSQU0g0Lgg0L/QvtC70YzQt9C+0LLQsNGC0LXQu9GM0YHQutC40LUg0LDRgNCz0YPQvNC10L3RgtGLINC+0YHRgtCw0LvQuNGB0Ywg0L/QvtC0INC60L7QvdGC0YDQvtC70LXQvCDQuNCz0YDQvtC60LAu"
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

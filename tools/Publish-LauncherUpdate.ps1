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
        ConvertFrom-Utf8Base64 "0JvQsNGD0L3Rh9C10YAg0L/QtdGA0LXQtCDQt9Cw0L/Rg9GB0LrQvtC8IE1pbmVjcmFmdCDRgdC40L3RhdGA0L7QvdC40LfQuNGA0YPQtdGCINCy0YHQtSBHaXRIdWIt0YHQutC40L3RiyDQsiDQu9C+0LrQsNC70YzQvdGL0Lkg0LrRjdGIIE9mZmxpbmVTa2lucy4="
        ConvertFrom-Utf8Base64 "0KHQutC40L3RiyDRgdC+0YXRgNCw0L3Rj9GO0YLRgdGPINC/0L4g0L3QuNC60YMsIGxvd2VyLWNhc2Ug0LDQu9C40LDRgdGDINC4IG9mZmxpbmUgVVVJRCwg0YfRgtC+0LHRiyBPZmZsaW5lU2tpbnMg0L3QsNGF0L7QtNC40Lsg0LjRhSDRgdGC0LDQsdC40LvRjNC90LXQtS4="
        ConvertFrom-Utf8Base64 "0JXRgdC70LggR2l0SHViINCy0YDQtdC80LXQvdC90L4g0L3QtdC00L7RgdGC0YPQv9C10L0sINC30LDQv9GD0YHQuiBNaW5lY3JhZnQg0L/RgNC+0LTQvtC70LbQsNC10YLRgdGPINGB0L4g0YHRgtCw0YDRi9C8INC70L7QutCw0LvRjNC90YvQvCDQutGN0YjQtdC8Lg=="
        ConvertFrom-Utf8Base64 "0KTQuNC+0LvQtdGC0L7QstC+LdGH0LXRgNC90YvQtSBtaXNzaW5nIHRleHR1cmUg0LTQvtC70LbQvdGLINC/0YDQvtC/0LDRgdGC0YwsINC/0L7RgtC+0LzRgyDRh9GC0L4g0LjQs9GA0LAg0LHQvtC70YzRiNC1INC90LUg0LfQsNCy0LjRgdC40YIg0L7RgiDRgdC60LDRh9C40LLQsNC90LjRjyDRgdC60LjQvdCwINCyINC80L7QvNC10L3RgiDRgNC10L3QtNC10YDQsC4="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

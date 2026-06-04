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
        ConvertFrom-Utf8Base64 "0JvQsNGD0L3Rh9C10YAg0YLQtdC/0LXRgNGMINCx0LvQvtC60LjRgNGD0LXRgiDQv9C+0LLRgtC+0YDQvdGL0Lkg0LfQsNC/0YPRgdC6IE1pbmVjcmFmdCDQv9C+0LQg0YLQtdC8INC20LUg0L3QuNC60L7QvCwg0L/QvtC60LAg0L/RgNC10LTRi9C00YPRidC40Lkg0L/RgNC+0YbQtdGB0YEg0LXRidC1INC20LjQsi4="
        ConvertFrom-Utf8Base64 "0JIg0LrQvtC90YHQvtC70YwgTWluZWNyYWZ0INC00L7QsdCw0LLQu9C10L3QsCDQtNC40LDQs9C90L7RgdGC0LjQutCwINC30LDQv9GD0YHQutCwOiDQvdC40LosIFVVSUQsIFZlcnNpb25JZCwgTWFpbkNsYXNzINC4INC90LDQu9C40YfQuNC1IE5lb0ZvcmdlINCyIGNsYXNzcGF0aC4="
        ConvertFrom-Utf8Base64 "0JvQsNGD0L3Rh9C10YAg0YPQtNCw0LvRj9C10YIg0YPRgdGC0LDRgNC10LLRiNC40LUgbG9jay3RhNCw0LnQu9GLINC/0L7RgdC70LUg0LrRgNCw0YjQsCwg0L/QtdGA0LXQt9Cw0LPRgNGD0LfQutC4INC40LvQuCDQv9C10YDQtdC40YHQv9C+0LvRjNC30L7QstCw0L3QuNGPIFBJRCDRgdC40YHRgtC10LzQvtC5Lg=="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

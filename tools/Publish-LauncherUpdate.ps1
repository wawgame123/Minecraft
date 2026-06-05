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
        ConvertFrom-Utf8Base64 "0JrQvdC+0L/QutCwICLQmNCz0YDQsNGC0YwiINCx0LvQvtC60LjRgNGD0LXRgtGB0Y8sINC10YHQu9C4INC00L7RgdGC0YPQv9C90LAg0LHQvtC70LXQtSDQvdC+0LLQsNGPINCy0LXRgNGB0LjRjyDQu9Cw0YPQvdGH0LXRgNCwLg=="
        ConvertFrom-Utf8Base64 "0J3QvtCy0L7RgdGC0Lgg0L/QtdGA0LXRh9C40YLRi9Cy0LDRjtGC0YHRjyDQv9GA0Lgg0L7RgtC60YDRi9GC0LjQuCDQstC60LvQsNC00LrQuCDQuCDQv9C+INC60L3QvtC/0LrQtSAi0J7QsdC90L7QstC40YLRjCIu"
        ConvertFrom-Utf8Base64 "SlZNINC30LDQv9GD0YHQuiDQuNGB0L/QvtC70YzQt9GD0LXRgiDQtNC10YTQvtC70YLQvdGL0LUgRzFHQy3Qv9Cw0YDQsNC80LXRgtGA0Ysg0L7RhNC40YbQuNCw0LvRjNC90L7Qs9C+IE1pbmVjcmFmdCBMYXVuY2hlci4="
        ConvertFrom-Utf8Base64 "UkFNINC/0L4t0L/RgNC10LbQvdC10LzRgyDQstGL0LTQtdC70Y/QtdGC0YHRjyDRgNC+0LLQvdC+INC/0L4g0L3QsNGB0YLRgNC+0LnQutC1INC/0L7Qu9GM0LfQvtCy0LDRgtC10LvRjy4="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

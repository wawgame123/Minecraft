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
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "--self-contained", "false", "--no-restore", "-o", $publishDir)
    $zipName = "Minivibe-$version-WIN-portable.zip"
}
else {
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "-r", $Runtime, "--self-contained", "false", "--no-restore", "-o", $publishDir)
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
        ConvertFrom-Utf8Base64 "0JjRgdC/0YDQsNCy0LvQtdC90LAg0LDQstGC0L7QvtGH0LjRgdGC0LrQsCDQutC+0L3RhNC70LjQutGC0YPRjtGJ0LjRhSDQstC10YDRgdC40Lkg0LzQvtC00L7Qsjog0LXRgdC70LggbW9kSWQg0YHQvtCy0L/QsNC00LDQtdGCINGBIG1hbmlmZXN0Lmpzb24sINC90L4gamFyINC90LUg0YLQvtGCLCDQu9Cw0YPQvdGH0LXRgCDQt9Cw0LzQtdC90LjRgiDQtdCz0L4g0YTQsNC50LvQvtC8INC40LcgR2l0SHViLg=="
        ConvertFrom-Utf8Base64 "0K3RgtC+INGH0LjQvdC40YIg0YHQu9GD0YfQsNC4INCy0YDQvtC00LUg0YfRg9C20L7Qs9C+IFNvZGl1bS9TYWJsZSDQuNC3INGB0YLQsNGA0L7QuSDQv9Cw0L/QutC4IC5taW5lY3JhZnQsINC40Lct0LfQsCDQutC+0YLQvtGA0YvRhSDQuNCz0YDQsCDQv9Cw0LTQsNC70LAg0L3QsCBtaXhpbi3QvtGI0LjQsdC60LDRhS4="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

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
        ConvertFrom-Utf8Base64 "0JjRgdC/0YDQsNCy0LvQtdC9INC30LDQv9GD0YHQuiBOZW9Gb3JnZTog0LvQsNGD0L3Rh9C10YAg0LHQvtC70YzRiNC1INC90LUg0LfQsNCy0LjRgdC40YIg0L7RgiDQuNC80LXQvdC4IGphci3RhNCw0LnQu9CwINC/0YDQvtGE0LjQu9GPINC4INC40YnQtdGCINGA0LXQsNC70YzQvdGL0LUgY2xpZW50L3VuaXZlcnNhbCDQsNGA0YLQtdGE0LDQutGC0Ysg0LvQvtCw0LTQtdGA0LAu"
        ConvertFrom-Utf8Base64 "0J/QsNC/0LrQsCDQuNCz0YDRiyDQvNC+0LbQtdGCINCx0YvRgtGMINGD0LrQsNC30LDQvdCwINC40LPRgNC+0LrQvtC8INCy0YDRg9GH0L3Rg9GOOiDQv9C+0LjRgdC6INCy0LXRgNGB0LjQuSDQuCBsaWJyYXJpZXMg0YPRh9C40YLRi9Cy0LDQtdGCINCy0YvQsdGA0LDQvdC90YvQuSDQv9GD0YLRjCDQuCDQtdCz0L4g0YDQvtC00LjRgtC10LvQtdC5Lg=="
        ConvertFrom-Utf8Base64 "UkFNINGC0LXQv9C10YDRjCDQstGL0LTQtdC70Y/QtdGC0YHRjyDRgNC+0LLQvdC+INC/0L4g0L3QsNGB0YLRgNC+0LnQutC1INC/0L7Qu9GM0LfQvtCy0LDRgtC10LvRjy4="
        ConvertFrom-Utf8Base64 "0JTQvtCx0LDQstC70LXQvdGLINC/0LvQsNCy0L3Ri9C1INC/0L7Qu9C30YPQvdC60Lgg0L/RgNC+0LfRgNCw0YfQvdC+0YHRgtC4INC/0LDQvdC10LvQtdC5INC4INCy0LjQtNC40LzQvtGB0YLQuCDQtNC40L3QsNC80LjRh9C10YHQutC+0LPQviDRhNC+0L3QsC4="
        ConvertFrom-Utf8Base64 "0JzQvtC00Ysg0LjQtyBtYW5pZmVzdCByZXF1aXJlZEZpbGVzINGC0LXQv9C10YDRjCDRgdC40L3RhdGA0L7QvdC40LfQuNGA0YPRjtGC0YHRjyDQutCw0Log0L7QsdGP0LfQsNGC0LXQu9GM0L3Ri9C1Lg=="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

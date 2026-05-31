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
    Invoke-Checked dotnet @("restore", $project, "--ignore-failed-sources")
    Invoke-Checked dotnet @("publish", $project, "-c", $Configuration, "--self-contained", "false", "--no-restore", "-o", $publishDir)
    $packageSuffix = "framework"
}
else {
    Invoke-Checked dotnet @("restore", $project, "-r", $Runtime, "--ignore-failed-sources")
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
        $utf8.GetString([Convert]::FromBase64String("0JjRgdC/0YDQsNCy0LvQtdC9INC30LDQv9GD0YHQuiBOZW9Gb3JnZTogdmFuaWxsYSAxLjIxLjEuamFyINCx0L7Qu9GM0YjQtSDQvdC1INC00L7QsdCw0LLQu9GP0LXRgtGB0Y8g0LIgY2xhc3NwYXRoIE5lb0ZvcmdlLCDQuNC3LdC30LAg0YfQtdCz0L4g0L/RgNC+0L/QsNC00LDQtdGCINC60L7QvdGE0LvQuNC60YIg0LzQvtC00YPQu9C10LkgbWluZWNyYWZ0INC4IF8xLl8yMS5fMS4=")),
        $utf8.GetString([Convert]::FromBase64String("0J7QsdC90L7QstC70LXQvSDQstC40LfRg9Cw0Lsg0L3QsNGB0YLRgNC+0LXQujog0YPQsdGA0LDQvdCwINC70LjRiNC90Y/RjyDQutC90L7Qv9C60LAg0L/QsNC/0LrQuCDQuNCz0YDRiyDRgSDQs9C70LDQstC90L7Qs9C+INGN0LrRgNCw0L3QsCwg0YPQtNCw0LvQtdC90Ysg0YLQtdC80LAv0LDQutGG0LXQvdGCINC4INC60L7QvNC/0LDQutGC0L3Ri9C5INGA0LXQttC40LwsINC00L7QsdCw0LLQu9C10L3RiyDRhtCy0LXRgtCwINCz0YDQsNC00LjQtdC90YLQsC4=")),
        $utf8.GetString([Convert]::FromBase64String("0KPQsdGA0LDQvdGLINC/0L7Rh9GC0LAsIGVuZHBvaW50INC4INCw0LLRgtC+0L7RgtC60YDRi9GC0LjQtSDQv9C40YHRjNC80LAg0LTQu9GPINC+0YLRh9C10YLQvtCyOyDQvtGI0LjQsdC60Lgg0YLQtdC/0LXRgNGMINGB0L7RhdGA0LDQvdGP0Y7RgtGB0Y8g0YLQvtC70YzQutC+INC70L7QutCw0LvRjNC90L4g0Lgg0LrQvtC/0LjRgNGD0Y7RgtGB0Y8g0LIg0LHRg9GE0LXRgC4="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

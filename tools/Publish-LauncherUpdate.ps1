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
        $utf8.GetString([Convert]::FromBase64String("0JrQvdC+0L/QutCwINGB0LrQuNC90LAg0YPQv9GA0L7RidC10L3QsDogwqvQodC+0YXRgNCw0L3QuNGC0Ywg0YHQutC40L3CuyDRgdGA0LDQt9GDINGB0YLQsNCy0LjRgiDQtdCz0L4g0LvQvtC60LDQu9GM0L3QviDQuCDQt9Cw0LPRgNGD0LbQsNC10YIg0LIgR2l0SHViLg==")),
        $utf8.GetString([Convert]::FromBase64String("0KEg0L/RgNCw0LLQvtC5INC/0LDQvdC10LvQuCDRgdC60LjQvdC+0LIg0YPQsdGA0LDQvdGLINGB0YHRi9C70LrQuCwgVVJMINC4INC70LjRiNC90LjQtSDQutC90L7Qv9C60Lgu")),
        $utf8.GetString([Convert]::FromBase64String("0JHQsNC30L7QstGL0Lkg0LDQtNGA0LXRgSDRgdC60LjQvdC+0LIg0Lgg0LDQtNGA0LXRgSDQt9Cw0LPRgNGD0LfRh9C40LrQsCDRgtC10L/QtdGA0Ywg0LfQsNC00LDRjtGC0YHRjyDQstC90YPRgtGA0Lgg0LvQsNGD0L3Rh9C10YDQsC4="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

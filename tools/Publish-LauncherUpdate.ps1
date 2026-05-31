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
        $utf8.GetString([Convert]::FromBase64String("0JjRgdC/0YDQsNCy0LvQtdC9INC30LDQv9GD0YHQuiBOZW9Gb3JnZTog0LvQsNGD0L3Rh9C10YAg0YLQtdC/0LXRgNGMINGD0YHRgtCw0L3QsNCy0LvQuNCy0LDQtdGCINC+0YTQuNGG0LjQsNC70YzQvdGL0Lkg0LrQu9C40LXQvdGC0YHQutC40Lkg0L/RgNC+0YTQuNC70Ywg0LggcGF0Y2hlZCBsaWJyYXJpZXMg0L/QtdGA0LXQtCDRgdGC0LDRgNGC0L7QvCDQuNCz0YDRiy4=")),
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvSDQsdGL0YHRgtGA0YvQuSDQstGL0LHQvtGAINC/0LDQv9C60Lgg0LjQs9GA0Ysg0L3QsCDQs9C70LDQstC90L7QvCDRjdC60YDQsNC90LU7INGB0YPRidC10YHRgtCy0YPRjtGJ0LjQtSB2ZXJzaW9ucywgbW9kcywgY29uZmlnINC4IHNhdmVzINC+0YHRgtCw0Y7RgtGB0Y8g0L3QsCDQvNC10YHRgtC1Lg==")),
        $utf8.GetString([Convert]::FromBase64String("0JjRgdC/0YDQsNCy0LvQtdC90Ysg0L/QvtCy0YLQvtGA0L3Ri9C1IC5kb3dubG9hZC3RhNCw0LnQu9GLINC4INC/0L7QtNCz0L7RgtC+0LLQutCwIHJ1bnRpbWUg0L/RgNC4INGD0YHRgtCw0L3QvtCy0LrQtSwg0YfRgtC+0LHRiyDQutC90L7Qv9C60LAg0JjQs9GA0LDRgtGMINC/0L7Rj9Cy0LvRj9C70LDRgdGMINGC0L7Qu9GM0LrQviDQv9C+0YHQu9C1INCz0L7RgtC+0LLQvtC5INGB0LHQvtGA0LrQuC4="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

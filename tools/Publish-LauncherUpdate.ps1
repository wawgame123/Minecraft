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
        $utf8.GetString([Convert]::FromBase64String("0JjRgdC/0YDQsNCy0LvQtdC9INC30LDQv9GD0YHQuiBOZW9Gb3JnZTog0LvQsNGD0L3Rh9C10YAg0YHQutCw0YfQuNCy0LDQtdGCINCx0LjQsdC70LjQvtGC0LXQutC4IGxvYWRlcifQsCDQuCDRgdGC0LDRgNGC0YPQtdGCINGH0LXRgNC10LcgQm9vdHN0cmFwTGF1bmNoZXIsINC/0L7RjdGC0L7QvNGDINC80L7QtNGLINC40Lcg0L/QsNC/0LrQuCBtb2RzINC/0L7QtNGF0LLQsNGC0YvQstCw0Y7RgtGB0Y8u")),
        $utf8.GetString([Convert]::FromBase64String("0J/RgNC+0LLQtdGA0LrQsCDRhNCw0LnQu9C+0LIg0YPRgdC60L7RgNC10L3QsDog0LHRi9GB0YLRgNGL0Lkg0YHRgtCw0YDRgiDQv9GA0L7QstC10YDRj9C10YIg0YDQsNC30LzQtdGAINC/0LDRgNCw0LvQu9C10LvRjNC90L4sINGD0YHRgtCw0L3QvtCy0LrQsCDQuCDRgNC10LzQvtC90YIg0YHQutCw0YfQuNCy0LDRjtGCINC00L4gMTYg0YTQsNC50LvQvtCyINC+0LTQvdC+0LLRgNC10LzQtdC90L3Qvi4=")),
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvdGLINC/0L7QtNGC0LLQtdGA0LbQtNC10L3QuNC1INC90LjQutCwLCDRhtC10L3RgtGA0LjRgNC+0LLQsNC90LjQtSDQvtC60L7QvSDQuCDQstGB0YLRgNC+0LXQvdC90LDRjyAzRC3QutCw0YDRgtCwIEJsdWVNYXAg0LIg0YDQsNC30LTQtdC70LUg0JrQsNGA0YLQsC4="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

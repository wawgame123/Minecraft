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
        $utf8.GetString([Convert]::FromBase64String("0JLQtdGA0YHQuNGPINC70LDRg9C90YfQtdGA0LAg0YLQtdC/0LXRgNGMINC/0L7QutCw0LfRi9Cy0LDQtdGC0YHRjyDQvtGC0LTQtdC70YzQvdC+INC+0YIg0LLQtdGA0YHQuNC4INGB0LHQvtGA0LrQuCBNaW5lY3JhZnQu")),
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvSDRgNCw0LfQtNC10Lsg0YHQutC40L3QvtCyOiDQstGL0LHQvtGAIFBORywg0YPRgdGC0LDQvdC+0LLQutCwINC70L7QutCw0LvRjNC90L7Qs9C+IE9mZmxpbmVTa2lucy3QutC10YjQsCDQuCAzRC3Qv9GA0LXQtNC/0YDQvtGB0LzQvtGC0YAu")),
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvdCwINC90LDRgdGC0YDQvtC50LrQsCDQvtCx0YnQtdCz0L4g0YHQtdGA0LLQtdGA0LAg0YHQutC40L3QvtCyINGE0L7RgNC80LDRgtCwIHNraW5zL9Cd0LjQui5wbmcg0LTQu9GPINC40LPRgNC+0LrQvtCyINC/0LjRgNCw0YLRgdC60L7Qs9C+INGB0LXRgNCy0LXRgNCwLg==")),
        $utf8.GetString([Convert]::FromBase64String("0J3QtdC80L3QvtCz0L4g0L7RgtC/0L7Qu9C40YDQvtCy0LDQvSDQstC40LfRg9Cw0Lsg0Lgg0YDQtdC90LTQtdGA0LjQvdCzINGC0LXQutGB0YLQsC4="))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

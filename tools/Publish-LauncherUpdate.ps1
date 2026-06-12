param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
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

Invoke-Checked dotnet @(
    "publish",
    $project,
    "-c",
    $Configuration,
    "-r",
    $Runtime,
    "--self-contained",
    "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o",
    $publishDir
)
$zipName = "Minivibe-$version-WIN-portable.zip"

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
        ConvertFrom-Utf8Base64 "0JIgbWFjT1Mt0LvQsNGD0L3Rh9C10YAg0LLRgdGC0YDQvtC10L3QsCBCbHVlTWFwINGH0LXRgNC10Lcg0YHQuNGB0YLQtdC80L3Ri9C5IFdLV2ViVmlldy4="
        ConvertFrom-Utf8Base64 "0JTQvtCx0LDQstC70LXQvdC+INC40L3RgtC10YDQsNC60YLQuNCy0L3QvtC1IDNELdC/0YDQtdCy0YzRjiDRgdC60LjQvdCwINCy0L3Rg9GC0YDQuCBtYWNPUy3Qu9Cw0YPQvdGH0LXRgNCwLg=="
        ConvertFrom-Utf8Base64 "SFRNTC3QvdC+0LLQvtGB0YLQuCDRgtC10L/QtdGA0Ywg0L7RgtC60YDRi9Cy0LDRjtGC0YHRjyDQstC90YPRgtGA0Lgg0LvQsNGD0L3Rh9C10YDQsC4="
        ConvertFrom-Utf8Base64 "0J3QvtCy0L7RgdGC0Lgg0Lgg0LjQt9C+0LHRgNCw0LbQtdC90LjRjyDQv9GA0LjQvdGD0LTQuNGC0LXQu9GM0L3QviDQvtCx0L3QvtCy0LvRj9GO0YLRgdGPINCx0LXQtyDRg9GB0YLQsNGA0LXQstGI0LXQs9C+IENETi3QutGN0YjQsC4="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

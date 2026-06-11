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
        ConvertFrom-Utf8Base64 "0J7QutC90L4g0LvQsNGD0L3Rh9C10YDQsCDQsNCy0YLQvtC80LDRgtC40YfQtdGB0LrQuCDRg9C80LXQvdGM0YjQsNC10YLRgdGPINC/0L7QtCDRgNCw0LHQvtGH0YPRjiDQvtCx0LvQsNGB0YLRjCDQvdC10LHQvtC70YzRiNC40YUg0Y3QutGA0LDQvdC+0LIu"
        ConvertFrom-Utf8Base64 "0J/QvtC40YHQuiDRg9GB0YLQsNC90L7QstC70LXQvdC90L7Qs9C+IE5lb0ZvcmdlINC00L7Qv9C+0LvQvdC40YLQtdC70YzQvdC+INC/0YDQvtCy0LXRgNGP0LXRgiBKQVIg0YEg0LjQvNC10L3QtdC8INCy0LXRgNGB0LjQuCDQuCBKQVIg0YEg0LjQvNC10L3QtdC8INC/0LDQv9C60Lgg0YHQsdC+0YDQutC4Lg=="
        ConvertFrom-Utf8Base64 "V2luZG93cyBwb3J0YWJsZSDRgtC10L/QtdGA0Ywg0L/Rg9Cx0LvQuNC60YPQtdGC0YHRjyBzZWxmLWNvbnRhaW5lZCDQuCDQvdC1INGC0YDQtdCx0YPQtdGCINC+0YLQtNC10LvRjNC90L7QuSDRg9GB0YLQsNC90L7QstC60LggLk5FVCBSdW50aW1lLg=="
        ConvertFrom-Utf8Base64 "0JjRgdC/0YDQsNCy0LvQtdC90LAg0LrQvtC80L/QvtC90L7QstC60LAg0YPRgdGC0LDQvdC+0LLRidC40LrQsDog0LrQvdC+0L/QutC4INCy0YvQsdC+0YDQsCDQv9Cw0L/QutC4INC4INGD0YHRgtCw0L3QvtCy0LrQuCDQv9C+0LvQvdC+0YHRgtGM0Y4g0LLQuNC00L3RiyDQv9GA0LggRFBJLdC80LDRgdGI0YLQsNCx0LjRgNC+0LLQsNC90LjQuC4="
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

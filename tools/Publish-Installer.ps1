param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "installer\MinivibeInstaller.csproj"
$publishDir = Join-Path $root "artifacts\installer"
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
    throw "Version is missing in MinivibeInstaller.csproj"
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
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o",
    $publishDir
)

$sourceExe = Join-Path $publishDir "MinivibeInstaller.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Single-file installer was not produced: $sourceExe"
}

$installerName = "MinivibeInstaller-$version.exe"
$installerPath = Join-Path $launcherDir $installerName
Assert-UnderRoot -Path $installerPath -Root $root
if (Test-Path -LiteralPath $installerPath) {
    Remove-Item -LiteralPath $installerPath -Force
}

Copy-Item -LiteralPath $sourceExe -Destination $installerPath -Force
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Output "Published installer: $installerPath"
Write-Output "SHA256: $hash"

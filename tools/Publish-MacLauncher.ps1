param(
    [string]$Configuration = "Release",
    [string[]]$Runtimes = @("osx-arm64", "osx-x64")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "mac\Minivibe.Mac\Minivibe.Mac.csproj"
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
    throw "Version is missing in Minivibe.Mac.csproj"
}

Assert-UnderRoot -Path $launcherDir -Root $root
New-Item -ItemType Directory -Force -Path $launcherDir | Out-Null

foreach ($runtime in $Runtimes) {
    $publishDir = Join-Path $root "artifacts\publish\minivibe-mac-$runtime"
    Assert-UnderRoot -Path $publishDir -Root $root
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Invoke-Checked dotnet @(
        "publish",
        $project,
        "-c",
        $Configuration,
        "-r",
        $runtime,
        "--self-contained",
        "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o",
        $publishDir
    )

    $readmePath = Join-Path $publishDir "README-mac.txt"
    @"
minivibe mac $version ($runtime)

If macOS blocks the app after unzip, open Terminal in this folder and run:
chmod +x ./MinivibeMac
./MinivibeMac

The launcher is self-contained and does not require installing .NET separately.
"@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

    $zipName = "MinivibeMac-$version-$runtime.zip"
    $zipPath = Join-Path $launcherDir $zipName
    Assert-UnderRoot -Path $zipPath -Root $root
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "Published mac launcher: $zipPath"
    Write-Output "SHA256: $hash"
}

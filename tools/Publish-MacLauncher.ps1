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

function ConvertTo-ZipExternalAttributes {
    param(
        [Parameter(Mandatory = $true)]
        [int]$UnixMode
    )

    $value = [uint32](([uint64]$UnixMode) -shl 16)
    return [BitConverter]::ToInt32([BitConverter]::GetBytes($value), 0)
}

function New-ZipArchiveWithUnixPermissions {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [Parameter(Mandatory = $true)]
        [string[]]$ExecutableEntries
    )

    Add-Type -AssemblyName System.IO.Compression
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $executableLookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $ExecutableEntries) {
        [void]$executableLookup.Add($entry.Replace("\", "/"))
    }

    $zipStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse) {
                $fullName = [System.IO.Path]::GetFullPath($file.FullName)
                $relativePath = $fullName.Substring($sourceRoot.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar).Replace("\", "/")
                $entry = $archive.CreateEntry($relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.ExternalAttributes = if ($executableLookup.Contains($relativePath)) {
                    ConvertTo-ZipExternalAttributes 0x81ED
                } else {
                    ConvertTo-ZipExternalAttributes 0x81A4
                }

                $input = [System.IO.File]::OpenRead($fullName)
                try {
                    $output = $entry.Open()
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $zipStream.Dispose()
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
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o",
        $publishDir
    )

    $readmePath = Join-Path $publishDir "README-mac.txt"
    @"
minivibe mac $version ($runtime)

Fast start:
1. Unzip this archive.
2. Double-click Run-Minivibe.command.

Terminal start from any folder:
cd "/path/to/unzipped/minivibe"
chmod +x ./Run-Minivibe.command ./MinivibeMac
./Run-Minivibe.command

The launcher is self-contained and does not require installing .NET separately.
"@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

    $runScriptPath = Join-Path $publishDir "Run-Minivibe.command"
    $runScript = @(
        '#!/bin/sh'
        'cd "$(dirname "$0")" || exit 1'
        'chmod +x ./MinivibeMac 2>/dev/null'
        'exec ./MinivibeMac'
    ) -join "`n"
    [System.IO.File]::WriteAllText($runScriptPath, $runScript + "`n", [System.Text.Encoding]::ASCII)

    $arch = if ($runtime -eq "osx-arm64") { "arm64" } elseif ($runtime -eq "osx-x64") { "x64" } else { $runtime }
    $zipName = "Minivibe-$version-MAC-$arch.zip"
    $zipPath = Join-Path $launcherDir $zipName
    Assert-UnderRoot -Path $zipPath -Root $root
    New-ZipArchiveWithUnixPermissions -SourceDirectory $publishDir -DestinationPath $zipPath -ExecutableEntries @("MinivibeMac", "Run-Minivibe.command")
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "Published mac launcher: $zipPath"
    Write-Output "SHA256: $hash"
}

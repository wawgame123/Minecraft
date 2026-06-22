param(
    [string]$Configuration = "Release",
    [string[]]$Runtimes = @("osx-arm64", "osx-x64"),
    [switch]$SkipUpdateManifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "mac\Minivibe.Mac\Minivibe.Mac.csproj"
$launcherDir = Join-Path $root "launcher"
$updatePath = Join-Path $launcherDir "update.json"
$platforms = [ordered]@{}
$existingUpdate = if (Test-Path -LiteralPath $updatePath) {
    Get-Content -Raw -LiteralPath $updatePath | ConvertFrom-Json
} else { $null }

function Get-JsonPropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

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

function ConvertFrom-Utf8Base64([string]$Value) {
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

function Test-IsOlderVersion([string]$SourceVersion, [string]$TargetVersion) {
    $source = $null
    $target = $null
    if (-not [Version]::TryParse($SourceVersion, [ref]$source)) { return $false }
    if (-not [Version]::TryParse($TargetVersion, [ref]$target)) { return $false }
    return $source -ge [Version]"0.4.0" -and $source -lt $target
}

function New-ChangedFilesPatch {
    param(
        [Parameter(Mandatory = $true)][string]$PlatformKey,
        [Parameter(Mandatory = $true)][string]$PreviousZip,
        [Parameter(Mandatory = $true)][string]$NewDirectory,
        [Parameter(Mandatory = $true)][string]$PatchZip
    )

    $workRoot = Join-Path $root "artifacts\patch\$PlatformKey"
    $previousDirectory = Join-Path $workRoot "previous"
    $changedDirectory = Join-Path $workRoot "changed"
    if (Test-Path -LiteralPath $PatchZip) {
        Remove-Item -LiteralPath $PatchZip -Force
    }
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $previousDirectory, $changedDirectory | Out-Null
    Expand-Archive -LiteralPath $PreviousZip -DestinationPath $previousDirectory -Force
    $previousFileCount = (Get-ChildItem -LiteralPath $previousDirectory -File -Recurse).Count
    $newFileCount = (Get-ChildItem -LiteralPath $NewDirectory -File -Recurse).Count
    if ($previousFileCount -le 10 -and $newFileCount -gt 10) {
        Write-Output "Skipping patch from single-file package; the old client requires one full migration update."
        return $false
    }

    $newRoot = [IO.Path]::GetFullPath($NewDirectory).TrimEnd('\', '/')
    $newPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $NewDirectory -File -Recurse) {
        $relativePath = [IO.Path]::GetFullPath($file.FullName).Substring($newRoot.Length).TrimStart('\', '/')
        [void]$newPaths.Add($relativePath.Replace('\', '/'))
        $previousFile = Join-Path $previousDirectory $relativePath
        $changed = -not (Test-Path -LiteralPath $previousFile)
        if (-not $changed) {
            $changed = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $previousFile -Algorithm SHA256).Hash
        }

        if ($changed) {
            $destination = Join-Path $changedDirectory $relativePath
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }

    $previousRoot = [IO.Path]::GetFullPath($previousDirectory).TrimEnd('\', '/')
    $removedPaths = @(
        foreach ($file in Get-ChildItem -LiteralPath $previousDirectory -File -Recurse) {
            $relativePath = [IO.Path]::GetFullPath($file.FullName).Substring($previousRoot.Length).TrimStart('\', '/').Replace('\', '/')
            if ($relativePath -ne '.minivibe-delete.txt' -and -not $newPaths.Contains($relativePath)) {
                $relativePath
            }
        }
    )
    if ($removedPaths.Count -gt 0) {
        [IO.File]::WriteAllLines((Join-Path $changedDirectory '.minivibe-delete.txt'), $removedPaths, [Text.UTF8Encoding]::new($false))
    }

    if ((Get-ChildItem -LiteralPath $changedDirectory -File -Recurse).Count -eq 0) {
        return $false
    }

    New-ZipArchiveWithUnixPermissions `
        -SourceDirectory $changedDirectory `
        -DestinationPath $PatchZip `
        -ExecutableEntries @("MinivibeMac", "Run-Minivibe.command")
    return $true
}

[xml]$projectXml = Get-Content -Raw -LiteralPath $project
$version = $projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version is missing in Minivibe.Mac.csproj"
}

Assert-UnderRoot -Path $launcherDir -Root $root
New-Item -ItemType Directory -Force -Path $launcherDir | Out-Null
$existingPlatforms = Get-JsonPropertyValue $existingUpdate "platforms"
$macNotes = @(
    ConvertFrom-Utf8Base64 "0JIgbWFjT1Mt0LvQsNGD0L3Rh9C10YDQtSDRgtC40L/RiyDQvNC+0LTQvtCyINGC0LXQv9C10YDRjCDRgdCy0LXRgNC90YPRgtGLINCyINGB0L/QuNGB0L7QuiDQuCDQvdC1INC30LDQvdC40LzQsNGO0YIg0LzQtdGB0YLQviDQv9C+INGD0LzQvtC70YfQsNC90LjRji4="
    ConvertFrom-Utf8Base64 "0J/QvtGB0LvQtSDRgdCw0LzQvtC+0LHQvdC+0LLQu9C10L3QuNGPIG1hbmlmZXN0Lmpzb24g0L/RgNC+0LLQtdGA0Y/QtdGC0YHRjyDRgdGA0LDQt9GDINC/0L7RgdC70LUg0L/QtdGA0LXQt9Cw0L/Rg9GB0LrQsCDQu9Cw0YPQvdGH0LXRgNCwLCDQtNC+INC/0L7QutCw0LfQsCDQv9Cw0YLRh9C90L7Rg9GC0L7Qsi4="
    ConvertFrom-Utf8Base64 "0JLQtdGA0YHQuNGPINC/0LDQutC10YLQsCDQv9C+0LTQvdGP0YLQsCDQtNC+IDAuNC4yLCB1cGRhdGUuanNvbiDRg9C60LDQt9GL0LLQsNC10YIg0L3QsCDQvdC+0LLRi9C1INCw0YDRhdC40LLRiyDQuCDQstC60LvRjtGH0LDQtdGCINC/0LDRgtGH0Lgg0L7RgiAwLjQuMCDQuCAwLjQuMS4="
)

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
    $platformKey = "mac-$arch"
    $patches = [ordered]@{}
    Get-ChildItem -LiteralPath $launcherDir -Filter "Minivibe-$version-FROM-*-MAC-$arch-patch.zip" -File |
        Remove-Item -Force
    foreach ($previousPackage in Get-ChildItem -LiteralPath $launcherDir -Filter "Minivibe-*-MAC-$arch.zip" -File) {
        if ($previousPackage.Name -notmatch "^Minivibe-(?<version>.+)-MAC-$([Regex]::Escape($arch))\.zip$") { continue }
        $sourceVersion = $Matches.version
        if (-not (Test-IsOlderVersion -SourceVersion $sourceVersion -TargetVersion $version)) { continue }

        $patchName = "Minivibe-$version-FROM-$sourceVersion-MAC-$arch-patch.zip"
        $patchPath = Join-Path $launcherDir $patchName
        if (New-ChangedFilesPatch -PlatformKey $platformKey -PreviousZip $previousPackage.FullName -NewDirectory $publishDir -PatchZip $patchPath) {
            $patches[$sourceVersion] = [ordered]@{
                url = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/launcher/$patchName"
                sha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            Write-Output "Published mac patch: $patchPath"
        }
    }

    $platforms[$platformKey] = [ordered]@{
        version = $version
        url = "https://raw.githubusercontent.com/wawgame123/Minecraft/main/launcher/$zipName"
        sha256 = $hash
        notes = $macNotes
        patches = $patches
    }
    Write-Output "Published mac launcher: $zipPath"
    Write-Output "SHA256: $hash"
}

if ($SkipUpdateManifest) {
    Write-Output "Skipped update manifest for prerelease build."
    exit 0
}

if ($null -eq $existingUpdate) {
    throw "Launcher update manifest was not found: $updatePath"
}

$allPlatforms = [ordered]@{}
if ($null -ne $existingPlatforms) {
    foreach ($property in $existingPlatforms.PSObject.Properties) {
        $allPlatforms[$property.Name] = $property.Value
    }
}
foreach ($entry in $platforms.GetEnumerator()) {
    $allPlatforms[$entry.Key] = $entry.Value
}

$mergedUpdate = [ordered]@{
    version = [string](Get-JsonPropertyValue $existingUpdate "version")
    url = [string](Get-JsonPropertyValue $existingUpdate "url")
    sha256 = [string](Get-JsonPropertyValue $existingUpdate "sha256")
    mandatory = [bool](Get-JsonPropertyValue $existingUpdate "mandatory")
    notes = @((Get-JsonPropertyValue $existingUpdate "notes"))
    platforms = $allPlatforms
}
$mergedUpdate | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $updatePath -Encoding UTF8
Write-Output "Updated platform assets: $updatePath"

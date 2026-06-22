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
$updatePath = Join-Path $launcherDir "update.json"
$existingUpdate = if (Test-Path -LiteralPath $updatePath) {
    Get-Content -Raw -LiteralPath $updatePath | ConvertFrom-Json
} else { $null }

function Get-JsonPropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

$existingPlatforms = Get-JsonPropertyValue $existingUpdate "platforms"

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

function Test-IsOlderVersion([string]$SourceVersion, [string]$TargetVersion) {
    $source = $null
    $target = $null
    if (-not [Version]::TryParse($SourceVersion, [ref]$source)) { return $false }
    if (-not [Version]::TryParse($TargetVersion, [ref]$target)) { return $false }
    return $source -ge [Version]"0.4.0" -and $source -lt $target
}

function New-ChangedFilesPatch {
    param(
        [Parameter(Mandatory = $true)][string]$PreviousZip,
        [Parameter(Mandatory = $true)][string]$NewDirectory,
        [Parameter(Mandatory = $true)][string]$PatchZip
    )

    $workRoot = Join-Path $root "artifacts\patch\win-x64"
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
    $newPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
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

    Compress-Archive -Path (Join-Path $changedDirectory "*") -DestinationPath $PatchZip -Force
    return $true
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
$patches = [ordered]@{}
Get-ChildItem -LiteralPath $launcherDir -Filter "Minivibe-$version-FROM-*-WIN-patch.zip" -File |
    Remove-Item -Force
foreach ($previousPackage in Get-ChildItem -LiteralPath $launcherDir -Filter "Minivibe-*-WIN-portable.zip" -File) {
    if ($previousPackage.Name -notmatch '^Minivibe-(?<version>.+)-WIN-portable\.zip$') { continue }
    $sourceVersion = $Matches.version
    if (-not (Test-IsOlderVersion -SourceVersion $sourceVersion -TargetVersion $version)) { continue }

    $patchName = "Minivibe-$version-FROM-$sourceVersion-WIN-patch.zip"
    $patchPath = Join-Path $launcherDir $patchName
    if (New-ChangedFilesPatch -PreviousZip $previousPackage.FullName -NewDirectory $publishDir -PatchZip $patchPath) {
        $patches[$sourceVersion] = [ordered]@{
            url = "https://raw.githubusercontent.com/$Repository/$Branch/launcher/$patchName"
            sha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        Write-Output "Published patch: $patchPath"
    }
}

$windowsNotes = @(
    ConvertFrom-Utf8Base64 "0JTQvtCx0LDQstC70LXQvSDQstGL0LHQvtGAINGC0LjQv9C+0LIg0LzQvtC00L7QsiDQsiDQvdCw0YHRgtGA0L7QudC60LDRhSDQu9Cw0YPQvdGH0LXRgNCwOiDQvtCx0Y/Qt9Cw0YLQtdC70YzQvdGL0LUg0LzQvtC00Ysg0YHQutCw0YfQuNCy0LDRjtGC0YHRjyDQstGB0LXQs9C00LAsINC90LXQvtCx0Y/Qt9Cw0YLQtdC70YzQvdGL0LUgLSDRgtC+0LvRjNC60L4g0LTQu9GPINCy0YvQsdGA0LDQvdC90YvRhSDRgtC40L/QvtCyLg=="
    ConvertFrom-Utf8Base64 "0JLQtdGA0YHQuNGPINC/0LDQutC10YLQsCDQv9C+0LTQvdGP0YLQsCDQtNC+IDAuNC4xLCB1cGRhdGUuanNvbiDRg9C60LDQt9GL0LLQsNC10YIg0L3QsCDQvdC+0LLRi9C1INCw0YDRhdC40LLRiyDQuCDQstC60LvRjtGH0LDQtdGCINC/0LDRgtGHINC+0YIgMC40LjAu"
)
$allPlatforms = [ordered]@{}
if ($null -ne $existingPlatforms) {
    foreach ($property in $existingPlatforms.PSObject.Properties) {
        $allPlatforms[$property.Name] = $property.Value
    }
}
$allPlatforms["win-x64"] = [ordered]@{
    version = $version
    url = $url
    sha256 = $hash
    notes = $windowsNotes
    patches = $patches
}

$update = [ordered]@{
    version = $version
    url = $url
    sha256 = $hash
    mandatory = $false
    notes = $windowsNotes
    platforms = $allPlatforms
}

$update | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

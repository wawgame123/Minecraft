param(
    [string]$PackRoot = "server-pack/neoforge-21.1.228",
    [string]$Repository = "wawgame123/Minecraft",
    [string]$Branch = "main",
    [string]$Output = "manifest.json",
    [string]$ServerName = "minivibe",
    [string]$PackVersion = "1.0.0",
    [string]$MinecraftVersion = "1.21.1",
    [string]$Loader = "neoforge",
    [string]$LoaderVersion = "21.1.228",
    [string]$BlueMapUrl = "http://213.152.43.44:25738/#world:338:0:-823:6754:0:0:0:1:flat"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Convert-ToRawUrl([string]$RelativePath) {
    $segments = $RelativePath -split '[\\/]'
    $encoded = foreach ($segment in $segments) {
        [uri]::EscapeDataString($segment)
    }

    "https://raw.githubusercontent.com/$Repository/$Branch/" + ($encoded -join "/")
}

function Get-Category([string]$RelativePath) {
    $firstSegment = ($RelativePath -split '[\\/]')[0].ToLowerInvariant()

    switch ($firstSegment) {
        "mods" { "mod" }
        "config" { "config" }
        "shaderpacks" { "shaderpack" }
        "resourcepacks" { "resourcepack" }
        "emotes" { "emote" }
        default {
            if ($RelativePath.EndsWith(".jar", [StringComparison]::OrdinalIgnoreCase)) {
                "loader"
            }
            else {
                "file"
            }
        }
    }
}

function Get-RelativePathCompat([string]$BasePath, [string]$FullPath) {
    $baseWithSlash = $BasePath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $baseUri = New-Object System.Uri($baseWithSlash)
    $fullUri = New-Object System.Uri($FullPath)
    [uri]::UnescapeDataString($baseUri.MakeRelativeUri($fullUri).ToString()).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
}

function Convert-CrlfBytesToLf([byte[]]$Bytes) {
    $normalized = New-Object 'System.Collections.Generic.List[byte]'

    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        $isCrlf = $Bytes[$index] -eq 13 -and
            $index + 1 -lt $Bytes.Length -and
            $Bytes[$index + 1] -eq 10

        if (-not $isCrlf) {
            $normalized.Add($Bytes[$index])
        }
    }

    $normalized.ToArray()
}

function Get-ManifestFileMetadata([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)

    if ($Path.EndsWith(".json", [StringComparison]::OrdinalIgnoreCase)) {
        $bytes = Convert-CrlfBytesToLf $bytes
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    [pscustomobject]@{
        Hash = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
        Size = $bytes.Length
    }
}

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$packAbsolute = (Resolve-Path -LiteralPath (Join-Path $root $PackRoot)).Path
$outputAbsolute = Join-Path $root $Output

$requiredFiles = New-Object System.Collections.Generic.List[object]
$optionalShaders = New-Object System.Collections.Generic.List[object]

Get-ChildItem -LiteralPath $packAbsolute -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relativeToPack = (Get-RelativePathCompat $packAbsolute $_.FullName).Replace("\", "/")
        $relativeToRepo = ($PackRoot.TrimEnd("/", "\") + "/" + $relativeToPack).Replace("\", "/")
        $metadata = Get-ManifestFileMetadata $_.FullName
        $category = Get-Category $relativeToPack

        $entry = [ordered]@{
            path = $relativeToPack
            url = Convert-ToRawUrl $relativeToRepo
            sha256 = $metadata.Hash
            size = $metadata.Size
            category = $category
            required = $category -ne "shaderpack"
        }

        if ($category -eq "shaderpack") {
            $entry.required = $false
            $optionalShaders.Add([pscustomobject]$entry)
        }
        else {
            $requiredFiles.Add([pscustomobject]$entry)
        }
    }

$manifest = [ordered]@{
    serverName = $ServerName
    packVersion = $PackVersion
    minecraftVersion = $MinecraftVersion
    loader = $Loader
    loaderVersion = $LoaderVersion
    blueMapUrl = $BlueMapUrl
    requiredFiles = $requiredFiles
    optionalShaders = $optionalShaders
    news = @(
        "minivibe pack is ready for launcher sync.",
        "The launcher checks only required files and does not delete user mods."
    )
    changelog = @(
        "Added NeoForge $LoaderVersion server pack for Minecraft $MinecraftVersion.",
        "EMI, Forgematica/Litematica, Light Overlay and Replay/Reforged PlayMod are excluded from required sync.",
        "Shaderpacks are listed as optionalShaders and download only when shaders are enabled."
    )
    launch = [ordered]@{
        mainClass = ""
        classpath = @()
        jvmArgs = @()
        gameArgs = @(
            "--username",
            '${player_name}',
            "--version",
            '${version_name}',
            "--gameDir",
            '${game_directory}'
        )
    }
}

$json = $manifest | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $outputAbsolute -Value $json -Encoding UTF8

Write-Output "Manifest written: $outputAbsolute"
Write-Output "Required files: $($requiredFiles.Count)"
Write-Output "Optional shaders: $($optionalShaders.Count)"

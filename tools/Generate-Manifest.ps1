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
    [string]$BlueMapUrl = "http://213.152.43.44:25738/#world:338:0:-823:6754:0:0:0:1:flat",
    [string]$LaunchMainClass = "net.minecraft.client.main.Main",
    [string[]]$LaunchClasspath = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertFrom-Utf8Base64([string]$Value) {
    [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

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

if ($LaunchClasspath.Count -eq 0) {
    $LaunchClasspath = @("$Loader-$LoaderVersion.jar")
}

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
    news = @()
    changelog = @()
    launch = [ordered]@{
        mainClass = $LaunchMainClass
        classpath = $LaunchClasspath
        jvmArgs = @()
        gameArgs = @(
            "--username",
            '${player_name}',
            "--version",
            '${version_name}',
            "--gameDir",
            '${game_directory}',
            "--uuid",
            '${player_uuid}',
            "--accessToken",
            "0",
            "--userType",
            "legacy"
        )
    }
}

$json = $manifest | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $outputAbsolute -Value $json -Encoding UTF8

Write-Output "Manifest written: $outputAbsolute"
Write-Output "Required files: $($requiredFiles.Count)"
Write-Output "Optional shaders: $($optionalShaders.Count)"

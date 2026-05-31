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
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvdCwINC30LDQs9GA0YPQt9C60LAg0YHQutC40L3QvtCyINGH0LXRgNC10LcgQ2xvdWRmbGFyZSBXb3JrZXIg0LIg0L/QsNC/0LrRgyBza2lucyDRgNC10L/QvtC30LjRgtC+0YDQuNGPLg==")),
        $utf8.GetString([Convert]::FromBase64String("0JvQsNGD0L3Rh9C10YAg0L/RgNC40L3QuNC80LDQtdGCIFBORy9KUEcvSlBFRywg0L3QviDRhdGA0LDQvdC40YIg0Lgg0L7RgtC/0YDQsNCy0LvRj9C10YIg0LjRgtC+0LPQvtCy0YvQuSBQTkcg0LTQu9GPIE9mZmxpbmVTa2lucy4=")),
        $utf8.GetString([Convert]::FromBase64String("M0Qt0L/RgNC10LLRjNGOINGB0LrQuNC90LAg0YLQtdC/0LXRgNGMINC/0L7QutCw0LfRi9Cy0LDQtdGCINCy0LXRgNGF0L3QuNC1INGB0LvQvtC4INC80L7QtNC10LvQuC4=")),
        $utf8.GetString([Convert]::FromBase64String("0JTQvtCx0LDQstC70LXQvdGLINC30LDQs9C+0YLQvtCy0LrQsCBXb3JrZXIt0LAg0Lgg0LjQvdGB0YLRgNGD0LrRhtC40Y8g0L/QviDQvdCw0YHRgtGA0L7QudC60LUgR2l0SHViIHRva2VuL1VQTE9BRF9TRUNSRVQu"))
    )
}

$updatePath = Join-Path $launcherDir "update.json"
$update | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updatePath -Encoding UTF8

Write-Output "Published: $zipPath"
Write-Output "Update manifest: $updatePath"
Write-Output "SHA256: $hash"

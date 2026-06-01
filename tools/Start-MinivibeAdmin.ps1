Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "admin\Minivibe.Admin\Minivibe.Admin.csproj"

dotnet run --project $project --configuration Release

<#
.SYNOPSIS
    Publishes HFSQL ODBC Studio as ONE self-contained 64-bit EXE and copies it to .\dist\HfsqlOdbcStudio.exe

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root       = $PSScriptRoot
$project    = Join-Path $root 'src\HfsqlOdbcStudio\HfsqlOdbcStudio.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$distDir    = Join-Path $root 'dist'

# Locate the .NET SDK (PATH first, then a per-user install made by dotnet-install.ps1).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $userDotnet) { $dotnet = $userDotnet }
    else { throw 'The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0' }
}

Write-Host "Using $dotnet" -ForegroundColor DarkGray
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

& $dotnet publish $project -c $Configuration -r win-x64 -o $publishDir -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$exe = Join-Path $publishDir 'HfsqlOdbcStudio.exe'
if (-not (Test-Path $exe)) { throw "Publish output not found: $exe" }
Copy-Item $exe (Join-Path $distDir 'HfsqlOdbcStudio.exe') -Force

$size = '{0:N1} MB' -f ((Get-Item (Join-Path $distDir 'HfsqlOdbcStudio.exe')).Length / 1MB)
Write-Host ""
Write-Host "Done: $(Join-Path $distDir 'HfsqlOdbcStudio.exe') ($size)" -ForegroundColor Green

# Publish PayNex application files for Contabo Windows VPS.
# Does NOT touch SQL Server databases or D:\SqlBackups.
# On the server, this script preserves C:\PayNex\app\appsettings.Production.json.

param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\src\PayNex.Cloud.Api\PayNex.Cloud.Api.csproj"),
    [string]$OutputPath = "C:\PayNex\publish-staging",
    [string]$SitePath = "C:\PayNex\app",
    [switch]$DeployToSite
)

$ErrorActionPreference = "Stop"
$project = Resolve-Path $ProjectPath

Write-Host "Publishing PayNex (code only)..."
dotnet publish $project -c Release -o $OutputPath
Copy-Item (Join-Path $PSScriptRoot "web.config") (Join-Path $OutputPath "web.config") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $OutputPath "logs") | Out-Null

if (-not $DeployToSite) {
    Write-Host "Published to $OutputPath"
    Write-Host "To copy onto IIS without replacing production secrets, re-run with -DeployToSite"
    exit 0
}

if (-not (Test-Path $SitePath)) {
    New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
}

$productionConfig = Join-Path $SitePath "appsettings.Production.json"
$backupConfig = Join-Path $env:TEMP ("appsettings.Production." + (Get-Date -Format "yyyyMMddHHmmss") + ".json")
$hadProductionConfig = Test-Path $productionConfig
if ($hadProductionConfig) {
    Copy-Item $productionConfig $backupConfig -Force
}

Write-Host "Stopping IIS site PayNex (if present)..."
Import-Module WebAdministration -ErrorAction SilentlyContinue
if (Get-Website -Name "PayNex" -ErrorAction SilentlyContinue) {
    Stop-Website -Name "PayNex"
}

robocopy $OutputPath $SitePath /E /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

if ($hadProductionConfig) {
    Copy-Item $backupConfig $productionConfig -Force
    Write-Host "Preserved existing appsettings.Production.json"
} else {
    $sample = Join-Path $OutputPath "appsettings.Production.sample.json"
    if (Test-Path $sample) {
        Copy-Item $sample $productionConfig
        Write-Host "Created appsettings.Production.json from sample. EDIT SECRETS before starting IIS."
    }
}

if (Get-Website -Name "PayNex" -ErrorAction SilentlyContinue) {
    Start-Website -Name "PayNex"
}

Write-Host "Code deployment complete. SQL Server databases were not modified."

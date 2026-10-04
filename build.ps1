# Builds the Angular client into the API's wwwroot, tests both projects and publishes a single deployable app.
# Usage: ./build.ps1 [-SkipTests] [-E2E] [-Output <dir>]
param([switch]$SkipTests, [switch]$E2E, [string]$Output = "$PSScriptRoot/artifacts/publish")
$ErrorActionPreference = 'Stop'

function Invoke-Step([string]$cmd) { Write-Host "> $cmd"; Invoke-Expression $cmd; if ($LASTEXITCODE -ne 0) { throw "Failed: $cmd" } }

Write-Host '==> Frontend: install + i18n check + test + production build (-> backend wwwroot)'
Push-Location "$PSScriptRoot/frontend"
Invoke-Step 'npm ci'
Invoke-Step 'npm run check:i18n'
if (-not $SkipTests) { Invoke-Step 'npm test' }
Invoke-Step 'npm run build:prod'
Pop-Location

Write-Host '==> Backend: restore + build + test'
Push-Location "$PSScriptRoot/backend"
Invoke-Step 'dotnet restore Timetable.sln'
Invoke-Step 'dotnet build Timetable.sln -c Release --no-restore'
if (-not $SkipTests) { Invoke-Step 'dotnet test Timetable.sln -c Release --no-build' }
Write-Host "==> Publish single deployable app to $Output"
Invoke-Step "dotnet publish src/Timetable.Api/Timetable.Api.csproj -c Release -o `"$Output`" -p:SkipClientBuild=true"
Pop-Location

if ($E2E) {
  Write-Host '==> End-to-end tests (Playwright: en/ar x light/dark)'
  Push-Location "$PSScriptRoot/frontend"
  Invoke-Step 'npx playwright install chromium'
  Invoke-Step 'npm run e2e'
  Pop-Location
}

if (-not (Test-Path "$Output/wwwroot/index.html")) { throw 'wwwroot/index.html missing from publish output' }
Write-Host "Published to $Output (run: cd $Output; dotnet Timetable.Api.dll)"

# Full offline check of NexusWindows, the same command as the CI: the engine, its launcher and installers (xUnit,
# dotnet test from the global.json folder), then the Codex adapter's JavaScript (codex/test.ps1: node --test).
# Build and test outputs go to %USERPROFILE%\.nexus\build\nexuswindows. -Full also runs the Codex live tests.
param([switch]$Full)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($Full) { $env:NEXUS_TESTS_FULL = '1' }
Push-Location $PSScriptRoot
try { dotnet test --project tests; if ($LASTEXITCODE) { throw 'dotnet test failed.' } } finally { Pop-Location }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'codex\test.ps1') -Full:$Full
if ($LASTEXITCODE) { throw 'codex/test.ps1 failed.' }
Write-Host 'NexusWindows: all checks passed.'

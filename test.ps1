# Full offline check of NexusWindows, the same command as the CI: the engine, its launcher and installers (xUnit,
# dotnet test from the global.json folder). Build and test outputs go to %USERPROFILE%\.nexus\build\nexuswindows.
# -Full also runs the live tests that need a real desktop session or hold the PC's shared foreground token.
param([switch]$Full)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($Full) { $env:NEXUS_TESTS_FULL = '1' }
Push-Location $PSScriptRoot
try { dotnet test --project tests; if ($LASTEXITCODE) { throw 'dotnet test failed.' } } finally { Pop-Location }
Write-Host 'NexusWindows: all checks passed.'

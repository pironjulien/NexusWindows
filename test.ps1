# Full offline check of NexusWindows, the same command as the CI: the engine, its launcher and installers (xUnit,
# dotnet test from the global.json folder). Build and test outputs go to %USERPROFILE%\.nexus\build\nexuswindows.
# -Full also runs the live tests that need a real desktop session or hold the PC's shared foreground token.
# -Engine runs the process tests (MCP over stdio, helper children) against that engine executable instead of the one
# built for the tests, such as the Native AOT engine of a release package or of an installation.
param([switch]$Full, [string]$Engine)
# Nexus pre-push gate: on a Nexus workstation the run goes through ~\.nexus\git\Invoke-NexusTest.ps1 (NexusInfra), which
# records the tested tree when it passes; the pre-push hook refuses a pushed tree that has not passed this script.
if (-not $env:NEXUS_TEST_EMPREINTE -and (Test-Path -LiteralPath "$HOME\.nexus\git\Invoke-NexusTest.ps1")) { & "$HOME\.nexus\git\Invoke-NexusTest.ps1" -Script $PSCommandPath -Parametres $PSBoundParameters -Arguments $args; exit $LASTEXITCODE }
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($Full) { $env:NEXUS_TESTS_FULL = '1' }
if ($Engine) {
    if (-not (Test-Path -LiteralPath $Engine -PathType Leaf)) { throw "No engine at $Engine." }
    $env:NEXUS_TEST_ENGINE = (Resolve-Path -LiteralPath $Engine).Path
}
Push-Location $PSScriptRoot
try { dotnet test --project tests; if ($LASTEXITCODE) { throw 'dotnet test failed.' } } finally { Pop-Location }
Write-Host 'NexusWindows: all checks passed.'

# Full offline check of NexusWindows: the shared policy (commun/tests, standard library only), then each profile's own
# test.ps1 in its environment outside the repository. Exit code 0 when everything passes.
# -Full is passed to the profiles (end-to-end tests on real windows, slower).
param([switch]$Full)
$ErrorActionPreference = 'Stop'
$env:PYTHONDONTWRITEBYTECODE = '1'
$python = (Get-Command py -ErrorAction SilentlyContinue).Source
$pyArgs = @('-3')
if (-not $python) { $python = (Get-Command python -ErrorAction Stop).Source; $pyArgs = @() }
& $python @pyArgs -B -m unittest discover -s (Join-Path $PSScriptRoot 'commun\tests')
if ($LASTEXITCODE) { throw 'Shared policy: tests failed.' }
$failed = @()
foreach ($profileName in 'claude', 'agy', 'codex') {
    Write-Host "== $profileName" -ForegroundColor Cyan
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "$profileName\test.ps1") -Full:$Full
    if ($LASTEXITCODE) { $failed += $profileName }
}
if ($failed) { throw "Failed: $($failed -join ', ')." }
Write-Host 'NexusWindows: all checks passed.'

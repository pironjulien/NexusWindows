# Full offline check: JavaScript unit and integration tests, plus a build of the native helper outside the repository.
# Nothing is written to the source tree, and the tests never touch the real %USERPROFILE%\.nexus folder (a temporary
# shared folder is used instead). Exit code 0 when everything passes.
# -Full also runs, with the freshly built helper: the real foreground-token and emergency-stop tests, then the real
# background validation (tests/live/background.live.mjs), which builds its own WinForms + Win32 + WPF fixture, shows
# its windows without activation and closes them at the end. Steps that need the real foreground only run when you have
# been idle for 120 s; otherwise they are reported as skipped, never as passed.
[CmdletBinding()]
param([switch]$Full)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$node = (Get-Command node.exe -ErrorAction Stop).Source
$work = Join-Path ([IO.Path]::GetTempPath()) ('codex-windows-computer-use-' + [guid]::NewGuid().ToString('N'))
$saved = @{}
foreach ($name in 'NEXUS_SHARED_HOME', 'NEXUS_WINDOWS_CODEX_HELPER', 'NEXUS_GARDE_FOUS', 'NEXUS_FOREGROUND_EXE', 'NEXUS_LIVE_EXE', 'NEXUS_LIVE_FIXTURE', 'NEXUS_LIVE_REPORTS') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    $env:NEXUS_SHARED_HOME = Join-Path $work 'shared'
    New-Item -ItemType Directory -Path $env:NEXUS_SHARED_HOME -Force | Out-Null

    # The engine under test is built from these sources first: the tests never pick up a runtime installed on this PC.
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $nativeRoot = Join-Path $work 'native'
    & $dotnet publish (Join-Path $root 'native\NexusCodex.UIA.csproj') -c Release -o $nativeRoot `
        "-p:BaseIntermediateOutputPath=$work\build\obj\" "-p:BaseOutputPath=$work\build\bin\" --nologo -v q
    if ($LASTEXITCODE -or -not (Test-Path -LiteralPath (Join-Path $nativeRoot 'NexusCodex.UIA.exe'))) { throw 'Native helper build failed.' }
    $env:NEXUS_WINDOWS_CODEX_HELPER = Join-Path $nativeRoot 'NexusCodex.UIA.exe'
    # The engine reads its policy from NEXUS_GARDE_FOUS, as the installer declares it in production.
    $env:NEXUS_GARDE_FOUS = Join-Path $root 'garde-fous.json'
    & $node --test @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.test.mjs' -File | Select-Object -ExpandProperty FullName)
    if ($LASTEXITCODE) { throw 'JavaScript tests failed.' }

    # "Agent at work" indicator frames rendered by the freshly built helper (nothing is shown on screen).
    $env:NEXUS_FOREGROUND_EXE = Join-Path $nativeRoot 'NexusCodex.UIA.exe'
    & $node --test (Join-Path $root 'tests\indicator.test.mjs')
    if ($LASTEXITCODE) { throw 'Indicator rendering tests failed.' }

    # Real source installation, isolated from the user's Codex profile and running helper. Re-run it with an enabled
    # plugin to prove that the earlier standalone guide is archived instead of discovered twice.
    $cloneHome = Join-Path $work 'clone-codex'
    $cloneState = Join-Path $work 'clone-state'
    New-Item -ItemType Directory -Path $cloneHome -Force | Out-Null
    # A header inside a multiline string is data, never an enabled/disabled plugin declaration.
    [IO.File]::WriteAllText((Join-Path $cloneHome 'config.toml'), "notes = '''`n[plugins.'nexus-windows@public']`nenabled=false`n'''`n")
    & (Join-Path $root 'install.ps1') -CodexHome $cloneHome -StateRoot $cloneState
    & (Join-Path $root 'install.ps1') -CodexHome $cloneHome -StateRoot $cloneState -CheckOnly
    if (-not (Test-Path -LiteralPath (Join-Path $cloneHome 'skills\nexus-windows\SKILL.md'))) { throw 'Source install did not supply its guide.' }
    [IO.File]::WriteAllText((Join-Path $cloneHome 'config.toml'), "[plugins.`"nexus-windows@public`"]`nenabled = true`n")
    & (Join-Path $root 'install.ps1') -CodexHome $cloneHome -StateRoot $cloneState
    & (Join-Path $root 'install.ps1') -CodexHome $cloneHome -StateRoot $cloneState -CheckOnly
    if (Test-Path -LiteralPath (Join-Path $cloneHome 'skills\nexus-windows')) { throw 'Source install left a duplicate guide beside the plugin.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $cloneHome 'backups') -Directory).Count -ne 1) { throw 'The former standalone guide was not archived.' }

    # Alternate valid TOML syntax is tested through the real clone installer, twice and with CheckOnly. No fallback
    # guide or second native build is allowed for a disabled entry or a plugin from any catalogue.
    $cases = @(
        "[plugins.'nexus-windows@personal']`nenabled=false`n",
        "[ plugins . 'nexus-windows@another-catalogue' ] # kept`nenabled=false`n",
        "plugins = { 'nexus-windows@another-catalogue' = { enabled = true } }`n",
        "[mcp_servers . 'nexus-windows']`ncommand='custom.exe'`nenabled=false`n"
    )
    foreach ($initial in $cases) {
        $caseHome = Join-Path $work ([guid]::NewGuid().ToString('N'))
        $caseState = Join-Path $work ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $caseHome -Force | Out-Null
        $caseConfig = Join-Path $caseHome 'config.toml'
        [IO.File]::WriteAllText($caseConfig, $initial)
        & (Join-Path $root 'install.ps1') -CodexHome $caseHome -StateRoot $caseState
        & (Join-Path $root 'install.ps1') -CodexHome $caseHome -StateRoot $caseState
        & (Join-Path $root 'install.ps1') -CodexHome $caseHome -StateRoot $caseState -CheckOnly
        if ([IO.File]::ReadAllText($caseConfig) -cne $initial) { throw 'Clone installation changed an existing plugin/MCP declaration.' }
        if (Test-Path -LiteralPath (Join-Path $caseHome 'skills')) { throw 'Clone installation created a fallback guide for an existing plugin or disabled server.' }
        if (Test-Path -LiteralPath $caseState) { throw 'Clone installation built a second native runtime for an existing plugin or disabled server.' }
    }
    $invalidHome = Join-Path $work 'invalid-codex'
    $invalidState = Join-Path $work 'invalid-state'
    New-Item -ItemType Directory -Path $invalidHome -Force | Out-Null
    $invalidConfig = Join-Path $invalidHome 'config.toml'
    $invalid = "[plugins.'nexus-windows@public']`nenabled=false`n[plugins.'nexus-windows@public']`nenabled=true`n"
    [IO.File]::WriteAllText($invalidConfig, $invalid)
    $refused = $false
    try { & (Join-Path $root 'install.ps1') -CodexHome $invalidHome -StateRoot $invalidState } catch { $refused = $true }
    if (-not $refused -or (Test-Path -LiteralPath $invalidState) -or (Test-Path -LiteralPath (Join-Path $invalidHome 'skills')) -or
        [IO.File]::ReadAllText($invalidConfig) -cne $invalid) { throw 'Invalid TOML was not refused before all installation changes.' }

    if ($Full) {
        $env:NEXUS_FOREGROUND_EXE = Join-Path $nativeRoot 'NexusCodex.UIA.exe'
        & $node --test (Join-Path $root 'tests\foreground.test.mjs') (Join-Path $root 'tests\stop.test.mjs') (Join-Path $root 'tests\ocr.test.mjs')
        if ($LASTEXITCODE) { throw 'Foreground token, emergency stop, OCR or Windows.Graphics.Capture tests failed.' }

        $fixtureRoot = Join-Path $work 'fixture'
        & $dotnet build (Join-Path $root 'tests\live\NexusCodex.LiveFixture.csproj') -c Release -o $fixtureRoot `
            "-p:BaseIntermediateOutputPath=$work\fixture-build\obj\" "-p:BaseOutputPath=$work\fixture-build\bin\" --nologo -v q
        if ($LASTEXITCODE) { throw 'Live fixture build failed.' }
        $env:NEXUS_LIVE_EXE = $env:NEXUS_FOREGROUND_EXE
        $env:NEXUS_LIVE_FIXTURE = Join-Path $fixtureRoot 'NexusCodex.LiveFixture.exe'
        $env:NEXUS_LIVE_REPORTS = Join-Path $work 'reports'
        & $node --test (Join-Path $root 'tests\live\background.live.mjs')
        if ($LASTEXITCODE) { throw 'Live background validation failed.' }
    }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host 'All checks passed.'
exit 0

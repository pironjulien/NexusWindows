# Explicit native launcher; the three agents reuse the same verified immutable runtime.
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'native-runtime.ps1')
$runtime = Get-NexusAgyNativeRuntime
$env:NEXUS_GARDE_FOUS = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'commun\garde-fous.json'
& $runtime.NativeExecutable --mcp --agent antigravity
exit $LASTEXITCODE

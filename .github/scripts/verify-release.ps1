# Install the published, checksum- and provenance-verified files in an isolated profile. No real client is changed.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$Work)
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v\d+\.\d+\.\d+$') { throw 'A versioned release tag is required.' }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$files = Join-Path $Work 'release'
New-Item -ItemType Directory -Force -Path $files | Out-Null
gh release download $Tag --repo pironjulien/NexusWindows --dir $files
if ($LASTEXITCODE) { throw 'Release download failed.' }
$manifest = Join-Path $files 'SHA256SUMS.txt'
$verified = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([a-f0-9]{64})\s+\*?(\S+)$') { throw 'Invalid release checksum entry.' }
    $hash, $name = $Matches[1], $Matches[2]
    if ([IO.Path]::GetFileName($name) -cne $name) { throw 'Invalid checksum filename.' }
    if (-not $verified.Add($name)) { throw 'Duplicate release checksum entry.' }
    if ((Get-FileHash -LiteralPath (Join-Path $files $name) -Algorithm SHA256).Hash -ine $hash) { throw "Checksum mismatch: $name" }
}
foreach ($required in 'get.ps1', 'NexusWindows-win-x64.zip', 'NexusWindows-win-arm64.zip', 'nexus-windows-claude.mcpb', 'nexus-windows-claude-arm64.mcpb') {
    if (-not $verified.Contains($required)) { throw "Missing required checksum: $required" }
}
$installer = Join-Path $files 'get.ps1'
gh attestation verify $installer --repo pironjulien/NexusWindows --bundle (Join-Path $files "NexusWindows-$($Tag.TrimStart('v')).intoto.jsonl")
if ($LASTEXITCODE) { throw 'Installer provenance verification failed.' }

$testProfile = Join-Path $Work 'profile'
foreach ($folder in '.claude', '.codex', '.gemini', 'AppData\Roaming\Claude') {
    New-Item -ItemType Directory -Force -Path (Join-Path $testProfile $folder) | Out-Null
}
# Without a Claude CLI, the existing user configuration is the evidence that Claude Code is present.
[IO.File]::WriteAllText((Join-Path $testProfile '.claude.json'), '{}', [Text.UTF8Encoding]::new($false))
$env:NEXUS_WINDOWS_PROFILE = $testProfile
$env:NEXUS_WINDOWS_SOURCE = Join-Path $files 'NexusWindows-win-x64.zip'
$env:NEXUS_PACK = '1'
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($pass in 1..2) {
    # Intentionally inherit the pwsh module path: the public bootstrap must choose the Desktop built-ins itself.
    & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer
    if ($LASTEXITCODE) { throw "Release installation failed on pass $pass." }
}
$stable = Join-Path $testProfile '.nexus\nexus-windows\NexusWindows.exe'
$runtime = Get-Content -LiteralPath (Join-Path $testProfile '.nexus\nexus-windows\installation.json') -Raw | ConvertFrom-Json
if ($runtime.Version -ne $Tag.TrimStart('v')) { throw 'The installed version differs from the release.' }
$env:USERPROFILE = $testProfile
$env:APPDATA = Join-Path $testProfile 'AppData\Roaming'
$env:LOCALAPPDATA = Join-Path $testProfile 'AppData\Local'
$env:CODEX_HOME = Join-Path $testProfile '.codex'
$env:NEXUS_WINDOWS_NO_CLI = '1'
$env:NEXUS_SHARED_HOME = Join-Path $testProfile 'shared'
$env:NEXUS_JOURNAL_HOME = Join-Path $testProfile 'journal'
$env:NEXUS_WINDOWS_HOME = Join-Path $testProfile '.nexus\nexus-windows'
$env:NEXUS_NO_UPDATE_CHECK = '1'
& $stable --register all --check
if ($LASTEXITCODE) { throw 'Published client declarations did not pass their check.' }
foreach ($agent in 'codex', 'claude', 'antigravity') {
    $request = '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
    $reply = $request | & $stable --mcp --agent $agent | ConvertFrom-Json
    if ($LASTEXITCODE -or $reply.id -ne 1 -or -not $reply.result.tools) { throw "Installed $agent MCP startup failed." }
}
Write-Host "NexusWindows ${Tag}: verified release, two isolated installations, client checks and three native MCP profiles passed."

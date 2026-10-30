# One-step setup of NexusWindows for Codex on a fresh Windows PC, for people who never used a terminal:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\setup.ps1   (downloaded as a file and read first)
# 1. installs Node.js LTS (official package, through winget) when Node 22 or later is missing;
# 2. preserves any existing plugin or MCP declaration, including disabled and custom entries;
# 3. registers a fresh npm server in Codex config.toml and supplies its guide, or reuses the plugin guide.
# Nothing else is changed; run it again at any time (it only does what is missing).
# Called by the Nexus pack (get-nexus.ps1, NEXUS_PACK=1): no question, no window, Node.js installed by the pack first;
# exit code 0 = done, anything else = failed; last line `NEXUS_PACK_RESULT Codex=declared|already|disabled`.
$ErrorActionPreference = 'Stop'
$package = 'nexus-windows-codex'

$pack = $env:NEXUS_PACK -eq '1'
function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
# PATH as Windows has it now (Machine + User) ahead of this window's: a Node.js installed by winget in this same
# window (by the pack, or below) is otherwise not seen.
function RefreshPath {
    $env:PATH = (@([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User'), $env:PATH) |
        Where-Object { $_ }) -join ';'
}
function NodeMajor {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { return 0 }
    $version = & $node.Source --version
    if ($version -match '^v(\d+)\.') { return [int]$Matches[1] }
    return 0
}

if ($env:OS -ne 'Windows_NT') { throw 'NexusWindows for Codex runs on Windows 10 or 11 only.' }

# 1. Node.js 22+ (https://nodejs.org): the official LTS package; Windows asks once for administrator approval.
RefreshPath
if ((NodeMajor) -lt 22) {
    # The pack installs Node.js itself before calling this script: never a second installer (nor its window) here.
    if ($pack) { throw 'Node.js 22 or later was not found after the pack installed it: nothing was registered.' }
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw 'Node.js 22 or later is needed: install the LTS version from https://nodejs.org, then run this again.'
    }
    Say 'Installing Node.js LTS (official package, through winget)...' Cyan
    winget install --exact --id OpenJS.NodeJS.LTS --source winget --accept-source-agreements --accept-package-agreements
    # This window only: new programs (Codex included, once restarted) get the new PATH from Windows.
    RefreshPath
    if ((NodeMajor) -lt 22) { throw 'Node.js was not installed: install the LTS version from https://nodejs.org, then run this again.' }
}
Say "Node.js: $(node.exe --version)" Green

# Read the existing choices with the package's bundled TOML parser before starting any MCP runtime.
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$config = Join-Path $codexHome 'config.toml'
$current = if (Test-Path -LiteralPath $config) { [IO.File]::ReadAllText($config) } else { '' }
# An older npm mirror must not run a package which predates this read-only command.
$state = & npx.cmd --yes "$package@>=3.7.4" --codex-config-state
if ($LASTEXITCODE -ne 0) { throw 'Codex configuration could not be read: nothing was registered.' }
$state = $state | ConvertFrom-Json
if ($state.plugin -notin @('absent', 'enabled', 'disabled') -or $state.mcp -notin @('absent', 'enabled', 'disabled')) {
    throw 'The package did not return a valid Codex configuration state: nothing was registered.'
}
$fresh = $state.plugin -eq 'absent' -and $state.mcp -eq 'absent'

# 2. Server downloaded from npm (npx.cmd, not npx.ps1, which a default script policy blocks), then initialize and
#    tools/list over stdio: it must answer with its tools.
if ($fresh) {
    Say "Downloading $package..." Cyan
    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"setup","version":"1"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    )
    $ErrorActionPreference = 'Continue'  # Windows PowerShell 5.1 turns native stderr into a terminating error under 'Stop'
    $answers = $requests | npx.cmd --yes "$package@latest" 2> $null
    $ErrorActionPreference = 'Stop'
    $version = $null; $tools = @()
    foreach ($line in $answers) {
        try { $message = $line | ConvertFrom-Json } catch { continue }
        if ($message.id -eq 1) { $version = $message.result.serverInfo.version }
        if ($message.id -eq 2) { $tools = @($message.result.tools | ForEach-Object { $_.name }) }
    }
    if (-not $version -or $tools -notcontains 'look') {
        throw "$package did not answer: please open an issue at https://github.com/pironjulien/NexusWindows/issues"
    }
    Say "$package $version ready ($($tools -join ', '))." Green
}

# 3. Existing plugins and MCP declarations belong to the user. Preserve them verbatim, including custom commands.
$result = 'already'
if ($state.plugin -eq 'disabled' -or $state.mcp -eq 'disabled') {
    $result = 'disabled'
    Say "NexusWindows is disabled in ${config}: choice preserved." Yellow
} elseif (-not $fresh) {
    Say "NexusWindows is already declared in ${config}: choice preserved." Green
} else {
    $latest = if (Test-Path -LiteralPath $config) { [IO.File]::ReadAllText($config) } else { '' }
    if ($latest -cne $current) { throw 'Codex configuration changed during installation: run setup again.' }
    $result = 'declared'
    New-Item -ItemType Directory -Force -Path $codexHome | Out-Null
    $block = "[mcp_servers.nexus-windows]`ncommand = `"npx`"`nargs = [`"-y`", `"$package@latest`"]`nstartup_timeout_sec = 120`n"
    if ($current -and -not $current.EndsWith("`n")) { $block = "`n" + $block }
    if ($current) { $block = "`n" + $block }
    [IO.File]::AppendAllText($config, $block, (New-Object Text.UTF8Encoding $false))
    Say "Registered in $config." Green
}
# The guide command only archives an owned fallback when disabled; it never starts an MCP runtime.
$guide = & npx.cmd --yes "$package@latest" --install-skill
if ($LASTEXITCODE -ne 0 -or -not ($guide -match '^NEXUS_SKILL_RESULT (installed|plugin|disabled)$')) {
    throw 'The NexusWindows Codex guide could not be checked.'
}
if ($result -ne 'disabled') { Say 'Codex guide ready.' Green }
if ($pack) { Write-Output "NEXUS_PACK_RESULT Codex=$result"; exit 0 }
if ($result -eq 'disabled') { return }
Say ''
Say 'Last step: quit Codex completely and open it again,' Yellow
Say 'then ask: "Open Notepad and write a shopping list: milk, eggs, bread."' Yellow

[CmdletBinding()]
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'

function Assert-NexusWindowsClaudeMcpSnapshot([string]$Path, [byte[]]$Before) {
    $actual = [IO.File]::ReadAllBytes($Path)
    if ($actual.Length -ne $Before.Length -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($actual)) -cne
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Before))) {
        throw 'Déclaration Claude modifiée simultanément.'
    }
}
function Write-NexusWindowsClaudeMcpBytes([string]$Path, [byte[]]$Bytes) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
function Save-NexusWindowsClaudeMcp([string]$Path, [byte[]]$Before, $Data) {
    $identity = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Path).ToLowerInvariant())
    $mutex = [Threading.Mutex]::new($false, 'Local\NexusWindows-ClaudeMcp-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identity)))
    $held = $false
    $temporary = $null
    try {
        try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'Une installation de cette déclaration Claude est en cours.' }
        Assert-NexusWindowsClaudeMcpSnapshot $Path $Before
        $backupDirectory = Join-Path $nexusWindowsRuntimeHome 'backups'
        [void][IO.Directory]::CreateDirectory($backupDirectory)
        $backup = Join-Path $backupDirectory ('claude-mcp-' + [guid]::NewGuid().ToString('N') + '.json')
        Write-NexusWindowsClaudeMcpBytes $backup $Before
        $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        $next = [Text.UTF8Encoding]::new($false).GetBytes(($Data | ConvertTo-Json -Depth 16) + "`n")
        Write-NexusWindowsClaudeMcpBytes $temporary $next
        # The mutex serializes our installers; a fresh byte comparison also rejects outside changes during staging.
        Assert-NexusWindowsClaudeMcpSnapshot $Path $Before
        [IO.File]::Replace($temporary, $Path, [NullString]::Value)
        $temporary = $null
    } finally {
        if ($temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'commun\native-runtime.ps1')
$runtime = Install-NexusWindowsNativeRuntime -CheckOnly:$CheckOnly
$plugin = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'NexusClaude\plugins\nexus-windows'
if (-not (Test-Path -LiteralPath $plugin -PathType Container)) { throw 'Plugin Claude nexus-windows absent.' }
$installed = Join-Path $env:USERPROFILE '.claude\plugins\installed_plugins.json'
$targets = @((Join-Path $plugin '.mcp.json'))
if (Test-Path -LiteralPath $installed) {
    $registry = Get-Content -LiteralPath $installed -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in @($registry.plugins['nexus-windows@nexus'])) {
        if ($entry -and $entry.installPath) {
            $path = [IO.Path]::GetFullPath($entry.installPath)
            $allowed = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.claude\plugins\cache\nexus\nexus-windows')) + '\'
            if (-not $path.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cache Claude hors du périmètre nexus-windows.' }
            $targets += Join-Path $path '.mcp.json'
        }
    }
}
foreach ($path in $targets) {
    $before = [IO.File]::ReadAllBytes($path)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($before)
    if ($text.StartsWith([string][char]0xfeff, [StringComparison]::Ordinal)) { $text = $text.Substring(1) }
    $data = $text | ConvertFrom-Json -AsHashtable
    $server = $data.mcpServers['nexus-windows']
    if (-not $server) { throw "Déclaration nexus-windows absente : $path" }
    $command = [string]$server.command
    $argsJson = ConvertTo-Json -InputObject @($server.args) -Compress
    $knownPython = $command -in '${USERPROFILE}\.claude\nexus-claude\venv\Scripts\python.exe',(Join-Path $env:USERPROFILE '.claude\nexus-claude\venv\Scripts\python.exe') -and $argsJson -ceq '["-m","nexus_windows"]' -and
        @($server.Keys | Where-Object { $_ -notin 'type','command','args','env' }).Count -eq 0 -and
        @($server.env.Keys | Where-Object { $_ -notin 'PYTHONUTF8','PYTHONPYCACHEPREFIX' }).Count -eq 0 -and
        $server.env.PYTHONUTF8 -ceq '1' -and $server.env.PYTHONPYCACHEPREFIX -in '${USERPROFILE}\.claude\nexus-claude\cache\pycache',(Join-Path $env:USERPROFILE '.claude\nexus-claude\cache\pycache')
    $nativeCommand = [Environment]::ExpandEnvironmentVariables($command.Replace('${USERPROFILE}', $env:USERPROFILE))
    $nativeBase = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.nexus\nexus-windows\native')) + '\'
    $knownNative = $argsJson -ceq '["--mcp","--agent","claude"]' -and [IO.Path]::IsPathFullyQualified($nativeCommand) -and
        [IO.Path]::GetFullPath($nativeCommand).StartsWith($nativeBase, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetRelativePath($nativeBase, $nativeCommand) -cmatch '^[a-f0-9]{64}\\NexusWindows[.]exe$' -and @($server.Keys | Where-Object { $_ -notin 'type','command','args' }).Count -eq 0
    if (-not ($knownPython -or $knownNative)) { Write-Host "Choix MCP personnalisé/désactivé préservé : $path"; continue }
    $expectedCommand = if ($path -eq $targets[0]) { $runtime.NativeExecutable.Replace($env:USERPROFILE, '${USERPROFILE}') } else { $runtime.NativeExecutable }
    $desired = [ordered]@{ type='stdio'; command=$expectedCommand; args=@('--mcp','--agent','claude') }
    if (($server | ConvertTo-Json -Depth 8 -Compress) -cne ($desired | ConvertTo-Json -Depth 8 -Compress)) {
        if ($CheckOnly) { throw "Adaptateur natif Claude non aligné : $path" }
        $data.mcpServers['nexus-windows'] = $desired
        Save-NexusWindowsClaudeMcp $path $before $data
    }
}
# Claude Desktop côté discussion : jamais raccordé sur ce poste (choix de Julien du 28/10 : outils Nexus dans l'onglet
# Code seulement). scripts\desktop-native.ps1 reste disponible pour le parcours public, sans appel ici.
Write-Host 'NexusWindows Claude : déclarations examinées, aucun rechargement imposé.'

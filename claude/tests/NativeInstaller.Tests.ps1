BeforeAll {
    $script:installerSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'install-native.ps1'
    $script:desktopSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\desktop-native.ps1'
    $ast = [Management.Automation.Language.Parser]::ParseFile($script:installerSource, [ref]$null, [ref]$null)
    foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in 'Assert-NexusWindowsClaudeMcpSnapshot', 'Write-NexusWindowsClaudeMcpBytes', 'Save-NexusWindowsClaudeMcp' }, $false)) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    function Put-InstallerBytes([string]$Path, [string]$Text, [switch]$Bom) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
        $encoding = [Text.UTF8Encoding]::new([bool]$Bom)
        [byte[]]$bytes = $encoding.GetPreamble() + $encoding.GetBytes($Text)
        [IO.File]::WriteAllBytes($Path, $bytes)
        return ,$bytes
    }
    function New-InstallerFixture {
        $base = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $repo = Join-Path $base 'harness\NexusWindows'
        $plugin = Join-Path $base 'harness\NexusClaude\plugins\nexus-windows'
        $fixture = [pscustomobject]@{ Base=$base; Home=(Join-Path $base 'home'); Local=(Join-Path $base 'local'); Roaming=(Join-Path $base 'roaming'); Repo=$repo; Plugin=$plugin; Installer=(Join-Path $repo 'claude\install-native.ps1'); Runner=(Join-Path $base 'runner.ps1') }
        foreach ($directory in $fixture.Home, $fixture.Local, $fixture.Roaming, $plugin, (Join-Path $repo 'claude\scripts'), (Join-Path $repo 'commun')) { [void][IO.Directory]::CreateDirectory($directory) }
        [IO.File]::Copy($script:installerSource, $fixture.Installer)
        [IO.File]::Copy($script:desktopSource, (Join-Path $repo 'claude\scripts\desktop-native.ps1'))
        @'
$nexusWindowsRuntimeHome = Join-Path $env:USERPROFILE '.nexus\nexus-windows'
function Install-NexusWindowsNativeRuntime([switch]$CheckOnly) {
    [pscustomobject]@{ NativeExecutable=(Join-Path $env:USERPROFILE ('.nexus\nexus-windows\native\' + ('a' * 64) + '\NexusWindows.exe')) }
}
function Write-NexusWindowsJson { throw 'The common JSON writer must never reserialize an original Claude backup.' }
'@ | Set-Content -LiteralPath (Join-Path $repo 'commun\native-runtime.ps1')
        @'
param([string]$Installer, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
function Get-AppxPackage { param($Name); @() }
& $Installer -CheckOnly:$CheckOnly
'@ | Set-Content -LiteralPath $fixture.Runner
        return $fixture
    }
    function Invoke-InstallerFixture($Fixture, [switch]$CheckOnly) {
        $saved = @{}
        foreach ($name in 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA') { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
        try {
            $env:USERPROFILE = $Fixture.Home; $env:LOCALAPPDATA = $Fixture.Local; $env:APPDATA = $Fixture.Roaming
            $arguments = @('-NoProfile', '-File', $Fixture.Runner, '-Installer', $Fixture.Installer)
            if ($CheckOnly) { $arguments += '-CheckOnly' }
            $output = & pwsh @arguments 2>&1
            return [pscustomobject]@{ ExitCode=$LASTEXITCODE; Output=($output -join "`n") }
        } finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') } }
    }
    function New-OriginalClaudeJson {
        return "{`r`n  `"mcpServers`": {`r`n    `"nexus-windows`": {`"type`":`"stdio`",`"command`":`"`${USERPROFILE}\\.claude\\nexus-claude\\venv\\Scripts\\python.exe`",`"args`":[`"-m`",`"nexus_windows`"],`"env`":{`"PYTHONUTF8`":`"1`",`"PYTHONPYCACHEPREFIX`":`"`${USERPROFILE}\\.claude\\nexus-claude\\cache\\pycache`"}},`r`n    `"other`": {`"command`":`"keep.exe`",`"args`":[`"keep`"]}`r`n  },`r`n  `"keep`": {`"unicode`":`"été 😀`"}`r`n}`r`n"
    }
}

Describe 'Claude MCP exact-byte backup and optimistic concurrency, on fixtures only' {
    BeforeEach {
        $script:nexusWindowsRuntimeHome = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:mcpPath = Join-Path $TestDrive ([guid]::NewGuid().ToString('N') + '.json')
        $script:beforeBytes = Put-InstallerBytes $script:mcpPath "{ `"original`": `"été 😀`" }`r`n" -Bom
        $script:desiredData = [ordered]@{ replacement='verified' }
    }

    It 'backs up the original BOM, whitespace, newline and Unicode bytes exactly' {
        Save-NexusWindowsClaudeMcp $script:mcpPath $script:beforeBytes $script:desiredData
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $script:nexusWindowsRuntimeHome 'backups') -File)
        $backups.Count | Should -Be 1
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($backups[0].FullName)) | Should -BeExactly ([Convert]::ToBase64String($script:beforeBytes))
        (Get-Content -LiteralPath $script:mcpPath -Raw | ConvertFrom-Json).replacement | Should -BeExactly 'verified'
        @(Get-ChildItem -LiteralPath (Split-Path -Parent $script:mcpPath) -Filter '*.tmp').Count | Should -Be 0
    }

    It 'refuses a same-length outside edit before creating any backup' {
        $changed = [byte[]]$script:beforeBytes.Clone()
        $changed[$changed.Length - 4] = $changed[$changed.Length - 4] -bxor 1
        [IO.File]::WriteAllBytes($script:mcpPath, $changed)
        { Save-NexusWindowsClaudeMcp $script:mcpPath $script:beforeBytes $script:desiredData } | Should -Throw '*modifiée simultanément*'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($script:mcpPath)) | Should -BeExactly ([Convert]::ToBase64String($changed))
        Test-Path -LiteralPath (Join-Path $script:nexusWindowsRuntimeHome 'backups') | Should -BeFalse
    }

    It 'detects a formatting-only outside edit even when parsed JSON is identical' {
        $null = Put-InstallerBytes $script:mcpPath "{ `"original`": `"été 😀`" }`r`n"
        { Save-NexusWindowsClaudeMcp $script:mcpPath $script:beforeBytes $script:desiredData } | Should -Throw '*modifiée simultanément*'
        Test-Path -LiteralPath (Join-Path $script:nexusWindowsRuntimeHome 'backups') | Should -BeFalse
    }

    It 'rechecks after staging and preserves a concurrent outside update' {
        Mock Write-NexusWindowsClaudeMcpBytes {
            param($Path, $Bytes)
            [IO.File]::WriteAllBytes($Path, $Bytes)
            if ($Path.EndsWith('.tmp', [StringComparison]::Ordinal)) { [IO.File]::WriteAllText($script:mcpPath, '{"external":"kept"}') }
        }
        { Save-NexusWindowsClaudeMcp $script:mcpPath $script:beforeBytes $script:desiredData } | Should -Throw '*modifiée simultanément*'
        [IO.File]::ReadAllText($script:mcpPath) | Should -BeExactly '{"external":"kept"}'
        $backup = Get-ChildItem -LiteralPath (Join-Path $script:nexusWindowsRuntimeHome 'backups') -File
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($backup.FullName)) | Should -BeExactly ([Convert]::ToBase64String($script:beforeBytes))
        @(Get-ChildItem -LiteralPath (Split-Path -Parent $script:mcpPath) -Filter '*.tmp').Count | Should -Be 0
    }

    It 'leaves the original intact and cleans staging if a temporary write fails' {
        Mock Write-NexusWindowsClaudeMcpBytes {
            param($Path, $Bytes)
            [IO.File]::WriteAllBytes($Path, $Bytes)
            if ($Path.EndsWith('.tmp', [StringComparison]::Ordinal)) { throw 'Fixture disk failure' }
        }
        { Save-NexusWindowsClaudeMcp $script:mcpPath $script:beforeBytes $script:desiredData } | Should -Throw '*Fixture disk failure*'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($script:mcpPath)) | Should -BeExactly ([Convert]::ToBase64String($script:beforeBytes))
        @(Get-ChildItem -LiteralPath (Split-Path -Parent $script:mcpPath) -Filter '*.tmp').Count | Should -Be 0
    }
}

Describe 'Real Claude installer on isolated profiles with an inert common runtime' {
    It 'migrates catalogue and cache with separate exact original-byte backups and keeps unrelated servers' {
        $fixture = New-InstallerFixture
        $catalogue = Join-Path $fixture.Plugin '.mcp.json'
        $cacheDirectory = Join-Path $fixture.Home '.claude\plugins\cache\nexus\nexus-windows\fixture-version'
        $cache = Join-Path $cacheDirectory '.mcp.json'
        $catalogueBefore = Put-InstallerBytes $catalogue (New-OriginalClaudeJson) -Bom
        $cacheBefore = Put-InstallerBytes $cache ((New-OriginalClaudeJson).Replace('  ', '    '))
        $registry = @{ plugins=@{ 'nexus-windows@nexus'=@(@{installPath=$cacheDirectory}) } } | ConvertTo-Json -Depth 6
        $null = Put-InstallerBytes (Join-Path $fixture.Home '.claude\plugins\installed_plugins.json') $registry
        $result = Invoke-InstallerFixture $fixture
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $fixture.Home '.nexus\nexus-windows\backups') -File)
        $backups.Count | Should -Be 2
        $actualBackups = @($backups | ForEach-Object { [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName)) })
        $actualBackups | Should -Contain ([Convert]::ToBase64String($catalogueBefore))
        $actualBackups | Should -Contain ([Convert]::ToBase64String($cacheBefore))
        $catalogueData = Get-Content -LiteralPath $catalogue -Raw | ConvertFrom-Json -AsHashtable
        $cacheData = Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json -AsHashtable
        $catalogueData.mcpServers['nexus-windows'].command | Should -BeExactly ('${USERPROFILE}\.nexus\nexus-windows\native\' + ('a' * 64) + '\NexusWindows.exe')
        $cacheData.mcpServers['nexus-windows'].command | Should -BeExactly (Join-Path $fixture.Home ('.nexus\nexus-windows\native\' + ('a' * 64) + '\NexusWindows.exe'))
        foreach ($data in $catalogueData, $cacheData) {
            $data.mcpServers.other.command | Should -BeExactly 'keep.exe'
            $data.keep.unicode | Should -BeExactly 'été 😀'
            ($data.mcpServers['nexus-windows'].args -join ',') | Should -BeExactly '--mcp,--agent,claude'
        }
    }

    It 'CheckOnly refuses migration while preserving every original byte and creating no backup' {
        $fixture = New-InstallerFixture
        $path = Join-Path $fixture.Plugin '.mcp.json'
        $before = Put-InstallerBytes $path (New-OriginalClaudeJson) -Bom
        $result = Invoke-InstallerFixture $fixture -CheckOnly
        $result.ExitCode | Should -Not -Be 0
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) | Should -BeExactly ([Convert]::ToBase64String($before))
        Test-Path -LiteralPath (Join-Path $fixture.Home '.nexus\nexus-windows\backups') | Should -BeFalse
    }

    It 'preserves disabled and custom declarations without any serialization or backup' {
        foreach ($variant in 'disabled', 'enabled', 'custom') {
            $fixture = New-InstallerFixture
            $path = Join-Path $fixture.Plugin '.mcp.json'
            $data = New-OriginalClaudeJson | ConvertFrom-Json -AsHashtable
            switch ($variant) {
                'disabled' { $data.mcpServers['nexus-windows'].disabled = $true }
                'enabled' { $data.mcpServers['nexus-windows'].enabled = $false }
                'custom' { $data.mcpServers['nexus-windows'].args += '--mine' }
            }
            $before = Put-InstallerBytes $path (($data | ConvertTo-Json -Depth 8) + "`r`n") -Bom
            $result = Invoke-InstallerFixture $fixture
            $result.ExitCode | Should -Be 0 -Because $result.Output
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) | Should -BeExactly ([Convert]::ToBase64String($before))
            Test-Path -LiteralPath (Join-Path $fixture.Home '.nexus\nexus-windows\backups') | Should -BeFalse
        }
    }

    It 'refuses malformed UTF-8 without rewriting or backing up a replacement-decoded file' {
        $fixture = New-InstallerFixture
        $path = Join-Path $fixture.Plugin '.mcp.json'
        [byte[]]$before = 123, 34, 255, 34, 58, 49, 125
        [IO.File]::WriteAllBytes($path, $before)
        (Invoke-InstallerFixture $fixture).ExitCode | Should -Not -Be 0
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) | Should -BeExactly ([Convert]::ToBase64String($before))
        Test-Path -LiteralPath (Join-Path $fixture.Home '.nexus\nexus-windows\backups') | Should -BeFalse
    }
}

# Self-contained PowerShell 7 qualification. Only self-created fixtures under the local temp directory are used.
# Run: pwsh -NoProfile -File claude/tests/desktop-native.Tests.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\desktop-native.ps1')
$desktopCases = 0
$desktopFailures = [Collections.Generic.List[string]]::new()
$desktopRoot = Join-Path ([IO.Path]::GetTempPath()) ('nexus-claude-desktop-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($desktopRoot)

function Test-Desktop([string]$Name, [scriptblock]$Body) {
    $script:desktopCases++
    try { & $Body; Write-Host "OK $Name" }
    catch { $script:desktopFailures.Add($Name + ': ' + $_.Exception.Message); Write-Host "FAIL $Name : $($_.Exception.Message)" }
}
function Assert-Desktop($Condition, [string]$Message = 'Assertion failed') { if (-not $Condition) { throw $Message } }
function Throws-Desktop([scriptblock]$Body, [string]$Pattern) {
    try { & $Body | Out-Null } catch { if ($_.Exception.Message -match $Pattern) { return }; throw }
    throw "Expected refusal: $Pattern"
}
function New-DesktopFixture {
    $base = Join-Path $desktopRoot ([guid]::NewGuid().ToString('N'))
    $fixture = [pscustomobject]@{ Base = $base; Home = (Join-Path $base 'home'); Local = (Join-Path $base 'local'); Roaming = (Join-Path $base 'roaming'); Family = 'AnthropicPBC.Claude_testpublisher'; Path = (Join-Path $base 'config.json') }
    foreach ($folder in $fixture.Home, $fixture.Local, $fixture.Roaming) { [void][IO.Directory]::CreateDirectory($folder) }
    $fixture | Add-Member Native (Join-Path $fixture.Home ('.nexus\nexus-windows\native\' + ('a' * 64) + '\NexusWindows.exe'))
    $fixture | Add-Member OldNative (Join-Path $fixture.Home ('.nexus\nexus-windows\native\' + ('b' * 64) + '\NexusWindows.exe'))
    $fixture | Add-Member Backups (Join-Path $fixture.Home '.nexus\nexus-windows\backups')
    return $fixture
}
function Put-Desktop([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Plan-Desktop($Fixture) { New-NexusWindowsClaudeDesktopPlan -Path $Fixture.Path -NativeExecutable $Fixture.Native -UserProfile $Fixture.Home }
function Save-Desktop($Fixture, $Plan) { Save-NexusWindowsClaudeDesktopPlan -Plan $Plan -BackupDirectory $Fixture.Backups }
function Read-Desktop([string]$Path) { return ,([Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($Path))) }
function Discover-Desktop($Fixture) { Get-NexusWindowsClaudeDesktopConfig -LocalAppData $Fixture.Local -RoamingAppData $Fixture.Roaming -PackageFamilyNames @($Fixture.Family) }
function Modern-Desktop($Fixture) { Join-Path $Fixture.Local ("Claude-Data\$($Fixture.Family.ToLowerInvariant())\claude_desktop_config.json") }
function Old-Desktop($Fixture) { Join-Path $Fixture.Local ("Packages\$($Fixture.Family)\LocalCache\Roaming\Claude\claude_desktop_config.json") }
function Mark-DesktopMigrated($Fixture) { Put-Desktop (Join-Path ([IO.Path]::GetDirectoryName((Old-Desktop $Fixture))) ('.user-data-left.' + $Fixture.Family.ToLowerInvariant())) '' }
function Invoke-DesktopInstallerFixture($Fixture, [switch]$CheckOnly) {
    # Execute the real installer and helper. Only the common-runtime provider is inert: no C# build or live manifest.
    $source = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $Fixture.Base 'harness\NexusWindows'
    $claude = Join-Path $project 'claude'
    $plugin = Join-Path $Fixture.Base 'harness\NexusClaude\plugins\nexus-windows\.mcp.json'
    Put-Desktop (Join-Path $claude 'install-native.ps1') ([IO.File]::ReadAllText((Join-Path $source 'install-native.ps1')))
    Put-Desktop (Join-Path $claude 'scripts\desktop-native.ps1') ([IO.File]::ReadAllText((Join-Path $source 'scripts\desktop-native.ps1')))
    Put-Desktop $plugin ([ordered]@{ mcpServers = [ordered]@{ 'nexus-windows' = [ordered]@{ type = 'stdio'; command = $Fixture.Native.Replace($Fixture.Home, '${USERPROFILE}'); args = @('--mcp', '--agent', 'claude') } } } | ConvertTo-Json -Depth 8)
    Put-Desktop (Join-Path $project 'commun\native-runtime.ps1') @'
$nexusWindowsRuntimeHome = Join-Path $env:USERPROFILE '.nexus\nexus-windows'
function Install-NexusWindowsNativeRuntime([switch]$CheckOnly) {
    [pscustomobject]@{ NativeExecutable = (Join-Path $env:USERPROFILE ('.nexus\nexus-windows\native\' + ('a' * 64) + '\NexusWindows.exe')) }
}
function Write-NexusWindowsJson { throw 'Inert fixture: unexpected rewrite of the already aligned Claude Code declaration.' }
'@
    $oldHome = $env:USERPROFILE; $oldLocal = $env:LOCALAPPDATA; $oldRoaming = $env:APPDATA
    function Get-AppxPackage { [pscustomobject]@{ PackageFamilyName = $Fixture.Family } }
    try {
        $env:USERPROFILE = $Fixture.Home; $env:LOCALAPPDATA = $Fixture.Local; $env:APPDATA = $Fixture.Roaming
        & (Join-Path $claude 'install-native.ps1') -CheckOnly:$CheckOnly
    } finally { $env:USERPROFILE = $oldHome; $env:LOCALAPPDATA = $oldLocal; $env:APPDATA = $oldRoaming }
}

try {
    Test-Desktop 'missing config creates no guessed Desktop file' {
        $f = New-DesktopFixture
        Assert-Desktop ($null -eq (Discover-Desktop $f))
        $r = Install-NexusWindowsClaudeDesktop -NativeExecutable $f.Native -UserProfile $f.Home -LocalAppData $f.Local -RoamingAppData $f.Roaming -PackageFamilyNames @($f.Family)
        Assert-Desktop ($r.Status -eq 'not-configured')
        Assert-Desktop (@(Get-ChildItem -LiteralPath $f.Base -Recurse -File).Count -eq 0)
    }
    Test-Desktop 'classic config uses documented existing location' {
        $f = New-DesktopFixture; $path = Join-Path $f.Roaming 'Claude\claude_desktop_config.json'; Put-Desktop $path '{}'
        $r = Discover-Desktop $f; Assert-Desktop ($r.Path -eq $path -and $r.Kind -eq 'classic')
    }
    Test-Desktop 'Store config is discovered using the registered family without hardcoded publisher' {
        $f = New-DesktopFixture; $path = Old-Desktop $f; Put-Desktop $path '{}'
        $r = Discover-Desktop $f; Assert-Desktop ($r.Path -eq $path -and $r.Kind -eq 'store')
    }
    Test-Desktop 'modern Store config is discovered without creating legacy mirror' {
        $f = New-DesktopFixture; $path = Modern-Desktop $f; Put-Desktop $path '{}'
        $r = Discover-Desktop $f; Assert-Desktop ($r.Path -eq $path -and $r.Kind -eq 'store-modern')
        Assert-Desktop (-not (Test-Path -LiteralPath (Old-Desktop $f)))
    }
    Test-Desktop 'explicit migration marker selects modern config over stale legacy copies' {
        $f = New-DesktopFixture; $path = Modern-Desktop $f; Put-Desktop $path '{}'; Put-Desktop (Old-Desktop $f) '{}'; Put-Desktop (Join-Path $f.Roaming 'Claude\claude_desktop_config.json') '{}'; Mark-DesktopMigrated $f
        $r = Discover-Desktop $f; Assert-Desktop ($r.Path -eq $path -and $r.Kind -eq 'store-migrated')
    }
    Test-Desktop 'migration marker with missing destination refuses instead of writing the old location' {
        $f = New-DesktopFixture; Put-Desktop (Old-Desktop $f) '{}'; Mark-DesktopMigrated $f
        Throws-Desktop { Discover-Desktop $f } 'migrated.*unavailable'
    }
    Test-Desktop 'ambiguous classic and Store configs require evidence instead of a guess' {
        $f = New-DesktopFixture; Put-Desktop (Old-Desktop $f) '{}'; Put-Desktop (Join-Path $f.Roaming 'Claude\claude_desktop_config.json') '{}'
        Throws-Desktop { Discover-Desktop $f } 'Several.*without migration proof'
    }
    Test-Desktop 'two migrated installed profiles refuse without writing either config' {
        $f = New-DesktopFixture; Put-Desktop (Modern-Desktop $f) '{}'; Mark-DesktopMigrated $f
        $family2 = 'AnthropicPBC.Claude_otherpublisher'
        Put-Desktop (Join-Path $f.Local "Claude-Data\$($family2.ToLowerInvariant())\claude_desktop_config.json") '{}'
        Put-Desktop (Join-Path $f.Local "Packages\$family2\LocalCache\Roaming\Claude\.user-data-left.$($family2.ToLowerInvariant())") ''
        Throws-Desktop { Get-NexusWindowsClaudeDesktopConfig -LocalAppData $f.Local -RoamingAppData $f.Roaming -PackageFamilyNames @($f.Family, $family2) } 'Several migrated'
    }
    Test-Desktop 'package traversal input is refused' {
        $f = New-DesktopFixture
        Throws-Desktop { Get-NexusWindowsClaudeDesktopConfig -LocalAppData $f.Local -RoamingAppData $f.Roaming -PackageFamilyNames @('..\escape') } 'Invalid.*package family'
    }
    Test-Desktop 'adding the canonical entry preserves preferences, names, dates and large numbers' {
        $f = New-DesktopFixture
        Put-Desktop $f.Path '{"preferences":{"disabledChoice":false,"date":"2026-10-30T00:00:00Z","huge":123456789012345678901234567890},"mcpServers":{"My Exact Name":{"command":"custom.exe","disabled":true}}}'
        $before = Read-Desktop $f.Path; $preferences = $before['preferences'].DeepClone(); $custom = $before['mcpServers']['My Exact Name'].DeepClone()
        $r = Save-Desktop $f (Plan-Desktop $f); $after = Read-Desktop $f.Path
        Assert-Desktop ($r.Changed -and $r.Status -eq 'add')
        Assert-Desktop ([Text.Json.Nodes.JsonNode]::DeepEquals($preferences, $after['preferences']))
        Assert-Desktop ([Text.Json.Nodes.JsonNode]::DeepEquals($custom, $after['mcpServers']['My Exact Name']))
        Assert-Desktop ([string]$after['mcpServers']['nexus-windows']['command'] -ceq $f.Native)
        Assert-Desktop ($after['mcpServers']['nexus-windows']['args'].ToJsonString() -ceq '["--mcp","--agent","claude"]')
    }
    Test-Desktop 'backup preserves original bytes including BOM and whitespace' {
        $f = New-DesktopFixture; $text = "{ `r`n  `"preferences`": {`"value`":`"été`"} `r`n}"
        $original = [Text.UTF8Encoding]::new($true).GetPreamble() + [Text.Encoding]::UTF8.GetBytes($text)
        [IO.File]::WriteAllBytes($f.Path, $original)
        $r = Save-Desktop $f (Plan-Desktop $f)
        Assert-Desktop ([Convert]::ToBase64String([IO.File]::ReadAllBytes($r.Backup)) -ceq [Convert]::ToBase64String($original))
    }
    Test-Desktop 'second installation is byte-for-byte idempotent and creates no extra backup' {
        $f = New-DesktopFixture; Put-Desktop $f.Path '{}'; Save-Desktop $f (Plan-Desktop $f) | Out-Null
        $before = [IO.File]::ReadAllText($f.Path); $r = Save-Desktop $f (Plan-Desktop $f)
        Assert-Desktop (-not $r.Changed -and $r.Status -eq 'unchanged')
        Assert-Desktop ([IO.File]::ReadAllText($f.Path) -ceq $before)
        Assert-Desktop (@(Get-ChildItem -LiteralPath $f.Backups -File).Count -eq 1)
    }
    foreach ($setting in @(@{ disabled = $true }, @{ enabled = $false }, @{ disabled = $false }, @{ timeout = 10000 }, @{ env = @{ CUSTOM = 'keep' } }, @{ Env = @{ CUSTOM = 'case-sensitive' } }, @{ Type = 'http' })) {
        $caseSetting = $setting
        Test-Desktop ('existing choice is preserved: ' + ($caseSetting | ConvertTo-Json -Compress)) {
            $f = New-DesktopFixture
            $server = @{ command = $f.OldNative; args = @('--mcp', '--agent', 'claude') }
            foreach ($key in $caseSetting.Keys) { $server[$key] = $caseSetting[$key] }
            Put-Desktop $f.Path (@{ mcpServers = @{ 'nexus-windows' = $server } } | ConvertTo-Json -Depth 10)
            $before = [IO.File]::ReadAllText($f.Path); $r = Save-Desktop $f (Plan-Desktop $f)
            Assert-Desktop ($r.Status -eq 'preserved' -and -not $r.Changed)
            Assert-Desktop ([IO.File]::ReadAllText($f.Path) -ceq $before)
            Assert-Desktop (-not (Test-Path -LiteralPath $f.Backups))
        }
    }
    foreach ($name in 'Nexus-Windows', 'NexusWindows', 'nexus_windows') {
        $caseName = $name
        Test-Desktop "existing name $caseName is not renamed or duplicated" {
            $f = New-DesktopFixture; Put-Desktop $f.Path (@{ mcpServers = @{ $caseName = @{ command = 'custom.exe'; args = @() } } } | ConvertTo-Json -Depth 8)
            $r = Save-Desktop $f (Plan-Desktop $f); $after = Read-Desktop $f.Path
            Assert-Desktop ($r.Status -eq 'preserved')
            Assert-Desktop (@($after['mcpServers'].GetEnumerator()).Count -eq 1)
            Assert-Desktop ($null -ne $after['mcpServers'][$caseName])
        }
    }
    Test-Desktop 'custom canonical command remains untouched' {
        $f = New-DesktopFixture; Put-Desktop $f.Path '{"mcpServers":{"nexus-windows":{"command":"different.exe","args":[]}}}'
        $before = [IO.File]::ReadAllText($f.Path); $r = Save-Desktop $f (Plan-Desktop $f)
        Assert-Desktop ($r.Status -eq 'preserved' -and [IO.File]::ReadAllText($f.Path) -ceq $before)
    }
    Test-Desktop 'multiple NexusWindows aliases are preserved without creating a third declaration' {
        $f = New-DesktopFixture; Put-Desktop $f.Path '{"mcpServers":{"NexusWindows":{"command":"first.exe"},"nexus_windows":{"command":"second.exe"}}}'
        $before = [IO.File]::ReadAllText($f.Path); $r = Save-Desktop $f (Plan-Desktop $f)
        Assert-Desktop ($r.Status -eq 'preserved' -and [IO.File]::ReadAllText($f.Path) -ceq $before)
    }
    Test-Desktop 'recognized common native declaration updates the hash and retains explicit stdio' {
        $f = New-DesktopFixture; Put-Desktop $f.Path (@{ mcpServers = @{ 'nexus-windows' = @{ type = 'stdio'; command = $f.OldNative; args = @('--mcp', '--agent', 'claude') } } } | ConvertTo-Json -Depth 8)
        $r = Save-Desktop $f (Plan-Desktop $f); $after = Read-Desktop $f.Path
        Assert-Desktop ($r.Status -eq 'update' -and [string]$after['mcpServers']['nexus-windows']['type'] -ceq 'stdio')
        Assert-Desktop ([string]$after['mcpServers']['nexus-windows']['command'] -ceq $f.Native)
    }
    Test-Desktop 'recognized generated Python declaration migrates without preserving Python env' {
        $f = New-DesktopFixture; $server = @{ type = 'stdio'; command = (Join-Path $f.Home '.claude\nexus-claude\venv\Scripts\python.exe'); args = @('-m', 'nexus_windows'); env = @{ PYTHONUTF8 = '1'; PYTHONPYCACHEPREFIX = (Join-Path $f.Home '.claude\nexus-claude\cache\pycache') } }
        Put-Desktop $f.Path (@{ mcpServers = @{ 'nexus-windows' = $server } } | ConvertTo-Json -Depth 8)
        $r = Save-Desktop $f (Plan-Desktop $f); $after = Read-Desktop $f.Path
        Assert-Desktop ($r.Status -eq 'update' -and $null -eq $after['mcpServers']['nexus-windows']['env'])
    }
    Test-Desktop 'CheckOnly detects missing entry without writing or creating a backup' {
        $f = New-DesktopFixture; Put-Desktop $f.Path '{}'; $before = [IO.File]::ReadAllText($f.Path)
        Throws-Desktop { Save-NexusWindowsClaudeDesktopPlan -Plan (Plan-Desktop $f) -BackupDirectory $f.Backups -CheckOnly } 'CheckOnly wrote nothing'
        Assert-Desktop ([IO.File]::ReadAllText($f.Path) -ceq $before -and -not (Test-Path -LiteralPath $f.Backups))
    }
    Test-Desktop 'CheckOnly accepts an aligned server without normalizing its formatting' {
        $f = New-DesktopFixture; Put-Desktop $f.Path (@{ mcpServers = @{ 'nexus-windows' = @{ command = $f.Native; args = @('--mcp', '--agent', 'claude') } } } | ConvertTo-Json -Depth 8 -Compress)
        $before = [IO.File]::ReadAllText($f.Path)
        $r = Save-NexusWindowsClaudeDesktopPlan -Plan (Plan-Desktop $f) -BackupDirectory $f.Backups -CheckOnly
        Assert-Desktop ($r.Status -eq 'unchanged' -and [IO.File]::ReadAllText($f.Path) -ceq $before)
        Assert-Desktop (-not (Test-Path -LiteralPath $f.Backups))
    }
    Test-Desktop 'external modification after planning refuses replacement and preserves the newer file' {
        $f = New-DesktopFixture; Put-Desktop $f.Path '{}'; $plan = Plan-Desktop $f
        Put-Desktop $f.Path '{"preferences":{"newer":true}}'
        Throws-Desktop { Save-Desktop $f $plan } 'changed concurrently'
        Assert-Desktop ([IO.File]::ReadAllText($f.Path) -ceq '{"preferences":{"newer":true}}')
        Assert-Desktop (-not (Test-Path -LiteralPath $f.Backups))
    }
    Test-Desktop 'a second installer holding the named mutex refuses before backup or replacement' {
        $f = New-DesktopFixture; Put-Desktop $f.Path '{}'; $plan = Plan-Desktop $f
        $lockKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($plan.Path.ToUpperInvariant())))
        $childInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $childInfo.UseShellExecute = $false; $childInfo.CreateNoWindow = $true
        $childInfo.RedirectStandardInput = $true; $childInfo.RedirectStandardOutput = $true
        $childInfo.ArgumentList.Add('-NoProfile'); $childInfo.ArgumentList.Add('-Command')
        $childInfo.ArgumentList.Add('$m=[Threading.Mutex]::new($true,"Local\NexusWindows-ClaudeDesktop-' + $lockKey + '");try{[Console]::WriteLine("ready");[Console]::Out.Flush();[Console]::ReadLine()|Out-Null}finally{$m.ReleaseMutex();$m.Dispose()}')
        $child = [Diagnostics.Process]::Start($childInfo)
        try {
            $ready = $child.StandardOutput.ReadLineAsync()
            Assert-Desktop ($ready.Wait(10000) -and $ready.Result -ceq 'ready')
            Throws-Desktop { Save-Desktop $f $plan } 'Another Claude Desktop installation'
            Assert-Desktop ([IO.File]::ReadAllText($f.Path) -ceq '{}' -and -not (Test-Path -LiteralPath $f.Backups))
        } finally {
            $child.StandardInput.WriteLine('end'); $child.StandardInput.Close()
            if (-not $child.WaitForExit(5000)) { $child.Kill(); $child.WaitForExit() }
            $child.Dispose()
        }
    }
    Test-Desktop 'invalid UTF-8 is refused and original bytes survive' {
        $f = New-DesktopFixture; [IO.File]::WriteAllBytes($f.Path, [byte[]]@(0xff, 0xfe, 0x7b, 0x7d))
        Throws-Desktop { Plan-Desktop $f } 'translate|Unicode|UTF|bytes|octets'
        Assert-Desktop ([Convert]::ToBase64String([IO.File]::ReadAllBytes($f.Path)) -ceq '//57fQ==')
    }
    Test-Desktop 'configuration below a directory junction is refused rather than replacing a link' {
        $f = New-DesktopFixture; $destination = Join-Path $f.Base 'actual'; [void][IO.Directory]::CreateDirectory($destination)
        Put-Desktop (Join-Path $destination 'config.json') '{}'
        $junction = Join-Path $f.Base 'link'; New-Item -ItemType Junction -Path $junction -Target $destination | Out-Null
        Throws-Desktop { New-NexusWindowsClaudeDesktopPlan -Path (Join-Path $junction 'config.json') -NativeExecutable $f.Native -UserProfile $f.Home } 'reparse point'
        Assert-Desktop ([IO.File]::ReadAllText((Join-Path $destination 'config.json')) -ceq '{}')
    }
    foreach ($invalid in '{broken', '[]', '{"mcpServers":null}', '{"mcpServers":[]}', '{"preferences":{"same":1,"same":2}}') {
        $caseInvalid = $invalid
        Test-Desktop "malformed or ambiguous JSON is not replaced: $caseInvalid" {
            $f = New-DesktopFixture; Put-Desktop $f.Path $caseInvalid
            Throws-Desktop { Plan-Desktop $f } 'JSON|object|Duplicate|invalid'
            Assert-Desktop ([IO.File]::ReadAllText($f.Path) -ceq $caseInvalid)
        }
    }
    Test-Desktop 'out-of-scope runtime path is refused before any file mutation' {
        $f = New-DesktopFixture; Put-Desktop $f.Path '{}'
        Throws-Desktop { New-NexusWindowsClaudeDesktopPlan -Path $f.Path -NativeExecutable (Join-Path $f.Home 'custom.exe') -UserProfile $f.Home } 'canonical common runtime'
        Assert-Desktop ([IO.File]::ReadAllText($f.Path) -ceq '{}')
    }
    Test-Desktop 'wrapper discovers and changes only the migrated existing file' {
        $f = New-DesktopFixture; $path = Modern-Desktop $f; Put-Desktop $path '{"preferences":{"kept":true}}'; Mark-DesktopMigrated $f
        $r = Install-NexusWindowsClaudeDesktop -NativeExecutable $f.Native -UserProfile $f.Home -LocalAppData $f.Local -RoamingAppData $f.Roaming -PackageFamilyNames @($f.Family)
        Assert-Desktop ($r.Path -eq $path -and $r.Changed)
        Assert-Desktop ([bool](Read-Desktop $path)['preferences']['kept'])
        Assert-Desktop (-not (Test-Path -LiteralPath (Join-Path $f.Roaming 'Claude\claude_desktop_config.json')))
    }
    Test-Desktop 'full installer hook connects migrated Desktop using an inert common manifest' {
        $f = New-DesktopFixture; $path = Modern-Desktop $f; Put-Desktop $path '{"preferences":{"kept":"unchanged"}}'; Mark-DesktopMigrated $f
        Invoke-DesktopInstallerFixture $f
        $after = Read-Desktop $path
        Assert-Desktop ([string]$after['preferences']['kept'] -ceq 'unchanged')
        Assert-Desktop ([string]$after['mcpServers']['nexus-windows']['command'] -ceq $f.Native)
        Assert-Desktop ($after['mcpServers']['nexus-windows']['args'].ToJsonString() -ceq '["--mcp","--agent","claude"]')
        Invoke-DesktopInstallerFixture $f -CheckOnly
    }
    Test-Desktop 'full installer CheckOnly refuses missing Desktop entry without writing it' {
        $f = New-DesktopFixture; $path = Modern-Desktop $f; Put-Desktop $path '{"preferences":{"kept":true}}'; Mark-DesktopMigrated $f
        Throws-Desktop { Invoke-DesktopInstallerFixture $f -CheckOnly } 'CheckOnly wrote nothing'
        Assert-Desktop ([IO.File]::ReadAllText($path) -ceq '{"preferences":{"kept":true}}')
        Assert-Desktop (-not (Test-Path -LiteralPath $f.Backups))
    }
    Test-Desktop 'full installer preserves an existing disabled custom Desktop declaration' {
        $f = New-DesktopFixture; $path = Modern-Desktop $f; $original = '{"mcpServers":{"nexus-windows":{"command":"custom.exe","disabled":true}},"preferences":{"kept":true}}'; Put-Desktop $path $original; Mark-DesktopMigrated $f
        Invoke-DesktopInstallerFixture $f
        Assert-Desktop ([IO.File]::ReadAllText($path) -ceq $original)
        Assert-Desktop (-not (Test-Path -LiteralPath $f.Backups))
    }
} finally {
    # Only the exact self-created temp root is removed; resolved containment is checked before recursive cleanup.
    $actual = [IO.Path]::GetFullPath($desktopRoot)
    $temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($actual.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($actual).StartsWith('nexus-claude-desktop-')) {
        Remove-Item -LiteralPath $actual -Recurse -Force
    } else { throw 'Fixture cleanup target escaped the expected temp directory.' }
}
Write-Host "$desktopCases tests, $($desktopFailures.Count) failure(s)"
if ($desktopFailures.Count -gt 0) { throw ($desktopFailures -join "`n") }

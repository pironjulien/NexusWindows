# NexusWindows for Windows 10/11 (Windows PowerShell 5.1 or PowerShell 7), no administrator rights, nothing else to
# install (no Python, no Node.js, no .NET: the engine is self-contained). Attached to each release with SHA256SUMS.txt
# and its build provenance; download it as a file, check its SHA-256, then run it as a file:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\get.ps1
# Remove it (declarations removed, files deleted once no session uses them): the same with -Remove.
#
# It downloads NexusWindows-win-x64.zip or NexusWindows-win-arm64.zip (this PC's architecture) from the latest
# release, checks it against the release's SHA256SUMS.txt (its build provenance can be checked with
# `gh attestation verify <file> --repo pironjulien/NexusWindows`), installs the engine as an immutable version under
# %USERPROFILE%\.nexus\nexus-windows\native\<sha-256>, then the stable launcher
# %USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe that every client declares (`--mcp --agent claude|codex|antigravity`).
# Running sessions keep their engine; new ones get the new version. Finally the clients found on this PC are declared:
# Claude Code (or its nexus-windows plugin), Claude Desktop, Codex, Antigravity. An entry of your own is kept.
# Run it again to update.
# -UpdateOnly updates an existing engine without creating or changing client declarations. NexusSystem uses this
# for unattended maintenance; -RefreshExistingProfiles also refreshes an already active Antigravity plugin's files.
# NEXUS_PACK=1 (the Nexus pack, get-nexus.ps1): no question, no window; exit code 0 on success; a last
# "NEXUS_PACK_RESULT ..." line.
# For tests only: NEXUS_WINDOWS_SOURCE (a local NexusWindows-win-<arch>.zip instead of the latest release) and
# NEXUS_WINDOWS_PROFILE (a throwaway folder used as the user profile: everything is installed and declared there,
# the real profile and the Claude CLI are never touched). A local package aimed at the real profile is refused unless
# -Real is given.
[CmdletBinding()]
param([switch]$Remove, [switch]$Real, [switch]$UpdateOnly, [switch]$RefreshExistingProfiles)

$ErrorActionPreference = 'Stop'
# A Windows PowerShell child of PowerShell 7 inherits Core-only module folders ahead of its own modules.
# Restore this host's built-in modules first (Get-FileHash, Unblock-File...), preserving all other module paths.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $desktopModules = Join-Path $PSHOME 'Modules'
    $env:PSModulePath = (@($desktopModules) + @($env:PSModulePath -split ';' | Where-Object {
        $_ -and $_.TrimEnd('\') -ine $desktopModules.TrimEnd('\')
    })) -join ';'
}
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is many times slower with its progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = 'pironjulien/NexusWindows'
$Pack = $env:NEXUS_PACK -eq '1'
$RefreshAntigravity = $RefreshExistingProfiles -and (-not $Pack -or @($env:NEXUS_PACK_TARGETS -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() }) -contains 'antigravity')
$Sandbox = [bool]$env:NEXUS_WINDOWS_PROFILE
$ProfileRoot = if ($Sandbox) { $env:NEXUS_WINDOWS_PROFILE } else { $env:USERPROFILE }
$Home_ = Join-Path $ProfileRoot '.nexus\nexus-windows'
$Stable = Join-Path $Home_ 'NexusWindows.exe'
$Arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
$Asset = "NexusWindows-win-$Arch.zip"

function Say([string]$Text, [string]$Color = 'Gray') { if (-not $Pack) { Write-Host $Text -ForegroundColor $Color } }
function Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Get-LatestTag {
    # From the redirect of github.com/<repo>/releases/latest: no GitHub API (60 anonymous requests an hour per IP).
    try {
        $request = [Net.HttpWebRequest]::Create("https://github.com/$Repo/releases/latest")
        $request.AllowAutoRedirect = $false
        $request.UserAgent = 'NexusWindows-get'
        $response = $request.GetResponse()
        try { $location = $response.Headers['Location'] } finally { $response.Close() }
        if ($location -match '/releases/tag/(v[0-9][^/?#]*)$') { return $Matches[1] }
    } catch { Write-Verbose "Redirect lookup failed: $($_.Exception.Message)" }
    return (Invoke-RestMethod -UseBasicParsing "https://api.github.com/repos/$Repo/releases/latest").tag_name
}

function Get-ExpectedHash([byte[]]$Manifest, [string]$Name) {
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Manifest).TrimStart([char]0xfeff)
    $expected = $null
    foreach ($line in ($text -split '\r?\n' | Where-Object { $_.Trim() })) {
        if ($line -cnotmatch '^([0-9a-f]{64})\s+\*?(\S+)\s*$') { throw 'SHA256SUMS.txt has an invalid entry: installation refused.' }
        if ($Matches[2] -ceq $Name) {
            if ($expected) { throw "SHA256SUMS.txt lists $Name more than once: installation refused." }
            $expected = $Matches[1]
        }
    }
    if (-not $expected) { throw "$Name is absent from SHA256SUMS.txt: installation refused." }
    return $expected
}

function Write-Json([string]$Path, $Data) {
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, ($Data | ConvertTo-Json -Depth 8) + "`n", (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) } else { [IO.File]::Move($temporary, $Path) }
}

# A running EXE cannot be overwritten but can be renamed: the new launcher replaces it by ReplaceFile, the running copy
# becomes NexusWindows.exe.old-<guid>, removed by a later run once its session has ended.
function Update-Launcher([string]$Source) {
    $staged = "$Stable.$([guid]::NewGuid().ToString('N')).new"
    Copy-Item -LiteralPath $Source -Destination $staged
    try {
        if (Test-Path -LiteralPath $Stable) {
            if ((Sha256 $Stable) -eq (Sha256 $Source)) { return }
            [IO.File]::Replace($staged, $Stable, (Join-Path $Home_ ('NexusWindows.exe.old-' + [guid]::NewGuid().ToString('N'))))
        } else { [IO.File]::Move($staged, $Stable) }
    } finally { if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force } }
}

function Remove-Leftovers {
    foreach ($old in @(Get-ChildItem -LiteralPath $Home_ -Filter 'NexusWindows.exe.old-*' -File -ErrorAction SilentlyContinue)) {
        try { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction Stop } catch { Write-Verbose "In use: $($old.Name)" }
    }
}

function Invoke-Register([string[]]$Arguments) {
    # The registration runs in the (test) profile: every client path is taken from these variables.
    $saved = @{}
    foreach ($name in 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'CODEX_HOME', 'NEXUS_WINDOWS_NO_CLI') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        if ($Sandbox) {
            $env:USERPROFILE = $ProfileRoot
            $env:APPDATA = Join-Path $ProfileRoot 'AppData\Roaming'
            $env:LOCALAPPDATA = Join-Path $ProfileRoot 'AppData\Local'
            $env:CODEX_HOME = Join-Path $ProfileRoot '.codex'
            $env:NEXUS_WINDOWS_NO_CLI = '1'
        }
        $output = @(& $Stable @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
    foreach ($line in $output) { if ($line -notlike 'NEXUS_PACK_RESULT *') { Say "  $line" } }
    return [pscustomobject]@{ Code = $code; Result = @($output | Where-Object { $_ -like 'NEXUS_PACK_RESULT *' } | Select-Object -Last 1) }
}

function Install-NexusWindows {
    $work = Join-Path ([IO.Path]::GetTempPath()) "nexuswindows-$PID-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        if ($env:NEXUS_WINDOWS_SOURCE) {
            $package = (Resolve-Path -LiteralPath $env:NEXUS_WINDOWS_SOURCE).Path
            $tag = 'local'
            Say "Installing NexusWindows from $package..."
        } else {
            $tag = Get-LatestTag
            $base = "https://github.com/$Repo/releases/download/$tag"
            Say "Downloading NexusWindows $tag ($Asset)..." Cyan
            $package = Join-Path $work $Asset
            Invoke-WebRequest -UseBasicParsing "$base/$Asset" -OutFile $package
            $manifest = (Invoke-WebRequest -UseBasicParsing "$base/SHA256SUMS.txt").RawContentStream.ToArray()
            if ((Get-ExpectedHash $manifest $Asset) -ne (Sha256 $package).ToLowerInvariant()) { throw "SHA-256 mismatch for $($Asset): the download is not the published package." }
        }
        $hash = (Sha256 $package).ToLowerInvariant()
        $unpacked = Join-Path $work 'package'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($package, $unpacked)
        $launcher = Join-Path $unpacked 'NexusWindows.exe'
        $engine = Join-Path $unpacked 'engine'
        foreach ($required in $launcher, (Join-Path $engine 'NexusWindows.exe'), (Join-Path $engine 'NexusWindows.dll'), (Join-Path $engine 'garde-fous.json')) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw 'Not a NexusWindows package.' }
        }
        Get-ChildItem -LiteralPath $unpacked -Recurse -File | Unblock-File

        # 1. Immutable engine version (never rewritten once in place), with the hashes the launcher checks.
        $target = Join-Path $Home_ "native\$hash"
        $version = (Get-Item -LiteralPath (Join-Path $engine 'NexusWindows.exe')).VersionInfo.ProductVersion
        if (-not (Test-Path -LiteralPath (Join-Path $target 'runtime.json'))) {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }  # an interrupted copy
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            # Keep staging short for Windows PowerShell 5.1's legacy path limit; the final hash directory remains immutable.
            $staging = Join-Path $Home_ ('stage-' + [guid]::NewGuid().ToString('N'))
            Copy-Item -LiteralPath $engine -Destination $staging -Recurse
            $artifacts = [ordered]@{}
            foreach ($file in Get-ChildItem -LiteralPath $staging -File) { $artifacts[$file.Name] = Sha256 $file.FullName }
            $runtime = [ordered]@{ Schema = 2; SourceRoot = "release $tag"; NativeHash = $hash; NativeExecutable = (Join-Path $target 'NexusWindows.exe'); Version = $version; NativeArtifacts = $artifacts }
            Write-Json (Join-Path $staging 'runtime.json') $runtime
            [IO.Directory]::Move($staging, $target)
        }
        $runtime = Get-Content -LiteralPath (Join-Path $target 'runtime.json') -Raw | ConvertFrom-Json
        foreach ($name in 'NexusWindows.exe', 'NexusWindows.dll') {
            if ((Sha256 (Join-Path $target $name)) -ne $runtime.NativeArtifacts.$name) { throw "Installed engine file $name was altered: delete $target and run this again." }
        }
        # Profile refresh uses the immutable package's plugin assets. Validate those too against this verified ZIP;
        # an unchanged engine hash is not proof that a nested guide or manifest has not been edited since installation.
        if ($RefreshAntigravity) {
            $pluginRoot = Join-Path $engine 'plugins\antigravity'
            if (Test-Path -LiteralPath $pluginRoot -PathType Container) {
                foreach ($file in Get-ChildItem -LiteralPath $pluginRoot -Recurse -File) {
                    $relative = $file.FullName.Substring($engine.Length + 1)
                    $installedFile = Join-Path $target $relative
                    if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or (Sha256 $installedFile) -ne (Sha256 $file.FullName)) {
                        throw "Installed plugin asset $relative differs from the verified package: profile refresh refused."
                    }
                }
            }
        }
        # 2. installation.json, then the stable launcher: a new session never pairs the new launcher with an old engine.
        Write-Json (Join-Path $Home_ 'installation.json') $runtime
        Update-Launcher $launcher
        Remove-Leftovers
        Say "NexusWindows $version is in $Home_." Green
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    # 3. Maintenance never registers clients or creates a plugin. Existing sessions keep their own engine.
    if ($UpdateOnly) {
        if ($RefreshAntigravity) { return Invoke-Register @('--refresh-installed', 'antigravity') }
        return [pscustomobject]@{ Code = 0; Result = @('NEXUS_PACK_RESULT NexusWindows=updated') }
    }
    # A first installation declares the clients found on this PC.
    $registered = Invoke-Register @('--register', 'all')
    if (-not $Pack) {
        Say ''
        Say 'Last step: restart Claude, Codex or Antigravity (or open a new conversation), then ask:' Yellow
        Say '"Open Notepad and write a shopping list: milk, eggs, bread."' Yellow
    }
    return $registered
}

function Remove-NexusWindows {
    if (-not (Test-Path -LiteralPath $Stable)) { Say 'NexusWindows is not installed.'; return [pscustomobject]@{ Code = 0; Result = @('NEXUS_PACK_RESULT NexusWindows=absent') } }
    $removed = Invoke-Register @('--register', 'all', '--remove')
    if ($removed.Code) { return $removed }
    # Files still used by a running session stay until the next run; everything else goes.
    foreach ($item in @(Get-ChildItem -LiteralPath $Home_ -Force)) {
        try { Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop } catch { Say "Still in use (removed later): $($item.Name)" Yellow }
    }
    Say 'NexusWindows removed. Your routines and logs stay in %USERPROFILE%\.nexus.' Green
    return $removed
}

$code = 1
$result = $null
$installationMutex = $null
$installationHeld = $false
try {
    if ($Remove -and $UpdateOnly) { throw '-Remove and -UpdateOnly cannot be combined.' }
    if ($RefreshExistingProfiles -and -not $UpdateOnly) { throw '-RefreshExistingProfiles requires -UpdateOnly.' }
    if ($UpdateOnly -and (-not (Test-Path -LiteralPath $Stable -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $Home_ 'installation.json') -PathType Leaf))) {
        throw '-UpdateOnly requires an existing NexusWindows installation.'
    }
    if ($env:NEXUS_WINDOWS_SOURCE -and -not $Sandbox -and -not $Real) {
        throw 'NEXUS_WINDOWS_SOURCE (a test package) is set but NEXUS_WINDOWS_PROFILE is not: refusing to touch the real profile. Set NEXUS_WINDOWS_PROFILE to a test folder, or pass -Real.'
    }
    if ($env:OS -ne 'Windows_NT') { throw 'NexusWindows runs on Windows 10 or 11 only.' }
    # Same exclusion as the source installer. No concurrent manifest/launcher swap or removal.
    $installationMutex = New-Object Threading.Mutex($false, 'Local\NexusWindows-CommonRuntime')
    try { $installationHeld = $installationMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $installationHeld = $true }
    if (-not $installationHeld) { throw 'Another NexusWindows installation is in progress; retry later.' }
    $outcome = if ($Remove) { Remove-NexusWindows } else { Install-NexusWindows }
    $code = $outcome.Code
    $result = $outcome.Result
} catch {
    Say "NexusWindows: $($_.Exception.Message)" Red
    if ($Pack) { Write-Host "NexusWindows: $($_.Exception.Message)" }
    $result = 'NEXUS_PACK_RESULT NexusWindows=failed'
    $code = 1
} finally {
    if ($installationHeld) { $installationMutex.ReleaseMutex() }
    if ($installationMutex) { $installationMutex.Dispose() }
}
if ($Pack -and $result) { Write-Output $result }
# Run as a file (-File): its exit code. Dot-sourced or run as a script block: never `exit`, which would close the
# user's window; a failure is an error instead.
if ($PSCommandPath) { exit $code }
if ($code) { throw 'NexusWindows: not installed (see above).' }

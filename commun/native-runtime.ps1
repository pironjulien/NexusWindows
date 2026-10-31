# Runtime C# unique des trois adaptateurs. Aucun chemin AppData (virtualisation MSIX), aucun cache Codex.
# Moteur immuable par empreinte (native\<sha-256>), puis le lanceur STABLE %USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe
# que toutes les déclarations nomment : il lance le moteur indiqué par installation.json (commun\launcher).
$nexusWindowsSource = Split-Path -Parent $PSScriptRoot
$nexusWindowsRuntimeHome = Join-Path $env:USERPROFILE '.nexus\nexus-windows'
$nexusWindowsManifest = Join-Path $nexusWindowsRuntimeHome 'installation.json'
$nexusWindowsLauncher = Join-Path $nexusWindowsRuntimeHome 'NexusWindows.exe'

function Write-NexusWindowsJson([string]$Path, $Data) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try { [IO.File]::WriteAllText($temporary, ($Data | ConvertTo-Json -Depth 16) + "`n", [Text.UTF8Encoding]::new($false)); Move-Item -LiteralPath $temporary -Destination $Path -Force }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
function Get-NexusWindowsNativeHash {
    $lines = @(Get-ChildItem -LiteralPath (Join-Path $nexusWindowsSource 'codex\native') -File | Sort-Object Name | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
    $lines += 'garde-fous.json:' + (Get-FileHash -LiteralPath (Join-Path $nexusWindowsSource 'commun\garde-fous.json') -Algorithm SHA256).Hash
    $lines += 'publish:AssemblyName=NexusWindows;Release;net10.0-windows'
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines -join "`n"))).ToLowerInvariant()
}
function Test-NexusWindowsRuntime($Manifest) {
    if ($null -eq $Manifest -or $Manifest.Schema -ne 2 -or $Manifest.NativeHash -cnotmatch '^[a-f0-9]{64}$') { return $false }
    $expected = Join-Path $nexusWindowsRuntimeHome "native\$($Manifest.NativeHash)\NexusWindows.exe"
    if ([IO.Path]::GetFullPath($Manifest.NativeExecutable) -ine [IO.Path]::GetFullPath($expected)) { return $false }
    $artifacts = $Manifest.NativeArtifacts
    foreach ($required in 'NexusWindows.exe','NexusWindows.dll','NexusWindows.deps.json','NexusWindows.runtimeconfig.json','garde-fous.json') { if ($null -eq $artifacts.$required) { return $false } }
    foreach ($artifact in $artifacts.PSObject.Properties) {
        if ($artifact.Name -in '.','..' -or $artifact.Name.IndexOfAny([char[]]'/\') -ge 0 -or $artifact.Value -cnotmatch '^[A-Fa-f0-9]{64}$') { return $false }
        $path = Join-Path (Split-Path -Parent $expected) $artifact.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $artifact.Value) { return $false }
    }
    return $true
}
function Get-NexusWindowsNativeRuntime {
    if (-not (Test-Path -LiteralPath $nexusWindowsManifest)) { throw 'Runtime commun absent : exécuter NexusWindows/install.ps1.' }
    $runtime = Get-Content -LiteralPath $nexusWindowsManifest -Raw | ConvertFrom-Json
    if (-not (Test-NexusWindowsRuntime $runtime)) { throw 'Runtime commun incomplet ou altéré.' }
    $runtime
}
function Get-NexusWindowsLauncherHash {
    $lines = @(Get-ChildItem -LiteralPath (Join-Path $nexusWindowsSource 'commun\launcher') -File | Sort-Object Name | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
    $lines += 'publish:framework-dependent single file;' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines -join "`n"))).ToLowerInvariant()
}
# Replaces the stable launcher while MCP sessions may run it: a running EXE cannot be overwritten or deleted, but it can
# be renamed. The new copy is staged beside it, then swapped in by ReplaceFile (the running one becomes a .old-<guid>
# file, freed when its session ends and removed at a later pass). Sessions already started keep their engine.
function Update-NexusWindowsLauncher([string]$Source, [string]$Target) {
    $directory = Split-Path -Parent $Target
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $staged = "$Target.$([guid]::NewGuid().ToString('N')).new"
    Copy-Item -LiteralPath $Source -Destination $staged
    try {
        if (Test-Path -LiteralPath $Target) {
            $aside = Join-Path $directory ('NexusWindows.exe.old-' + [guid]::NewGuid().ToString('N'))
            [IO.File]::Replace($staged, $Target, $aside)
        } else { Move-Item -LiteralPath $staged -Destination $Target }
    } finally { if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force } }
    foreach ($old in Get-ChildItem -LiteralPath $directory -Filter 'NexusWindows.exe.old-*' -File) {
        try { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction Stop } catch { } # still running: next pass
    }
}
function Install-NexusWindowsLauncher([switch]$CheckOnly) {
    $hash = Get-NexusWindowsLauncherHash
    $built = Join-Path $nexusWindowsRuntimeHome "launcher\$hash\NexusWindows.exe"
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) {
        if ($CheckOnly) { throw 'Lanceur stable différent des sources.' }
        $rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
        $build = Join-Path $nexusWindowsRuntimeHome ("build\launcher-$hash-" + [guid]::NewGuid().ToString('N'))
        & dotnet publish (Join-Path $nexusWindowsSource 'commun\launcher\NexusWindows.Launcher.csproj') -c Release -r $rid -o "$build\publish" `
            -p:PublishAot=false -p:PublishSingleFile=true -p:SelfContained=false -p:DebugType=none "-p:BaseIntermediateOutputPath=$build\obj\" "-p:BaseOutputPath=$build\bin\" --nologo | Out-Host
        if ($LASTEXITCODE -or -not (Test-Path -LiteralPath "$build\publish\NexusWindows.exe")) { throw 'Compilation du lanceur stable en échec.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $built) -Force | Out-Null
        Move-Item -LiteralPath "$build\publish\NexusWindows.exe" -Destination $built
        Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue
    }
    $current = if (Test-Path -LiteralPath $nexusWindowsLauncher) { (Get-FileHash -LiteralPath $nexusWindowsLauncher -Algorithm SHA256).Hash } else { '' }
    if ($current -ne (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash) {
        if ($CheckOnly) { throw "Lanceur stable non aligné : $nexusWindowsLauncher" }
        Update-NexusWindowsLauncher $built $nexusWindowsLauncher
    }
    $nexusWindowsLauncher
}
function Install-NexusWindowsNativeRuntime([switch]$CheckOnly) {
    $mutex = [Threading.Mutex]::new($false, 'Local\NexusWindows-CommonRuntime')
    $held = $false
    try {
        try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'Une installation du moteur commun est en cours.' }
        $hash = Get-NexusWindowsNativeHash
        $target = Join-Path $nexusWindowsRuntimeHome "native\$hash"
        $versionManifest = Join-Path $target 'runtime.json'
        $runtime = if (Test-Path -LiteralPath $versionManifest) { Get-Content -LiteralPath $versionManifest -Raw | ConvertFrom-Json } else { $null }
        if ($null -eq $runtime -or $runtime.NativeHash -cne $hash -or -not (Test-NexusWindowsRuntime $runtime)) {
            if ($CheckOnly) { throw 'Runtime commun différent des sources.' }
            if (Test-Path -LiteralPath $target) { throw "Runtime immutable incomplet ou altéré : $target. Aucune réécriture d'un binaire existant." }
            $build = Join-Path $nexusWindowsRuntimeHome ("build\$hash-" + [guid]::NewGuid().ToString('N'))
            $output = Join-Path $build 'publish'
            & dotnet publish (Join-Path $nexusWindowsSource 'codex\native\NexusCodex.UIA.csproj') -c Release -o $output '-p:AssemblyName=NexusWindows' "-p:BaseIntermediateOutputPath=$build\obj\" "-p:MSBuildProjectExtensionsPath=$build\obj\" "-p:BaseOutputPath=$build\bin\" --nologo | Out-Host
            if ($LASTEXITCODE) { throw 'Compilation du moteur commun en échec.' }
            Copy-Item -LiteralPath (Join-Path $nexusWindowsSource 'commun\garde-fous.json') -Destination $output
            $artifacts = [ordered]@{}
            foreach ($file in Get-ChildItem -LiteralPath $output -File) { $artifacts[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
            $runtime = [pscustomobject]@{ Schema=2; SourceRoot=$nexusWindowsSource; NativeHash=$hash; NativeExecutable=(Join-Path $target 'NexusWindows.exe'); Version='3.9.7'; NativeArtifacts=[pscustomobject]$artifacts }
            Write-NexusWindowsJson (Join-Path $output 'runtime.json') $runtime
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            # Both resolved paths are within the explicitly named common runtime directory.
            if (-not ([IO.Path]::GetFullPath($output)).StartsWith(([IO.Path]::GetFullPath($nexusWindowsRuntimeHome) + '\'), [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFullPath($target)).StartsWith(([IO.Path]::GetFullPath($nexusWindowsRuntimeHome) + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Chemin de runtime hors périmètre.' }
            Move-Item -LiteralPath $output -Destination $target
            if (-not (Test-NexusWindowsRuntime $runtime)) { throw 'Vérification des artefacts du moteur commun en échec.' }
        }
        if (-not $CheckOnly) { $runtime.SourceRoot = $nexusWindowsSource; Write-NexusWindowsJson $nexusWindowsManifest $runtime }
        else { $installed = Get-NexusWindowsNativeRuntime; if ($installed.NativeHash -cne $hash) { throw 'Manifest commun non aligné.' } }
        # installation.json first, then the launcher: a new session never starts the new launcher on the old engine.
        $runtime | Add-Member -NotePropertyName StableExecutable -NotePropertyValue (Install-NexusWindowsLauncher -CheckOnly:$CheckOnly) -Force
        $runtime
    } finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}

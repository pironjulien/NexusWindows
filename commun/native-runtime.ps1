# Runtime C# unique des trois adaptateurs. Aucun chemin AppData (virtualisation MSIX), aucun cache Codex.
$nexusWindowsSource = Split-Path -Parent $PSScriptRoot
$nexusWindowsRuntimeHome = Join-Path $env:USERPROFILE '.nexus\nexus-windows'
$nexusWindowsManifest = Join-Path $nexusWindowsRuntimeHome 'installation.json'

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
            $runtime = [pscustomobject]@{ Schema=2; SourceRoot=$nexusWindowsSource; NativeHash=$hash; NativeExecutable=(Join-Path $target 'NexusWindows.exe'); Version='3.8.0'; NativeArtifacts=[pscustomobject]$artifacts }
            Write-NexusWindowsJson (Join-Path $output 'runtime.json') $runtime
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            # Both resolved paths are within the explicitly named common runtime directory.
            if (-not ([IO.Path]::GetFullPath($output)).StartsWith(([IO.Path]::GetFullPath($nexusWindowsRuntimeHome) + '\'), [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFullPath($target)).StartsWith(([IO.Path]::GetFullPath($nexusWindowsRuntimeHome) + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Chemin de runtime hors périmètre.' }
            Move-Item -LiteralPath $output -Destination $target
            if (-not (Test-NexusWindowsRuntime $runtime)) { throw 'Vérification des artefacts du moteur commun en échec.' }
        }
        if (-not $CheckOnly) { $runtime.SourceRoot = $nexusWindowsSource; Write-NexusWindowsJson $nexusWindowsManifest $runtime }
        else { $installed = Get-NexusWindowsNativeRuntime; if ($installed.NativeHash -cne $hash) { throw 'Manifest commun non aligné.' } }
        $runtime
    } finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}

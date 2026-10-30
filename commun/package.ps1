# Release files (run by the release workflow, or by hand to try get.ps1 on a local package):
#   NexusWindows-win-x64.zip, NexusWindows-win-arm64.zip  NexusWindows.exe (stable launcher, Native AOT) + engine\ (the
#       self-contained engine, the shared policy and the Antigravity plugin): what get.ps1 installs;
#   nexus-windows-claude.mcpb                            Claude Desktop extension (the x64 engine, runs on ARM64 too);
#   get.ps1                                              the installer, attested with the packages.
# Builds go to %USERPROFILE%\.nexus\build\nexuswindows\package; nothing is written to the source tree.
[CmdletBinding()]
param([Parameter(Mandatory)] [string]$Version, [Parameter(Mandatory)] [string]$Out, [string[]]$Runtimes = @('win-x64', 'win-arm64'), [switch]$NoMcpb)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:USERPROFILE ".nexus\build\nexuswindows\package\$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
# Native AOT finds the C++ linker through vswhere, which is not on PATH outside a developer prompt.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path -LiteralPath $vswhere) { $env:PATH = "$env:PATH;$vswhere" }
try {
    foreach ($rid in $Runtimes) {
        $stage = Join-Path $work $rid
        $engine = Join-Path $stage 'engine'
        & dotnet publish (Join-Path $root 'codex\native\NexusCodex.UIA.csproj') -c Release -r $rid --self-contained -o $engine `
            -p:AssemblyName=NexusWindows -p:DebugType=none "-p:BaseIntermediateOutputPath=$work\obj-engine-$rid\" "-p:BaseOutputPath=$work\bin-engine-$rid\" --nologo
        if ($LASTEXITCODE) { throw "Engine build failed ($rid)." }
        & dotnet publish (Join-Path $root 'commun\launcher\NexusWindows.Launcher.csproj') -c Release -r $rid -o (Join-Path $work "launcher-$rid") `
            -p:DebugType=none "-p:BaseIntermediateOutputPath=$work\obj-launcher-$rid\" "-p:BaseOutputPath=$work\bin-launcher-$rid\" --nologo
        if ($LASTEXITCODE) { throw "Launcher build failed ($rid)." }
        Copy-Item (Join-Path $work "launcher-$rid\NexusWindows.exe") $stage
        Copy-Item (Join-Path $root 'commun\garde-fous.json') $engine
        $plugin = Join-Path $engine 'plugins\antigravity'
        Copy-Item (Join-Path $root 'agy\plugins\nexus-windows') $plugin -Recurse
        $actual = (Get-Item (Join-Path $engine 'NexusWindows.exe')).VersionInfo.ProductVersion
        if ($actual -ne $Version) { throw "The engine says version $actual, not $Version." }
        $zip = Join-Path $Out "NexusWindows-$rid.zip"
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    }
    if (-not $NoMcpb) {
        $mcpb = Join-Path $work 'mcpb'
        Copy-Item (Join-Path $root 'claude\mcpb') $mcpb -Recurse
        $manifest = Get-Content (Join-Path $mcpb 'manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.version -ne $Version) { throw "claude/mcpb/manifest.json has version $($manifest.version), not $Version." }
        # Only the manifest and the icon come from claude\mcpb; server\ is the x64 engine without the Antigravity plugin.
        Get-ChildItem -LiteralPath $mcpb -Force | Where-Object Name -notin 'manifest.json', 'icon.png' | Remove-Item -Recurse -Force
        Copy-Item (Join-Path $work 'win-x64\engine') (Join-Path $mcpb 'server') -Recurse
        Remove-Item (Join-Path $mcpb 'server\plugins') -Recurse -Force
        & npx --yes '@anthropic-ai/mcpb@2.1.2' pack $mcpb (Join-Path $Out 'nexus-windows-claude.mcpb')
        if ($LASTEXITCODE) { throw 'Claude Desktop extension packing failed.' }
    }
    Copy-Item (Join-Path $root 'get.ps1') $Out
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $Out -File | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Where-Object { $_ -notmatch '  SHA256SUMS\.txt$' } | Set-Content (Join-Path $Out 'SHA256SUMS.txt') -Encoding utf8NoBOM

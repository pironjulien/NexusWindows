[CmdletBinding(SupportsShouldProcess = $true)]
param([switch]$CheckOnly)
# Private clone installation: shared immutable C# MCP runtime outside OneDrive, then the native launcher in the plugin.
# Python remains installation/test tooling; it never serves this clone's desktop tools.
$ErrorActionPreference = "Stop"
if ($CheckOnly) { & (Join-Path $PSScriptRoot "verify.ps1"); exit $LASTEXITCODE }
if (-not $PSCmdlet.ShouldProcess("Ce PC", "Installer NexusWindows for Antigravity")) { return }
. (Join-Path $PSScriptRoot "scripts\runtime.ps1")
. (Join-Path $PSScriptRoot 'scripts\native-runtime.ps1')
Install-NexusAgyNativeRuntime
$uv = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uv) {
    & winget install --id astral-sh.uv --exact --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE) { throw "Installation de uv en echec" }
    $env:PATH += ";$env:USERPROFILE\.local\bin;$env:LOCALAPPDATA\Microsoft\WinGet\Links"
    $uv = Get-Command uv -ErrorAction Stop
}
& $uv.Source sync --locked --all-groups --project $PSScriptRoot
if ($LASTEXITCODE) { throw "Preparation de l'outillage d'installation en echec" }
& $pythonExe -B (Join-Path $PSScriptRoot "client\launch.py") install --launcher (Join-Path $PSScriptRoot "scripts\start-computer-mcp.ps1") @guide
if ($LASTEXITCODE) { throw "Installation du plugin en echec" }
& (Join-Path $PSScriptRoot "verify.ps1")
exit $LASTEXITCODE

$ErrorActionPreference = "Stop"
try {
    . (Join-Path $PSScriptRoot "scripts\runtime.ps1")
    . (Join-Path $PSScriptRoot 'scripts\native-runtime.ps1')
    $runtime = Get-NexusAgyNativeRuntime
    if ($runtime.NativeHash -cne (Get-NexusAgyNativeHash)) { throw 'Les sources natives ont évolué : exécuter agy/install.ps1.' }
    if (-not (Test-Path -LiteralPath $pythonExe)) { throw "Runtime local absent : executer install.ps1" }
    & $pythonExe -B (Join-Path $PSScriptRoot "client\launch.py") install --check --launcher (Join-Path $PSScriptRoot "scripts\start-computer-mcp.ps1") @guide
    if ($LASTEXITCODE) { throw "Plugin nexus-windows non aligne : executer install.ps1" }
    Write-Host "NexusWindows for Antigravity : runtime C# natif commun, empreintes et déclaration du plugin alignés. Ce contrôle ne remplace pas un geste réel."
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}

[CmdletBinding()]
param([Parameter(Mandatory)][string]$Plan,[switch]$Apply,[switch]$Remove)
$ErrorActionPreference = 'Stop'
$fixturePlan = Get-Content -LiteralPath $Plan -Raw | ConvertFrom-Json
if ($fixturePlan.schema -ne 1 -or $fixturePlan.name -cnotmatch '^Nexus\.LocalUwpQA\.[a-f0-9]{12}$' -or $fixturePlan.publisher -cne 'CN=NexusPrivateQualification') { throw 'Not an owned private fixture plan.' }
$expectedLayout = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE ('.nexus\nexus-windows\qualification\uwp-private\' + $fixturePlan.name + '\layout')))
if ([IO.Path]::GetFullPath($fixturePlan.layout) -ine $expectedLayout -or [IO.Path]::GetFullPath($fixturePlan.manifest) -ine (Join-Path $expectedLayout 'AppxManifest.xml')) { throw 'Fixture paths differ.' }
if ((Get-FileHash -LiteralPath $fixturePlan.executable).Hash -ine $fixturePlan.exe_sha256 -or (Get-FileHash -LiteralPath $fixturePlan.manifest).Hash -ine $fixturePlan.manifest_sha256) { throw 'Fixture executable or manifest changed.' }
$existing = @(Get-AppxPackage -Name $fixturePlan.name)
if ($existing.Count -gt 1) { throw 'Ambiguous private fixture registration.' }
if ($existing.Count -eq 1 -and (-not $existing[0].IsDevelopmentMode -or [IO.Path]::GetFullPath($existing[0].InstallLocation) -ine $expectedLayout)) { throw 'An unrelated package occupies this identity.' }
if (-not $Apply) {
    [pscustomobject]@{prepared=$true;applied=$false;name=$fixturePlan.name;registered=($existing.Count -eq 1);action=$(if($Remove){'remove'}else{'register'})}|ConvertTo-Json -Compress
    return
}
if ($Remove) {
    if ($existing.Count -eq 1) {
        $python = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python314\python.exe'
        $statusText = & $python -I -B (Join-Path $PSScriptRoot 'probe.py') --plan $Plan
        if ($LASTEXITCODE) { throw 'Private fixture process state is unknown; removal refused.' }
        $status = $statusText | ConvertFrom-Json
        if (@($status.processes).Count -ne 0) { throw 'Close only the private fixture normally before removing its registration.' }
        Remove-AppxPackage -Package $existing[0].PackageFullName -ErrorAction Stop
    }
    [pscustomobject]@{removed=(@(Get-AppxPackage -Name $fixturePlan.name).Count -eq 0);name=$fixturePlan.name}|ConvertTo-Json -Compress
    return
}
if ((Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock').AllowDevelopmentWithoutDevLicense -ne 1) { throw 'Developer Mode is no longer already enabled; no setting will be changed.' }
if ($existing.Count -eq 0) { Add-AppxPackage -Register $fixturePlan.manifest -ErrorAction Stop }
$installed = Get-AppxPackage -Name $fixturePlan.name
if (-not $installed -or -not $installed.IsDevelopmentMode -or [IO.Path]::GetFullPath($installed.InstallLocation) -ine $expectedLayout) { throw 'Owned loose registration not confirmed.' }
[pscustomobject]@{registered=$true;name=$installed.Name;package_full_name=$installed.PackageFullName;family=$installed.PackageFamilyName;app_id=($installed.PackageFamilyName+'!App');layout=$installed.InstallLocation}|ConvertTo-Json -Compress

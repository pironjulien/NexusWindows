[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$fixtureSource = $PSScriptRoot
$developerMode = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock').AllowDevelopmentWithoutDevLicense
if ($developerMode -ne 1) { throw 'Developer Mode is not already enabled; no setting will be changed.' }
$fixtureName = 'Nexus.LocalUwpQA.' + [guid]::NewGuid().ToString('N').Substring(0,12)
$fixtureRoot = Join-Path $env:USERPROFILE ('.nexus\nexus-windows\qualification\uwp-private\' + $fixtureName)
$fixtureLayout = Join-Path $fixtureRoot 'layout'
New-Item -ItemType Directory -Path $fixtureLayout -Force | Out-Null
$sdkVersion = '10.0.26100.0'
$vcVersion = '14.44.35207'
$vsRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
$compiler = Join-Path $vsRoot "VC\Tools\MSVC\$vcVersion\bin\Hostx64\x64\cl.exe"
$linker = Join-Path $vsRoot "VC\Tools\MSVC\$vcVersion\bin\Hostx64\x64\link.exe"
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$source = Join-Path $fixtureSource 'main.cpp'
$executable = Join-Path $fixtureLayout 'NexusUwpFixture.exe'
$object = Join-Path $fixtureRoot 'main.obj'
$vcRuntime = Get-AppxPackage -Name Microsoft.VCLibs.140.00 | Where-Object Architecture -eq X64 | Select-Object -First 1
if (-not $vcRuntime -or [version]$vcRuntime.Version -lt [version]'14.0.33519.0') { throw 'Required existing UWP x64 VCLibs missing; no dependency will be downloaded.' }
$includePaths = @(
    (Join-Path $vsRoot "VC\Tools\MSVC\$vcVersion\include"),
    (Join-Path $sdkRoot "Include\$sdkVersion\ucrt"),
    (Join-Path $sdkRoot "Include\$sdkVersion\shared"),
    (Join-Path $sdkRoot "Include\$sdkVersion\um"),
    (Join-Path $sdkRoot "Include\$sdkVersion\winrt"),
    (Join-Path $sdkRoot "Include\$sdkVersion\cppwinrt")
)
$libraryPaths = @(
    (Join-Path $vsRoot "VC\Tools\MSVC\$vcVersion\lib\x64\store"),
    (Join-Path $vsRoot "VC\Tools\MSVC\$vcVersion\lib\x64"),
    (Join-Path $sdkRoot "Lib\$sdkVersion\ucrt\x64"),
    (Join-Path $sdkRoot "Lib\$sdkVersion\um\x64")
)
foreach ($required in @($compiler,$linker,$source) + $includePaths + $libraryPaths) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Existing compiler/SDK component missing: $required" }
}
$compileArgs = @('/nologo','/c','/std:c++20','/EHsc','/utf-8','/MD','/permissive-',
    '/DWINAPI_FAMILY=WINAPI_FAMILY_APP','/DUNICODE','/D_UNICODE','/DWINRT_LEAN_AND_MEAN',"/Fo$object",("/Fd" + (Join-Path $fixtureRoot 'fixture.pdb')))
foreach ($include in $includePaths) { $compileArgs += '/I' + $include }
$compileArgs += $source
& $compiler @compileArgs
if ($LASTEXITCODE) { throw "Private UWP C++ compilation failed ($LASTEXITCODE), no app was registered." }
$linkArgs = @('/nologo','/SUBSYSTEM:WINDOWS','/APPCONTAINER','/DYNAMICBASE','/NXCOMPAT','/MANIFEST:NO',"/OUT:$executable",$object,'WindowsApp.lib')
foreach ($library in $libraryPaths) { $linkArgs += '/LIBPATH:' + $library }
& $linker @linkArgs
if ($LASTEXITCODE) { throw "Private UWP link failed ($LASTEXITCODE), no app was registered." }
$manifest = (Get-Content -LiteralPath (Join-Path $fixtureSource 'AppxManifest.template.xml') -Raw).Replace('__FIXTURE_NAME__',$fixtureName)
[IO.File]::WriteAllText((Join-Path $fixtureLayout 'AppxManifest.xml'),$manifest,[Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path (Join-Path $fixtureLayout 'Assets') -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$sourceIcon = [Drawing.Image]::FromFile((Join-Path (Split-Path -Parent (Split-Path -Parent $fixtureSource)) 'native\logo.png'))
try {
    foreach ($asset in @(@('StoreLogo.png',50),@('Square150.png',150),@('Square44.png',44))) {
        $bitmap = [Drawing.Bitmap]::new([int]$asset[1],[int]$asset[1])
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($sourceIcon,0,0,[int]$asset[1],[int]$asset[1])
            $bitmap.Save((Join-Path $fixtureLayout ('Assets\' + $asset[0])),[Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $sourceIcon.Dispose() }
$plan = [ordered]@{schema=1;name=$fixtureName;publisher='CN=NexusPrivateQualification';version='1.0.0.0';appId='App';
    layout=$fixtureLayout;executable=$executable;manifest=(Join-Path $fixtureLayout 'AppxManifest.xml');
    source_sha256=(Get-FileHash -LiteralPath $source).Hash;exe_sha256=(Get-FileHash -LiteralPath $executable).Hash;
    manifest_sha256=(Get-FileHash -LiteralPath (Join-Path $fixtureLayout 'AppxManifest.xml')).Hash;
    developer_mode_observed=$developerMode;vclibs=$vcRuntime.PackageFullName;built_at=[DateTimeOffset]::UtcNow.ToString('o');
    registered=$false;launched=$false;real_package_lifecycle_qualified=$false}
$planPath = Join-Path $fixtureRoot 'plan.json'
[IO.File]::WriteAllText($planPath,($plan|ConvertTo-Json -Depth 5)+"`n",[Text.UTF8Encoding]::new($false))
Write-Output ($plan|ConvertTo-Json -Depth 5)
Write-Output "NEXUS_UWP_PRIVATE_PLAN $planPath"

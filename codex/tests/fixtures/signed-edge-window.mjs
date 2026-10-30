import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileArguments } from './script-file.mjs';

const execFileAsync = promisify(execFile);
const exactBirthPowerShell = String.raw`
function Test-ExactUtcBirth([object]$expected,[DateTime]$actual) {
 if($expected -isnot [string]){throw 'Full precision ISO birth must remain a string'}
 $parsed=[DateTime]::ParseExact($expected,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
 return $parsed.ToUniversalTime().Ticks -eq $actual.ToUniversalTime().Ticks
}
`;
async function readOnlyProbe(source, env, label) {
  const guarded = "$ErrorActionPreference='Stop'\n$probeStage='prepare'\ntry {\n" + exactBirthPowerShell + source + String.raw`
} catch {
 @{probeError='nexus-edge-read-only';stage=$probeStage;type=$_.Exception.GetType().Name}|ConvertTo-Json -Compress
 exit 1
}
`;
  let stdout, failure;
  try {
    ({ stdout } = await execFileAsync('pwsh.exe', fileArguments(guarded), {
      windowsHide: true, timeout: 10000, maxBuffer: 1024 * 1024, env: { ...process.env, ...env },
    }));
  } catch (error) { failure = error; stdout = error.stdout; }
  let result;
  try { result = JSON.parse(stdout ?? ''); } catch { /* A start/transport failure has no trustworthy result. */ }
  if (failure || result?.probeError) {
    const stage = result?.probeError === 'nexus-edge-read-only' && /^[a-z-]+$/.test(result.stage) ? result.stage : 'transport';
    const kind = result?.probeError === 'nexus-edge-read-only' && /^[A-Za-z0-9]+$/.test(result.type) ? result.type : 'ProbeUnavailable';
    // Never attach the child error/cause: it contains the encoded command and PowerShell CLIXML.
    throw new Error(`${label} refused (${stage}/${kind}); read-only probe, no input sent`);
  }
  if (result === undefined) throw new Error(`${label} returned no structured proof; read-only probe, no input sent`);
  return result;
}
export async function signedEdgeProof(executable) {
  const source = String.raw`
$ErrorActionPreference='Stop'
$path=[IO.Path]::GetFullPath($env:NEXUS_SIGNED_EDGE)
if([IO.Path]::GetFileName($path) -ine 'msedge.exe'){throw 'The installed Microsoft Edge executable is required'}
$signature=Get-AuthenticodeSignature -LiteralPath $path
if($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)'){throw 'A valid Microsoft Authenticode signature is required'}
$policies=@()
foreach($key in @('HKCU:\Software\Policies\Microsoft\Edge','HKLM:\Software\Policies\Microsoft\Edge')){
 $value=Get-ItemProperty -LiteralPath $key -Name InPrivateModeAvailability -ErrorAction SilentlyContinue
 if($null -ne $value){$policies+=@{key=$key;value=[int]$value.InPrivateModeAvailability};if([int]$value.InPrivateModeAvailability -eq 1){throw 'Edge policy disables InPrivate mode; no browser launched'}}
}
@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;signature='Valid';subject=$signature.SignerCertificate.Subject;thumbprint=$signature.SignerCertificate.Thumbprint;version=(Get-Item -LiteralPath $path).VersionInfo.FileVersion;inPrivatePolicies=$policies}|ConvertTo-Json -Depth 4 -Compress
`;
  return readOnlyProbe(source, { NEXUS_SIGNED_EDGE: executable }, 'Signed Edge proof');
}

export async function signedChromeProof(executable) {
  const source = String.raw`
$path=[IO.Path]::GetFullPath($env:NEXUS_SIGNED_CHROME)
if([IO.Path]::GetFileName($path) -ine 'chrome.exe'){throw 'The installed Google Chrome executable is required'}
$signature=Get-AuthenticodeSignature -LiteralPath $path
if($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Google LLC(,|$)'){throw 'A valid Google Authenticode signature is required'}
$policies=@()
foreach($key in @('HKCU:\Software\Policies\Google\Chrome','HKLM:\Software\Policies\Google\Chrome')){
 $value=Get-ItemProperty -LiteralPath $key -Name IncognitoModeAvailability -ErrorAction SilentlyContinue
 if($null -ne $value){$policies+=@{key=$key;value=[int]$value.IncognitoModeAvailability};if([int]$value.IncognitoModeAvailability -notin @(0,2)){throw 'Chrome policy refuses Incognito mode; no browser launched'}}
}
@{path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;signature='Valid';subject=$signature.SignerCertificate.Subject;thumbprint=$signature.SignerCertificate.Thumbprint;version=(Get-Item -LiteralPath $path).VersionInfo.FileVersion;incognitoPolicies=$policies}|ConvertTo-Json -Depth 4 -Compress
`;
  return readOnlyProbe(source, { NEXUS_SIGNED_CHROME: executable }, 'Signed Chrome proof');
}

export function privateChromiumWindowProof(browser, current, elements, title, requirePrivacy = true) {
  const tabs = elements.filter(element => element.role === 'tab');
  // Edge's historical app-only constraint is retained. Chrome may publish one exact fixture tab.
  if (tabs.length && !(browser === 'chrome' && tabs.length === 1 && tabs[0].name?.includes(title)))
    throw new Error('Only an app window or one exact fixture tab is allowed; other tabs are refused');
  const pattern = browser === 'chrome' ? /incognito|navigation priv[ée]e/i : /inprivate/i;
  const privateControl = elements.find(element => ['button', 'toolbar', 'group', 'pane', 'text'].includes(element.role) && pattern.test(element.name ?? ''));
  const privacy = pattern.test(current.title) ? { source: 'window-title' } : privateControl ? { source: 'accessible-control', role: privateControl.role, name: privateControl.name } : null;
  if (!privacy && requirePrivacy) throw new Error('Private mode must be visible in this exact fixture window; command-line intent alone is insufficient');
  return { privacy, layout: tabs.length ? 'one-exact-fixture-tab' : 'app-without-tabs', tabCount: tabs.length };
}

export const chromiumWindowIdentity = edgeWindowIdentity;
export const chromiumWindowInventory = edgeWindowInventory;

// Read-only identity proof. It never opens tabs, reads a browser profile or sends a message to a window.
export async function edgeWindowIdentity(spec) {
  const source = String.raw`
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class EdgeFixtureIdentity {
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window,uint kind);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr window,StringBuilder title,int size);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr window,StringBuilder name,int size);
}
'@
$probeStage='decode'
$spec=$env:NEXUS_EDGE_WINDOW_IDENTITY|ConvertFrom-Json -DateKind String
$probeStage='window'
$window=[IntPtr][long]$spec.hwnd
if(![EdgeFixtureIdentity]::IsWindow($window)){@{exists=$false}|ConvertTo-Json -Compress;exit}
[uint32]$ownerPid=0;[void][EdgeFixtureIdentity]::GetWindowThreadProcessId($window,[ref]$ownerPid)
$probeStage='process'
$process=[Diagnostics.Process]::GetProcessById([int]$ownerPid)
try {
 $held=$process.Handle;$actualBirth=$process.StartTime.ToUniversalTime();$birth=$actualBirth.ToString('o',[Globalization.CultureInfo]::InvariantCulture);$exe=$process.MainModule.FileName
 $title=New-Object Text.StringBuilder 4096;[void][EdgeFixtureIdentity]::GetWindowTextW($window,$title,$title.Capacity)
 $class=New-Object Text.StringBuilder 256;[void][EdgeFixtureIdentity]::GetClassNameW($window,$class,$class.Capacity)
 $probeStage='executable'
 if($exe -ine [IO.Path]::GetFullPath($spec.executable)){throw 'The exact local signed Edge executable changed'}
 $probeStage='title'
 if(($null -ne $spec.title) -and (!$title.ToString().Contains([string]$spec.title))){throw 'The exact local nonce title is not proved'}
 $probeStage='class'
 if(!$class.ToString().StartsWith('Chrome_WidgetWin_')){throw 'The Edge window class changed'}
 $probeStage='pid'
 if(($null -ne $spec.pid) -and ([long]$spec.pid -ne $ownerPid)){throw 'The owned Edge PID changed'}
 $probeStage='birth'
 if(($null -ne $spec.birth) -and (!(Test-ExactUtcBirth $spec.birth $actualBirth))){throw 'The owned Edge process birth changed'}
 $probeStage='recheck'
 if($process.HasExited){throw 'The owned Edge process ended'}
 [uint32]$againPid=0;[void][EdgeFixtureIdentity]::GetWindowThreadProcessId($window,[ref]$againPid)
 if($againPid -ne $ownerPid){throw 'The owned Edge HWND changed while reading identity'}
 @{exists=$true;hwnd=$window.ToInt64();pid=$ownerPid;birth=$birth;executable=$exe;title=$title.ToString();class=$class.ToString();owner=[EdgeFixtureIdentity]::GetWindow($window,4).ToInt64()}|ConvertTo-Json -Compress
} finally {$process.Dispose()}
`;
  return readOnlyProbe(source, { NEXUS_EDGE_WINDOW_IDENTITY: JSON.stringify(spec) }, 'Owned Edge window identity');
}

export async function edgeWindowInventory(executable, candidates) {
  const source = String.raw`
$ErrorActionPreference='Stop'
$spec=$env:NEXUS_EDGE_WINDOW_INVENTORY|ConvertFrom-Json
$owners=@{}
foreach($number in @($spec.windows.pid|Sort-Object -Unique)){
 $process=$null
 try{$process=[Diagnostics.Process]::GetProcessById([int]$number);if($process.MainModule.FileName -ieq $spec.executable){$owners[[string]$number]=$process.StartTime.ToUniversalTime().ToString('o')}}catch{}finally{if($process){$process.Dispose()}}
}
$result=@(foreach($window in $spec.windows){if($owners.ContainsKey([string]$window.pid)){@{id=[long]$window.id;pid=[long]$window.pid;birth=$owners[[string]$window.pid]}}})
ConvertTo-Json -InputObject $result -Compress
`;
  return readOnlyProbe(source, { NEXUS_EDGE_WINDOW_INVENTORY: JSON.stringify({ executable, windows: candidates.map(window => ({ id: window.id, pid: window.pid })) }) }, 'Existing Edge window inventory');
}

export async function edgeBirthProbeSelfTest(fail = false) {
  if (fail) return readOnlyProbe("$probeStage='offline-test';throw 'private-fixture-detail CLIXML EncodedCommand'", {}, 'Offline Edge failure fixture');
  const source = String.raw`
$probeStage='offline-test'
$spec='{"birth":"2026-10-30T02:40:46.9512907Z"}'|ConvertFrom-Json -DateKind String
$actual=[DateTime]::ParseExact('2026-10-30T02:40:46.9512907Z','O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
$invalidRejected=$false
try{[void](Test-ExactUtcBirth '2026-10-30T02:40:46.951Z' $actual)}catch{$invalidRejected=$true}
@{inputType=$spec.birth.GetType().FullName;exactIso=$spec.birth;exact=(Test-ExactUtcBirth $spec.birth $actual);sameInstantOffset=(Test-ExactUtcBirth '2026-10-30T03:40:46.9512907+01:00' $actual);differentTick=(Test-ExactUtcBirth '2026-10-30T02:40:46.9512908Z' $actual);differentSecond=(Test-ExactUtcBirth '2026-10-30T02:40:47.9512907Z' $actual);invalidRejected=$invalidRejected;gui=$false}|ConvertTo-Json -Compress
`;
  return readOnlyProbe(source, {}, 'Offline Edge birth fixture');
}

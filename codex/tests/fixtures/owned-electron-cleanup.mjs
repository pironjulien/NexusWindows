import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
// Only the exact fixture main process can be closed, even when its MCP transport died during launch.
export async function closeOwnedElectron(identity) {
  const source = String.raw`
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ElectronFixtureNative {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string command,out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessageW(IntPtr window,uint message,IntPtr w,IntPtr l);
 public static string[] Arguments(string command) {
  int count;var p=CommandLineToArgvW(command,out count);if(p==IntPtr.Zero)throw new InvalidOperationException("Cannot parse fixture argv");
  try{var result=new string[count];for(int i=0;i<count;i++)result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size));return result;}finally{LocalFree(p);}
 }
}
'@
$spec=$env:NEXUS_SIMULATED_FIXTURE_IDENTITY|ConvertFrom-Json
$exePath=[IO.Path]::GetFullPath($spec.executable)
$scriptPath=[IO.Path]::GetFullPath($spec.script)
$statePath=[IO.Path]::GetFullPath($spec.state)
$name=[IO.Path]::GetFileName($exePath).Replace("'","''")
$matches=@()
foreach($candidate in @(Get-CimInstance Win32_Process -Filter "Name='$name'")) {
 $process=$null
 try {
  $process=[Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId);$held=$process.Handle;$birth=$process.StartTime.ToUniversalTime()
  $row=Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
  if(!$row -or $row.ExecutablePath -ine $exePath -or !$row.CommandLine -or $process.MainModule.FileName -ine $exePath){$process.Dispose();continue}
  $argv=[ElectronFixtureNative]::Arguments($row.CommandLine)
  if($argv.Length -ne 6 -or [IO.Path]::GetFullPath($argv[0]) -ine $exePath -or [IO.Path]::GetFullPath($argv[1]) -ine $scriptPath -or $argv[2] -cne $spec.url -or [IO.Path]::GetFullPath($argv[3]) -ine $statePath -or $argv[4] -cne '--disable-features=CalculateNativeWinOcclusion' -or $argv[5] -cne '--force-renderer-accessibility'){$process.Dispose();continue}
  $marker=Get-Content -LiteralPath (Join-Path $statePath '.nexus-simulated-fixture.json') -Raw|ConvertFrom-Json
  if($marker.owner -cne 'nexus-simulated-fixture' -or $marker.script -cne $scriptPath -or $marker.state -cne $statePath -or $marker.url -cne $spec.url){throw 'Exact fixture marker changed before cleanup'}
  if($spec.pid -and [int]$spec.pid -ne $process.Id){throw 'Returned fixture PID changed'}
  if(!$spec.started -or $birth -lt [DateTime]::Parse($spec.started).ToUniversalTime().AddMilliseconds(-50)){throw 'Fixture process predates this launch'}
  if([Math]::Abs(($row.CreationDate.ToUniversalTime()-$birth).TotalMilliseconds) -gt 1 -or $process.HasExited){throw 'Fixture process identity changed during ownership proof'}
  $matches+=@{Process=$process;Birth=$birth};$process=$null
 } finally {if($process){$process.Dispose()}}
}
if($matches.Count -gt 1){foreach($entry in $matches){$entry.Process.Dispose()};throw 'Multiple exact fixture processes; nothing closed'}
if($matches.Count -eq 0){
 if($spec.hwnd -and [ElectronFixtureNative]::IsWindow([IntPtr][long]$spec.hwnd)){throw 'Fixture HWND exists without its exact owned process'}
 @{closed=$true;running=$false;matched=0}|ConvertTo-Json -Compress;exit
}
$entry=$matches[0];$process=$entry.Process
try {
 $window=if($spec.hwnd){[IntPtr][long]$spec.hwnd}else{$process.MainWindowHandle}
 if($window -ne [IntPtr]::Zero -and [ElectronFixtureNative]::IsWindow($window)){
  [uint32]$ownerPid=0;[void][ElectronFixtureNative]::GetWindowThreadProcessId($window,[ref]$ownerPid)
  if($ownerPid -ne $process.Id -or $process.HasExited -or $process.StartTime.ToUniversalTime() -ne $entry.Birth){throw 'Exact fixture HWND/process changed before close'}
  if(![ElectronFixtureNative]::PostMessageW($window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)){throw 'Exact fixture WM_CLOSE refused'}
 }
 [void]$process.WaitForExit(5000)
 @{closed=($window -eq [IntPtr]::Zero -or ![ElectronFixtureNative]::IsWindow($window));running=(!$process.HasExited);matched=1;pid=$process.Id}|ConvertTo-Json -Compress
} finally {$process.Dispose()}
`;
  const result = await execFileAsync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(source, 'utf16le').toString('base64')], {
    windowsHide: true, timeout: 10000, maxBuffer: 1024 * 1024,
    env: { ...process.env, NEXUS_SIMULATED_FIXTURE_IDENTITY: JSON.stringify(identity) },
  });
  return JSON.parse(result.stdout);
}

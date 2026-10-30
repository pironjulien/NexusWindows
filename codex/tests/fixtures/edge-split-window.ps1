$ErrorActionPreference='Stop'
# The Node host can inherit PowerShell 7's module search path; this helper runs Windows PowerShell 5.1.
$env:PSModulePath=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
Add-Type -TypeDefinition @'
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public static class EdgeSplitNative {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string c,out int n);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h,Callback c,IntPtr p);
 [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h,uint f);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool sorted,int family,int tableClass,uint reserved);
 delegate bool Callback(IntPtr h,IntPtr p);struct Rect { public int l,t,r,b; }
 public static string[] Arguments(string c){int n;var p=CommandLineToArgvW(c,out n);if(p==IntPtr.Zero)throw new Exception("argv unavailable");try{var a=new string[n];for(int i=0;i<n;i++)a[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size));return a;}finally{LocalFree(p);}}
 public static object[] Surfaces(IntPtr root){var result=new List<object>();var old=SetThreadDpiAwarenessContext(new IntPtr(-4));try{EnumChildWindows(root,(h,p)=>{var name=new StringBuilder(256);GetClassNameW(h,name,256);Rect r;if(name.ToString()=="Chrome_RenderWidgetHostHWND"&&IsWindowVisible(h)&&GetAncestor(h,2)==root&&GetWindowRect(h,out r))result.Add(new {hwnd=h.ToInt64(),root=root.ToInt64(),className=name.ToString(),visible=true,left=r.l,top=r.t,width=r.r-r.l,height=r.b-r.t});return true;},IntPtr.Zero);return result.ToArray();}finally{SetThreadDpiAwarenessContext(old);}}
 public static bool OwnsPort(int pid,int port){int found=0;foreach(int family in new[]{2,23}){int size=0;uint queried=GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,3,0);if(queried!=0&&queried!=122)throw new Exception("TCP listener inventory unavailable");if(size==0)continue;IntPtr table=Marshal.AllocHGlobal(size);try{if(GetExtendedTcpTable(table,ref size,false,family,3,0)!=0)throw new Exception("TCP listener inventory changed");int count=Marshal.ReadInt32(table),rowSize=family==2?24:56;if(count<0||count>(size-4)/rowSize)throw new Exception("Malformed TCP inventory");for(int i=0;i<count;i++){IntPtr row=IntPtr.Add(table,4+i*rowSize);uint raw=(uint)Marshal.ReadInt32(row,family==2?8:20);int p=(int)(((raw&255)<<8)|((raw>>8)&255));if(p!=port)continue;string address;if(family==2)address=new System.Net.IPAddress((uint)Marshal.ReadInt32(row,4)).ToString();else{byte[] bytes=new byte[16];Marshal.Copy(row,bytes,0,16);address=new System.Net.IPAddress(bytes).ToString();}if(Marshal.ReadInt32(row,family==2?20:52)!=pid||(address!="127.0.0.1"&&address!="::1"))return false;found++;}}finally{Marshal.FreeHGlobal(table);}}return found>0;}
}
'@
$spec=$env:NEXUS_EDGE_SPLIT_IDENTITY|ConvertFrom-Json
$exePath=[IO.Path]::GetFullPath($spec.executable)
$signature=Get-AuthenticodeSignature -LiteralPath $exePath
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw 'Expected the installed Microsoft-signed Edge executable'}
$state=[IO.Path]::GetFullPath($spec.state)
$markerPath=Join-Path $state '.nexus-cdp-fixture.json'
foreach($path in @($state,$markerPath)){if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Fixture state/marker is a reparse point'}}
$marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json
if($marker.owner -cne 'nexus-cdp-local-acceptance' -or $marker.run -cne $spec.run -or ![IO.Path]::GetFileName($state).StartsWith('nexus-claude-cdp-live-edge-split-',[StringComparison]::Ordinal)){throw 'Fixture state marker changed'}
$profileRoot=[IO.Path]::GetFullPath((Join-Path $state 'devtools-profiles')).TrimEnd('\')+'\'
$name=[IO.Path]::GetFileName($exePath).Replace("'","''");$owned=@()
foreach($row in @(Get-CimInstance Win32_Process -Filter "Name='$name'")){
 if($row.ExecutablePath -ine $exePath -or !$row.CommandLine){continue}
 $argv=[EdgeSplitNative]::Arguments($row.CommandLine)
 if(@($spec.urls|Where-Object {$argv -cnotcontains $_}).Count -gt 0){continue}
 if(@($argv|Where-Object {$_.StartsWith('--type=')}).Count -gt 0){continue}
 $profiles=@($argv|Where-Object {$_.StartsWith('--user-data-dir=',[StringComparison]::Ordinal)})
 if($profiles.Count -ne 1){continue}
 $profile=[IO.Path]::GetFullPath($profiles[0].Substring('--user-data-dir='.Length))
 if(!$profile.StartsWith($profileRoot,[StringComparison]::OrdinalIgnoreCase)){continue}
 $ownerMarker=Join-Path $profile '.nexus-profile'
 foreach($path in @($profile,$ownerMarker)){if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Fixture profile/marker is a reparse point'}}
 $owner=Get-Content -LiteralPath $ownerMarker -Raw|ConvertFrom-Json
 if($owner.owner -cne 'nexus-windows-claude' -or $owner.id -cne [IO.Path]::GetFileName($profile)){throw 'Engine-owned profile marker changed'}
 $ports=@($argv|Where-Object {$_.StartsWith('--remote-debugging-port=',[StringComparison]::Ordinal)})
 if($ports.Count -ne 1 -or ($spec.port -and $ports[0] -cne ('--remote-debugging-port='+$spec.port))){throw 'Fixture DevTools port changed'}
 $p=[Diagnostics.Process]::GetProcessById([int]$row.ProcessId);$held=$p.Handle;$birth=$p.StartTime.ToUniversalTime()
 if(($spec.pid -and [int]$spec.pid -ne $p.Id) -or ($spec.birth -and $birth -ne [DateTime]::Parse($spec.birth).ToUniversalTime()) -or ($spec.started -and $birth -lt [DateTime]::Parse($spec.started).ToUniversalTime().AddMilliseconds(-50))){$p.Dispose();throw 'Fixture PID/birth changed'}
 $owned+=@{Process=$p;Birth=$birth;Profile=$profile;Port=[int]$ports[0].Substring('--remote-debugging-port='.Length)}
}
if($owned.Count -gt 1){foreach($item in $owned){$item.Process.Dispose()};throw 'Ambiguous exact fixture process'}
if($owned.Count -eq 0){if($spec.hwnd -and [EdgeSplitNative]::IsWindow([IntPtr][long]$spec.hwnd)){throw 'Fixture HWND exists without attributable process/profile'};if($spec.mode -ne 'close'){throw 'Exact fixture process missing'};@{closed=$true;running=$false;matched=0}|ConvertTo-Json -Compress;exit}
$item=$owned[0];$p=$item.Process
try{
 $window=if($spec.hwnd){[IntPtr][long]$spec.hwnd}else{$p.MainWindowHandle}
 if($window -ne [IntPtr]::Zero -and [EdgeSplitNative]::IsWindow($window)){
  [uint32]$ownerPid=0;[void][EdgeSplitNative]::GetWindowThreadProcessId($window,[ref]$ownerPid)
  if($ownerPid -ne $p.Id -or $p.HasExited -or $p.StartTime.ToUniversalTime() -ne $item.Birth){throw 'Fixture HWND/process changed'}
  if($spec.mode -eq 'close'){
   if(![EdgeSplitNative]::PostMessageW($window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)){throw 'Exact fixture WM_CLOSE refused'}
  }
 }elseif($spec.mode -ne 'close'){throw 'Exact fixture window missing'}
 if($spec.mode -eq 'close'){
  $clock=[Diagnostics.Stopwatch]::StartNew();while($clock.ElapsedMilliseconds -lt 5000 -and !$p.HasExited){[Threading.Thread]::Sleep(50)}
  @{closed=($window -eq [IntPtr]::Zero -or ![EdgeSplitNative]::IsWindow($window));running=(!$p.HasExited);pid=$p.Id;hwnd=$window.ToInt64()}|ConvertTo-Json -Compress;exit
 }
 if(![EdgeSplitNative]::OwnsPort($p.Id,$item.Port)){throw 'Fixture port is not loopback-owned by the exact browser process'}
 @{pid=$p.Id;hwnd=$window.ToInt64();birth=$item.Birth.ToString('o');port=$item.Port;profile=$item.Profile;surfaces=@([EdgeSplitNative]::Surfaces($window))}|ConvertTo-Json -Depth 10 -Compress
}finally{$p.Dispose()}

import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { fileArguments } from './script-file.mjs';

// A new native probe process can inherit a different DPI default. Compare every sample in PMv2,
// restore that thread's original context, and never move/focus anything while observing.
const source = String.raw`
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OcrPhysicalProbe {
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [StructLayout(LayoutKind.Sequential)] public struct LastInput { public uint Size,Time; }
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
 [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
 [DllImport("user32.dll")] public static extern bool GetPhysicalCursorPos(out Point point);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LastInput info);
 [DllImport("kernel32.dll")] public static extern uint GetTickCount();
}
'@
$previous=[OcrPhysicalProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
if($previous -eq [IntPtr]::Zero){throw 'PMv2 observation context unavailable'}
try {
 $awareness=[OcrPhysicalProbe]::GetAwarenessFromDpiAwarenessContext([OcrPhysicalProbe]::GetThreadDpiAwarenessContext())
 if($awareness -ne 2){throw 'Physical probe is not per-monitor aware'}
 $point=New-Object OcrPhysicalProbe+Point;$physical=New-Object OcrPhysicalProbe+Point
 if(![OcrPhysicalProbe]::GetCursorPos([ref]$point) -or ![OcrPhysicalProbe]::GetPhysicalCursorPos([ref]$physical)){throw 'Pointer observation failed'}
 $window=[OcrPhysicalProbe]::GetForegroundWindow();[uint32]$ownerPid=0
 [void][OcrPhysicalProbe]::GetWindowThreadProcessId($window,[ref]$ownerPid)
 $inputInfo=New-Object OcrPhysicalProbe+LastInput;$inputInfo.Size=[Runtime.InteropServices.Marshal]::SizeOf($inputInfo)
 if(![OcrPhysicalProbe]::GetLastInputInfo([ref]$inputInfo)){throw 'Presence observation failed'}
 @{ok=$true;awareness=$awareness;foreground=$window.ToInt64();foregroundPid=$ownerPid;cursor=@{x=$point.X;y=$point.Y};physicalCursor=@{x=$physical.X;y=$physical.Y};display=@{left=[OcrPhysicalProbe]::GetSystemMetrics(76);top=[OcrPhysicalProbe]::GetSystemMetrics(77);width=[OcrPhysicalProbe]::GetSystemMetrics(78);height=[OcrPhysicalProbe]::GetSystemMetrics(79)};lastInput=$inputInfo.Time;idleMs=(([long][OcrPhysicalProbe]::GetTickCount()-$inputInfo.Time)-band 0xffffffffL)}|ConvertTo-Json -Compress
} finally {[void][OcrPhysicalProbe]::SetThreadDpiAwarenessContext($previous)}
`;

export function physicalDesktopProbe() {
  let result;
  try {
    result = JSON.parse(execFileSync('powershell.exe', fileArguments(source), {
      windowsHide: true, encoding: 'utf8', timeout: 5000,
    }));
  } catch { throw new Error('Physical desktop probe failed; no input attempted'); }
  if (!result.ok || result.awareness !== 2) throw new Error('Physical desktop probe context is unproved');
  return result;
}

// Curseur gardé : strictement identique tant que l'affichage n'a pas changé. Un écran en veille ou un câble qui
// renégocie change le mode d'affichage sans aucune entrée (relevé du 30/10 : 2560x1440, 3840x2160 et 1680x1050 en
// boucle, LastInput figé) et Windows recentre alors le curseur : seule cette position recentrée est admise, et le
// changement est rapporté. Les sondes doivent venir de physicalDesktopProbe (curseur et affichage lus ensemble).
export function assertCursorKept(before, after, message = 'The cursor must be unchanged or restored') {
  assert.ok(before?.display && after?.display, 'Cursor comparison needs the display read with each cursor sample');
  const same = ['left', 'top', 'width', 'height'].every(key => before.display[key] === after.display[key]);
  if (same) { assert.deepEqual(after.cursor, before.cursor, message); return { displayChanged: false }; }
  assert.equal(after.lastInput, before.lastInput, `${message}: display mode changed while the user was active`);
  const center = { x: after.display.left + Math.floor(after.display.width / 2), y: after.display.top + Math.floor(after.display.height / 2) };
  assert.deepEqual(after.cursor, center, `${message}: after a display mode change only the system recentred cursor is accepted`);
  return { displayChanged: true, before: before.display, after: after.display };
}

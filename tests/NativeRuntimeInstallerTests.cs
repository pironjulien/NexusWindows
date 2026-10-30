// commun/native-runtime.ps1 (the workstation installer of the immutable engine) on fixtures: every case runs in its own
// PowerShell with a throwaway runtime home, `dotnet` replaced by a function that fails the case if a build is
// attempted, and the stable launcher step stubbed (covered by LauncherTests). Formerly commun/tests/NativeRuntime.Tests.ps1.
using System.Diagnostics;

namespace NexusWindows.Tests;

public sealed class NativeRuntimeInstallerTests
{
    const string Prelude = """
        $ErrorActionPreference = 'Stop'
        . (Join-Path $env:NEXUS_REPO 'commun\native-runtime.ps1')
        $nexusWindowsRuntimeHome = $env:NEXUS_FIXTURE_HOME
        $nexusWindowsManifest = Join-Path $nexusWindowsRuntimeHome 'installation.json'
        function dotnet { throw 'A fixture test must never build or execute dotnet.' }
        function Get-NexusWindowsNativeHash { 'a' * 64 }
        function Install-NexusWindowsLauncher { 'stub' }
        function New-RuntimeFixture([string]$Hash) {
            $directory = Join-Path $nexusWindowsRuntimeHome "native\$Hash"
            [void][IO.Directory]::CreateDirectory($directory)
            $artifacts = [ordered]@{}
            foreach ($name in 'NexusWindows.exe', 'NexusWindows.dll', 'NexusWindows.deps.json', 'NexusWindows.runtimeconfig.json', 'garde-fous.json') {
                $file = Join-Path $directory $name
                [IO.File]::WriteAllText($file, "fixture $Hash $name")
                $artifacts[$name] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            }
            $manifest = [pscustomobject]@{ Schema=2; SourceRoot='fixture'; NativeHash=$Hash; NativeExecutable=(Join-Path $directory 'NexusWindows.exe'); Version='3.9.0'; NativeArtifacts=[pscustomobject]$artifacts }
            Write-NexusWindowsJson (Join-Path $directory 'runtime.json') $manifest
            return $manifest
        }
        function Expect([bool]$Condition, [string]$What) { if (-not $Condition) { throw "Expectation failed: $What" } }
        function Throws([scriptblock]$Block, [string]$Like) {
            try { & $Block } catch { if ($_.Exception.Message -like $Like) { return }; throw "Unexpected error: $($_.Exception.Message)" }
            throw "No error, expected $Like"
        }
        $a = 'a' * 64; $b = 'b' * 64

        """;

    public static TheoryData<string, string> Cases => new()
    {
        { "accepts the matching version only after validating every artifact", """
            $runtime = New-RuntimeFixture $a
            Write-NexusWindowsJson $nexusWindowsManifest $runtime
            $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest))
            Expect ((Install-NexusWindowsNativeRuntime -CheckOnly).NativeHash -ceq $a) 'matching hash'
            Expect ([Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest)) -ceq $before) 'manifest unchanged'
            """ },
        { "rejects a valid other version copied into the current target in CheckOnly", """
            $current = New-RuntimeFixture $a; $other = New-RuntimeFixture $b
            Expect (Test-NexusWindowsRuntime $other) 'other is valid'
            Write-NexusWindowsJson (Join-Path (Split-Path -Parent $current.NativeExecutable) 'runtime.json') $other
            Write-NexusWindowsJson $nexusWindowsManifest $current
            Throws { Install-NexusWindowsNativeRuntime -CheckOnly } '*différent des sources*'
            Expect ((Get-NexusWindowsNativeRuntime).NativeHash -ceq $a) 'installed unchanged'
            """ },
        { "never publishes a foreign manifest or rebuilds an existing immutable target", """
            $current = New-RuntimeFixture $a; $other = New-RuntimeFixture $b
            Write-NexusWindowsJson (Join-Path (Split-Path -Parent $current.NativeExecutable) 'runtime.json') $other
            Write-NexusWindowsJson $nexusWindowsManifest $current
            $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest))
            Throws { Install-NexusWindowsNativeRuntime } '*immutable incomplet ou altéré*'
            Expect ([Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest)) -ceq $before) 'manifest unchanged'
            """ },
        { "rejects a changed artifact despite a matching source version", """
            $runtime = New-RuntimeFixture $a
            [IO.File]::AppendAllText($runtime.NativeExecutable, 'changed')
            Expect (-not (Test-NexusWindowsRuntime $runtime)) 'changed refused'
            Throws { Install-NexusWindowsNativeRuntime -CheckOnly } '*différent des sources*'
            """ },
        { "rejects a missing required artifact", """
            $runtime = New-RuntimeFixture $a
            [IO.File]::Delete((Join-Path (Split-Path -Parent $runtime.NativeExecutable) 'NexusWindows.dll'))
            Expect (-not (Test-NexusWindowsRuntime $runtime)) 'missing refused'
            """ },
        { "rejects a manifest executable of another version directory", """
            $runtime = New-RuntimeFixture $a; $other = New-RuntimeFixture $b
            $runtime.NativeExecutable = $other.NativeExecutable
            Expect (-not (Test-NexusWindowsRuntime $runtime)) 'foreign executable refused'
            """ },
        { "rejects malformed version JSON before any write", """
            $runtime = New-RuntimeFixture $a
            [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $runtime.NativeExecutable) 'runtime.json'), '{invalid')
            Throws { Install-NexusWindowsNativeRuntime -CheckOnly } '*'
            Expect (-not (Test-Path -LiteralPath $nexusWindowsManifest)) 'no manifest written'
            """ },
        { "requires the installed manifest to match the current version", """
            $null = New-RuntimeFixture $a; $other = New-RuntimeFixture $b
            Write-NexusWindowsJson $nexusWindowsManifest $other
            Throws { Install-NexusWindowsNativeRuntime -CheckOnly } '*Manifest commun non aligné*'
            Expect ((Get-NexusWindowsNativeRuntime).NativeHash -ceq $b) 'installed unchanged'
            """ },
        { "swaps the stable launcher while its old copy runs", """
            $target = Join-Path $nexusWindowsRuntimeHome 'NexusWindows.exe'
            [void][IO.Directory]::CreateDirectory($nexusWindowsRuntimeHome)
            Copy-Item (Join-Path $env:SystemRoot 'System32\cmd.exe') $target
            $running = Start-Process $target -ArgumentList '/d /c ping -n 30 127.0.0.1 >nul' -WindowStyle Hidden -PassThru
            try {
                Start-Sleep -Milliseconds 300
                $source = Join-Path $nexusWindowsRuntimeHome 'new.exe'
                [IO.File]::WriteAllText($source, 'new launcher')
                Update-NexusWindowsLauncher $source $target
                Expect ([IO.File]::ReadAllText($target) -ceq 'new launcher') 'new launcher in place'
                Expect (@(Get-ChildItem $nexusWindowsRuntimeHome -Filter 'NexusWindows.exe.old-*').Count -eq 1) 'running copy kept aside'
                Expect (-not $running.HasExited) 'old session still running'
            } finally { $running.Kill(); $running.WaitForExit() }
            Update-NexusWindowsLauncher $source $target
            Expect (@(Get-ChildItem $nexusWindowsRuntimeHome -Filter 'NexusWindows.exe.old-*').Count -eq 0) 'released copies removed'
            """ },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Workstation_installer(string name, string script)
    {
        var home = Path.Combine(Path.GetTempPath(), "nexuswindows-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", Prelude + script }) start.ArgumentList.Add(arg);
            start.Environment["NEXUS_REPO"] = TestEnvironment.RepositoryRoot;
            start.Environment["NEXUS_FIXTURE_HOME"] = home;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60_000), name);
            Assert.True(process.ExitCode == 0, $"{name}: {error}{output.Result}");
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

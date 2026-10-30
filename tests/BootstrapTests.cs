// The public installer's real bootstrap and hash helper under Windows PowerShell 5.1 with PowerShell 7's module
// folder first, as inherited when NexusSystem is started from a pwsh terminal. No installation or GUI is run.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NexusWindows.Tests;

public sealed class BootstrapTests
{
    [Fact]
    public async Task Windows_PowerShell_prefers_its_builtins_and_keeps_custom_module_paths()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(TestEnvironment.SharedHome, "bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "get.ps1"));
        var start = source.IndexOf("$ErrorActionPreference = 'Stop'", StringComparison.Ordinal);
        var end = source.IndexOf("$ProgressPreference =", start, StringComparison.Ordinal);
        var hash = source.Split('\n').Single(line => line.StartsWith("function Sha256(", StringComparison.Ordinal));

        var pwsh = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine((Join-Path $PSHOME 'Modules'))" }) pwsh.ArgumentList.Add(arg);
        using var core = Process.Start(pwsh)!;
        var coreModules = (await core.StandardOutput.ReadToEndAsync(token)).Trim();
        var coreError = await core.StandardError.ReadToEndAsync(token);
        await core.WaitForExitAsync(token);
        Assert.True(core.ExitCode == 0 && Directory.Exists(coreModules), coreError);

        var desktop = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        var custom = Path.Combine(root, "custom-modules");
        Directory.CreateDirectory(custom);
        var file = Path.Combine(root, "known.txt");
        File.WriteAllText(file, "Nexus bootstrap hash fixture", new UTF8Encoding(false));
        var fixture = Path.Combine(root, "bootstrap.ps1");
        File.WriteAllText(fixture, source[start..end] + "\n" + hash + "\n" + """
            if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Expected Windows PowerShell.' }
            $paths = $env:PSModulePath -split ';'
            $builtins = Join-Path $PSHOME 'Modules'
            if ($paths[0] -ine $builtins) { throw 'Desktop modules must come first.' }
            if (@($paths | Where-Object { $_ -ieq $builtins }).Count -ne 1) { throw 'Duplicate Desktop module path.' }
            foreach ($kept in $env:NEXUS_CORE_MODULES, $env:NEXUS_CUSTOM_MODULES) {
                if ($kept -notin $paths) { throw 'An existing module path was removed.' }
            }
            $unblock = Get-Command Unblock-File -ErrorAction Stop
            if (-not $unblock.Module.Path.StartsWith($builtins, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Unblock-File resolved the wrong host module.'
            }
            [Console]::WriteLine((Sha256 $env:NEXUS_HASH_FILE))
            """, new UTF8Encoding(false));
        var child = new ProcessStartInfo(Path.Combine(desktop, "powershell.exe")) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", fixture }) child.ArgumentList.Add(arg);
        child.Environment["PSModulePath"] = string.Join(';', coreModules, Path.Combine(desktop, "Modules"), custom);
        child.Environment["NEXUS_CORE_MODULES"] = coreModules;
        child.Environment["NEXUS_CUSTOM_MODULES"] = custom;
        child.Environment["NEXUS_HASH_FILE"] = file;
        using var process = Process.Start(child)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), (await output).Trim());
    }
}

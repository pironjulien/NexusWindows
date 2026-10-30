// The stable launcher (commun/launcher): it runs only the versioned engine installation.json names with its recorded
// hashes, passes stdin/stdout/exit code through, and can be replaced on disk while a copy of it is running.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using StableLauncher = NexusWindows.Launcher.Launcher;

namespace NexusWindows.Tests;

public sealed class LauncherTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "nexuswindows-launcher-" + Guid.NewGuid().ToString("N"));

    public LauncherTests() => Directory.CreateDirectory(home);
    public void Dispose() { try { Directory.Delete(home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // A stand-in engine: cmd.exe under the engine's name, with a dummy NexusWindows.dll, recorded like an install.
    string InstallFakeEngine(char version = 'a')
    {
        var hash = new string(version, 64);
        var directory = Path.Combine(home, "native", hash);
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "NexusWindows.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        File.WriteAllText(Path.Combine(directory, "NexusWindows.dll"), "engine " + version);
        WriteManifest(hash, exe, new() { ["NexusWindows.exe"] = Sha(exe), ["NexusWindows.dll"] = Sha(Path.Combine(directory, "NexusWindows.dll")) });
        return exe;
    }

    void WriteManifest(string hash, string exe, Dictionary<string, string> artifacts) =>
        File.WriteAllText(Path.Combine(home, "installation.json"), JsonSerializer.Serialize(new { Schema = 2, NativeHash = hash, NativeExecutable = exe, Version = "3.9.0", NativeArtifacts = artifacts }));

    string CopyLauncher(string? name = null)
    {
        // The launcher built next to the tests (framework-dependent apphost + its dll), as a stable copy in the home.
        foreach (var file in new[] { "NexusWindows.exe", "NexusWindows.dll", "NexusWindows.runtimeconfig.json", "NexusWindows.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(home, file), true);
        if (name is null) return Path.Combine(home, "NexusWindows.exe");
        File.Move(Path.Combine(home, "NexusWindows.exe"), Path.Combine(home, name), true);
        return Path.Combine(home, name);
    }

    static (int Code, string Out, string Err) RunProcess(string exe, string input, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Write(input); process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, output.Result, error);
    }

    [Fact]
    public void Resolves_only_the_recorded_versioned_engine()
    {
        var exe = InstallFakeEngine();
        Assert.Equal(exe, StableLauncher.Resolve(home), ignoreCase: true);
        // Engine outside native\<sha-256>\, unknown schema, altered file: refused.
        WriteManifest("../x", exe, []);
        Assert.Throws<InvalidDataException>(() => StableLauncher.Resolve(home));
        File.WriteAllText(Path.Combine(home, "installation.json"), File.ReadAllText(Path.Combine(home, "installation.json")).Replace("\"Schema\":2", "\"Schema\":1"));
        Assert.Throws<InvalidDataException>(() => StableLauncher.Resolve(home));
        InstallFakeEngine('b');
        File.AppendAllText(Path.Combine(home, "native", new string('b', 64), "NexusWindows.dll"), "changed");
        Assert.Throws<InvalidDataException>(() => StableLauncher.Resolve(home));
        File.Delete(Path.Combine(home, "installation.json"));
        Assert.Throws<FileNotFoundException>(() => StableLauncher.Resolve(home));
    }

    [Fact]
    public void Passes_arguments_stdin_stdout_and_exit_code_through()
    {
        InstallFakeEngine();
        var launcher = CopyLauncher();
        var (code, output, _) = RunProcess(launcher, "", "/d", "/c", "echo through& exit 7");
        Assert.Equal(7, code);
        Assert.Equal("through", output.Trim());
        (code, output, _) = RunProcess(launcher, "line from the client\r\n", "/d", "/c", "more");
        Assert.Equal(0, code);
        Assert.Contains("line from the client", output);
        // No installation: a clear message and code 3, nothing started.
        File.Delete(Path.Combine(home, "installation.json"));
        var (missing, _, error) = RunProcess(launcher, "");
        Assert.Equal(3, missing);
        Assert.Contains("installer", error);
    }

    [Fact]
    public void Can_be_replaced_and_retargeted_while_a_session_runs()
    {
        InstallFakeEngine('a');
        var launcher = CopyLauncher();
        // A running session (waits on its stdin, as an MCP server does).
        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "/d", "/c", "more" }) start.ArgumentList.Add(arg);
        using var session = Process.Start(start)!;
        Thread.Sleep(500);
        Assert.False(session.HasExited);
        // The installers' swap: the running file is renamed aside, the new copy takes the stable name.
        var aside = Path.Combine(home, "NexusWindows.exe.old-" + Guid.NewGuid().ToString("N"));
        File.Move(launcher, aside);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "NexusWindows.exe"), launcher);
        // A new engine version is recorded: the next start uses it, the running session keeps its own.
        InstallFakeEngine('b');
        var (code, output, _) = RunProcess(launcher, "", "/d", "/c", "echo next& exit 4");
        Assert.Equal((4, "next"), (code, output.Trim()));
        Assert.False(session.HasExited);
        session.StandardInput.WriteLine("still served"); session.StandardInput.Close();
        Assert.Contains("still served", session.StandardOutput.ReadToEnd());
        Assert.True(session.WaitForExit(10_000));
        Assert.Equal(0, session.ExitCode);
        File.Delete(aside); // released once the old session ended
    }

    [Fact]
    public void Killing_the_launcher_ends_its_engine()
    {
        InstallFakeEngine();
        var launcher = CopyLauncher();
        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "/d", "/c", "more" }) start.ArgumentList.Add(arg);
        using var session = Process.Start(start)!;
        Thread.Sleep(500);
        var engine = Process.GetProcessesByName("NexusWindows").Where(p => { try { return p.MainModule!.FileName.StartsWith(home, StringComparison.OrdinalIgnoreCase) && p.Id != session.Id; } catch { return false; } }).ToList();
        Assert.Single(engine);
        session.Kill();
        Assert.True(engine[0].WaitForExit(10_000));
    }
}

// Process-wide setup shared by every test: the engine reads its policy from NEXUS_GARDE_FOUS, as the installer
// declares it in production, and the shared state of the tests never touches the real %USERPROFILE%\.nexus folder.
using System.Runtime.CompilerServices;

namespace NexusWindows.Tests;

static class TestEnvironment
{
    public static readonly string RepositoryRoot = FindRoot();
    public static readonly string Policy = Path.Combine(RepositoryRoot, "commun", "garde-fous.json");
    public static readonly string SharedHome = Path.Combine(Path.GetTempPath(), "nexuswindows-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("NEXUS_GARDE_FOUS", Policy);
        Directory.CreateDirectory(SharedHome);
        Environment.SetEnvironmentVariable("NEXUS_SHARED_HOME", SharedHome);
        // The common engine's profile state and journal have their own roots: NEXUS_SHARED_HOME only
        // isolates locks/receipts. Child MCP servers inherit these roots instead of polluting the user's history.
        Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", Path.Combine(SharedHome, "native-state"));
        Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", Path.Combine(SharedHome, "journal"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(SharedHome, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
    }

    static string FindRoot([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root (global.json) not found.");
    }
}

using System.IO;

// Local capture files are short-lived evidence for the model: kept 10 minutes and 32 at most (README promise). Pruned
// after each capture written, at server start and at a clean stop. Only the engine's own file names (32 hex digits and
// an image extension) are touched, so a custom NEXUS_WINDOWS_HOME never loses anything else.
static class CaptureRetention
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    public const int MaxFiles = 32;

    static bool Owned(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var extension = file.Extension.ToLowerInvariant();
        return name.Length == 32 && name.All(Uri.IsHexDigit) && extension is ".webp" or ".jpg" or ".jpeg" or ".png";
    }

    // Returns the number of files removed. Never throws: a file in use or already gone is left for the next pass.
    public static int Prune(string directory, DateTime? nowUtc = null)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return 0;
            var now = nowUtc ?? DateTime.UtcNow;
            var files = new DirectoryInfo(directory).EnumerateFiles().Where(Owned).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var removed = 0;
            for (var i = 0; i < files.Count; i++)
            {
                if (i < MaxFiles && now - files[i].LastWriteTimeUtc <= MaxAge) continue;
                try { files[i].Delete(); removed++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}

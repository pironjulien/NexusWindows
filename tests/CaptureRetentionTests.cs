namespace NexusWindows.Tests;

// README promise: local captures are kept 10 minutes and 32 at most, removed at the next capture, at server start and
// at a clean stop. Only the engine's own capture names are touched.
public class CaptureRetentionTests
{
    static string Make(string directory, DateTime writtenUtc, string? name = null)
    {
        var path = Path.Combine(directory, name ?? Guid.NewGuid().ToString("N") + ".webp");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void Old_captures_go_and_only_the_32_newest_stay_and_foreign_files_are_left_alone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nexus-windows-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTime.UtcNow;
            var old = Make(directory, now.AddMinutes(-11));
            var oldJpeg = Make(directory, now.AddMinutes(-30), Guid.NewGuid().ToString("N") + ".jpg");
            var recent = Enumerable.Range(0, 40).Select(i => Make(directory, now.AddSeconds(-i))).ToList();
            var foreign = Make(directory, now.AddDays(-3), "notes.webp");
            var foreignText = Make(directory, now.AddDays(-3), Guid.NewGuid().ToString("N") + ".txt");

            var removed = CaptureRetention.Prune(directory, now);

            Assert.Equal(2 + 8, removed);
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(oldJpeg));
            Assert.All(recent.Take(CaptureRetention.MaxFiles), path => Assert.True(File.Exists(path)));
            Assert.All(recent.Skip(CaptureRetention.MaxFiles), path => Assert.False(File.Exists(path)));
            Assert.True(File.Exists(foreign));
            Assert.True(File.Exists(foreignText));
            Assert.Equal(0, CaptureRetention.Prune(directory, now));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void A_missing_or_relative_directory_is_a_no_op()
    {
        Assert.Equal(0, CaptureRetention.Prune(Path.Combine(Path.GetTempPath(), "nexus-windows-absent-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(0, CaptureRetention.Prune("captures"));
    }
}

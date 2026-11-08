using System.Diagnostics;
using System.Runtime.InteropServices;

unsafe sealed partial class ComUia
{
    readonly ChromiumContentWarmup chromiumWarmup = new();
    readonly Stopwatch chromiumWarmupClock = Stopwatch.StartNew();

    static ChromiumContentWarmup.Identity? ChromiumIdentity(nint hwnd)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd)) return null;
        var pid = Win32.ProcessOf(hwnd);
        var birth = Program.ProcessStartedUtc(pid);
        if (pid == 0 || string.IsNullOrEmpty(birth)) return null;
        var identity = new ChromiumContentWarmup.Identity((long)hwnd, pid, birth, Win32.ClassName(hwnd));
        // Process metadata and native window queries are independent reads. Recheck the endpoints so a
        // closed/replaced HWND cannot create a mixed identity that later retries would treat as current.
        return Win32.IsWindow(hwnd) && Win32.ProcessOf(hwnd) == pid && Program.ProcessStartedUtc(pid) == birth
            && Win32.ClassName(hwnd) == identity.WindowClass ? identity : null;
    }
}

// Chromium initializes accessibility on demand. A first real tree may contain only its native toolbar.
// Preserve that observation, but allow the same short read-only warmup as the former Python snapshot.
// This policy owns every tree handed to it: discarded trees are disposed, returned trees belong to caller.
// No cached tree, UIA pattern, simulated activation, foreground call or input is used by the retries.
sealed class ChromiumContentWarmup
{
    internal const int BudgetMs = 1500, PollMs = 250, MissingCooldownMs = 60000, RevealEveryMs = 2000;
    readonly Dictionary<long, (double At, bool Done)> lastReveal = new();
    internal readonly record struct Identity(long Hwnd, uint Pid, string Birth, string WindowClass);
    readonly record struct Miss(Identity Identity, double At);
    readonly Dictionary<long, Miss> missing = new();

    internal static bool HasWebContent(UiTree tree)
    {
        var documents = tree.Nodes.Where(node => node.Role == "document").Select(node => node.Index).ToHashSet();
        return documents.Count != 0 && tree.Nodes.Any(node => node.Ancestors.Length != 0 && documents.Contains(node.Ancestors[^1]));
    }

    internal UiTree Read(UiTree initial, bool chromium, Identity? expected, Func<Identity?> currentIdentity,
        Func<UiTree> readFresh, Func<bool> stopped, Func<double> nowMs, Action<int> wait, Func<bool>? reveal = null)
    {
        var tree = initial;
        try
        {
            // Missing birth permits the ordinary initial observation, never an extra delayed observation.
            if (!chromium || expected is not { Hwnd: not 0, Pid: not 0, Birth.Length: > 0 } identity) return tree;
            void Revalidate()
            {
                if (currentIdentity() != identity)
                    throw new ActionRefusedException("Chromium window identity changed while waiting for accessibility; inspect again");
            }
            Revalidate();
            var now = nowMs();
            foreach (var stale in missing.Where(pair => now - pair.Value.At >= MissingCooldownMs || now < pair.Value.At).Select(pair => pair.Key).ToArray())
                missing.Remove(stale);
            if (HasWebContent(tree)) { missing.Remove(identity.Hwnd); return tree; }
            if (stopped()) return tree; // STOP keeps the native toolbar readable and ends only the optional warmup.
            if (missing.TryGetValue(identity.Hwnd, out var miss))
            {
                // Within the cooldown a covered browser window may still be revealed, at most every RevealEveryMs: a page that loaded
                // after the previous reveal was hidden again by Chrome (bench of 05/11: every look after a navigation missed).
                if (miss.Identity == identity && now - miss.At < MissingCooldownMs &&
                    (reveal == null || !lastReveal.TryGetValue(identity.Hwnd, out var last) || !last.Done || now - last.At < RevealEveryMs && now >= last.At)) return tree;
                missing.Remove(identity.Hwnd); // HWND reuse, PID replacement or process rebirth resets the miss.
            }
            var deadline = now + BudgetMs;
            var revealed = false;
            while (true)
            {
                if (stopped()) return tree;
                Revalidate();
                var remaining = deadline - nowMs();
                if (remaining <= 0) break;
                wait(Math.Min(PollMs, (int)Math.Ceiling(remaining)));
                if (stopped()) return tree;
                Revalidate();
                // Never begin a provider call after the deadline. One synchronous UIA call already in
                // progress still has its existing connection/transaction timeout; it cannot be preempted.
                if (nowMs() > deadline) break;
                UiTree? next = null;
                try { next = readFresh(); Revalidate(); }
                catch { next?.Dispose(); throw; }
                tree.Dispose(); tree = next;
                if (HasWebContent(tree)) { missing.Remove(identity.Hwnd); return tree; }
                // Still no page after a fresh read: a covered window does not render a page loaded behind (ChromiumReveal.cs).
                // One reveal per read, then the same budget again for the page to publish its document.
                if (!revealed && reveal != null)
                {
                    revealed = true;
                    var done = reveal(); lastReveal[identity.Hwnd] = (nowMs(), done);
                    if (done) { Revalidate(); deadline = nowMs() + BudgetMs; }
                }
            }
            missing[identity.Hwnd] = new(identity, nowMs());
            return tree; // Last independently read toolbar remains usable; no fabricated document or status.
        }
        catch { tree.Dispose(); throw; }
    }
}

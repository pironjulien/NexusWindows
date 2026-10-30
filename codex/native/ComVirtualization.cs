using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

static partial class Program
{
    // Internal navigation only. The facade must re-inspect and re-resolve its exact semantic selector afterwards.
    static object RealizeVirtualized(nint window, JsonElement request, List<Node> nodes)
    {
        var selector = request.GetProperty("selector");
        if (selector.ValueKind != JsonValueKind.Object || !selector.TryGetProperty("name", out var nameValue)
            || nameValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameValue.GetString()))
            throw new ActionRefusedException("Materialization needs an exact semantic name, never a displayed index");
        foreach (var property in selector.EnumerateObject())
            if (property.Name is not ("name" or "role" or "automationId" or "ancestor"))
                throw new ActionRefusedException("Unsupported materialization selector; nothing was sent");
        var pid = Win32.ProcessOf(window);
        var birth = ProcessStartedUtc(pid) ?? throw new ActionRefusedException("Window birth unavailable for materialization");
        var containers = nodes.Where(n => n.Role is "list" or "tree" or "datagrid" or "table")
            .Where(n => !n.Disabled && n.RuntimeId.Length > 0 && !ComUia.IsSynthetic(n.RuntimeId))
            .Where(n => !selector.TryGetProperty("ancestor", out var ancestor) || Matches(n, ancestor)
                || n.Ancestors.Any(i => Matches(nodes[i], ancestor)))
            .Select(n => new { runtimeId = n.RuntimeId, nativeWindow = (long)n.NativeWindow,
                expectedControlPid = n.ProcessId, expectedControlBirth = ProcessStartedUtc((uint)n.ProcessId),
                expectedName = n.Name, expectedRole = n.Role, expectedAutomationId = n.AutomationId }).ToArray();
        if (containers.Any(c => c.expectedControlBirth == null))
            throw new ActionRefusedException("Container birth unavailable for materialization; nothing was sent");
        InputStarted = true; // A lost reply can follow Realize/ScrollIntoView, even when lookup itself is read-only.
        try
        {
            var result = ComBridge.CallData(new { op = "realizeNamed", windowId = (long)window,
                expectedWindowPid = pid, expectedWindowBirth = birth, name = nameValue.GetString(), containers });
            InputStarted = result.GetProperty("inputSent").GetBoolean();
            return new { ok = true, found = result.GetProperty("found").GetBoolean(), inputSent = InputStarted,
                requiresFreshInspection = true, technique = "background:uia", mechanism = "ItemContainer/VirtualizedItem",
                effect = InputStarted ? "sent" : "none" };
        }
        catch (Exception error) when (error.Data["beforeInput"] is true) { InputStarted = false; throw; }
    }
}

unsafe sealed partial class ComUia
{
    // Windows SDK UIAutomationClient.h: ItemContainer10019 / VirtualizedItem10020, first own method at slot3.
    const int ItemContainerPatternId = 10019, VirtualizedItemPatternId = 10020;
    static readonly Guid IidItemContainer = new("c690fdb2-27a8-423c-812d-429773c9084e");
    static readonly Guid IidVirtualizedItem = new("6ba3d7a6-04cf-4f11-8793-a8d1cde9969f");
    static nint VirtualPattern(nint element, int patternId, Guid iid)
    {
        nint pattern;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Vt(element)[14])(element, patternId, &iid, &pattern);
        return hr < 0 ? 0 : pattern;
    }
    internal static nint ItemContainer(nint element) => VirtualPattern(element, ItemContainerPatternId, IidItemContainer);
    internal static nint VirtualizedItem(nint element) => VirtualPattern(element, VirtualizedItemPatternId, IidVirtualizedItem);
    internal bool SameElement(nint first, nint second) => Same(first, second);
    internal static nint NamedVirtualItem(nint pattern, string name, nint after = 0)
    {
        var value = new Variant { Vt = VT_BSTR, Pointer = Marshal.StringToBSTR(name) };
        try
        {
            nint found;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int, Variant, nint*, int>)Vt(pattern)[3])
                (pattern, after, NameProperty, value, &found));
            return found;
        }
        finally { VariantClear(ref value); }
    }
}

static partial class ComWorker
{
    static nint CheckedContainer(nint window, JsonElement container)
    {
        var element = ComUia.Shared.FindByRuntimeId(window,
            container.GetProperty("runtimeId").EnumerateArray().Select(v => v.GetInt32()).ToArray(),
            (nint)container.GetProperty("nativeWindow").GetInt64());
        try
        {
            var pid = container.GetProperty("expectedControlPid").GetInt32();
            if (ComUia.ProcessIdOf(element) != pid
                || Program.ProcessStartedUtc((uint)pid) != container.GetProperty("expectedControlBirth").GetString()
                || !ComUia.FocusIdentityMatches(element, container))
                throw Before("Virtualized container identity changed; nothing was sent");
            return element;
        }
        catch { ComUia.Release(element); throw; }
    }
    static object RealizeNamed(nint window, JsonElement request)
    {
        RequireWindowIdentity(window, request);
        var name = request.GetProperty("name").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(name)) throw Before("An exact name is required");
        var clock = Stopwatch.StartNew(); var chosen = (nint)0; JsonElement owner = default;
        try
        {
            foreach (var container in request.GetProperty("containers").EnumerateArray())
            {
                EmergencyStop.ThrowIfStopped(); RequireWindowIdentity(window, request);
                if (clock.ElapsedMilliseconds >= 2000) throw Before("Virtualized search incomplete within its budget; nothing was sent");
                var element = CheckedContainer(window, container); nint pattern = 0, found = 0, next = 0;
                try
                {
                    pattern = ComUia.ItemContainer(element);
                    if (pattern == 0) continue;
                    found = ComUia.NamedVirtualItem(pattern, name);
                    if (found == 0) continue;
                    next = ComUia.NamedVirtualItem(pattern, name, found);
                    if (next != 0) throw Before("Several virtualized items have that exact name; use an unambiguous container");
                    if (chosen != 0)
                    {
                        if (!ComUia.Shared.SameElement(chosen, found))
                            throw Before("Several containers contain that name; nothing was materialized");
                    }
                    else { chosen = found; found = 0; owner = container; }
                }
                catch (COMException) when (found == 0 && chosen == 0) { /* This container does not support searching Name. */ }
                finally { ComUia.Release(next); ComUia.Release(found); ComUia.Release(pattern); ComUia.Release(element); }
            }
            if (clock.ElapsedMilliseconds >= 2000) throw Before("Virtualized search incomplete within its budget; nothing was sent");
            if (chosen == 0) return new { ok = true, found = false, inputSent = false, technique = Uia, mechanism = "ItemContainer.FindItemByProperty", effect = "none" };
            void CheckIdentity()
            {
                EmergencyStop.ThrowIfStopped(); RequireWindowIdentity(window, request);
                var current = CheckedContainer(window, owner); ComUia.Release(current);
            }
            MaterializeSafely(CheckIdentity, () => ComUia.VirtualizedItem(chosen), () => ComUia.Pattern(chosen, ComUia.ScrollItemPatternId),
                p => { BeforeAct(); ComUia.CallSlot(p, 3); }, ComUia.Release);
            return new { ok = true, found = true, inputSent = Acted, technique = Uia,
                mechanism = "VirtualizedItem.Realize/ScrollItem.ScrollIntoView", effect = Acted ? "sent" : "none" };
        }
        finally { ComUia.Release(chosen); }
    }
    // This seam drives the real COM sequence and its offline failure/interruption tests.
    internal static void MaterializeSafely(Action checkIdentity, Func<nint> virtualPattern, Func<nint> scrollPattern, Action<nint> call, Action<nint> release)
    {
        foreach (var get in new[] { virtualPattern, scrollPattern })
        {
            checkIdentity();
            var pattern = get();
            if (pattern == 0) continue;
            try { checkIdentity(); call(pattern); }
            finally { release(pattern); }
        }
    }
    public static int VirtualizationSelfTest()
    {
        var checks = 0; var calls = new List<long>(); var releases = new List<long>();
        void Assert(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        MaterializeSafely(() => { }, () => 1, () => 2, p => calls.Add(p), p => releases.Add(p));
        Assert(calls.SequenceEqual(new long[] { 1, 2 }), "Realize precedes ScrollIntoView");
        Assert(releases.SequenceEqual(new long[] { 1, 2 }), "All acquired patterns released");
        calls.Clear(); releases.Clear();
        var stopped = false;
        try { MaterializeSafely(() => { if (calls.Count > 0) throw new ActionRefusedException("identity changed"); }, () => 1, () => 2,
            p => calls.Add(p), p => releases.Add(p)); }
        catch (ActionRefusedException) { stopped = true; }
        Assert(stopped && calls.SequenceEqual(new long[] { 1 }), "Identity change after Realize forbids ScrollIntoView");
        Assert(releases.SequenceEqual(new long[] { 1 }), "Pattern released on interruption");
        calls.Clear(); releases.Clear(); var unknown = false;
        try { MaterializeSafely(() => { }, () => 1, () => 2, p => { calls.Add(p); throw new IOException("lost after Realize"); }, p => releases.Add(p)); }
        catch (IOException) { unknown = true; }
        Assert(unknown && calls.SequenceEqual(new long[] { 1 }), "An uncertain Realize is never retried and never scrolls");
        Assert(releases.SequenceEqual(new long[] { 1 }), "Failed pattern released");
        calls.Clear(); MaterializeSafely(() => { }, () => 0, () => 0, p => calls.Add(p), _ => { });
        Assert(calls.Count == 0, "An unsupported pattern sends nothing");
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, checks })); return 0;
    }
}

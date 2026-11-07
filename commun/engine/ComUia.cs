using System.Diagnostics;
using System.Runtime.InteropServices;

// UI Automation COM client (CUIAutomation8 / IUIAutomation2): reading the tree AND background patterns.
// The managed client of System.Windows.Automation is no longer loaded anywhere: it does not expose AutoSetFocus (by
// default, UIA gives the focus to the element before a pattern, which activates the target window) and it reads the tree
// property by property (one cross-process round trip per property and per element). Here AutoSetFocus = FALSE, read back
// after it is set, and the tree is read in ONE round trip (CacheRequest, Subtree scope, Control view).
// Calls through virtual table slots (order of UIAutomationClient.h, Windows SDK 10.0.26100): no interop library or
// typelib to generate.
unsafe sealed partial class ComUia
{
    static readonly Guid ClsidCUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    static readonly Guid IidIUIAutomation2 = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
    static readonly Guid IidInvoke = new("fb377fbe-8ea6-46d5-9c73-6499642d3059");
    static readonly Guid IidToggle = new("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70");
    static readonly Guid IidValue = new("a94cd8b1-0844-4cd6-9d2d-640537ab39e9");
    static readonly Guid IidSelectionItem = new("a8efa66a-0fda-421a-9194-38021f3578ea");
    static readonly Guid IidExpandCollapse = new("619be086-1f4e-4ee4-bafa-210128738730");
    static readonly Guid IidSelection = new("5ed5202e-b2ac-47a6-b638-4b0bf140d78e");
    static readonly Guid IidText = new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");
    static readonly Guid IidScroll = new("88f4d42a-e881-459d-a77c-73bbbb7e02dc");
    static readonly Guid IidLegacy = new("828055ad-355b-4435-86d5-3b51c14a9b1b");
    static readonly Guid IidScrollItem = new("b488300f-d015-4f19-9c29-bb595e3645ef");
    static readonly Guid IidGrid = new("414c3cdc-856b-4f5b-8538-3131c6302550");
    static readonly Guid IidTable = new("620e691c-ea96-4710-a850-754b24ce2417");
    public const int InvokePatternId = 10000, SelectionPatternId = 10001, ValuePatternId = 10002, ExpandCollapsePatternId = 10005,
        SelectionItemPatternId = 10010, TextPatternId = 10014, TogglePatternId = 10015, ScrollPatternId = 10004,
        ScrollItemPatternId = 10017, LegacyIAccessiblePatternId = 10018, GridPatternId = 10006, TablePatternId = 10012;
    const int RuntimeIdProperty = 30000, BoundingRectangleProperty = 30001, ProcessIdProperty = 30002, ControlTypeProperty = 30003,
        NameProperty = 30005, AccessKeyProperty = 30007, HasKeyboardFocusProperty = 30008, LegacyIAccessibleStateProperty = 30096, IsEnabledProperty = 30010, AutomationIdProperty = 30011,
        HelpTextProperty = 30013, IsPasswordProperty = 30019, NativeWindowHandleProperty = 30020, IsOffscreenProperty = 30022, FrameworkIdProperty = 30024,
        IsExpandCollapseAvailable = 30028, IsInvokeAvailable = 30031, IsSelectionItemAvailable = 30036, IsSelectionAvailable = 30037,
        IsTextAvailable = 30040, IsToggleAvailable = 30041, IsValueAvailable = 30043, ValueValueProperty = 30045,
        ExpandCollapseStateProperty = 30070, SelectionItemIsSelectedProperty = 30079, ToggleStateProperty = 30086;
    static readonly int[] TreeProperties =
    [
        RuntimeIdProperty, BoundingRectangleProperty, ProcessIdProperty, ControlTypeProperty, NameProperty, HasKeyboardFocusProperty,
        IsEnabledProperty, AutomationIdProperty, HelpTextProperty, IsPasswordProperty, NativeWindowHandleProperty, IsOffscreenProperty, FrameworkIdProperty,
        IsExpandCollapseAvailable, IsInvokeAvailable, IsSelectionItemAvailable, IsSelectionAvailable, IsTextAvailable, IsToggleAvailable,
        IsValueAvailable, ValueValueProperty, ExpandCollapseStateProperty, SelectionItemIsSelectedProperty, ToggleStateProperty,
        AccessKeyProperty, LegacyIAccessibleStateProperty, // guard rails: Alt shortcut and default button (STATE_SYSTEM_DEFAULT)
    ];
    const int TreeScopeElement = 1, TreeScopeChildren = 2, TreeScopeSubtree = 7;
    const ushort VT_I4 = 3, VT_R8 = 5, VT_BSTR = 8, VT_BOOL = 11, VT_ARRAY = 0x2000;
    public const int MaxNodes = 4096, MaxDepth = 64, MaxText = 32768;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public nint Pointer;
        [FieldOffset(8)] public int Int;
        [FieldOffset(8)] public short Bool;
    }

    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("oleaut32.dll")] private static partial nint SafeArrayCreateVector(ushort vt, int lowerBound, uint count);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayAccessData(nint array, out nint data);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayUnaccessData(nint array);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayGetLBound(nint array, uint dimension, out int bound);
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayGetUBound(nint array, uint dimension, out int bound);
    [LibraryImport("oleaut32.dll")] private static partial int VariantClear(ref Variant value);

    static ComUia? shared;
    readonly nint automation, treeRequest, wpfTreeRequest, elementRequest, childrenRequest, walker;
    public static ComUia Shared => shared ??= new ComUia();

    ComUia()
    {
        // A 24-byte VARIANT assumes a 64-bit process (x64, ARM64).
        if (!Environment.Is64BitProcess) throw new BackgroundNotApplicableException("COM UI Automation requires a 64-bit helper");
        Check(CoCreateInstance(ClsidCUIAutomation8, 0, 1 /* CLSCTX_INPROC_SERVER */, IidIUIAutomation2, out automation));
        var vtable = *(nint**)automation;
        Check(((delegate* unmanaged[Stdcall]<nint, int, int>)vtable[59])(automation, 0));            // put_AutoSetFocus(FALSE)
        Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)vtable[61])(automation, 2000));         // put_ConnectionTimeout
        Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)vtable[63])(automation, 5000));         // put_TransactionTimeout
        int autoSetFocus;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)vtable[58])(automation, &autoSetFocus)); // get_AutoSetFocus
        if (autoSetFocus != 0) throw new BackgroundNotApplicableException("UI Automation refused AutoSetFocus = FALSE");
        treeRequest = Request(TreeScopeSubtree);
        // WPF: the MSAA state (default button) costs a bridge per element, 0.4 of the 1.1 s of the lab window's 663
        // elements (01/11); it is read only on the elements that can be invoked (Describe), the only ones it concerns.
        wpfTreeRequest = Request(TreeScopeSubtree, legacyState: false);
        elementRequest = Request(TreeScopeElement);
        childrenRequest = Request(TreeScopeChildren);
        nint w;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[14])(automation, &w));          // get_ControlViewWalker
        walker = w;
    }
    // Cache request: every property the inspection reads, Control view, full elements (current patterns still callable
    // afterwards).
    nint Request(int scope, bool legacyState = true)
    {
        nint request, condition;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(automation)[20])(automation, &request));  // CreateCacheRequest
        foreach (var property in TreeProperties) if (legacyState || property != LegacyIAccessibleStateProperty) Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Vt(request)[3])(request, property));
        Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Vt(request)[7])(request, scope));                // put_TreeScope
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(automation)[18])(automation, &condition)); // get_ControlViewCondition
        try { Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Vt(request)[9])(request, condition)); } // put_TreeFilter
        finally { Release(condition); }
        return request;
    }

    public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    static nint* Vt(nint unknown) => *(nint**)unknown;
    public static void Release(nint unknown) { if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(unknown)[2])(unknown); }

    nint ElementFromHandle(nint hwnd)
    {
        nint element;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(automation)[6])(automation, hwnd, &element));
        if (element == 0) throw new BackgroundNotApplicableException("No UI Automation element for the window");
        return element;
    }
    nint Condition(int property, Variant value)
    {
        nint condition;
        try { Check(((delegate* unmanaged[Stdcall]<nint, int, Variant, nint*, int>)Vt(automation)[23])(automation, property, value, &condition)); }
        finally { VariantClear(ref value); }
        return condition;
    }
    nint RuntimeIdCondition(int[] runtimeId)
    {
        var array = SafeArrayCreateVector(VT_I4, 0, (uint)runtimeId.Length);
        if (array == 0) throw new OutOfMemoryException();
        Check(SafeArrayAccessData(array, out var data));
        Marshal.Copy(runtimeId, 0, data, runtimeId.Length);
        Check(SafeArrayUnaccessData(array));
        return Condition(RuntimeIdProperty, new Variant { Vt = VT_ARRAY | VT_I4, Pointer = array });
    }
    static nint FindFirst(nint scopeRoot, nint condition)
    {
        nint found;
        Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(scopeRoot)[5])(scopeRoot, TreeScopeSubtree, condition, &found));
        return found;
    }
    bool Same(nint a, nint b)
    {
        int same;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int*, int>)Vt(automation)[3])(automation, a, b, &same));
        return same != 0;
    }

    // Current element with the given runtime id, under the window. The result must be released (Release). Hint of an own
    // window (Win32/WinForms controls): the element is taken directly on its window if its identity matches, without
    // walking the tree (a walk would cross an open combo box, see ReadTree).
    public nint FindByRuntimeId(nint window, int[] runtimeId, nint hint = 0)
    {
        if (FromHint(hint, runtimeId, 0) is var direct and not 0) return direct;
        var root = ElementFromHandle(window);
        try
        {
            var condition = RuntimeIdCondition(runtimeId);
            try
            {
                var found = FindFirst(root, condition);
                if (found == 0) throw new BackgroundNotApplicableException("Target is no longer present for UI Automation");
                return found;
            }
            finally { Release(condition); }
        }
        finally { Release(root); }
    }
    nint FromHint(nint hint, int[] runtimeId, nint request)
    {
        if (hint == 0 || !Win32.IsWindow(hint)) return 0;
        nint element;
        var hr = request == 0
            ? ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(automation)[6])(automation, hint, &element)
            : ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vt(automation)[10])(automation, hint, request, &element);
        if (hr < 0 || element == 0) return 0;
        var same = (request == 0 ? CurrentRuntimeId(element) : CachedIntArray(element, RuntimeIdProperty))?.SequenceEqual(runtimeId) == true;
        if (same) return element;
        Release(element);
        return 0;
    }
    static int[]? CurrentRuntimeId(nint element)
    {
        nint array;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(element)[4])(element, &array) < 0 || array == 0) return null; // GetRuntimeId
        try { return ReadArray<int>(array); } finally { _ = SafeArrayDestroy(array); }
    }
    [LibraryImport("oleaut32.dll")] private static partial int SafeArrayDestroy(nint array);

    // ---- Reading the tree --------------------------------------------------------------------------------------------
    // A single round trip (Subtree scope). Measured exception: an OPEN Win32/WinForms combo box makes its UIA subtree
    // pathological (.NET Framework WinForms proxy: "Malformed Cacheresponse" after ~7 s, then navigation to its children
    // stops answering). Classic combo boxes are therefore never walked through UIA: their options are read through
    // messages (CB_GETCOUNT, CB_GETLBTEXT, CB_GETCURSEL), open or not, as synthetic nodes (runtime id [-1, window, kind,
    // rank]). If one of them is open, the read goes level by level (one round trip per element that has children) so as
    // not to enter its subtree.
    public UiTree ReadTree(nint hwnd, bool withPopups = true)
    {
        var chromium = ChromiumInput.HasChromiumContent(hwnd);
        var identity = chromium ? ChromiumIdentity(hwnd) : null;
        var initial = GatedReadTreeOnce(hwnd, withPopups);
        var revealed = false;
        var tree = chromiumWarmup.Read(initial, chromium, identity, () => ChromiumIdentity(hwnd),
            () => GatedReadTreeOnce(hwnd, withPopups, publish: false), () => EmergencyStop.RemainingSeconds() > 0,
            () => chromiumWarmupClock.Elapsed.TotalMilliseconds, Thread.Sleep, () => revealed = ChromiumReveal.TryReveal(hwnd));
        // The publication describes the tree returned, not only the first read: a page published by the warmup or after a
        // reveal of the covered window is no longer « no-document ».
        if (chromium && LastPublication == "no-document" && ChromiumContentWarmup.HasWebContent(tree)) LastPublication = revealed ? "revealed" : "requested";
        return tree;
    }

    // Full reads of the agents of this session in turn (UiaReadGate.cs): simultaneous ones are slower than in turn.
    UiTree GatedReadTreeOnce(nint hwnd, bool withPopups, bool publish = true)
    {
        using var gate = UiaReadGate.Enter();
        // A provider that cannot serialize its whole subtree in one cache response (06/11, the « Signaler un problème »
        // form of the Feedback Hub: a WinUI 3 window hosting a WebView2, « Malformed Cacheresponse pTreeStructure String
        // (missing closing parens) » on every look and act) is read again level by level, one round trip per element.
        return TreeReadFallback.Read(() => ReadTreeOnce(hwnd, withPopups, publish), () => ReadTreeOnce(hwnd, withPopups, publish: false, byLevels: true));
    }

    UiTree ReadTreeOnce(nint hwnd, bool withPopups, bool publish = true, bool byLevels = false)
    {
        // A read-only pass on a covered Chromium may lag one action behind: publish BEFORE building the cache, otherwise
        // every wait reads the same old tree again. No new click.
        if (publish) LastPublication = Flush(hwnd);
        var levels = byLevels;
        if (!levels) Win32.EnumChildWindows(hwnd, (child, _) =>
        {
            if (Win32.BaseClass(child) == "combobox" && Win32.Send(child, Win32.CB_GETDROPPEDSTATE) is > 0) { levels = true; return false; }
            return true;
        }, 0);
        nint root;
        var wpf = !levels && Win32.ClassName(hwnd).StartsWith("HwndWrapper", StringComparison.Ordinal);
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vt(automation)[10])(automation, hwnd, levels ? elementRequest : wpf ? wpfTreeRequest : treeRequest, &root)); // ElementFromHandleBuildCache
        if (root == 0) throw new InvalidOperationException("No UI Automation root");
        var tree = new UiTree();
        // Each element belongs either to the stack (not described yet) or to the tree: released only once.
        var pending = new Stack<(nint Element, int Depth, int[] Ancestors)>();
        pending.Push((root, 0, []));
        // Open WinForms context or drop-down menu (ContextMenuStrip: a top-level window, outside the window's UIA tree;
        // shared bench of 21/10: "found 0" for « Copier »): its subtree is read under the root, its root carrying the
        // popup's HWND (nativeWindow), which becomes the scope of this menu's commands.
        var popups = new Queue<nint>(withPopups ? Win32.LinkedMenuPopups(hwnd) : []);
        // Element already read elsewhere in the tree (same runtime id): a provider may expose the same element under two
        // parents (Explorer context menu, bench of 28/10: « Propriétés » twice, an ambiguous target then "Control
        // ancestry changed"). Only its first place, in document order, is kept, with its subtree.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (pending.Count > 0 || popups.Count > 0)
            {
                if (pending.Count == 0)
                {
                    var popup = popups.Dequeue();
                    // Already read under the window: once owned by the form (after its first opening), UIA files it among
                    // its children; read again here, each command appeared twice (bench of 22/10: "found 2" for
                    // « Copier » on the second round).
                    if (tree.Nodes.Any(n => n.NativeWindow == popup)) continue;
                    nint popupRoot;
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vt(automation)[10])(automation, popup, levels ? elementRequest : treeRequest, &popupRoot));
                    if (popupRoot == 0) throw new InvalidOperationException("Open menu could not be read");
                    pending.Push((popupRoot, 1, [0]));
                    continue;
                }
                var (element, depth, ancestors) = pending.Peek();
                if (tree.Nodes.Count >= MaxNodes || depth > MaxDepth)
                    throw new InvalidOperationException("Accessibility tree exceeds inspection bound; target a smaller window");
                var node = Describe(element, depth, ancestors, currentDefaultState: wpf);
                pending.Pop();
                if (node.RuntimeId.Length > 0 && !seen.Add(string.Join(",", node.RuntimeId)))
                {
                    Release(element); // duplicate: neither node nor children (its children are not taken into the cache)
                    continue;
                }
                tree.Add(node);
                if (IsClassicCombo(node))
                {
                    if (!levels) foreach (var child in CachedChildren(element)) Release(child);
                    SynthesizeCombo(tree, node);
                    continue;
                }
                if (IsClassicListBox(node))
                {
                    if (!levels) foreach (var child in CachedChildren(element)) Release(child);
                    SynthesizeListBox(tree, node);
                    continue;
                }
                // Level by level, an element whose children the provider cannot describe keeps its place without them
                // (flag « unreadable »): the rest of the window stays readable, its image and points still usable.
                var children = levels ? TreeReadFallback.ChildrenOrNone(() => Children(element), out node.Unreadable) : CachedChildren(element);
                // Document order: first child handled first.
                for (var i = children.Count - 1; i >= 0; i--) pending.Push((children[i], depth + 1, [.. ancestors, node.Index]));
            }
            return tree;
        }
        catch
        {
            while (pending.Count > 0) Release(pending.Pop().Element);
            tree.Dispose();
            throw;
        }
    }
    // Children (Control view) with their cached properties, in one round trip.
    List<nint> Children(nint element)
    {
        nint updated;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(element)[9])(element, childrenRequest, &updated)); // BuildUpdatedCache
        try { return CachedChildren(updated); } finally { Release(updated); }
    }
    static List<nint> CachedChildren(nint element)
    {
        nint array;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(element)[19])(element, &array)); // GetCachedChildren
        var children = new List<nint>();
        if (array == 0) return children;
        try
        {
            int length;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(array)[3])(array, &length));
            for (var i = 0; i < length; i++)
            {
                nint child;
                Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(array)[4])(array, i, &child));
                if (child != 0) children.Add(child);
            }
        }
        finally { Release(array); }
        return children;
    }

    // Win32/WinForms combo box (COMBOBOX class, except CBS_SIMPLE).
    public static bool IsClassicCombo(Node node) => node.Role == "combobox" && node.NativeWindow != 0 && node.FrameworkId is "Win32" or "WinForm" &&
        Win32.BaseClass(node.NativeWindow) == "combobox" && (Win32.Style(node.NativeWindow) & 0x3) != 0x1;
    public const int Synthetic = -1, SyntheticList = 1, SyntheticItem = 2, SyntheticEdit = 3;
    // Single-selection WinForms list (LISTBOX class): its UIA elements have no stable identity (shared bench of 21/10:
    // "Target is no longer present" on click); they are read through messages, like the options of a combo box. Only a
    // simple list: neither multiple selection nor drawn by the application (CheckedListBox, owner-draw lists: LB_GETTEXT
    // returns the item's data there, not the label; checkboxes lost) - Codex's review.
    public static bool IsClassicListBox(Node node) => node.Role == "list" && node.NativeWindow != 0 && node.FrameworkId == "WinForm" &&
        Win32.BaseClass(node.NativeWindow) == "listbox" &&
        (Win32.Style(node.NativeWindow) & (Win32.LBS_MULTIPLESEL | Win32.LBS_EXTENDEDSEL | 0x10 | 0x20)) == 0; // + LBS_OWNERDRAWFIXED/VARIABLE
    public static void SynthesizeListBox(UiTree tree, Node list)
    {
        var hwnd = list.NativeWindow;
        var key = unchecked((int)(long)hwnd);
        var count = Win32.Send(hwnd, Win32.LB_GETCOUNT);
        if (count is not { } total || total < 0) return;
        var chosen = Win32.Send(hwnd, Win32.LB_GETCURSEL) ?? -1;
        Win32.GetClientRect(hwnd, out var client);
        var scale = Capture.Scale(hwnd);
        for (var i = 0; i < Math.Min((int)total, 1000); i++)
        {
            var text = Win32.SendGetText(hwnd, Win32.LB_GETTEXTLEN, Win32.LB_GETTEXT, i, true) ?? "";
            // Real rectangle of the line (LB_GETITEMRECT, screen) and visibility: a line outside the visible part (or a
            // hidden list) stays "offscreen", as for UIA (Codex's review).
            double[] bounds = [];
            var visible = false;
            if (Win32.ItemRect(hwnd, i, out var logical))
            {
                // LB_GETITEMRECT returns pixels in the LIST's frame (logical if it is not DPI aware: no message is
                // converted); the helper works in real pixels (app.manifest), like GetClientRect.
                var r = Scaled(logical, scale);
                visible = !r.Intersect(client).Empty;
                // A line outside the visible part of the LIST: no rectangle, so refused as out of view (McpSession,
                // Program.cs), even if it still falls inside the window (Codex's review, 21/10: a line below the list).
                var origin = new Win32.Point { X = r.Left, Y = r.Top };
                Win32.ClientToScreen(hwnd, ref origin);
                if (visible) bounds = [origin.X, origin.Y, r.Width, r.Height];
            }
            tree.Add(new Node
            {
                Role = "listitem", Name = text, AutomationId = "", Description = "", RuntimeId = [Synthetic, key, SyntheticItem, i], Bounds = bounds,
                Depth = list.Depth + 1, Ancestors = [.. list.Ancestors, list.Index], FrameworkId = list.FrameworkId, ProcessId = list.ProcessId,
                Disabled = list.Disabled, Selected = i == chosen, Offscreen = list.Offscreen || !visible,
            });
        }
    }
    static Win32.Rect Scaled(Win32.Rect r, double scale) => scale == 1.0 ? r : new Win32.Rect
    {
        Left = (int)Math.Round(r.Left * scale), Top = (int)Math.Round(r.Top * scale),
        Right = (int)Math.Round(r.Right * scale), Bottom = (int)Math.Round(r.Bottom * scale),
    };
    public static bool IsSynthetic(int[] runtimeId) => runtimeId is [Synthetic, _, _, _];
    // Nodes read through messages under a classic combo box: its field (CBS_DROPDOWN), its list and its options.
    // The open state (CB_GETDROPPEDSTATE) and the value (the chosen option) are carried by the combo box itself.
    public static void SynthesizeCombo(UiTree tree, Node combo)
    {
        var hwnd = combo.NativeWindow;
        var key = unchecked((int)(long)hwnd);
        Node Child(string role, string name, int kind, int rank, int depth, int[] ancestors) => new()
        {
            Role = role, Name = name, AutomationId = "", Description = "", RuntimeId = [Synthetic, key, kind, rank], Bounds = [],
            Depth = depth, Ancestors = ancestors, FrameworkId = combo.FrameworkId, ProcessId = combo.ProcessId, Disabled = combo.Disabled,
        };
        int[] under = [.. combo.Ancestors, combo.Index];
        var edit = Win32.FindWindowExW(hwnd, 0, "Edit", null);
        var editValueRead = false;
        if (edit != 0)
        {
            var field = Child("edit", combo.Name, SyntheticEdit, 0, combo.Depth + 1, under);
            field.NativeWindow = edit;
            field.Value = Win32.SendGetText(edit, Win32.WM_GETTEXTLENGTH, Win32.WM_GETTEXT, 0, false);
            // Editable combos (notably the common file dialog) can expose an empty or stale UIA Value.
            // The live Edit text is authoritative, including an intentionally cleared field.
            if (!combo.Password && field.Value != null) { combo.Value = field.Value; editValueRead = true; }
            field.Disabled = combo.Disabled || !Win32.IsWindowEnabled(edit);
            // The field is on screen where its combo box is: its own Edit rectangle (05/11, the « Nom du fichier » of a
            // Windows Open dialog: without bounds it read as hidden, and typing first tried to scroll it into view).
            if (combo.Bounds.Length == 4 && Win32.IsWindowVisible(edit) && Win32.GetWindowRect(edit, out var box) && box.Right > box.Left && box.Bottom > box.Top)
            {
                field.Bounds = [box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top];
                field.Offscreen = combo.Offscreen;
            }
            tree.Add(field);
        }
        var count = Win32.Send(hwnd, Win32.CB_GETCOUNT);
        if (count is not { } total || total < 0) return;
        var chosen = Win32.Send(hwnd, Win32.CB_GETCURSEL) ?? -1;
        var list = tree.Add(Child("list", combo.Name, SyntheticList, 0, combo.Depth + 1, under));
        for (var i = 0; i < Math.Min((int)total, 1000); i++)
        {
            var text = Win32.SendGetText(hwnd, Win32.CB_GETLBTEXTLEN, Win32.CB_GETLBTEXT, i, true) ?? "";
            var item = tree.Add(Child("listitem", text, SyntheticItem, i, combo.Depth + 2, [.. under, list.Index]));
            item.Selected = i == chosen;
            if (!editValueRead && i == chosen && string.IsNullOrEmpty(combo.Value)) combo.Value = text;
        }
    }

    // Identity read-back limited to the target and its ancestors (Control view): the target is found again by its
    // runtime id (directly on its own window if it has one, otherwise under the window), then its chain of parents up
    // to the window's root. Nodes: root at 0, target last. A target outside the window is refused.
    public UiTree ReadIdentity(nint hwnd, int[] runtimeId, nint hint = 0, bool readAncestorText = true)
    {
        nint root;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vt(automation)[10])(automation, hwnd, elementRequest, &root));
        if (root == 0) throw new BackgroundNotApplicableException("No UI Automation element for the window");
        var chain = new List<nint>();
        try
        {
            var found = FromHint(hint, runtimeId, elementRequest);
            if (found == 0)
            {
                var condition = RuntimeIdCondition(runtimeId);
                try { Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint, nint*, int>)Vt(root)[7])(root, TreeScopeSubtree, condition, elementRequest, &found)); } // FindFirstBuildCache
                finally { Release(condition); }
            }
            // Runtime id given and not found: the target was replaced or removed. Refused, never a fallback.
            if (found == 0) throw new ActionRefusedException("Target is no longer present (control identity changed); inspect again");
            chain.Add(found);
            var current = found;
            while (!Same(current, root))
            {
                if (chain.Count > MaxDepth + 1) throw new ActionRefusedException("Target ancestry is too deep; inspect again");
                nint parent;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vt(walker)[9])(walker, current, elementRequest, &parent)); // GetParentElementBuildCache
                if (parent == 0) throw new ActionRefusedException("Target is no longer inside the window; inspect again");
                chain.Add(parent);
                current = parent;
            }
            chain.Reverse();
            var tree = new UiTree();
            try
            {
                for (var i = 0; i < chain.Count; i++)
                {
                    tree.Add(Describe(chain[i], i, [.. Enumerable.Range(0, i)], readDocumentText: readAncestorText || i == chain.Count - 1));
                    chain[i] = 0; // now owned by the tree
                }
                return tree;
            }
            catch { tree.Dispose(); throw; }
        }
        finally
        {
            foreach (var element in chain) Release(element);
            Release(root);
        }
    }

    // Rank of the target among its parent's children with the same role (tabs without an own window).
    public int ChildRank(Node parent, Node target)
    {
        nint updated;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(parent.Element)[9])(parent.Element, childrenRequest, &updated)); // BuildUpdatedCache
        var children = new List<nint>();
        try
        {
            children = CachedChildren(updated);
            var rank = 0;
            foreach (var child in children)
            {
                if (Role(CachedInt(child, ControlTypeProperty) ?? 0) != target.Role) continue;
                if ((CachedIntArray(child, RuntimeIdProperty) ?? []).SequenceEqual(target.RuntimeId)) return rank;
                rank++;
            }
            return -1;
        }
        finally
        {
            foreach (var child in children) Release(child);
            Release(updated);
        }
    }

    static Node Describe(nint element, int depth, int[] ancestors, bool readDocumentText = true, bool currentDefaultState = false)
    {
        var password = CachedBool(element, IsPasswordProperty) ?? false;
        var role = Role(CachedInt(element, ControlTypeProperty) ?? 0);
        var hasText = CachedBool(element, IsTextAvailable) == true;
        var hasSelection = CachedBool(element, IsSelectionAvailable) == true;
        var framework = CachedString(element, FrameworkIdProperty) ?? "";
        // Passwords expose only their identity, never their value.
        string? value = null;
        if (!password && CachedBool(element, IsValueAvailable) == true) value = CachedString(element, ValueValueProperty);
        if (readDocumentText && !password && value == null && hasText && (role == "document" || role == "edit" && framework == "Chrome")) value = DocumentText(element);
        var nativeWindow = (nint)(CachedInt(element, NativeWindowHandleProperty) ?? 0);
        var classicCombo = role == "combobox" && nativeWindow != 0 && framework is "Win32" or "WinForm" && Win32.BaseClass(nativeWindow) == "combobox";
        // A combo box without a value (non-editable WPF, a list without the Value pattern): the value is the chosen option,
        // read on the container and not from the presence of a list item in the tree. Classic list: through messages
        // (SynthesizeCombo), never through UIA (a provider may stop answering while the list is open).
        if (!password && role == "combobox" && !classicCombo && string.IsNullOrEmpty(value) && hasSelection)
        {
            var chosen = SelectionNames(element);
            if (chosen.Count > 0) value = string.Join(", ", chosen);
        }
        var truncated = value is { Length: > MaxText };
        if (truncated) value = value![..MaxText];
        bool? check = CachedBool(element, IsToggleAvailable) == true
            ? CachedInt(element, ToggleStateProperty) switch { 1 => true, 0 => false, _ => null } : null;
        var node = new Node
        {
            Element = element, Depth = depth, Ancestors = ancestors, Role = role,
            Name = CachedString(element, NameProperty) ?? "", AutomationId = CachedString(element, AutomationIdProperty) ?? "",
            Description = CachedString(element, HelpTextProperty) ?? "", Value = value, ValueTruncated = truncated,
            Disabled = CachedBool(element, IsEnabledProperty) == false, Focused = CachedBool(element, HasKeyboardFocusProperty) == true,
            Password = password, RuntimeId = CachedIntArray(element, RuntimeIdProperty) ?? [],
            Selected = CachedBool(element, IsSelectionItemAvailable) == true ? CachedBool(element, SelectionItemIsSelectedProperty) : null,
            Checked = check, NativeWindow = nativeWindow, FrameworkId = framework, ProcessId = CachedInt(element, ProcessIdProperty) ?? 0,
            HasInvoke = CachedBool(element, IsInvokeAvailable) == true, HasToggle = CachedBool(element, IsToggleAvailable) == true,
            HasText = hasText, Bounds = Bounds(element), Offscreen = CachedBool(element, IsOffscreenProperty) == true,
            AccessKey = CachedString(element, AccessKeyProperty) ?? "",
        };
        // STATE_SYSTEM_DEFAULT: from the cache, or read live on an invokable element of a WPF read (wpfTreeRequest).
        var state = currentDefaultState ? node.HasInvoke ? CurrentInt(element, LegacyIAccessibleStateProperty) : 0 : CachedInt(element, LegacyIAccessibleStateProperty);
        node.IsDefault = ((state ?? 0) & 0x100) != 0;
        if (CachedBool(element, IsExpandCollapseAvailable) == true)
        {
            switch (CachedInt(element, ExpandCollapseStateProperty))
            {
                case 0: node.Actions.Add("Expand"); node.Expanded = false; break;
                case 1: node.Actions.Add("Collapse"); node.Expanded = true; break;
                case 2: node.Expanded = true; break; // partiellement deplie
            }
        }
        return node;
    }
    static double[] Bounds(nint element)
    {
        var v = Cached(element, BoundingRectangleProperty);
        try
        {
            if (v.Vt != (VT_ARRAY | VT_R8) || v.Pointer == 0) return [];
            var values = ReadArray<double>(v.Pointer);
            return values.Length == 4 && values[2] > 0 && values[3] > 0 ? values : [];
        }
        finally { VariantClear(ref v); }
    }
    static T[] ReadArray<T>(nint array) where T : unmanaged
    {
        Check(SafeArrayGetLBound(array, 1, out var low));
        Check(SafeArrayGetUBound(array, 1, out var high));
        var count = Math.Max(0, high - low + 1);
        var result = new T[count];
        Check(SafeArrayAccessData(array, out var data));
        try { new ReadOnlySpan<T>((void*)data, count).CopyTo(result); }
        finally { _ = SafeArrayUnaccessData(array); }
        return result;
    }
    static Variant Cached(nint element, int property)
    {
        Variant value;
        Check(((delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)Vt(element)[12])(element, property, &value)); // GetCachedPropertyValue
        return value;
    }
    static string? CachedString(nint element, int property)
    {
        var v = Cached(element, property);
        try { return v.Vt == VT_BSTR ? (v.Pointer == 0 ? "" : Marshal.PtrToStringBSTR(v.Pointer)) : null; }
        finally { VariantClear(ref v); }
    }
    static int? CachedInt(nint element, int property)
    {
        var v = Cached(element, property);
        try { return v.Vt == VT_I4 ? v.Int : null; }
        finally { VariantClear(ref v); }
    }
    static bool? CachedBool(nint element, int property)
    {
        var v = Cached(element, property);
        try { return v.Vt == VT_BOOL ? v.Bool != 0 : null; }
        finally { VariantClear(ref v); }
    }
    static int[]? CachedIntArray(nint element, int property)
    {
        var v = Cached(element, property);
        try { return v.Vt == (VT_ARRAY | VT_I4) && v.Pointer != 0 ? ReadArray<int>(v.Pointer) : null; }
        finally { VariantClear(ref v); }
    }
    static string CurrentName(nint element)
    {
        nint bstr;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(element)[23])(element, &bstr) < 0 || bstr == 0) return ""; // get_CurrentName
        try { return Marshal.PtrToStringBSTR(bstr); } finally { Marshal.FreeBSTR(bstr); }
    }
    // Names of the chosen items of a selection container (current Selection pattern).
    static List<string> SelectionNames(nint element)
    {
        var names = new List<string>();
        var pattern = Pattern(element, SelectionPatternId);
        if (pattern == 0) return names;
        try
        {
            nint array;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(pattern)[3])(pattern, &array) < 0 || array == 0) return names; // GetCurrentSelection
            try
            {
                int length;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(array)[3])(array, &length));
                for (var i = 0; i < length && i < 64; i++)
                {
                    nint item;
                    if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(array)[4])(array, i, &item) < 0 || item == 0) continue;
                    try { names.Add(CurrentName(item)); } finally { Release(item); }
                }
            }
            finally { Release(array); }
        }
        finally { Release(pattern); }
        return names;
    }
    // Text of a document (current Text pattern), bounded to MaxText + 1 characters to report truncation.
    public static string? DocumentText(nint element)
    {
        var pattern = Pattern(element, TextPatternId);
        if (pattern == 0) return null;
        try
        {
            nint range;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(pattern)[7])(pattern, &range) < 0 || range == 0) return null; // get_DocumentRange
            try
            {
                nint bstr;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(range)[12])(range, MaxText + 1, &bstr) < 0) return null; // GetText
                if (bstr == 0) return "";
                try { return Marshal.PtrToStringBSTR(bstr); } finally { Marshal.FreeBSTR(bstr); }
            }
            finally { Release(range); }
        }
        finally { Release(pattern); }
    }

    public static string Role(int controlType) => controlType switch
    {
        50000 => "button", 50001 => "calendar", 50002 => "checkbox", 50003 => "combobox", 50004 => "edit", 50005 => "link",
        50006 => "image", 50007 => "listitem", 50008 => "list", 50009 => "menu", 50010 => "menubar", 50011 => "menuitem",
        50012 => "progressbar", 50013 => "radio", 50014 => "scrollbar", 50015 => "slider", 50016 => "spinner", 50017 => "statusbar",
        50018 => "tab", 50019 => "tab", 50020 => "text", 50021 => "toolbar", 50022 => "tooltip", 50023 => "tree", 50024 => "treeitem",
        50025 => "custom", 50026 => "group", 50027 => "thumb", 50028 => "datagrid", 50029 => "dataitem", 50030 => "document",
        50031 => "splitbutton", 50032 => "window", 50033 => "pane", 50034 => "header", 50035 => "headeritem", 50036 => "table",
        50037 => "titlebar", 50038 => "separator", 50039 => "semanticzoom", 50040 => "appbar",
        _ => "unknown",
    };

    // ---- Tables (read only) --------------------------------------------------------------------------------------------
    // Grid of the element or, failing that, the first one in its subtree (Explorer Details view, DataGridView, WPF
    // DataGrid): headers through TablePattern, cells through GridPattern.GetItem (which also realizes virtualized rows).
    // Text of a cell: its value (DataGridView, Explorer), otherwise its name. Bounded in rows, columns and time; null
    // without a grid.
    public TableRead? ReadTable(nint window, int[] runtimeId, nint hint, int maxRows, int maxColumns = 24, int budgetMs = 3000)
    {
        var element = FindByRuntimeId(window, runtimeId, hint);
        nint grid = 0, table = 0, owner = 0;
        try
        {
            grid = Pattern(element, GridPatternId);
            if (grid == 0)
            {
                var condition = Condition(30030 /* UIA_IsGridPatternAvailablePropertyId */, new Variant { Vt = VT_BOOL, Bool = -1 });
                try { owner = FindFirst(element, condition); } finally { Release(condition); }
                if (owner == 0) return null;
                grid = Pattern(owner, GridPatternId);
                if (grid == 0) return null;
            }
            var rowCount = ReadInt(grid, 4); var columnCount = ReadInt(grid, 5); // get_CurrentRowCount, get_CurrentColumnCount
            var columns = Math.Clamp(columnCount, 0, maxColumns);
            var headers = new List<string>();
            table = Pattern(owner != 0 ? owner : element, TablePatternId);
            if (table != 0) headers = ElementNames(table, 4); // GetCurrentColumnHeaders
            var rows = new List<string[]>();
            var clock = Stopwatch.StartNew();
            for (var r = 0; r < Math.Min(rowCount, maxRows) && clock.ElapsedMilliseconds < budgetMs; r++)
            {
                var cells = new string[columns];
                for (var c = 0; c < columns; c++)
                {
                    nint cell;
                    if (((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Vt(grid)[3])(grid, r, c, &cell) < 0 || cell == 0) { cells[c] = ""; continue; }
                    try { cells[c] = CellText(cell); } finally { Release(cell); }
                }
                rows.Add(cells);
            }
            return new TableRead(headers.Take(columns).ToList(), rows, rowCount, columnCount);
        }
        finally { Release(table); Release(grid); Release(owner); Release(element); }
    }
    static string CellText(nint cell)
    {
        var pattern = CurrentBool(cell, IsPasswordProperty) == true ? 0 : Pattern(cell, ValuePatternId);
        try { if (pattern != 0 && Value(pattern) is { Length: > 0 } value) return value; } finally { Release(pattern); }
        return CurrentName(cell);
    }
    static int? CurrentInt(nint element, int property)
    {
        Variant value;
        if (((delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)Vt(element)[10])(element, property, &value) < 0) return null; // GetCurrentPropertyValue
        try { return value.Vt == VT_I4 ? value.Int : null; } finally { VariantClear(ref value); }
    }
    static bool? CurrentBool(nint element, int property)
    {
        Variant value;
        if (((delegate* unmanaged[Stdcall]<nint, int, Variant*, int>)Vt(element)[10])(element, property, &value) < 0) return null; // GetCurrentPropertyValue
        try { return value.Vt == VT_BOOL ? value.Bool != 0 : null; } finally { VariantClear(ref value); }
    }
    // Names of an IUIAutomationElementArray returned by the method `slot` of a pattern (column headers).
    static List<string> ElementNames(nint pattern, int slot)
    {
        var names = new List<string>();
        nint array;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(pattern)[slot])(pattern, &array) < 0 || array == 0) return names;
        try
        {
            int length;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(array)[3])(array, &length));
            for (var i = 0; i < length && i < 64; i++)
            {
                nint item;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(array)[4])(array, i, &item) < 0 || item == 0) { names.Add(""); continue; }
                try { names.Add(CurrentName(item)); } finally { Release(item); }
            }
        }
        finally { Release(array); }
        return names;
    }

    // ---- Patterns ----------------------------------------------------------------------------------------------------
    // Current pattern (GetCurrentPatternAs) or 0 if it is not supported. To be released.
    public static nint Pattern(nint element, int patternId)
    {
        var iid = patternId switch
        {
            InvokePatternId => IidInvoke, TogglePatternId => IidToggle, ValuePatternId => IidValue,
            SelectionItemPatternId => IidSelectionItem, ExpandCollapsePatternId => IidExpandCollapse,
            SelectionPatternId => IidSelection, TextPatternId => IidText, ScrollPatternId => IidScroll,
            ScrollItemPatternId => IidScrollItem, LegacyIAccessiblePatternId => IidLegacy, GridPatternId => IidGrid, TablePatternId => IidTable,
            _ => throw new ArgumentOutOfRangeException(nameof(patternId)),
        };
        nint pattern;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Vt(element)[14])(element, patternId, &iid, &pattern);
        return hr < 0 ? 0 : pattern;
    }
    // First method of each pattern interface: Invoke, Toggle, SetValue, Select, Expand (slot 3).
    public static void CallSlot(nint pattern, int slot) => Check(((delegate* unmanaged[Stdcall]<nint, int>)Vt(pattern)[slot])(pattern));
    public static double ReadDouble(nint pattern, int slot)
    {
        double value;
        Check(((delegate* unmanaged[Stdcall]<nint, double*, int>)Vt(pattern)[slot])(pattern, &value));
        return value;
    }
    // IUIAutomationScrollPattern::Scroll(horizontalAmount, verticalAmount): ScrollAmount 1 = small step back, 2 = nothing,
    // 4 = small step forward.
    public static void Scroll(nint pattern, int horizontal, int vertical) =>
        Check(((delegate* unmanaged[Stdcall]<nint, int, int, int>)Vt(pattern)[3])(pattern, horizontal, vertical));
    public static int ProcessIdOf(nint element)
    {
        int pid;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(element)[20])(element, &pid) < 0 ? 0 : pid; // get_CurrentProcessId
    }
    public static int ReadInt(nint pattern, int slot)
    {
        int value;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(pattern)[slot])(pattern, &value));
        return value;
    }
    public static void SetValue(nint valuePattern, string text)
    {
        var bstr = Marshal.StringToBSTR(text);
        try { Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Vt(valuePattern)[3])(valuePattern, bstr)); }
        finally { Marshal.FreeBSTR(bstr); }
    }
    public static string? Value(nint valuePattern)
    {
        nint bstr;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(valuePattern)[4])(valuePattern, &bstr) < 0) return null;
        if (bstr == 0) return "";
        try { return Marshal.PtrToStringBSTR(bstr); } finally { Marshal.FreeBSTR(bstr); }
    }
    // Explicit focus (SetFocus) and read-back of the current keyboard focus.
    public static void SetFocus(nint element) => Check(((delegate* unmanaged[Stdcall]<nint, int>)Vt(element)[3])(element));
    public static bool HasKeyboardFocus(nint element)
    {
        int focused;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(element)[26])(element, &focused) >= 0 && focused != 0; // get_CurrentHasKeyboardFocus
    }
    public int ControlTypeOfWindow(nint hwnd)
    {
        var element = ElementFromHandle(hwnd);
        try { return ControlType(element); } finally { Release(element); }
    }
    public static int ControlType(nint element)
    {
        int type;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(element)[21])(element, &type) >= 0 ? type : 0; // get_CurrentControlType
    }

    // Container scrollable on the axis under a point, searched in the UI Automation SUBTREE of the window `hwnd` (never a
    // global hit test at the point: another window, even of the same process, may cover the target; Codex's review,
    // 21/10): visible candidates, of the window's process, whose rectangle contains the point; chosen by ScrollChoice
    // (smallest area, the deepest at equal area, ambiguous = refused). (element, pattern) to be released, or (0, 0)
    // without a candidate. No COM leak on exception. Rectangles in pixels of the calling thread's DPI context.
    public (nint Element, nint Pattern) ScrollContainerAt(nint hwnd, int x, int y, bool vertical)
    {
        nint array;
        var root = ElementFromHandle(hwnd);
        try
        {
            var condition = Condition(30034 /* UIA_IsScrollPatternAvailablePropertyId */, new Variant { Vt = VT_BOOL, Bool = -1 });
            try { Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(root)[6])(root, TreeScopeSubtree, condition, &array)); } // FindAll
            finally { Release(condition); }
        }
        finally { Release(root); }
        if (array == 0) return (0, 0);
        var pid = (int)Win32.ProcessOf(hwnd);
        var candidates = new List<(nint Element, nint Pattern, long Area)>();
        var chosen = -1;
        try
        {
            int length;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(array)[3])(array, &length));
            ScrollChoice.CheckCount(length);
            for (var i = 0; i < length; i++)
            {
                nint element;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(array)[4])(array, i, &element) < 0 || element == 0) continue;
                nint pattern = 0;
                var keep = false;
                try
                {
                    Win32.Rect r;
                    int offscreen;
                    if (ProcessIdOf(element) != pid) continue;
                    if (((delegate* unmanaged[Stdcall]<nint, Win32.Rect*, int>)Vt(element)[43])(element, &r) < 0) continue; // get_CurrentBoundingRectangle
                    if (x < r.Left || y < r.Top || x >= r.Right || y >= r.Bottom) continue;
                    if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(element)[38])(element, &offscreen) < 0 || offscreen != 0) continue; // get_CurrentIsOffscreen
                    pattern = Pattern(element, ScrollPatternId);
                    if (pattern == 0 || ReadInt(pattern, vertical ? 10 : 9) == 0) continue; // get_CurrentVertically/HorizontallyScrollable
                    candidates.Add((element, pattern, (long)r.Width * r.Height));
                    keep = true;
                }
                finally { if (!keep) { Release(pattern); Release(element); } }
            }
            chosen = ScrollChoice.Pick(candidates.Select(c => c.Area).ToList(), (a, b) => IsDescendant(candidates[a].Element, candidates[b].Element));
            return chosen < 0 ? (0, 0) : (candidates[chosen].Element, candidates[chosen].Pattern);
        }
        finally
        {
            Release(array);
            for (var i = 0; i < candidates.Count; i++) if (i != chosen) { Release(candidates[i].Pattern); Release(candidates[i].Element); }
        }
    }

    // Is `child` a strict descendant of `ancestor` (Control view, 64 levels at most)?
    bool IsDescendant(nint child, nint ancestor)
    {
        var current = child;
        var owned = false;
        try
        {
            for (var depth = 0; depth < 64; depth++)
            {
                nint parent = 0;
                if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(walker)[3])(walker, current, &parent) < 0 || parent == 0) return false; // GetParentElement
                if (owned) Release(current);
                current = parent;
                owned = true;
                if (Same(current, ancestor)) return true;
            }
            return false;
        }
        finally { if (owned) Release(current); }
    }

    // Menu item DIRECTLY under `scope` (MenuSearch: without going past another menu item, name and automation id, a
    // complete read or refused), through the Control view. 0 if absent; to be released.
    public nint MenuChild(nint scope, string name, string? automationId)
    {
        var held = new List<nint>();
        nint kept = 0;
        try
        {
            IReadOnlyList<nint> Children(nint parent)
            {
                var list = new List<nint>();
                nint child;
                if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(walker)[4])(walker, parent, &child) < 0) // GetFirstChildElement
                    throw new BackgroundNotApplicableException("Menu could not be read completely: nothing was sent");
                while (child != 0)
                {
                    held.Add(child);
                    list.Add(child);
                    if (held.Count > MenuSearch.Max + 1) throw new BackgroundNotApplicableException("Menu too large to be read completely: nothing was sent");
                    nint next;
                    if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(walker)[6])(walker, child, &next) < 0) // GetNextSiblingElement
                        throw new BackgroundNotApplicableException("Menu could not be read completely: nothing was sent");
                    child = next;
                }
                return list;
            }
            var (found, item) = MenuSearch.Find(scope, Children, e => ControlType(e) == 50011 /* MenuItem */, CurrentName, CurrentAutomationId, name, automationId);
            if (found) kept = item;
            return kept;
        }
        finally { foreach (var element in held) if (element != kept) Release(element); }
    }
    static string CurrentAutomationId(nint element)
    {
        nint bstr;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(element)[29])(element, &bstr) < 0 || bstr == 0) return ""; // get_CurrentAutomationId
        try { return Marshal.PtrToStringBSTR(bstr); } finally { Marshal.FreeBSTR(bstr); }
    }
    public nint WindowElement(nint hwnd) => ElementFromHandle(hwnd);

    // ---- Chromium without activation (shared recipe: NexusClaude act_chromium, NexusAGY _chromium_simulated_typing) ----
    // DOM focus through the MSAA action accSelect(SELFLAG_TAKEFOCUS): unlike UIA SetFocus, it does not activate the window.
    public static void TakeFocus(nint element)
    {
        var pattern = Pattern(element, LegacyIAccessiblePatternId);
        if (pattern == 0) throw new BackgroundNotApplicableException("No MSAA action to focus without activation");
        try { Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Vt(pattern)[3])(pattern, 1 /* SELFLAG_TAKEFOCUS */)); } // Select
        finally { Release(pattern); }
    }

    // Caret at the end of the text (an empty range at the end of the element's document, selected): typing is appended.
    public static void CaretToEnd(nint element)
    {
        var pattern = Pattern(element, TextPatternId);
        if (pattern == 0) throw new BackgroundNotApplicableException("No text pattern to place the caret: nothing was typed");
        nint range = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(pattern)[7])(pattern, &range)); // get_DocumentRange
            Check(((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)Vt(range)[15])(range, 0 /* Start */, range, 1 /* End */)); // MoveEndpointByRange
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Vt(range)[16])(range)); // Select
        }
        finally { Release(range); Release(pattern); }
    }

    public string LastPublication { get; private set; } = "not-requested";

    // A covered Chromium delays the UIA publication of its changes. ScrollIntoView of its RootWebArea viewport makes it
    // publish what is pending, without targeting a control or an iframe document that would scroll the page. Only the
    // parents of documents are walked; several top-level web views are handled.
    // The emergency stop keeps the possible reads, but forbids this pattern. Publication failures are not read
    // failures: the diagnostic only says whether the request went through, never that the effect is proven.
    public string Flush(nint hwnd)
    {
        if (!ChromiumInput.HasChromiumContent(hwnd)) return "not-chromium";
        if (ChromiumInput.InForeground(hwnd)) return "foreground";
        if (EmergencyStop.RemainingSeconds() > 0) return "stopped";
        var pending = new Stack<(nint Element, int Depth)>();
        var visited = 0;
        var documents = 0;
        var requested = 0;
        var failed = false;
        try
        {
            nint root;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vt(automation)[10])(automation, hwnd, elementRequest, &root));
            if (root == 0) return "unavailable";
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                var (element, depth) = pending.Pop();
                try
                {
                    if (++visited > MaxNodes || depth > MaxDepth) { failed = true; break; }
                    if (CachedInt(element, ControlTypeProperty) == 50030 /* Document */)
                    {
                        // Any document bounds the walk, a non-web document included: never an iframe.
                        if (CachedString(element, FrameworkIdProperty) != "Chrome" ||
                            !string.Equals(CachedString(element, AutomationIdProperty), "RootWebArea", StringComparison.OrdinalIgnoreCase)) continue;
                        documents++;
                        // A hidden view, or one without a visible area, is never brought into view for a mere read.
                        if (CachedBool(element, IsOffscreenProperty) == true || Bounds(element).Length != 4) { failed = true; continue; }
                        if (EmergencyStop.RemainingSeconds() > 0) return "stopped";
                        if (ChromiumInput.InForeground(hwnd)) return "foreground";
                        var pattern = Pattern(element, ScrollItemPatternId);
                        if (pattern == 0) { failed = true; continue; }
                        try { CallSlot(pattern, 3); requested++; } // ScrollIntoView on the viewport only
                        finally { Release(pattern); }
                        continue;
                    }
                    var children = Children(element);
                    for (var i = children.Count - 1; i >= 0; i--) pending.Push((children[i], depth + 1));
                }
                finally { Release(element); }
            }
            return failed ? "unavailable" : documents == 0 ? "no-document" : requested == documents ? "requested" : "unavailable";
        }
        catch (Exception error) when (error is COMException or InvalidOperationException) { return "unavailable"; }
        finally { while (pending.Count > 0) Release(pending.Pop().Element); }
    }

    // Selectable elements (SelectionItem pattern) under a container, with their name, in one round trip. To be released.
    public List<(nint Element, string Name)> SelectableItems(nint container)
    {
        var condition = Condition(IsSelectionItemAvailable, new Variant { Vt = VT_BOOL, Bool = -1 });
        nint array;
        try { Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint, nint*, int>)Vt(container)[8])(container, 4 /* Descendants */, condition, elementRequest, &array)); } // FindAllBuildCache
        finally { Release(condition); }
        var items = new List<(nint, string)>();
        if (array == 0) return items;
        try
        {
            int length;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(array)[3])(array, &length));
            for (var i = 0; i < length && i < 5000; i++)
            {
                nint item;
                Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(array)[4])(array, i, &item));
                if (item != 0) items.Add((item, CachedString(item, NameProperty) ?? ""));
            }
        }
        finally { Release(array); }
        return items;
    }
}

sealed record TableRead(List<string> Headers, List<string[]> Rows, int RowCount, int ColumnCount);

// Node of the tree read: cached properties, plus the COM element (released with the tree) for current reads.
sealed class Node
{
    public nint Element;
    public int Index, Depth, ProcessId;
    public required string Role, Name, AutomationId, Description;
    public string FrameworkId = "";
    public string? Value;
    // Offscreen: UIA IsOffscreen (scrolled out of the visible part, minimized window...), reported to the session.
    public bool Disabled, Focused, Password, ValueTruncated, HasInvoke, HasToggle, HasText, Offscreen, IsDefault;
    // Unreadable: the provider failed to describe this element's children (TreeReadFallback); the subtree is missing.
    public bool Unreadable;
    public string AccessKey = "";
    public bool? Selected, Checked, Expanded;
    public nint NativeWindow;
    public required int[] RuntimeId, Ancestors;
    public required double[] Bounds;
    public List<string> Actions = new();
}

sealed class UiTree : IDisposable
{
    public readonly List<Node> Nodes = new();
    // Parents added before their children. An element without a FrameworkId (MSAA child of a Win32 list served by the
    // native proxy) inherits its parent's: the interface family decides the technique (never SelectionItem.Select on a
    // Win32/WinForms list, the selection would change there without notifying the application).
    public Node Add(Node node)
    {
        node.Index = Nodes.Count;
        if (node.FrameworkId.Length == 0 && node.Ancestors.Length > 0) node.FrameworkId = Nodes[node.Ancestors[^1]].FrameworkId;
        Nodes.Add(node);
        return node;
    }
    public void Dispose()
    {
        foreach (var node in Nodes) { ComUia.Release(node.Element); node.Element = 0; }
        Nodes.Clear();
    }
}

// Provider faults while UI Automation builds a cached tree: the whole-subtree request is replaced by a read level by
// level, and a single element whose children cannot be read is kept without them. Only provider failures (COM errors,
// malformed cache responses surfacing as argument or index errors) are absorbed; the engine's own refusals, bounds and
// emergency stop (InvalidOperationException family) pass through unchanged.
static class TreeReadFallback
{
    public static bool IsProviderFault(Exception error) => error is ExternalException or ArgumentException or IndexOutOfRangeException;
    public static int Fallbacks;
    public static UiTree Read(Func<UiTree> subtree, Func<UiTree> levels)
    {
        try { return subtree(); }
        catch (Exception error) when (IsProviderFault(error))
        {
            Interlocked.Increment(ref Fallbacks);
            return levels();
        }
    }
    public static List<nint> ChildrenOrNone(Func<List<nint>> read, out bool unreadable)
    {
        unreadable = false;
        try { return read(); }
        catch (Exception error) when (IsProviderFault(error)) { unreadable = true; return []; }
    }
}

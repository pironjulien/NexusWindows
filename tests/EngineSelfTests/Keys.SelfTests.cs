// Offline self-checks of commun/engine/Keys.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Runtime.InteropServices;
using static Keys;

static partial class KeysSelfTests
{
    internal static void CheckPhysicalGrammar()
    {
        var layout = GetKeyboardLayout(0);
        if (!ParseFor("alt+f4", layout).Mods.Contains(0x12) || !ParseFor("win+r", layout).Mods.Contains(0x5B)) throw new InvalidOperationException("Physical system modifier parsing failed");
        try { Parse("alt+f4", layout); throw new InvalidOperationException("Background Alt unexpectedly accepted"); }
        catch (BackgroundNotApplicableException) { }
    }

    internal static int KeyGrammarAssertions()
    {
        var count = 0;
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException("Key grammar: " + why); count++; }
        short Scan(char ch) => ch switch { '+' => 0x1BB, 'a' => 0x41, '1' => 0x131, '#' => 0x6DC, _ => -1 };
        (List<int> Mods, int Vk) Read(string spec, bool physical = false) => ParseCore(spec, physical, Scan);
        void Refused(string spec, bool physical = false)
        {
            try { Read(spec, physical); } catch (BackgroundNotApplicableException) { count++; return; }
            throw new InvalidOperationException("Key grammar accepted " + spec);
        }
        foreach (var physical in new[] { false, true })
        {
            for (var f = 1; f <= 24; f++) Check(Read("f" + f, physical).Vk == 0x6F + f, "F1-F24");
            foreach (var pair in new Dictionary<string, int> { ["apps"] = 0x5D, ["printscreen"] = 0x2C, ["pause"] = 0x13, ["capslock"] = 0x14,
                ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE })
                Check(Read(pair.Key, physical).Vk == pair.Value, "named canonical key");
            var plus = Read("+", physical); Check(plus.Vk == 0xBB && plus.Mods.SequenceEqual(new[] { VK_SHIFT }), "literal plus uses injected target layout");
            var ctrlPlus = Read("ctrl++", physical); Check(ctrlPlus.Vk == 0xBB && ctrlPlus.Mods.SequenceEqual(new[] { VK_CONTROL, VK_SHIFT }), "final ++ means plus key");
            var dedup = Read("ctrl+Control_L+shift+shift+a", physical); Check(dedup.Mods.SequenceEqual(new[] { VK_CONTROL, VK_SHIFT }), "duplicate modifiers send once");
            Check(Read("ctrl", physical).Vk == VK_CONTROL && Read("ctrl", physical).Mods.Count == 0, "control alone");
            Check(Read("shift", physical).Vk == VK_SHIFT && Read("shift", physical).Mods.Count == 0, "shift alone");
            Check(Read("1", physical).Mods.SequenceEqual(new[] { VK_SHIFT }), "layout-dependent digit keeps implicit shift");
            foreach (var malformed in new[] { "", "ctrl+", "ctrl+shift+", "++", "ctrl+++", "ctrl++a", "+a", "a+enter", "ctrl+shift", "f0", "f25", "ctrl+f25", "#" }) Refused(malformed, physical);
        }
        foreach (var system in new[] { "alt", "alt_l", "alt_r", "win", "super", "meta", "windows", "alt+f4", "win+a", "ctrl+win+a" })
        {
            Refused(system);
            Check(Read(system, true).Vk != 0, "physical system key remains available");
        }
        Check(IsExtended(Read("apps").Vk), "apps carries the extended flag");
        Check(Read("menu").Vk == 0x5D, "named menu aliases apps");
        return count;
    }
}

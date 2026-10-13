using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace SearchLayout
{
    internal static class KeyboardLayoutTests
    {
        static readonly List<object> checks = new List<object>();
        static int failures;
        static void Check(string name, bool passed, string detail = null) { checks.Add(new { name, passed, detail }); if (!passed) failures++; }
        static PhysicalKeyCell Cell(uint scan, string text, PhysicalModifiers modifiers = PhysicalModifiers.None, bool dead = false) { return new PhysicalKeyCell(scan, modifiers, text, dead); }
        static KeyboardLayoutInfo Layout(long id, string tag, params PhysicalKeyCell[] cells) { return new KeyboardLayoutInfo(new IntPtr(id), tag, KeyboardProfileKind.KeyboardLayout, false, cells); }
        static void Converts(string name, KeyboardLayoutPair pair, string from, string to)
        {
            string actual; LayoutMappingFailure reason; bool converted = pair.TryConvert(from, out actual, out reason);
            Check(name, converted && actual == to, "actual=" + actual + "; reason=" + reason);
        }
        static void Refuses(string name, KeyboardLayoutPair pair, string value, LayoutMappingFailure expected)
        {
            string actual; LayoutMappingFailure reason; bool converted = pair.TryConvert(value, out actual, out reason);
            Check(name, !converted && actual == null && reason == expected, reason.ToString());
        }
        static object PairEvidence(KeyboardLayoutPair pair)
        {
            var refused = new List<object>();
            foreach (string value in pair.Source.Cells.Where(c => c.Simple).Select(c => c.Text).Distinct())
            {
                string converted; LayoutMappingFailure failure;
                if (!pair.TryConvert(value, out converted, out failure)) refused.Add(new { character = value, code = "U+" + ((int)value[0]).ToString("X4"), reason = failure.ToString() });
            }
            return new { source = pair.Source.Id, target = pair.Target.Id, pair.MappedCharacters, pair.RefusedCharacters, refused };
        }
        static void Fixtures()
        {
            var en = Layout(0x409, "en-US", Cell(1, "c"), Cell(2, "h"), Cell(1, "C", PhysicalModifiers.Shift), Cell(2, "H", PhysicalModifiers.Shift));
            var ru = Layout(0x419, "ru-RU", Cell(1, "с"), Cell(2, "р"), Cell(1, "С", PhysicalModifiers.Shift), Cell(2, "Р", PhysicalModifiers.Shift));
            var pair = new KeyboardLayoutPair(ru, en);
            Converts("Fixture direct RU to EN", pair, "ср", "ch");
            Converts("Fixture Shift preserved", pair, "Ср", "Ch");
            Converts("Fixture reverse", new KeyboardLayoutPair(en, ru), "cH", "сР");
            Refuses("Mixed script is not partially rewritten", pair, "сh", LayoutMappingFailure.UnknownCharacter);
            Refuses("Unknown punctuation is not silently preserved", pair, "с?", LayoutMappingFailure.UnknownCharacter);
            Refuses("Surrogates are not split", pair, "😀", LayoutMappingFailure.DeadKeyOrComposition);
            var de = Layout(0x407, "de-DE", Cell(3, "z"), Cell(4, "y"));
            var enLatin = Layout(0x409, "en-US", Cell(3, "y"), Cell(4, "z"));
            Converts("Same-script physical mapping", new KeyboardLayoutPair(de, enLatin), "zy", "yz");
            var altSource = Layout(0x407, "fixture", Cell(5, "€", PhysicalModifiers.AltGr), Cell(5, "Ω", PhysicalModifiers.AltGr | PhysicalModifiers.Shift));
            var altTarget = Layout(0x409, "fixture", Cell(5, "@", PhysicalModifiers.AltGr), Cell(5, "#", PhysicalModifiers.AltGr | PhysicalModifiers.Shift));
            Converts("AltGr keeps modifier state", new KeyboardLayoutPair(altSource, altTarget), "€", "@");
            Converts("Shift AltGr keeps both modifiers", new KeyboardLayoutPair(altSource, altTarget), "Ω", "#");
            Refuses("Missing target AltGr does not fall back to base", new KeyboardLayoutPair(altSource, enLatin), "€", LayoutMappingFailure.UnknownCharacter);
            var duplicated = Layout(0x407, "fixture", Cell(1, "x"), Cell(2, "x"));
            Refuses("Two physical keys with different outputs are ambiguous", new KeyboardLayoutPair(duplicated, en), "x", LayoutMappingFailure.AmbiguousCharacter);
            var duplicateSameTarget = Layout(0x409, "fixture", Cell(1, "a"), Cell(2, "a"));
            Converts("Duplicate physical keys with identical output agree", new KeyboardLayoutPair(duplicated, duplicateSameTarget), "x", "a");
            var deadSource = Layout(0x407, "fixture", Cell(1, "´", PhysicalModifiers.None, true));
            Refuses("Source dead key is excluded", new KeyboardLayoutPair(deadSource, en), "´", LayoutMappingFailure.DeadKeyOrComposition);
            var deadTarget = Layout(0x409, "fixture", Cell(1, "`", PhysicalModifiers.None, true));
            Refuses("Target dead key is excluded", new KeyboardLayoutPair(ru, deadTarget), "с", LayoutMappingFailure.DeadKeyOrComposition);
            var ligature = Layout(0x407, "fixture", Cell(1, "ab"), Cell(2, "a"), Cell(3, "b"));
            Refuses("Source ligature cannot be mistaken for two direct keys", new KeyboardLayoutPair(ligature, en), "ab", LayoutMappingFailure.DeadKeyOrComposition);
            var composed = Layout(0x407, "fixture", Cell(1, "e"), Cell(2, "\u0301"));
            Refuses("Combining sequence is excluded", new KeyboardLayoutPair(composed, en), "e\u0301", LayoutMappingFailure.DeadKeyOrComposition);
            var ime = new KeyboardLayoutInfo(new IntPtr(0x411), "ja-JP", KeyboardProfileKind.InputProcessorOrSubstituted, true, en.Cells);
            Refuses("IME is excluded even with ASCII fallback cells", new KeyboardLayoutPair(ime, en), "ch", LayoutMappingFailure.UnsupportedInputMethod);
            var unknown = new KeyboardLayoutInfo(new IntPtr(0x412), "ko-KR", KeyboardProfileKind.Unknown, true, en.Cells);
            Refuses("Unclassified profile is excluded", new KeyboardLayoutPair(unknown, en), "ch", LayoutMappingFailure.UnsupportedInputMethod);
            var catalog = new KeyboardLayoutCatalog(new[] { en, ru, de, ime });
            Check("Only non-IME English pairs are created", catalog.EnglishPairs.Count == 4 && catalog.EnglishPairs.All(p => p.Source.IsEnglish != p.Target.IsEnglish));
            KeyboardLayoutCandidate candidate; LayoutMappingFailure failure;
            Check("Catalog predicate selects query independently of app count", catalog.TryUniqueCandidate("ср", s => s == "ch", out candidate, out failure) && candidate.Value == "ch");
            Check("Catalog predicate preserves unmatched query", !catalog.TryUniqueCandidate("ср", s => false, out candidate, out failure));
            var uk = Layout(0x422, "uk-UA", Cell(1, "с"), Cell(2, "р"));
            var ambiguous = new KeyboardLayoutCatalog(new[] { en, ru, uk });
            Check("Equal prefix with two physical routes remains explicit", !ambiguous.TryUniqueCandidate("ср", s => s == "ch", out candidate, out failure) && failure == LayoutMappingFailure.AmbiguousLayouts && ambiguous.Candidates("ср", s => s == "ch").Count == 2);
            Check("Active Russian layout resolves shared Cyrillic prefix", ambiguous.TryUniqueCandidate("ср", s => s == "ch", ru.Handle, out candidate, out failure) && candidate.Pair.Source == ru);
            Check("Active Ukrainian layout resolves shared Cyrillic prefix", ambiguous.TryUniqueCandidate("ср", s => s == "ch", uk.Handle, out candidate, out failure) && candidate.Pair.Source == uk);
            Check("Clipboard ambiguity remains when active English cannot identify source", !ambiguous.TryUniqueCandidate("ср", s => s == "ch", en.Handle, out candidate, out failure) && failure == LayoutMappingFailure.AmbiguousLayouts);
        }
        [MTAThread]
        public static int Main(string[] args)
        {
            object inventory = null; long elapsed = 0;
            try
            {
                Fixtures();
                var before = KeyboardLayoutNative.AvailableHandles().Select(h => h.ToInt64()).OrderBy(h => h).ToArray();
                var active = KeyboardLayoutNative.GetKeyboardLayout(0);
                var timer = Stopwatch.StartNew(); var catalog = KeyboardLayoutCatalog.ReadAvailable(); timer.Stop(); elapsed = timer.ElapsedMilliseconds;
                var after = KeyboardLayoutNative.AvailableHandles().Select(h => h.ToInt64()).OrderBy(h => h).ToArray();
                Check("Real available layout set is unchanged", before.SequenceEqual(after));
                Check("Calling thread layout is unchanged", active == KeyboardLayoutNative.GetKeyboardLayout(0));
                Check("Read-only API mapping runs on MTA", Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA);
                inventory = new { layouts = catalog.Layouts.Select(l => new { l.Id, l.LanguageTag, profileKind = l.ProfileKind.ToString(), l.ImmReportsIme, l.Supported, cells = l.Cells.Count, direct = l.Cells.Count(c => c.Simple), l.DeadKeyCells, l.ComplexCells }).ToArray(), pairs = catalog.EnglishPairs.Select(PairEvidence).ToArray() };
                var en = catalog.Layouts.SingleOrDefault(l => l.Handle.ToInt64() == 0x04090409);
                var ru = catalog.Layouts.SingleOrDefault(l => l.Handle.ToInt64() == 0x04190419);
                Check("Actual en-US and ru-RU ordinary profiles available", en != null && ru != null && en.Supported && ru.Supported);
                if (en != null && ru != null && en.Supported && ru.Supported)
                {
                    var forward = new KeyboardLayoutPair(ru, en); var reverse = new KeyboardLayoutPair(en, ru);
                    Converts("Actual ср to ch", forward, "ср", "ch");
                    Converts("Actual срфепзе to chatgpt", forward, "срфепзе", "chatgpt");
                    Converts("Actual сзг-я to cpu-z", forward, "сзг-я", "cpu-z");
                    Converts("Actual rfkmrekznjh to калькулятор", reverse, "rfkmrekznjh", "калькулятор");
                    Converts("Actual shifted word", forward, "СРКЩЬУ", "CHROME");
                    Converts("Actual shifted number symbol", forward, "№", "#");
                    Refuses("Actual mixed Cyrillic Latin query", forward, "сh", LayoutMappingFailure.UnknownCharacter);
                    KeyboardLayoutCandidate candidate; LayoutMappingFailure failure;
                    Check("Actual prefix predicate returns one route", catalog.TryUniqueCandidate("ср", text => text == "ch", out candidate, out failure) && candidate.Value == "ch");
                }
            }
            catch (Exception e) { Check("Read-only tests complete", false, e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
            var report = new { timestamp = DateTimeOffset.Now.ToString("o"), layer = "Read-only Windows input-layout APIs plus synthetic mapping fixtures. No Search, UI, hooks, layout loading, activation or registration.", failures, total = checks.Count, readMilliseconds = elapsed, inventory, checks, notVerified = new[] { "No additional languages are installed here; other layouts, AltGr, dead keys and IME use synthetic fixtures only.", "Composed text and IME are unsupported. Direct characters do not reveal whether they were previously composed.", "Foreground input-service/composition state is not detected by this mapping module." } };
            string json = new JavaScriptSerializer { MaxJsonLength = 2000000 }.Serialize(report);
            if (args.Length > 0) File.WriteAllText(Path.GetFullPath(args[0]), json, new System.Text.UTF8Encoding(false));
            Console.WriteLine(json); return failures == 0 ? 0 : 1;
        }
    }
}

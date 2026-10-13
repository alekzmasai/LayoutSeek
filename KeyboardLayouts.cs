using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SearchLayout
{
    [Flags] public enum PhysicalModifiers { None = 0, Shift = 1, AltGr = 2 }
    public enum LayoutMappingFailure { None, MissingLayout, UnsupportedInputMethod, UnknownCharacter, AmbiguousCharacter, DeadKeyOrComposition, AmbiguousLayouts }
    public enum KeyboardProfileKind { KeyboardLayout, InputProcessorOrSubstituted, Unknown }

    public sealed class PhysicalKeyCell
    {
        public readonly uint ScanCode;
        public readonly PhysicalModifiers Modifiers;
        public readonly string Text;
        public readonly bool Dead;
        public PhysicalKeyCell(uint scanCode, PhysicalModifiers modifiers, string text, bool dead = false)
        { ScanCode = scanCode; Modifiers = modifiers; Text = text ?? ""; Dead = dead; }
        internal int Position { get { return ((int)ScanCode << 2) | (int)Modifiers; } }
        internal bool Simple { get { return !Dead && Text.Length == 1 && IsSimple(Text[0]); } }
        internal static bool IsSimple(char c)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            return !char.IsControl(c) && !char.IsSurrogate(c) && category != UnicodeCategory.NonSpacingMark && category != UnicodeCategory.SpacingCombiningMark && category != UnicodeCategory.EnclosingMark && category != UnicodeCategory.Format;
        }
    }
    public sealed class KeyboardLayoutInfo
    {
        public readonly string Id, LanguageTag;
        public readonly IntPtr Handle;
        public readonly KeyboardProfileKind ProfileKind;
        public readonly bool ImmReportsIme;
        public readonly ReadOnlyCollection<PhysicalKeyCell> Cells;
        public bool IsEnglish { get { return (Handle.ToInt64() & 0x3ff) == 9; } }
        public bool Supported { get { return ProfileKind == KeyboardProfileKind.KeyboardLayout; } }
        public int DeadKeyCells { get { return Cells.Count(c => c.Dead); } }
        public int ComplexCells { get { return Cells.Count(c => !c.Dead && c.Text.Length > 0 && !c.Simple); } }
        internal KeyboardLayoutInfo(IntPtr handle, string languageTag, KeyboardProfileKind profileKind, bool immReportsIme, IEnumerable<PhysicalKeyCell> cells)
        {
            Handle = handle; Id = handle.ToInt64().ToString("X16"); LanguageTag = languageTag;
            ProfileKind = profileKind; ImmReportsIme = immReportsIme;
            Cells = new ReadOnlyCollection<PhysicalKeyCell>(cells.ToArray());
        }
    }
    // One direct physical keystroke per UTF-16 character. No transliteration or dictionary.
    public sealed class KeyboardLayoutPair
    {
        public readonly KeyboardLayoutInfo Source, Target;
        readonly Dictionary<char, char> map = new Dictionary<char, char>();
        readonly Dictionary<char, LayoutMappingFailure> refused = new Dictionary<char, LayoutMappingFailure>();
        readonly string[] complexSource;
        public int MappedCharacters { get { return map.Count; } }
        public int RefusedCharacters { get { return refused.Count; } }
        public KeyboardLayoutPair(KeyboardLayoutInfo source, KeyboardLayoutInfo target)
        {
            Source = source; Target = target;
            var targetCells = target.Cells.ToDictionary(c => c.Position);
            complexSource = source.Cells.Where(c => c.Text.Length > 0 && !c.Simple).Select(c => c.Text).Distinct().ToArray();
            foreach (var group in source.Cells.Where(c => c.Simple).GroupBy(c => c.Text[0]))
            {
                var outputs = new HashSet<char>();
                LayoutMappingFailure failure = LayoutMappingFailure.None;
                foreach (var cell in group)
                {
                    PhysicalKeyCell to;
                    if (!targetCells.TryGetValue(cell.Position, out to) || to.Text.Length == 0)
                    { failure = LayoutMappingFailure.UnknownCharacter; break; }
                    if (!to.Simple) { failure = LayoutMappingFailure.DeadKeyOrComposition; break; }
                    outputs.Add(to.Text[0]);
                }
                if (failure == LayoutMappingFailure.None && outputs.Count == 1) map[group.Key] = outputs.Single();
                else refused[group.Key] = failure != LayoutMappingFailure.None ? failure : LayoutMappingFailure.AmbiguousCharacter;
            }
        }
        public bool TryConvert(string value, out string converted, out LayoutMappingFailure failure)
        {
            converted = null; failure = LayoutMappingFailure.None;
            if (!Source.Supported || !Target.Supported) { failure = LayoutMappingFailure.UnsupportedInputMethod; return false; }
            if (value == null) { failure = LayoutMappingFailure.UnknownCharacter; return false; }
            if (complexSource.Any(sequence => value.IndexOf(sequence, StringComparison.Ordinal) >= 0))
            { failure = LayoutMappingFailure.DeadKeyOrComposition; return false; }
            var result = new char[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                if (!map.TryGetValue(value[i], out result[i]))
                {
                    if (!refused.TryGetValue(value[i], out failure)) failure = PhysicalKeyCell.IsSimple(value[i]) ? LayoutMappingFailure.UnknownCharacter : LayoutMappingFailure.DeadKeyOrComposition;
                    return false;
                }
            }
            converted = new string(result); return true;
        }
    }
    public sealed class KeyboardLayoutCandidate
    {
        public readonly KeyboardLayoutPair Pair;
        public readonly string Value;
        internal KeyboardLayoutCandidate(KeyboardLayoutPair pair, string value) { Pair = pair; Value = value; }
    }
    public sealed class KeyboardLayoutCatalog
    {
        public readonly ReadOnlyCollection<KeyboardLayoutInfo> Layouts;
        public readonly ReadOnlyCollection<KeyboardLayoutPair> EnglishPairs;
        internal KeyboardLayoutCatalog(IEnumerable<KeyboardLayoutInfo> layouts)
        {
            Layouts = new ReadOnlyCollection<KeyboardLayoutInfo>(layouts.ToArray());
            EnglishPairs = new ReadOnlyCollection<KeyboardLayoutPair>((from source in Layouts from target in Layouts where source.Supported && target.Supported && source.IsEnglish != target.IsEnglish select new KeyboardLayoutPair(source, target)).ToArray());
        }
        public ReadOnlyCollection<KeyboardLayoutCandidate> Candidates(string value, Func<string, bool> acceptedPrefix)
        {
            var results = new List<KeyboardLayoutCandidate>();
            foreach (var pair in EnglishPairs)
            {
                string mapped; LayoutMappingFailure failure;
                if (pair.TryConvert(value, out mapped, out failure) && mapped != value && (acceptedPrefix == null || acceptedPrefix(mapped))) results.Add(new KeyboardLayoutCandidate(pair, mapped));
            }
            return new ReadOnlyCollection<KeyboardLayoutCandidate>(results);
        }
        // A unique string is not a unique physical route: a continuation needs a definite source and target.
        public bool TryUniqueCandidate(string value, Func<string, bool> acceptedPrefix, out KeyboardLayoutCandidate candidate, out LayoutMappingFailure failure)
        { return TryUniqueCandidate(value, acceptedPrefix, IntPtr.Zero, out candidate, out failure); }
        public bool TryUniqueCandidate(string value, Func<string, bool> acceptedPrefix, IntPtr activeLayout, out KeyboardLayoutCandidate candidate, out LayoutMappingFailure failure)
        {
            var candidates = Candidates(value, acceptedPrefix); candidate = null;
            if (candidates.Count > 1 && activeLayout != IntPtr.Zero)
            {
                var active = candidates.Where(c => c.Pair.Source.Handle == activeLayout).ToArray();
                if (active.Length > 0) candidates = new ReadOnlyCollection<KeyboardLayoutCandidate>(active);
            }
            failure = candidates.Count == 0 ? LayoutMappingFailure.MissingLayout : candidates.Count > 1 ? LayoutMappingFailure.AmbiguousLayouts : LayoutMappingFailure.None;
            if (failure != LayoutMappingFailure.None) return false;
            candidate = candidates[0]; return true;
        }
        public static KeyboardLayoutCatalog ReadAvailable()
        {
            IntPtr[] handles = KeyboardLayoutNative.AvailableHandles();
            var results = new List<KeyboardLayoutInfo>();
            object profilesObject = null;
            KeyboardLayoutNative.IProfileReader profiles = null;
            try
            {
                try { profilesObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("33c53a50-f456-4884-b049-85fd643ecfed"))); profiles = (KeyboardLayoutNative.IProfileReader)profilesObject; }
                catch (COMException) { /* Unknown profiles remain unavailable for correction. */ }
                foreach (IntPtr handle in handles)
                {
                    var kind = KeyboardProfileKind.Unknown;
                    if (profiles != null)
                    {
                        var nil = Guid.Empty; KeyboardLayoutNative.Profile profile;
                        int hr = profiles.GetProfile(2, (ushort)(handle.ToInt64() & 0xffff), ref nil, ref nil, handle, out profile);
                        if (hr == 0 && profile.Type == 2 && profile.Handle == handle)
                            kind = (profile.Flags & 4) == 0 ? KeyboardProfileKind.KeyboardLayout : KeyboardProfileKind.InputProcessorOrSubstituted;
                    }
                    string language;
                    try { language = CultureInfo.GetCultureInfo((int)(handle.ToInt64() & 0xffff)).Name; }
                    catch (CultureNotFoundException) { language = "und"; }
                    bool ime = KeyboardLayoutNative.ImmIsIME(handle) != 0;
                    var cells = kind == KeyboardProfileKind.KeyboardLayout ? KeyboardLayoutNative.ReadCells(handle) : new PhysicalKeyCell[0];
                    results.Add(new KeyboardLayoutInfo(handle, language, kind, ime, cells));
                }
            }
            finally { if (profilesObject != null) Marshal.FinalReleaseComObject(profilesObject); }
            return new KeyboardLayoutCatalog(results);
        }
    }
    internal static class KeyboardLayoutNative
    {
        [DllImport("user32.dll", SetLastError = true)] static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] handles);
        [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint key, uint scan, byte[] state, [Out] char[] buffer, int size, uint flags, IntPtr layout);
        [DllImport("imm32.dll", ExactSpelling = true)] internal static extern int ImmIsIME(IntPtr layout);
        internal static IntPtr[] AvailableHandles()
        {
            int count = GetKeyboardLayoutList(0, null);
            if (count <= 0) throw new InvalidOperationException("Windows returned no available input locales.");
            var handles = new IntPtr[count + 8];
            count = GetKeyboardLayoutList(handles.Length, handles);
            if (count <= 0) throw new InvalidOperationException("Windows input locale enumeration failed.");
            return handles.Take(count).Distinct().ToArray();
        }
        internal static PhysicalKeyCell[] ReadCells(IntPtr handle)
        {
            var cells = new List<PhysicalKeyCell>();
            for (uint scan = 1; scan < 128; scan++)
            {
                uint key = MapVirtualKeyEx(scan, 3, handle);
                if (key == 0 || key >= 0x60 && key <= 0x6f) continue; // Numpad/NumLock is outside this direct typing map.
                for (int modifiers = 0; modifiers < 4; modifiers++)
                {
                    var state = new byte[256];
                    if ((modifiers & 1) != 0) state[0x10] = state[0xa0] = 0x80;
                    if ((modifiers & 2) != 0) { state[0x11] = state[0xa2] = 0x80; state[0x12] = state[0xa5] = 0x80; }
                    var buffer = new char[16];
                    // Bit 2, available since Windows 10 1607, explicitly prevents keyboard-state mutation.
                    int length = ToUnicodeEx(key, scan, state, buffer, buffer.Length, 4, handle);
                    int copied = Math.Min(Math.Abs(length), buffer.Length);
                    string text = copied > 0 ? new string(buffer, 0, copied) : "";
                    if (length >= 0 && text.Any(char.IsControl)) text = "";
                    cells.Add(new PhysicalKeyCell(scan, (PhysicalModifiers)modifiers, text, length < 0));
                }
            }
            return cells.ToArray();
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Profile
        {
            public uint Type; public ushort Language;
            public Guid Clsid, GuidProfile, Category;
            public IntPtr Substitute; public uint Capabilities; public IntPtr Handle; public uint Flags;
        }
        // Only the third, read-only vtable entry is called. No activation/registration APIs are used.
        [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IProfileReader
        {
            void UnusedActivateSlot();
            void UnusedDeactivateSlot();
            [PreserveSig] int GetProfile(uint type, ushort language, ref Guid clsid, ref Guid profileId, IntPtr handle, out Profile profile);
        }
    }
}

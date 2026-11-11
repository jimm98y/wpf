// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Spell checking off Windows: SpellCheck.IsEnabled with the OS's own spelling, where Windows uses its spell checking
// service (WinRTSpellerInterop) -- macOS's NSSpellChecker (the languages the user has, the words the user learned),
// iOS's and visionOS's UITextChecker (the keyboard's dictionaries),
// and on Linux the desktop's Enchant (Hunspell's dictionaries, as GNOME's and KDE's editors use them), when it is
// installed: it is the system's, never carried with the app.
//
// The text is broken into words here (letters and digits, an apostrophe inside a word kept), the whole text one
// sentence as WinRTSpellerInterop also treats it; each word is asked of the engine, and its suggestions only when
// the context menu wants them. Custom dictionaries (SpellCheck.CustomDictionaries) are word lists, as on Windows:
// their words are spelled right.
//

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;

namespace System.Windows.Documents
{
    internal sealed class PlatformSpellerInterop : SpellerInteropBase
    {
        /// <summary>The OS's spelling, where it has some; null where it has none (no Enchant on a Linux machine).</summary>
        internal static PlatformSpellerInterop TryCreate()
        {
            SpellingEngine engine = OperatingSystem.IsMacOS() ? MacSpelling.TryCreate()
                : OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ? UIKitSpelling.TryCreate()
                : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? EnchantSpelling.TryCreate()
                : null;
            return engine == null ? null : new PlatformSpellerInterop(engine);
        }

        private PlatformSpellerInterop(SpellingEngine engine)
        {
            _engine = engine;
            _culture = CultureInfo.CurrentUICulture;
        }

        public override void Dispose() => Dispose(true);

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _engine.Dispose();
        }

        internal override void SetLocale(CultureInfo culture) => _culture = culture;

        internal override SpellerMode Mode { set => _mode = value; }

        internal override bool MultiWordMode { set { } }

        internal override void SetReformMode(CultureInfo culture, SpellingReform spellingReform) { }

        internal override bool CanSpellCheck(CultureInfo culture) => !_disposed && _engine.Supports(culture);

        internal override int EnumTextSegments(char[] text, int count, EnumSentencesCallback sentenceCallback, EnumTextSegmentsCallback segmentCallback, object data)
        {
            if (_disposed) return 0;
            var sentence = new Sentence(new string(text, 0, count), this);
            bool go = true;
            if (segmentCallback != null)
                for (int i = 0; go && i < sentence.Segments.Count; i++) go = segmentCallback(sentence.Segments[i], data);
            if (go && sentenceCallback != null) sentenceCallback(sentence, data);
            return sentence.Segments.Count;
        }

        // ---- custom dictionaries: word lists (a first line "#LID nnnn" names their language, as on Windows) --------

        internal override object LoadDictionary(string lexiconFilePath) => _disposed ? null : Load(lexiconFilePath);

        internal override object LoadDictionary(Uri item, string trustedFolder) => _disposed ? null : Load(item.LocalPath);

        private object Load(string path)
        {
            var words = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (string line in File.ReadLines(path))
            {
                string word = line.Trim();
                if (word.Length > 0 && word[0] != '#') words.Add(word);
            }
            _dictionaries.Add(words);
            return words;
        }

        internal override void UnloadDictionary(object dictionary) => _dictionaries.Remove(dictionary as HashSet<string>);

        internal override void ReleaseAllLexicons() => _dictionaries.Clear();

        // ---- words --------------------------------------------------------------------------------------------------

        private bool IsCorrect(string word)
        {
            if ((_mode & SpellerMode.SpellingErrors) == 0) return true;
            // a word with digits in it is a code, a number with a unit: no speller marks those
            foreach (char c in word) if (char.IsDigit(c)) return true;
            foreach (var d in _dictionaries) if (d.Contains(word)) return true;
            return _engine.IsCorrect(word, _culture);
        }

        private IReadOnlyList<string> Suggest(string word) =>
            (_mode & SpellerMode.Suggestions) == 0 ? Array.Empty<string>() : _engine.Suggest(word, _culture);

        // a run of letters, digits and combining marks, an apostrophe between letters kept (don't, l'homme)
        private static List<(int Start, int Length)> Words(string s)
        {
            var words = new List<(int, int)>();
            int i = 0;
            while (i < s.Length)
            {
                if (!IsWordChar(s, i)) { i++; continue; }
                int start = i;
                while (i < s.Length && (IsWordChar(s, i) || (IsApostrophe(s[i]) && i + 1 < s.Length && char.IsLetter(s[i + 1]) && i > start)))
                    i += char.IsSurrogatePair(s, i) ? 2 : 1;
                words.Add((start, i - start));
            }
            return words;
        }

        private static bool IsWordChar(string s, int i)
        {
            UnicodeCategory c = CharUnicodeInfo.GetUnicodeCategory(s, i);
            return char.IsLetterOrDigit(s, i) || c == UnicodeCategory.NonSpacingMark || c == UnicodeCategory.SpacingCombiningMark;
        }

        private static bool IsApostrophe(char c) => c == '\'' || c == '’';

        private sealed class Range(int start, int length) : ITextRange
        {
            public int Start { get; } = start;
            public int Length { get; } = length;
        }

        private sealed class Segment(string source, int start, int length, PlatformSpellerInterop owner) : ISpellerSegment
        {
            private bool? _clean;
            private IReadOnlyList<string> _suggestions;

            public string SourceString { get; } = source;
            public IReadOnlyList<ISpellerSegment> SubSegments => Array.Empty<ISpellerSegment>();
            public ITextRange TextRange { get; } = new Range(start, length);
            public string Text => SourceString.Substring(start, length);
            public bool IsClean => _clean ??= owner.IsCorrect(Text);
            public IReadOnlyList<string> Suggestions => _suggestions ??= IsClean ? Array.Empty<string>() : owner.Suggest(Text);
            public void EnumSubSegments(EnumTextSegmentsCallback segmentCallback, object data) { }
        }

        private sealed class Sentence : ISpellerSentence
        {
            public Sentence(string text, PlatformSpellerInterop owner)
            {
                var segments = new List<ISpellerSegment>();
                foreach (var (start, length) in Words(text)) segments.Add(new Segment(text, start, length, owner));
                Segments = segments;
            }

            public IReadOnlyList<ISpellerSegment> Segments { get; }

            public int EndOffset => Segments.Count == 0 ? -1 : Segments[^1].TextRange.Start + Segments[^1].TextRange.Length;
        }

        private readonly SpellingEngine _engine;
        private readonly List<HashSet<string>> _dictionaries = new();
        private CultureInfo _culture;
        private SpellerMode _mode = SpellerMode.All;
        private bool _disposed;

        // ---- the engines ---------------------------------------------------------------------------------------------

        private abstract class SpellingEngine : IDisposable
        {
            public abstract bool Supports(CultureInfo culture);
            public abstract bool IsCorrect(string word, CultureInfo culture);
            public abstract IReadOnlyList<string> Suggest(string word, CultureInfo culture);
            public virtual void Dispose() { }

            // the engine's name for a culture among those it has: en_US, then en
            protected static string Match(CultureInfo culture, ICollection<string> available)
            {
                string full = culture.Name.Replace('-', '_'), language = culture.TwoLetterISOLanguageName;
                foreach (string candidate in new[] { full, language })
                    foreach (string a in available)
                        if (string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)) return a;
                foreach (string a in available)
                    if (a.StartsWith(language + "_", StringComparison.OrdinalIgnoreCase)) return a;
                return null;
            }
        }

        /// <summary>macOS's NSSpellChecker: the user's languages and learned words.</summary>
        private sealed unsafe class MacSpelling : SpellingEngine
        {
            private readonly IntPtr _checker;
            private readonly List<string> _languages;
            private readonly Dictionary<string, string> _byCulture = new();

            public static MacSpelling TryCreate()
            {
                try
                {
                    IntPtr cls = objc_getClass("NSSpellChecker");
                    if (cls == IntPtr.Zero) return null;
                    IntPtr checker = Send(cls, Sel("sharedSpellChecker"));
                    return checker == IntPtr.Zero ? null : new MacSpelling(checker);
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
            }

            private MacSpelling(IntPtr checker)
            {
                _checker = checker;
                _languages = Strings(Send(checker, Sel("availableLanguages")));
                WarmUp();
            }

            // The spelling server starts with the first question, and a question it times out on comes back as "no
            // misspelling" -- the first words a text box checked were passed whatever they were. So it is asked first
            // about a word no dictionary has, until it answers that it is misspelled (a few tries, half a second each
            // at most), before any of the user's text.
            private void WarmUp()
            {
                if (_languages.Count == 0) return;
                string language = Match(CultureInfo.GetCultureInfo("en"), _languages) ?? _languages[0];
                for (int i = 0; i < 6; i++)
                {
                    if (!IsCorrectIn("qzxwvkjhq", language)) return;
                }
            }

            private string Language(CultureInfo culture)
            {
                string key = culture.Name;
                if (!_byCulture.TryGetValue(key, out string language)) _byCulture[key] = language = Match(culture, _languages);
                return language;
            }

            public override bool Supports(CultureInfo culture) => Language(culture) != null;

            public override bool IsCorrect(string word, CultureInfo culture)
            {
                string language = Language(culture);
                return language == null || IsCorrectIn(word, language);
            }

            private bool IsCorrectIn(string word, string language)
            {
                IntPtr pool = objc_autoreleasePoolPush();
                try
                {
                    // checkSpellingOfString:startingAt:language:wrap:inSpellDocumentWithTag:wordCount: -- the first
                    // misspelling's range, NSNotFound where there is none
                    NSRange r = SendCheck(_checker, Sel("checkSpellingOfString:startingAt:language:wrap:inSpellDocumentWithTag:wordCount:"),
                        NSString(word), 0, NSString(language), 0, 0, null);
                    return r.Location == nint.MaxValue || r.Length == 0;
                }
                finally { objc_autoreleasePoolPop(pool); }
            }

            public override IReadOnlyList<string> Suggest(string word, CultureInfo culture)
            {
                string language = Language(culture);
                if (language == null) return Array.Empty<string>();
                IntPtr pool = objc_autoreleasePoolPush();
                try
                {
                    IntPtr guesses = SendGuesses(_checker, Sel("guessesForWordRange:inString:language:inSpellDocumentWithTag:"),
                        new NSRange { Location = 0, Length = word.Length }, NSString(word), NSString(language), 0);
                    return Strings(guesses);
                }
                finally { objc_autoreleasePoolPop(pool); }
            }

            private static IntPtr NSString(string s)
            {
                fixed (char* chars = s)
                    return SendChars(objc_getClass("NSString"), Sel("stringWithCharacters:length:"), chars, s.Length);
            }

            private static List<string> Strings(IntPtr array)
            {
                var result = new List<string>();
                if (array == IntPtr.Zero) return result;
                nint count = SendCount(array, Sel("count"));
                for (nint i = 0; i < count; i++)
                {
                    IntPtr s = SendIndex(array, Sel("objectAtIndex:"), i);
                    IntPtr utf8 = Send(s, Sel("UTF8String"));
                    if (utf8 != IntPtr.Zero) result.Add(Marshal.PtrToStringUTF8(utf8));
                }
                return result;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NSRange { public nint Location; public nint Length; }

            private const string ObjC = "/usr/lib/libobjc.A.dylib";
            private static IntPtr Sel(string name) => sel_registerName(name);
            [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
            [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
            [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
            [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendCount(IntPtr r, IntPtr s);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr r, IntPtr s, nint i);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendChars(IntPtr r, IntPtr s, char* chars, nint length);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRange SendCheck(IntPtr r, IntPtr s, IntPtr text, nint start, IntPtr language, byte wrap, nint tag, nint* wordCount);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendGuesses(IntPtr r, IntPtr s, NSRange range, IntPtr text, IntPtr language, nint tag);
        }

        /// <summary>UIKit's UITextChecker: the languages the keyboard has dictionaries for.</summary>
        private sealed unsafe class UIKitSpelling : SpellingEngine
        {
            private readonly IntPtr _checker;
            private readonly List<string> _languages;
            private readonly Dictionary<string, string> _byCulture = new();

            public static UIKitSpelling TryCreate()
            {
                try
                {
                    IntPtr cls = objc_getClass("UITextChecker");
                    if (cls == IntPtr.Zero) return null;
                    IntPtr checker = Send(Send(cls, Sel("alloc")), Sel("init"));
                    if (checker == IntPtr.Zero) return null;
                    var languages = Strings(Send(cls, Sel("availableLanguages")));
                    return languages.Count == 0 ? null : new UIKitSpelling(checker, languages);
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
            }

            private UIKitSpelling(IntPtr checker, List<string> languages)
            {
                _checker = checker;
                _languages = languages;
            }

            private string Language(CultureInfo culture)
            {
                if (!_byCulture.TryGetValue(culture.Name, out string language)) _byCulture[culture.Name] = language = Match(culture, _languages);
                return language;
            }

            public override bool Supports(CultureInfo culture) => Language(culture) != null;

            public override bool IsCorrect(string word, CultureInfo culture)
            {
                string language = Language(culture);
                if (language == null) return true;
                IntPtr pool = objc_autoreleasePoolPush();
                try
                {
                    // rangeOfMisspelledWordInString:range:startingAt:wrap:language: -- NSNotFound where it is spelled right
                    NSRange r = SendMisspelled(_checker, Sel("rangeOfMisspelledWordInString:range:startingAt:wrap:language:"),
                        NSString(word), new NSRange { Location = 0, Length = word.Length }, 0, 0, NSString(language));
                    return r.Location == nint.MaxValue || r.Length == 0;
                }
                finally { objc_autoreleasePoolPop(pool); }
            }

            public override IReadOnlyList<string> Suggest(string word, CultureInfo culture)
            {
                string language = Language(culture);
                if (language == null) return Array.Empty<string>();
                IntPtr pool = objc_autoreleasePoolPush();
                try
                {
                    return Strings(SendGuesses(_checker, Sel("guessesForWordRange:inString:language:"),
                        new NSRange { Location = 0, Length = word.Length }, NSString(word), NSString(language)));
                }
                finally { objc_autoreleasePoolPop(pool); }
            }

            public override void Dispose() => Send(_checker, Sel("release"));

            private static IntPtr NSString(string s)
            {
                fixed (char* chars = s)
                    return SendChars(objc_getClass("NSString"), Sel("stringWithCharacters:length:"), chars, s.Length);
            }

            private static List<string> Strings(IntPtr array)
            {
                var result = new List<string>();
                if (array == IntPtr.Zero) return result;
                nint count = SendCount(array, Sel("count"));
                for (nint i = 0; i < count; i++)
                {
                    IntPtr utf8 = Send(SendIndex(array, Sel("objectAtIndex:"), i), Sel("UTF8String"));
                    if (utf8 != IntPtr.Zero) result.Add(Marshal.PtrToStringUTF8(utf8));
                }
                return result;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NSRange { public nint Location; public nint Length; }

            private const string ObjC = "/usr/lib/libobjc.A.dylib";
            private static IntPtr Sel(string name) => sel_registerName(name);
            [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
            [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
            [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
            [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendCount(IntPtr r, IntPtr s);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr r, IntPtr s, nint i);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendChars(IntPtr r, IntPtr s, char* chars, nint length);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRange SendMisspelled(IntPtr r, IntPtr s, IntPtr text, NSRange range, nint start, byte wrap, IntPtr language);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendGuesses(IntPtr r, IntPtr s, NSRange range, IntPtr text, IntPtr language);
        }

        /// <summary>Enchant 2, the desktop's spelling library on Linux, with the dictionaries installed.</summary>
        private sealed unsafe class EnchantSpelling : SpellingEngine
        {
            private readonly IntPtr _broker;
            private readonly Dictionary<string, IntPtr> _dictionaries = new();
            private readonly Dictionary<string, string> _byCulture = new();
            private readonly List<string> _languages = new();

            private static IntPtr s_lib;
            private static delegate* unmanaged<IntPtr> s_init;
            private static delegate* unmanaged<IntPtr, void> s_free;
            private static delegate* unmanaged<IntPtr, byte*, IntPtr> s_request;
            private static delegate* unmanaged<IntPtr, IntPtr, void> s_freeDict;
            private static delegate* unmanaged<IntPtr, byte*, nint, int> s_check;
            private static delegate* unmanaged<IntPtr, byte*, nint, nuint*, byte**> s_suggest;
            private static delegate* unmanaged<IntPtr, byte**, void> s_freeList;
            private static delegate* unmanaged<IntPtr, delegate* unmanaged<byte*, byte*, byte*, byte*, IntPtr, void>, IntPtr, void> s_listDicts;

            public static EnchantSpelling TryCreate()
            {
                if (s_lib == IntPtr.Zero && !NativeLibrary.TryLoad("libenchant-2.so.2", out s_lib) && !NativeLibrary.TryLoad("libenchant-2.so", out s_lib)) return null;
                try
                {
                    s_init = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(s_lib, "enchant_broker_init");
                    s_free = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(s_lib, "enchant_broker_free");
                    s_request = (delegate* unmanaged<IntPtr, byte*, IntPtr>)NativeLibrary.GetExport(s_lib, "enchant_broker_request_dict");
                    s_freeDict = (delegate* unmanaged<IntPtr, IntPtr, void>)NativeLibrary.GetExport(s_lib, "enchant_broker_free_dict");
                    s_check = (delegate* unmanaged<IntPtr, byte*, nint, int>)NativeLibrary.GetExport(s_lib, "enchant_dict_check");
                    s_suggest = (delegate* unmanaged<IntPtr, byte*, nint, nuint*, byte**>)NativeLibrary.GetExport(s_lib, "enchant_dict_suggest");
                    s_freeList = (delegate* unmanaged<IntPtr, byte**, void>)NativeLibrary.GetExport(s_lib, "enchant_dict_free_string_list");
                    s_listDicts = (delegate* unmanaged<IntPtr, delegate* unmanaged<byte*, byte*, byte*, byte*, IntPtr, void>, IntPtr, void>)NativeLibrary.GetExport(s_lib, "enchant_broker_list_dicts");
                }
                catch (EntryPointNotFoundException) { return null; }
                IntPtr broker = s_init();
                return broker == IntPtr.Zero ? null : new EnchantSpelling(broker);
            }

            private EnchantSpelling(IntPtr broker)
            {
                _broker = broker;
                var handle = GCHandle.Alloc(_languages);
                try { s_listDicts(broker, &Listed, GCHandle.ToIntPtr(handle)); }
                finally { handle.Free(); }
            }

            [UnmanagedCallersOnly]
            private static void Listed(byte* tag, byte* providerName, byte* providerDescription, byte* providerFile, IntPtr data)
            {
                if (GCHandle.FromIntPtr(data).Target is List<string> list && Marshal.PtrToStringUTF8((IntPtr)tag) is string t) list.Add(t);
            }

            private IntPtr Dictionary(CultureInfo culture)
            {
                if (!_byCulture.TryGetValue(culture.Name, out string tag)) _byCulture[culture.Name] = tag = Match(culture, _languages);
                if (tag == null) return IntPtr.Zero;
                if (!_dictionaries.TryGetValue(tag, out IntPtr dict))
                {
                    fixed (byte* t = Encoding.UTF8.GetBytes(tag + "\0")) dict = s_request(_broker, t);
                    _dictionaries[tag] = dict;
                }
                return dict;
            }

            public override bool Supports(CultureInfo culture) => Dictionary(culture) != IntPtr.Zero;

            public override bool IsCorrect(string word, CultureInfo culture)
            {
                IntPtr dict = Dictionary(culture);
                if (dict == IntPtr.Zero) return true;
                byte[] utf8 = Encoding.UTF8.GetBytes(word);
                fixed (byte* w = utf8) return s_check(dict, w, utf8.Length) == 0;      // 0: found; 1: not; -1: an error
            }

            public override IReadOnlyList<string> Suggest(string word, CultureInfo culture)
            {
                IntPtr dict = Dictionary(culture);
                if (dict == IntPtr.Zero) return Array.Empty<string>();
                byte[] utf8 = Encoding.UTF8.GetBytes(word);
                nuint count = 0;
                byte** list;
                fixed (byte* w = utf8) list = s_suggest(dict, w, utf8.Length, &count);
                var result = new List<string>((int)count);
                if (list == null) return result;
                for (nuint i = 0; i < count; i++) result.Add(Marshal.PtrToStringUTF8((IntPtr)list[i]));
                s_freeList(dict, list);
                return result;
            }

            public override void Dispose()
            {
                foreach (IntPtr dict in _dictionaries.Values) if (dict != IntPtr.Zero) s_freeDict(_broker, dict);
                _dictionaries.Clear();
                s_free(_broker);
            }
        }
    }
}

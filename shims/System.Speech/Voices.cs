// The system's voices and how one speaks: macOS's through say (every voice the system has, its rate in words a
// minute, its volume an embedded command, sound to a WAVE file where asked), Linux's through speech-dispatcher's
// spd-say where it is installed (its voices, rate and volume in its -100..100). A machine with neither has no voices,
// as a Windows machine with none: speaking fails as SAPI fails then.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace System.Speech.Synthesis
{
    internal static class Voices
    {
        private static List<VoiceInfo> s_voices;
        private static readonly object s_gate = new object();

        private static string Tool(string name) =>
            new[] { "/usr/bin/", "/usr/local/bin/", "/opt/homebrew/bin/" }.Select(d => d + name).FirstOrDefault(File.Exists);

        internal static string Say => OperatingSystem.IsMacOS() ? Tool("say") : null;
        internal static string SpdSay => OperatingSystem.IsLinux() ? Tool("spd-say") : null;

        internal static IReadOnlyList<VoiceInfo> All
        {
            get
            {
                lock (s_gate) return s_voices ??= Read();
            }
        }

        private static List<VoiceInfo> Read()
        {
            var voices = new List<VoiceInfo>();
            if (Say is { } say)
            {
                // "Eddy (English (UK)) en_GB    # Hello! My name is Eddy."
                var line = new Regex(@"^(?<name>.+?)\s+(?<locale>[a-z]{2,3}(?:_[A-Za-z0-9]{2,4})?)\s+#", RegexOptions.CultureInvariant);
                foreach (string l in Run(say, "-v", "?"))
                {
                    Match m = line.Match(l);
                    if (!m.Success) continue;
                    string name = m.Groups["name"].Value.Trim();
                    voices.Add(new VoiceInfo(name, "com.apple.voice." + name, Culture(m.Groups["locale"].Value), VoiceGender.NotSet, VoiceAge.NotSet, name));
                }
            }
            else if (SpdSay is { } spd)
            {
                // spd-say -L: "NAME  LANGUAGE  VARIANT" after a header line
                foreach (string l in Run(spd, "-L").Skip(1))
                {
                    string[] parts = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    voices.Add(new VoiceInfo(parts[0], "speech-dispatcher." + parts[0], Culture(parts[1]), VoiceGender.NotSet, VoiceAge.NotSet, parts[0]));
                }
            }
            return voices;
        }

        private static CultureInfo Culture(string locale)
        {
            try { return CultureInfo.GetCultureInfo(locale.Replace('_', '-')); }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }

        private static IEnumerable<string> Run(string file, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return output.Split('\n');
            }
            catch (Exception e) when (e is ComponentModel.Win32Exception or InvalidOperationException) { return Array.Empty<string>(); }
        }

        /// <summary>The voice used where none is chosen: the system's for the user's language, else the first.</summary>
        internal static VoiceInfo Default
        {
            get
            {
                var all = All;
                if (all.Count == 0) return null;
                CultureInfo ui = CultureInfo.CurrentUICulture;
                return all.FirstOrDefault(v => v.Culture.Name == ui.Name) ?? all.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == ui.TwoLetterISOLanguageName) ?? all[0];
            }
        }

        /// <summary>The process that speaks <paramref name="text"/> in <paramref name="voice"/> (to <paramref name="waveFile"/>
        /// where given), and what it reads; SAPI's rate (-10..10, each step about a tenth faster) and volume (0..100).</summary>
        internal static (ProcessStartInfo Start, string Input) Speaker(string text, VoiceInfo voice, int rate, int volume, string waveFile, SpeechAudioFormat format)
        {
            if (Say is { } say)
            {
                var psi = new ProcessStartInfo(say) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
                if (voice != null) { psi.ArgumentList.Add("-v"); psi.ArgumentList.Add(voice.Name); }
                // SAPI's default is about 175 words a minute, its range a third to three times that
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add(((int)Math.Round(175 * Math.Pow(3, rate / 10.0))).ToString(CultureInfo.InvariantCulture));
                if (waveFile != null)
                {
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add(waveFile);
                    psi.ArgumentList.Add("--file-format=WAVE");
                    psi.ArgumentList.Add($"--data-format=LEI{format.Bits}@{format.Rate}");
                    if (format.Channels == 2) psi.ArgumentList.Add("--channels=2");
                }
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("-");
                return (psi, (volume < 100 ? $"[[volm {(volume / 100.0).ToString("0.00", CultureInfo.InvariantCulture)}]] " : "") + text);
            }
            if (SpdSay is { } spd)
            {
                if (waveFile != null) throw new PlatformNotSupportedException("speech-dispatcher speaks to the audio device only.");
                var psi = new ProcessStartInfo(spd) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-w");
                if (voice != null) { psi.ArgumentList.Add("-y"); psi.ArgumentList.Add(voice.Name); }
                psi.ArgumentList.Add("-r"); psi.ArgumentList.Add((rate * 10).ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add((volume * 2 - 100).ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-e");
                return (psi, text);
            }
            throw NoVoice();
        }

        internal static PlatformNotSupportedException NoVoice() =>
            new PlatformNotSupportedException("No voice installed on the system or none available with the current security setting.");
    }

    internal readonly record struct SpeechAudioFormat(int Rate, int Bits, int Channels)
    {
        public static readonly SpeechAudioFormat Default = new SpeechAudioFormat(22050, 16, 1);
    }
}

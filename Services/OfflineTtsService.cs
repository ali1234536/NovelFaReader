using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EpubReader.Services
{
    /// <summary>
    /// موتور روخوانی صوتی کاملاً آفلاین با ۲ لایه هوشمند:
    /// ۱) موتور عصبی پرتابل Piper داخل پوشه VoiceEngine کنار برنامه (صدای کاملاً طبیعی فارسی، بدون نیاز به نصب توسط کاربر)
    /// ۲) موتور بومی ویندوز System.Speech (SAPI5) به عنوان پشتیبان آفلاین
    /// </summary>
    public sealed class OfflineTtsService : IDisposable
    {
        private SpeechSynthesizer synth;
        private readonly object gate = new object();
        private bool isSpeaking;
        private bool isPaused;
        private bool isPersianNativeVoice;
        private bool isArabicFallbackVoice;
        private bool usingPiper;
        private string activeVoiceName = "";

        // مسیرهای موتور پرتابل Piper
        private string piperExePath;
        private string piperModelPath;
        private string piperConfigPath;
        private string piperEspeakDataDir;
        private string piperModelName = "";
        private CancellationTokenSource piperCts;
        private ManualResetEventSlim piperPauseGate = new ManualResetEventSlim(true);
        private Process activePiperProc;
        private SoundPlayer activePlayer;
        private double currentSpeed = 1.0;

        public event EventHandler StateChanged;

        public bool IsSpeaking { get { return isSpeaking; } }
        public bool IsPaused { get { return isPaused; } }
        public bool HasNativePersianVoice { get { return usingPiper || isPersianNativeVoice; } }
        public bool HasPortablePiper { get { return Environment.Is64BitOperatingSystem && !string.IsNullOrEmpty(piperExePath) && !string.IsNullOrEmpty(piperModelPath); } }
        public string ActiveVoiceName { get { return HasPortablePiper ? ("Piper: " + piperModelName) : activeVoiceName; } }

        public OfflineTtsService()
        {
            DetectPortablePiper();
            InitializeSynth();
        }

        public void DetectPortablePiper()
        {
            piperExePath = null;
            piperModelPath = null;
            piperConfigPath = null;
            piperEspeakDataDir = null;
            piperModelName = "";

            if (!Environment.Is64BitOperatingSystem)
            {
                return;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
            string[] candidateDirs = new string[]
            {
                Path.Combine(baseDir, "VoiceEngine"),
                Path.Combine(baseDir, "VoiceEngine", "piper"),
                Path.Combine(baseDir, "piper"),
                Path.Combine(appDataDir, "VoiceEngine"),
                Path.Combine(appDataDir, "VoiceEngine", "piper")
            };

            foreach (string dir in candidateDirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    string exe = Path.Combine(dir, "piper.exe");
                    if (!File.Exists(exe)) continue;

                    List<string> models = new List<string>();
                    models.AddRange(Directory.GetFiles(dir, "*.onnx", SearchOption.AllDirectories));
                    string parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) &&
                        string.Equals(Path.GetFileName(dir), "piper", StringComparison.OrdinalIgnoreCase))
                    {
                        models.AddRange(Directory.GetFiles(parent, "*.onnx", SearchOption.TopDirectoryOnly));
                    }

                    if (models.Count == 0) continue;

                    string chosen = models.FirstOrDefault(m => Path.GetFileName(m).IndexOf("fa", StringComparison.OrdinalIgnoreCase) >= 0)
                                    ?? models[0];
                    piperExePath = exe;
                    piperModelPath = chosen;
                    piperModelName = Path.GetFileNameWithoutExtension(chosen);

                    string cfg = chosen + ".json";
                    if (File.Exists(cfg))
                    {
                        piperConfigPath = cfg;
                    }

                    string espeak1 = Path.Combine(dir, "espeak-ng-data");
                    string espeak2 = !string.IsNullOrEmpty(parent) ? Path.Combine(parent, "espeak-ng-data") : "";
                    if (Directory.Exists(espeak1)) piperEspeakDataDir = espeak1;
                    else if (!string.IsNullOrEmpty(espeak2) && Directory.Exists(espeak2)) piperEspeakDataDir = espeak2;

                    return;
                }
                catch { }
            }
        }

        private void InitializeSynth()
        {
            try
            {
                synth = new SpeechSynthesizer();
                synth.SetOutputToDefaultAudioDevice();
                synth.SpeakCompleted += OnSpeakCompleted;
                SelectBestVoice(true);
            }
            catch
            {
                synth = null;
            }
        }

        private void SelectBestVoice(bool preferPersian)
        {
            if (synth == null) return;
            try
            {
                var voices = synth.GetInstalledVoices()
                    .Where(v => v != null && v.Enabled && v.VoiceInfo != null)
                    .Select(v => v.VoiceInfo)
                    .ToList();

                if (voices.Count == 0) return;

                VoiceInfo chosen = null;
                isPersianNativeVoice = false;
                isArabicFallbackVoice = false;

                if (preferPersian)
                {
                    chosen = voices.FirstOrDefault(v =>
                        (v.Culture != null && v.Culture.TwoLetterISOLanguageName.Equals("fa", StringComparison.OrdinalIgnoreCase)) ||
                        ContainsAny(v.Name, "persian", "farsi", "dariush", "parisa", "iran", "fa-ir", "espeak-fa"));

                    if (chosen != null)
                    {
                        isPersianNativeVoice = true;
                    }
                    else
                    {
                        chosen = voices.FirstOrDefault(v =>
                            (v.Culture != null && v.Culture.TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase)) ||
                            ContainsAny(v.Name, "arabic", "hoda", "naayf", "maged", "tarik"));
                        if (chosen != null)
                        {
                            isArabicFallbackVoice = true;
                        }
                    }
                }
                else
                {
                    chosen = voices.FirstOrDefault(v =>
                        v.Culture != null && v.Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase));
                }

                if (chosen == null) chosen = voices[0];
                synth.SelectVoice(chosen.Name);
                activeVoiceName = chosen.Name;
            }
            catch { }
        }

        private static bool ContainsAny(string src, params string[] keys)
        {
            if (string.IsNullOrEmpty(src)) return false;
            foreach (string k in keys)
            {
                if (src.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool LooksPersian(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int persianChars = 0;
            int sample = Math.Min(text.Length, 400);
            for (int i = 0; i < sample; i++)
            {
                char c = text[i];
                if (c >= '\u0600' && c <= '\u06FF') persianChars++;
            }
            return persianChars >= 6;
        }

        public static string NormalizeTextForOfflineVoice(string text, bool arabicFallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text.Replace("\r\n", "\n").Replace("\r", "\n");
            // تبدیل ارقام فارسی و عربی به انگلیسی استاندارد و حذف جداکننده هزارگان تا موتور صوتی دچار توهم نشود
            char[] faDigits = { '۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹' };
            char[] arDigits = { '٠', '١', '٢', '٣', '٤', '٥', '٦', '٧', '٨', '٩' };
            for (int d = 0; d < 10; d++)
            {
                s = s.Replace(faDigits[d], (char)('0' + d)).Replace(arDigits[d], (char)('0' + d));
            }
            s = Regex.Replace(s, @"(?<=\d)[،,](?=\d)", "");
            // حذف علائم مزاحم و تزئینی که باعث توهم مدل عصبی VITS می‌شوند
            s = s.Replace("…", ".").Replace("...", ".").Replace("—", "، ").Replace("–", "، ");
            s = Regex.Replace(s, @"[«»""'`~^*_+#@$%&=<>{}\[\]()\\/|]+", " ");
            s = Regex.Replace(s, @"[\u064B-\u065F\u0670\u0640]", ""); // حذف اعراب و کشیدگی
            s = Regex.Replace(s, @"[ \t]+", " ").Trim();

            if (!arabicFallback)
            {
                s = s.Replace('ي', 'ی')
                     .Replace('ك', 'ک')
                     .Replace('ة', 'ه')
                     .Replace('أ', 'ا')
                     .Replace('إ', 'ا')
                     .Replace('ؤ', 'و')
                     .Replace('ئ', 'ی')
                     .Replace("\u200c", " ");
            }
            else
            {
                s = s.Replace("\u200c", " ")
                     .Replace('ی', 'ي')
                     .Replace('ک', 'ك')
                     .Replace('پ', 'ب')
                     .Replace('چ', 'ج')
                     .Replace('ژ', 'ز')
                     .Replace('گ', 'ك')
                     .Replace('ۀ', 'ه');
            }
            return s;
        }

        // ساخت JSON کاملاً ASCII با کدهای \uXXXX تا ویندوز هرگز حروف فارسی را خراب نکند
        private static string MakeRelativeIfInside(string baseDir, string targetPath)
        {
            try
            {
                if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(targetPath)) return targetPath;
                string root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(targetPath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return full.Substring(root.Length);
                }
            }
            catch { }
            return targetPath;
        }

        private static string BuildAsciiJsonPayload(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length * 3 + 16);
            sb.Append("{\"text\":\"");
            foreach (char c in text)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') continue;
                else if (c == '\t') sb.Append(" ");
                else if (c < 0x20 || c > 0x7E)
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append("\"}\n");
            return sb.ToString();
        }

        // تقسیم متن به عبارات کوتاه و تمیز (حداکثر ~110 کاراکتر) تا مدل فارسی Piper هرگز توهم نزند یا گیر نکند
        private static List<string> SplitIntoSentences(string text)
        {
            List<string> chunks = new List<string>();
            string[] raw = Regex.Split(text ?? "", @"(?<=[\.\!\?؟؛:\n])\s+|\n+");
            foreach (string r in raw)
            {
                string s = (r ?? "").Trim();
                if (s.Length < 2) continue;
                // اگر جمله خیلی بلند بود، روی ویرگول فارسی بشکن
                if (s.Length > 125)
                {
                    string[] sub = Regex.Split(s, @"(?<=[،,])\s+");
                    StringBuilder cur = new StringBuilder();
                    foreach (string part in sub)
                    {
                        string pt = (part ?? "").Trim();
                        if (pt.Length == 0) continue;
                        if (cur.Length > 0 && cur.Length + pt.Length > 110)
                        {
                            chunks.Add(cur.ToString());
                            cur.Clear();
                        }
                        if (cur.Length > 0) cur.Append(' ');
                        cur.Append(pt);
                    }
                    if (cur.Length > 0) chunks.Add(cur.ToString());
                }
                else
                {
                    chunks.Add(s);
                }
            }
            // ادغام تکه‌های خیلی کوتاه (زیر ۲۰ کاراکتر) با تکه‌ی بعدی تا صدا بریده‌بریده نشود
            List<string> merged = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                string c = chunks[i];
                if (merged.Count > 0 && merged[merged.Count - 1].Length < 22 && merged[merged.Count - 1].Length + c.Length < 110)
                {
                    merged[merged.Count - 1] = merged[merged.Count - 1] + "، " + c;
                }
                else
                {
                    merged.Add(c);
                }
            }
            return merged;
        }

        private static int MapRate(double speedMultiplier)
        {
            double clamped = Math.Max(0.6, Math.Min(2.0, speedMultiplier));
            if (clamped < 1.0) return (int)Math.Round((clamped - 1.0) * 15.0);
            return (int)Math.Round((clamped - 1.0) * 7.0);
        }

        public bool Speak(string rawText, double speedMultiplier)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return false;
            Stop();
            DetectPortablePiper();
            currentSpeed = Math.Max(0.6, Math.Min(2.0, speedMultiplier));

            if (HasPortablePiper)
            {
                return SpeakWithPiper(rawText);
            }

            if (synth == null) return false;
            lock (gate)
            {
                try
                {
                    usingPiper = false;
                    bool persian = LooksPersian(rawText);
                    SelectBestVoice(persian);
                    synth.Rate = MapRate(currentSpeed);
                    synth.Volume = 100;

                    string clean = NormalizeTextForOfflineVoice(rawText, persian && isArabicFallbackVoice);
                    if (string.IsNullOrWhiteSpace(clean)) return false;
                    if (clean.Length > 25000) clean = clean.Substring(0, 25000);

                    isSpeaking = true;
                    isPaused = false;
                    synth.SpeakAsync(clean);
                    RaiseStateChanged();
                    return true;
                }
                catch
                {
                    isSpeaking = false;
                    isPaused = false;
                    RaiseStateChanged();
                    return false;
                }
            }
        }

        private bool SpeakWithPiper(string rawText)
        {
            string clean = NormalizeTextForOfflineVoice(rawText, false);
            List<string> sentences = SplitIntoSentences(clean);
            if (sentences.Count == 0) return false;

            CancellationTokenSource cts = new CancellationTokenSource();
            lock (gate)
            {
                usingPiper = true;
                isSpeaking = true;
                isPaused = false;
                piperCts = cts;
                piperPauseGate.Set();
            }
            RaiseStateChanged();

            Task.Run(() => RunPiperLoop(sentences, cts.Token));
            return true;
        }

        private string SynthesizePiperChunk(string sentence, string tempDir, CancellationToken token)
        {
            if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(sentence)) return null;
            string wavPath = Path.Combine(tempDir, "seg_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                double lengthScale = Math.Max(0.55, Math.Min(1.65, 1.0 / Math.Max(0.6, currentSpeed)));
                string workDir = Path.GetDirectoryName(piperExePath) ?? "";
                string modelArg = MakeRelativeIfInside(workDir, piperModelPath);
                string configArg = !string.IsNullOrEmpty(piperConfigPath) ? MakeRelativeIfInside(workDir, piperConfigPath) : null;
                string espeakArg = !string.IsNullOrEmpty(piperEspeakDataDir) ? MakeRelativeIfInside(workDir, piperEspeakDataDir) : null;

                StringBuilder argSb = new StringBuilder();
                argSb.Append("--model \"").Append(modelArg).Append("\" ");
                if (!string.IsNullOrEmpty(configArg))
                {
                    argSb.Append("--config \"").Append(configArg).Append("\" ");
                }
                if (!string.IsNullOrEmpty(espeakArg))
                {
                    argSb.Append("--espeak_data \"").Append(espeakArg).Append("\" ");
                }
                argSb.Append("--output_file \"").Append(wavPath).Append("\" ");
                argSb.Append("--length_scale ").Append(lengthScale.ToString("0.##", CultureInfo.InvariantCulture)).Append(" ");
                // تنظیمات پایداری مدل VITS برای حذف توهم و تلفظ دقیق‌تر فارسی
                argSb.Append("--noise_scale 0.5 --noise_w 0.6 --sentence_silence 0.2");

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = piperExePath;
                psi.Arguments = argSb.ToString();
                psi.WorkingDirectory = workDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardError = true;

                int exitCode = 0;
                using (Process p = Process.Start(psi))
                {
                    lock (gate) { activePiperProc = p; }
                    byte[] payload = new UTF8Encoding(false).GetBytes(sentence + "\n");
                    p.StandardInput.BaseStream.Write(payload, 0, payload.Length);
                    p.StandardInput.BaseStream.Flush();
                    p.StandardInput.Close();
                    p.WaitForExit(20000);
                    try { exitCode = p.HasExited ? p.ExitCode : -1; } catch { exitCode = -1; }
                    lock (gate) { if (activePiperProc == p) activePiperProc = null; }
                }

                if (exitCode == 0 && File.Exists(wavPath) && new FileInfo(wavPath).Length > 64)
                {
                    return wavPath;
                }
            }
            catch { }
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            return null;
        }

        // اجرای دوبافره (Double-Buffered): هم‌زمان با پخش جمله‌ی فعلی، جمله‌ی بعدی در پس‌زمینه ساخته می‌شود تا هیچ مکث و گیرکردنی نباشد
        private void RunPiperLoop(List<string> sentences, CancellationToken token)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "EpubReaderTts");
            try { Directory.CreateDirectory(tempDir); } catch { }

            Task<string> nextTask = null;
            try
            {
                if (sentences.Count > 0)
                {
                    nextTask = Task.Run(() => SynthesizePiperChunk(sentences[0], tempDir, token));
                }

                for (int i = 0; i < sentences.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    piperPauseGate.Wait(token);
                    if (token.IsCancellationRequested) break;

                    string wavPath = null;
                    try
                    {
                        wavPath = nextTask != null ? nextTask.GetAwaiter().GetResult() : null;
                    }
                    catch { wavPath = null; }

                    // شروع ساخت جمله‌ی بعدی در پس‌زمینه هم‌زمان با پخش جمله‌ی فعلی
                    if (i + 1 < sentences.Count && !token.IsCancellationRequested)
                    {
                        string nextSentence = sentences[i + 1];
                        nextTask = Task.Run(() => SynthesizePiperChunk(nextSentence, tempDir, token));
                    }
                    else
                    {
                        nextTask = null;
                    }

                    if (string.IsNullOrEmpty(wavPath))
                    {
                        if (i == 0) break;
                        continue;
                    }

                    try
                    {
                        if (token.IsCancellationRequested) break;
                        piperPauseGate.Wait(token);
                        if (token.IsCancellationRequested) break;

                        using (SoundPlayer sp = new SoundPlayer(wavPath))
                        {
                            lock (gate) { activePlayer = sp; }
                            sp.PlaySync();
                            lock (gate) { if (activePlayer == sp) activePlayer = null; }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                    finally
                    {
                        try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (gate)
                {
                    if (piperCts != null && piperCts.Token == token)
                    {
                        isSpeaking = false;
                        isPaused = false;
                        usingPiper = false;
                    }
                }
                RaiseStateChanged();
            }
        }

        public void SetRate(double speedMultiplier)
        {
            currentSpeed = Math.Max(0.6, Math.Min(2.0, speedMultiplier));
            if (synth != null)
            {
                try { synth.Rate = MapRate(currentSpeed); } catch { }
            }
        }

        public void TogglePause()
        {
            if (!isSpeaking) return;
            lock (gate)
            {
                if (usingPiper)
                {
                    if (isPaused)
                    {
                        isPaused = false;
                        piperPauseGate.Set();
                    }
                    else
                    {
                        isPaused = true;
                        piperPauseGate.Reset();
                        try { if (activePlayer != null) activePlayer.Stop(); } catch { }
                    }
                    RaiseStateChanged();
                    return;
                }

                if (synth == null) return;
                try
                {
                    if (isPaused)
                    {
                        synth.Resume();
                        isPaused = false;
                    }
                    else
                    {
                        synth.Pause();
                        isPaused = true;
                    }
                    RaiseStateChanged();
                }
                catch { }
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                try
                {
                    if (piperCts != null)
                    {
                        piperCts.Cancel();
                        piperPauseGate.Set();
                        piperCts = null;
                    }
                }
                catch { }

                try { if (activePlayer != null) activePlayer.Stop(); } catch { }
                try
                {
                    if (activePiperProc != null && !activePiperProc.HasExited)
                    {
                        activePiperProc.Kill();
                    }
                }
                catch { }
                activePiperProc = null;
                activePlayer = null;

                if (synth != null)
                {
                    try
                    {
                        if (isPaused) { try { synth.Resume(); } catch { } }
                        synth.SpeakAsyncCancelAll();
                    }
                    catch { }
                }

                isSpeaking = false;
                isPaused = false;
                usingPiper = false;
            }
            RaiseStateChanged();
        }

        private void OnSpeakCompleted(object sender, SpeakCompletedEventArgs e)
        {
            lock (gate)
            {
                if (!usingPiper && synth != null && synth.State == SynthesizerState.Ready)
                {
                    isSpeaking = false;
                    isPaused = false;
                    RaiseStateChanged();
                }
            }
        }

        private void RaiseStateChanged()
        {
            try
            {
                EventHandler h = StateChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            try { piperPauseGate.Dispose(); } catch { }
            try
            {
                if (synth != null)
                {
                    synth.SpeakCompleted -= OnSpeakCompleted;
                    synth.SpeakAsyncCancelAll();
                    synth.Dispose();
                    synth = null;
                }
            }
            catch { }
        }
    }
}

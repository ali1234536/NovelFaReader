using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Input;

namespace EpubReader.Services
{
    public static class AppSettings
    {
        private static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        private static bool loaded = false;
        private static bool dirty = false;
        private static Timer saveTimer;

        private static string FilePath
        {
            get
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                return Path.Combine(folder, "appsettings.txt");
            }
        }

        // همه‌ی دسترسی‌ها (از رشته‌ی رابط و رشته‌های پس‌زمینه‌ی ایمپورت/هش/XP) زیر یک قفلن
        private static readonly object gate = new object();

        private static void ReadInto(string path)
        {
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        }

        public static void Load()
        {
            lock (gate)
            {
                if (loaded) return;
                try
                {
                    string main = FilePath;
                    string bak = main + ".bak";
                    bool ok = false;
                    if (File.Exists(main))
                    {
                        try
                        {
                            ReadInto(main);
                            ok = values.Count > 0;
                        }
                        catch { values.Clear(); }
                    }
                    // فایل اصلی خراب/خالی/نیمه‌کاره بود: از نسخه‌ی قبلی (.bak) بخون
                    if (!ok && File.Exists(bak))
                    {
                        try { ReadInto(bak); } catch { }
                    }
                }
                catch { }
                loaded = true;
            }
        }

        // زمان‌بندی ذخیره‌ی غیرمسدودکننده تا تغییرات پشت‌سرهم اسلایدرها و تنظیمات باعث لگ نشوند
        private static void ScheduleSave()
        {
            lock (gate)
            {
                dirty = true;
                if (saveTimer == null)
                {
                    saveTimer = new Timer(_ => Flush(), null, 350, Timeout.Infinite);
                }
                else
                {
                    saveTimer.Change(350, Timeout.Infinite);
                }
            }
        }

        public static void Flush()
        {
            lock (gate)
            {
                if (!dirty) return;
                dirty = false;
                SaveInternal();
            }
        }

        // نوشتن اتمیک: اول فایل موقت، بعد جایگزینی (نسخه‌ی قبلی می‌شه .bak). وسط نوشتن قطع بشه هم فایل سالم می‌مونه
        public static void Save()
        {
            lock (gate)
            {
                dirty = false;
                SaveInternal();
            }
        }

        private static void SaveInternal()
        {
            try
            {
                if (!loaded) return;
                StringBuilder sb = new StringBuilder(values.Count * 32);
                foreach (KeyValuePair<string, string> kv in values)
                {
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                }
                string main = FilePath;
                string tmp = main + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(main))
                {
                    File.Replace(tmp, main, main + ".bak", true);
                }
                else
                {
                    File.Move(tmp, main);
                }
            }
            catch { }
        }

        public static string Get(string key, string defaultValue)
        {
            Load();
            lock (gate)
            {
                string v;
                return values.TryGetValue(key, out v) ? v : defaultValue;
            }
        }

        public static void Set(string key, string value)
        {
            Load();
            lock (gate)
            {
                string existing;
                if (values.TryGetValue(key, out existing) && existing == value)
                {
                    return;
                }
                values[key] = value;
            }
            ScheduleSave();
        }

        // "dark" یا "light"
        public static string Theme
        {
            get { return Get("Theme", "dark"); }
            set { Set("Theme", value); }
        }

        // رنگ اصلی برنامه به‌صورت #RRGGBB (پیش‌فرض: بنفش)
        public static string Accent
        {
            get { return Get("Accent", "#7C3AED"); }
            set { Set("Accent", value); }
        }
    }

    public sealed class ShortcutAction
    {
        public string Id;
        private string group;
        private string title;

        public ShortcutAction(string id, string group, string title)
        {
            Id = id;
            this.group = group;
            this.title = title;
        }

        // گروه و عنوان به زبان انتخاب‌شده
        public string Group { get { return Loc.T(group); } }

        public string Title { get { return Loc.T(title); } }
    }

    // کلیدهای میانبر قابل تنظیم. پیش‌فرض همه خالی است.
    // ذخیره در appsettings.txt با کلید Shortcut.<id>
    public static class ShortcutService
    {
        public const string GroupLibrary = "کتابخانه";
        public const string GroupReader = "صفحه خواندن";

        public static readonly ShortcutAction[] Actions = new ShortcutAction[]
        {
            new ShortcutAction("lib.add", GroupLibrary, "افزودن کتاب"),
            new ShortcutAction("lib.search", GroupLibrary, "رفتن به کادر جستجو"),
            new ShortcutAction("lib.tabAll", GroupLibrary, "بخش همه کتاب‌ها"),
            new ShortcutAction("lib.tabReading", GroupLibrary, "بخش در حال مطالعه"),
            new ShortcutAction("lib.tabCompleted", GroupLibrary, "بخش تمام‌شده"),
            new ShortcutAction("lib.tabRecent", GroupLibrary, "بخش اخیراً بازشده"),
            new ShortcutAction("lib.tabPrivate", GroupLibrary, "بخش خصوصی"),
            new ShortcutAction("lib.tabAdvanced", GroupLibrary, "بخش پیشرفته و هوشمند"),
            new ShortcutAction("lib.switchUi", GroupLibrary, "تغییر حالت رابط کاربری"),
            new ShortcutAction("lib.changePin", GroupLibrary, "تغییر رمز"),
            new ShortcutAction("lib.theme", GroupLibrary, "تغییر حالت روشن/تیره"),
            new ShortcutAction("lib.language", GroupLibrary, "تغییر زبان (فارسی / English)"),
            new ShortcutAction("lib.maximize", GroupLibrary, "بزرگ‌نمایی / بازگرداندن پنجره"),
            new ShortcutAction("lib.minimize", GroupLibrary, "کوچک کردن پنجره"),
            new ShortcutAction("lib.close", GroupLibrary, "بستن برنامه"),

            new ShortcutAction("rd.next", GroupReader, "بخش بعدی"),
            new ShortcutAction("rd.prev", GroupReader, "بخش قبلی"),
            new ShortcutAction("rd.autoScroll", GroupReader, "اسکرول خودکار (شروع / توقف)"),
            new ShortcutAction("rd.focusMode", GroupReader, "حالت تمرکز (ذن)"),
            new ShortcutAction("rd.smartHud", GroupReader, "نوار هوشمند مطالعه"),
            new ShortcutAction("rd.readingRuler", GroupReader, "خط‌کش تمرکز"),
            new ShortcutAction("rd.fontUp", GroupReader, "بزرگ‌تر کردن فونت"),
            new ShortcutAction("rd.fontDown", GroupReader, "کوچک‌تر کردن فونت"),
            new ShortcutAction("rd.addFont", GroupReader, "افزودن فونت شخصی"),
            new ShortcutAction("rd.spacing", GroupReader, "تنظیمات فاصله‌ها"),
            new ShortcutAction("rd.fullscreen", GroupReader, "تمام صفحه"),
            new ShortcutAction("rd.continuous", GroupReader, "حالت پیوسته"),
            new ShortcutAction("rd.scrollUp", GroupReader, "اسکرول به بالا"),
            new ShortcutAction("rd.scrollDown", GroupReader, "اسکرول به پایین"),
            new ShortcutAction("rd.openFile", GroupReader, "باز کردن کتاب دیگر"),
            new ShortcutAction("rd.maximize", GroupReader, "بزرگ‌نمایی / بازگرداندن پنجره"),
            new ShortcutAction("rd.minimize", GroupReader, "کوچک کردن پنجره"),
            new ShortcutAction("rd.close", GroupReader, "بستن صفحه خواندن")
        };

        public static ShortcutAction Find(string id)
        {
            foreach (ShortcutAction a in Actions)
            {
                if (a.Id == id) return a;
            }
            return null;
        }

        public static string Get(string id)
        {
            return AppSettings.Get("Shortcut." + id, "");
        }

        public static void Set(string id, string combo)
        {
            AppSettings.Set("Shortcut." + id, combo ?? "");
        }

        public static void ClearAll()
        {
            foreach (ShortcutAction a in Actions)
            {
                AppSettings.Set("Shortcut." + a.Id, "");
            }
            AppSettings.Flush();
        }

        // اگر این کلید برای کار دیگری تنظیم شده باشد همان کار را برمی‌گرداند
        public static ShortcutAction FindOwner(string combo, string exceptId)
        {
            if (string.IsNullOrEmpty(combo)) return null;
            foreach (ShortcutAction a in Actions)
            {
                if (a.Id == exceptId) continue;
                if (Get(a.Id) == combo) return a;
            }
            return null;
        }

        // متن ترکیب کلید از روی رویداد. اگر فقط Ctrl/Shift/Alt زده شده باشد null برمی‌گرداند.
        public static string FromEvent(KeyEventArgs e)
        {
            Key k = e.Key;
            if (k == Key.System) k = e.SystemKey;
            else if (k == Key.ImeProcessed) k = e.ImeProcessedKey;
            else if (k == Key.DeadCharProcessed) k = e.DeadCharProcessedKey;

            switch (k)
            {
                case Key.None:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LWin:
                case Key.RWin:
                case Key.System:
                case Key.ImeProcessed:
                case Key.DeadCharProcessed:
                    return null;
            }

            return FromKey(k, Keyboard.Modifiers);
        }

        public static string FromKey(Key k, ModifierKeys m)
        {
            if (k == Key.None) return null;
            string text = "";
            if ((m & ModifierKeys.Control) != 0) text += "Ctrl+";
            if ((m & ModifierKeys.Shift) != 0) text += "Shift+";
            if ((m & ModifierKeys.Alt) != 0) text += "Alt+";
            return text + KeyName(k);
        }

        // ترکیب‌هایی که کار ویرایش متن هستند و نباید گرفته شوند
        public static bool IsReserved(string combo)
        {
            return combo == "Ctrl+C" || combo == "Ctrl+V" || combo == "Ctrl+X" || combo == "Ctrl+A"
                || combo == "Ctrl+Z" || combo == "Ctrl+Y" || combo == "Alt+F4";
        }

        // کار مربوط به یک ترکیب (با پیشوند lib. یا rd.)
        public static string ResolveCombo(string combo, string prefix)
        {
            if (string.IsNullOrEmpty(combo)) return null;
            foreach (ShortcutAction a in Actions)
            {
                if (a.Id.StartsWith(prefix) && Get(a.Id) == combo) return a.Id;
            }
            return null;
        }

        private static string KeyName(Key k)
        {
            string s = k.ToString();
            if (s.Length == 2 && s[0] == 'D' && s[1] >= '0' && s[1] <= '9') return s.Substring(1);
            if (s.StartsWith("NumPad")) return "Num" + s.Substring(6);
            return s;
        }

        // کدام کار (با پیشوند lib. یا rd.) به این کلید اختصاص داده شده؟ اگر هیچ، null
        public static string Resolve(KeyEventArgs e, string prefix)
        {
            string combo = FromEvent(e);
            if (combo == null) return null;
            foreach (ShortcutAction a in Actions)
            {
                if (a.Id.StartsWith(prefix) && Get(a.Id) == combo) return a.Id;
            }
            return null;
        }
    }
}

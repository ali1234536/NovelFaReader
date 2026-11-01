using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EpubReader.Models;

namespace EpubReader.Services
{
    // یک عنوان رتبه‌ی XP (۱۲ عنوان)
    public sealed class XpTitle
    {
        public readonly int Index;
        private readonly string name;
        public readonly int MinXp;

        public XpTitle(int index, string name, int minXp)
        {
            Index = index;
            this.name = name;
            MinXp = minXp;
        }

        public string Name { get { return Loc.T(name); } }
    }

    // یک سطح خوانا: عنوان + ستاره (۱ تا ۵)، یا بعد از سقف، نشانه‌های ابدیت
    public sealed class XpLevel
    {
        public int Xp;
        public XpTitle Title;
        public int Stars;
        public int EternityMarks;
        public int XpToNext;
        public double Progress;

        public int TitleIndex { get { return Title.Index; } }

        // عنوان + ستاره‌ها (یا نشانه‌ی ابدیت)
        public string DisplayName
        {
            get
            {
                if (EternityMarks >= 1)
                {
                    return Title.Name + " ✦" + EternityMarks.ToString(CultureInfo.InvariantCulture);
                }
                return Title.Name + " " + new string('★', Stars) + new string('☆', Math.Max(0, 5 - Stars));
            }
        }

        public string Key { get { return TitleIndex + ":" + Stars + ":" + EternityMarks; } }
    }

    public sealed class XpCredit
    {
        public int UnitXp;
        public int BonusXp;
        public int TotalXp;
    }

    // موتور رتبه‌ی XP (پورت نسخه‌ی اندروید): واحد XP یک بخش کتابه و بر اساس حجم متنه
    public static class XpRank
    {
        public const int CapXp = 27000;
        public const int MarkStepXp = 10000;
        public const long MinDwellMs = 5000;

        public static readonly XpTitle[] Titles = new XpTitle[]
        {
            new XpTitle(0, "تازه‌وارد", 0),
            new XpTitle(1, "رهگذر کتابخانه", 25),
            new XpTitle(2, "کتاب‌باز", 100),
            new XpTitle(3, "کتاب‌خور", 300),
            new XpTitle(4, "شکارچی داستان", 750),
            new XpTitle(5, "کتابدار", 1500),
            new XpTitle(6, "غرق‌شده در دنیاها", 3000),
            new XpTitle(7, "نگهبان کتابخانه‌ی ممنوعه", 5000),
            new XpTitle(8, "افسانه‌ی کتابخانه", 8000),
            new XpTitle(9, "دانای پلایادس", 12000),
            new XpTitle(10, "هیولای پلایادس", 18000),
            new XpTitle(11, "بلعنده‌ی جهان‌ها", 27000)
        };

        // ده نشانه‌ی ابدیت (بعد از سقف، هر ۱۰٬۰۰۰ XP یکی؛ از نشانه‌ی ۱۰ به بعد همون طرح آخر)
        public static readonly string[] EternityMarkNames = new string[]
        {
            "اخگر ابدیت", "شبنم ستاره‌ای", "برگ زرین", "هلال نقره‌ای", "خورشید کاغذی",
            "تاج کهکشانی", "بالِ نگهبان کتاب", "قلب پلایادس", "تخت ابدیت", "خورشید ابدیتِ جهان‌ها"
        };

        public static string EternityMarkName(int count)
        {
            if (count < 1)
            {
                return "";
            }
            return Loc.T(EternityMarkNames[Math.Min(count, EternityMarkNames.Length) - 1]);
        }

        public static XpLevel LevelFor(int rawXp)
        {
            int xp = Math.Max(0, rawXp);
            XpLevel lv = new XpLevel();
            lv.Xp = xp;
            if (xp < CapXp)
            {
                XpTitle title = Titles[0];
                int nextMin = CapXp;
                for (int i = 0; i < Titles.Length; i++)
                {
                    if (xp >= Titles[i].MinXp)
                    {
                        title = Titles[i];
                        nextMin = (i + 1 < Titles.Length) ? Titles[i + 1].MinXp : CapXp;
                    }
                    else
                    {
                        break;
                    }
                }
                int span = Math.Max(1, nextMin - title.MinXp);
                double fraction = (double)(xp - title.MinXp) / span;
                lv.Title = title;
                lv.Stars = Math.Min(5, 1 + (int)(fraction * 5.0));
                lv.EternityMarks = 0;
                lv.XpToNext = nextMin - xp;
                lv.Progress = Math.Max(0.0, Math.Min(1.0, fraction));
                return lv;
            }
            int over = xp - CapXp;
            lv.Title = Titles[11];
            lv.Stars = 5;
            lv.EternityMarks = over / MarkStepXp;
            lv.XpToNext = MarkStepXp - (over % MarkStepXp);
            lv.Progress = (double)(over % MarkStepXp) / MarkStepXp;
            return lv;
        }

        // XP یک بخش از روی تعداد کلماتش
        public static int XpForUnit(int words)
        {
            return Math.Max(1, (Math.Max(0, words) + 1000) / 2000);
        }

        public static int BookBonus(int bookXp)
        {
            return Math.Max(10, bookXp / 10);
        }

        // کفِ XP برای رتبه‌ی قدیمی (بر اساس تعداد کتاب تمام‌شده) تا کسی پایین نیاد
        public static int OldRankToMinXp(int oldRank)
        {
            switch (oldRank)
            {
                case 1: return 100;
                case 2: return 300;
                case 3: return 1500;
                case 4: return 8000;
                case 5: return 12000;
                case 6: return 18000;
                default: return 0;
            }
        }

        private static bool IsCjk(char c)
        {
            return (c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '鿿')
                || (c >= '぀' && c <= 'ヿ') || (c >= '가' && c <= '힯');
        }

        // تعداد کلمات متن ساده (هر نویسه‌ی CJK یک کلمه)
        public static int CountWords(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            int count = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (IsCjk(c))
                {
                    count++;
                    inWord = false;
                }
                else if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }
            return count;
        }

        public static int WordsFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return 0;
            }
            string t = Regex.Replace(html, "<script[\\s\\S]*?</script\\s*>", " ", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<style[\\s\\S]*?</style\\s*>", " ", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]+>", " ");
            t = t.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
            return CountWords(t);
        }

        // شماره‌ی عنوان XP که هر تم باز می‌کنه (تم‌های فقط-مخفی با XP باز نمی‌شن: 99)
        public static int XpTitleFor(CharacterTheme t)
        {
            if (t == null || t.RequiredRank >= 99)
            {
                return 99;
            }
            switch (t.Id)
            {
                case "ram": return 2;
                case "rem": return 3;
                case "beatrice": return 5;
                case "emilia": return 8;
                case "echidna": return 9;
                case "tsugi": return 10;
                default: return 0;
            }
        }
    }

    // دفترچه‌ی بخش‌های XP‌گرفته‌ی هر کتاب. خواندن پشت‌سرهم به یک عدد (upTo) فشرده می‌شه.
    // قالب: id:upTo:extraCsv:bookXp;...|bonusId,bonusId
    public sealed class XpLedger
    {
        private sealed class BookLedger
        {
            public int UpTo = -1;
            public HashSet<int> Extras = new HashSet<int>();
            public int BookXp;
        }

        private readonly Dictionary<string, BookLedger> books = new Dictionary<string, BookLedger>();
        private readonly HashSet<string> bonusAwarded = new HashSet<string>();

        public static string SafeId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "";
            }
            return id.Replace(":", "_").Replace(";", "_").Replace("|", "_").Replace(",", "_");
        }

        public bool IsCredited(string bookId, int unit)
        {
            BookLedger b;
            if (!books.TryGetValue(bookId, out b))
            {
                return false;
            }
            return unit <= b.UpTo || b.Extras.Contains(unit);
        }

        public int CreditedCount(string bookId)
        {
            BookLedger b;
            if (!books.TryGetValue(bookId, out b))
            {
                return 0;
            }
            int baseCount = b.UpTo >= 0 ? b.UpTo + 1 : 0;
            return baseCount + b.Extras.Count((int x) => x > b.UpTo);
        }

        public int BookXp(string bookId)
        {
            BookLedger b;
            return books.TryGetValue(bookId, out b) ? b.BookXp : 0;
        }

        public bool IsBonusAwarded(string bookId)
        {
            return bonusAwarded.Contains(bookId);
        }

        public void MarkBonusAwarded(string bookId)
        {
            bonusAwarded.Add(bookId);
        }

        public bool MarkCredited(string bookId, int unit, int xp)
        {
            if (IsCredited(bookId, unit))
            {
                return false;
            }
            BookLedger b;
            if (!books.TryGetValue(bookId, out b))
            {
                b = new BookLedger();
                books[bookId] = b;
            }
            if (unit == b.UpTo + 1)
            {
                b.UpTo = unit;
                while (b.Extras.Contains(b.UpTo + 1))
                {
                    b.Extras.Remove(b.UpTo + 1);
                    b.UpTo++;
                }
            }
            else
            {
                b.Extras.Add(unit);
            }
            b.BookXp += xp;
            return true;
        }

        // کلید محتوایی کتاب: "h" + SHA-256 فایل. با هر بار افزودن دوباره‌ی همون فایل، و روی هر دستگاه، یکسانه
        // (برخلاف Book.Id که هر بار تازه ساخته می‌شه). نویسه‌ی جداکننده نداره تا فرمت ledger خراب نشه
        public static string ContentKeyFor(string hash)
        {
            return "h" + (hash ?? "").ToLowerInvariant();
        }

        public static bool IsContentKey(string key)
        {
            if (key == null || key.Length != 65 || key[0] != 'h')
            {
                return false;
            }
            for (int i = 1; i < key.Length; i++)
            {
                char c = key[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static void MergeEntryInto(BookLedger target, BookLedger source)
        {
            if (source.UpTo > target.UpTo)
            {
                target.UpTo = source.UpTo;
            }
            foreach (int x in source.Extras)
            {
                target.Extras.Add(x);
            }
            target.Extras.RemoveWhere((int x) => x <= target.UpTo);
            while (target.Extras.Contains(target.UpTo + 1))
            {
                target.Extras.Remove(target.UpTo + 1);
                target.UpTo++;
            }
            if (source.BookXp > target.BookXp)
            {
                target.BookXp = source.BookXp;
            }
        }

        // ورودیِ یک کتاب رو از کلید قدیمی (Id) به کلید جدید می‌بره. XP کل دست نمی‌خوره
        public bool Transfer(string oldKey, string newKey)
        {
            if (oldKey == newKey)
            {
                return false;
            }
            BookLedger old;
            if (!books.TryGetValue(oldKey, out old))
            {
                return false;
            }
            books.Remove(oldKey);
            bool wasBonus = bonusAwarded.Remove(oldKey);
            BookLedger target;
            if (!books.TryGetValue(newKey, out target))
            {
                target = new BookLedger();
                books[newKey] = target;
            }
            MergeEntryInto(target, old);
            if (wasBonus)
            {
                bonusAwarded.Add(newKey);
            }
            return true;
        }

        // ورودی‌های محتوایی یه ledger دیگه (بکاپ) رو با این یکی ادغام می‌کنه؛ کلیدهای Id مال همون دستگاهن و نادیده گرفته می‌شن
        public void MergeContentFrom(XpLedger other)
        {
            foreach (KeyValuePair<string, BookLedger> kv in other.books)
            {
                if (!IsContentKey(kv.Key))
                {
                    continue;
                }
                BookLedger target;
                if (!books.TryGetValue(kv.Key, out target))
                {
                    target = new BookLedger();
                    books[kv.Key] = target;
                }
                MergeEntryInto(target, kv.Value);
                if (other.bonusAwarded.Contains(kv.Key))
                {
                    bonusAwarded.Add(kv.Key);
                }
            }
        }

        // فقط ورودی‌های محتوایی (قابل‌حمل) برای بکاپ
        public string SerializeContentOnly()
        {
            XpLedger filtered = new XpLedger();
            foreach (KeyValuePair<string, BookLedger> kv in books)
            {
                if (!IsContentKey(kv.Key))
                {
                    continue;
                }
                BookLedger b = new BookLedger();
                b.UpTo = kv.Value.UpTo;
                b.BookXp = kv.Value.BookXp;
                foreach (int x in kv.Value.Extras)
                {
                    b.Extras.Add(x);
                }
                filtered.books[kv.Key] = b;
                if (bonusAwarded.Contains(kv.Key))
                {
                    filtered.bonusAwarded.Add(kv.Key);
                }
            }
            return filtered.Serialize();
        }

        // مهاجرت از سیستم قدیمی: بخش‌های 0..unitsRead-1 با ۱ XP
        public void SetLegacy(string bookId, int unitsRead, int bookXpValue, bool bonus)
        {
            if (unitsRead <= 0)
            {
                return;
            }
            BookLedger b;
            if (!books.TryGetValue(bookId, out b))
            {
                b = new BookLedger();
                books[bookId] = b;
            }
            if (unitsRead - 1 > b.UpTo)
            {
                b.UpTo = unitsRead - 1;
            }
            b.Extras.RemoveWhere((int x) => x <= b.UpTo);
            if (bookXpValue > b.BookXp)
            {
                b.BookXp = bookXpValue;
            }
            if (bonus)
            {
                bonusAwarded.Add(bookId);
            }
        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (string id in books.Keys.OrderBy((string k) => k, StringComparer.Ordinal))
            {
                BookLedger b = books[id];
                if (!first)
                {
                    sb.Append(';');
                }
                first = false;
                sb.Append(id).Append(':').Append(b.UpTo.ToString(CultureInfo.InvariantCulture)).Append(':');
                sb.Append(string.Join(",", b.Extras.OrderBy((int x) => x).Select((int x) => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
                sb.Append(':').Append(b.BookXp.ToString(CultureInfo.InvariantCulture));
            }
            if (bonusAwarded.Count > 0)
            {
                sb.Append('|').Append(string.Join(",", bonusAwarded.OrderBy((string k) => k, StringComparer.Ordinal).ToArray()));
            }
            return sb.ToString();
        }

        public static XpLedger Parse(string raw)
        {
            XpLedger led = new XpLedger();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return led;
            }
            string[] parts = raw.Split(new char[] { '|' }, 2);
            if (parts[0].Length > 0)
            {
                foreach (string entry in parts[0].Split(';'))
                {
                    if (entry.Length == 0)
                    {
                        continue;
                    }
                    string[] f = entry.Split(':');
                    int upTo, bookXp;
                    if (f.Length != 4 || f[0].Length == 0
                        || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out upTo)
                        || !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out bookXp)
                        || upTo < -1 || upTo > 1000000 || bookXp < 0)
                    {
                        continue;
                    }
                    BookLedger b = new BookLedger();
                    b.UpTo = upTo;
                    b.BookXp = bookXp;
                    if (f[2].Length > 0)
                    {
                        foreach (string s in f[2].Split(','))
                        {
                            int v;
                            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > upTo)
                            {
                                b.Extras.Add(v);
                            }
                        }
                    }
                    led.books[f[0]] = b;
                }
            }
            if (parts.Length > 1)
            {
                foreach (string id in parts[1].Split(','))
                {
                    if (id.Length > 0)
                    {
                        led.bonusAwarded.Add(id);
                    }
                }
            }
            return led;
        }
    }

    // ردیاب نشست: یه بخش وقتی XP می‌گیره که حداقل ۵ ثانیه توش مونده باشی و ازش بری (یا کتاب رو ببندی)
    public sealed class XpSessionTracker
    {
        private int current = -1;
        private DateTime enteredAt;

        // برمی‌گردونه بخش قبلی رو اگه شرایطش رو داشت، وگرنه -1
        public int Enter(int unit)
        {
            DateTime now = DateTime.UtcNow;
            if (unit == current)
            {
                return -1;
            }
            int prev = current;
            DateTime prevAt = enteredAt;
            current = unit;
            enteredAt = now;
            if (prev < 0)
            {
                return -1;
            }
            return (now - prevAt).TotalMilliseconds >= XpRank.MinDwellMs ? prev : -1;
        }

        public int Flush()
        {
            int cur = current;
            current = -1;
            if (cur < 0)
            {
                return -1;
            }
            return (DateTime.UtcNow - enteredAt).TotalMilliseconds >= XpRank.MinDwellMs ? cur : -1;
        }
    }

    // ذخیره‌ی XP کل، دفترچه و وضعیت جشن توی appsettings.txt
    public static class XpStore
    {
        private const string TotalKey = "TotalXp";
        private const string LedgerKey = "XpLedger";
        private const string MigratedKey = "XpMigrated";
        private const string CelebratedKey = "XpCelebratedXp";
        private const string RekeyedKey = "XpRekeyed";
        private static readonly object LedgerLock = new object();

        public static bool Rekeyed
        {
            get { return AppSettings.Get(RekeyedKey, "0") == "1"; }
            set { AppSettings.Set(RekeyedKey, value ? "1" : "0"); }
        }

        public static bool TransferLedger(string oldKey, string newKey)
        {
            lock (LedgerLock)
            {
                XpLedger led = Ledger;
                if (!led.Transfer(XpLedger.SafeId(oldKey), newKey))
                {
                    return false;
                }
                Ledger = led;
                return true;
            }
        }

        public static void MergeContentLedger(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
            {
                return;
            }
            lock (LedgerLock)
            {
                XpLedger led = Ledger;
                led.MergeContentFrom(XpLedger.Parse(serialized));
                Ledger = led;
            }
        }

        public static string ContentLedgerSerialized()
        {
            lock (LedgerLock)
            {
                return Ledger.SerializeContentOnly();
            }
        }

        public static int TotalXp
        {
            get { return ReadInt(TotalKey); }
            set { AppSettings.Set(TotalKey, Math.Max(0, value).ToString(CultureInfo.InvariantCulture)); }
        }

        public static int CelebratedXp
        {
            get { return ReadInt(CelebratedKey); }
            set { AppSettings.Set(CelebratedKey, Math.Max(0, value).ToString(CultureInfo.InvariantCulture)); }
        }

        public static bool Migrated
        {
            get { return AppSettings.Get(MigratedKey, "0") == "1"; }
            set { AppSettings.Set(MigratedKey, value ? "1" : "0"); }
        }

        public static XpLedger Ledger
        {
            get { return XpLedger.Parse(AppSettings.Get(LedgerKey, "")); }
            set { AppSettings.Set(LedgerKey, value.Serialize()); }
        }

        private static int ReadInt(string key)
        {
            int v;
            if (int.TryParse(AppSettings.Get(key, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0)
            {
                return v;
            }
            return 0;
        }

        // XP یک بخش رو ثبت می‌کنه. null یعنی چیزی ثبت نشد (خصوصی، تکراری، نامعتبر)
        public static XpCredit CreditUnit(string bookId, int unit, int words, int totalUnits, bool isPrivate)
        {
            bookId = XpLedger.SafeId(bookId);
            if (isPrivate || unit < 0 || totalUnits <= 0 || bookId.Length == 0)
            {
                return null;
            }
            lock (LedgerLock)
            {
                XpLedger led = Ledger;
                if (led.IsCredited(bookId, unit))
                {
                    return null;
                }
                int unitXp = XpRank.XpForUnit(words);
                if (!led.MarkCredited(bookId, unit, unitXp))
                {
                    return null;
                }
                int bonus = 0;
                int credited = led.CreditedCount(bookId);
                bool finished = led.IsCredited(bookId, totalUnits - 1);
                if (finished && !led.IsBonusAwarded(bookId) && credited * 10 >= totalUnits * 9)
                {
                    bonus = XpRank.BookBonus(led.BookXp(bookId));
                    led.MarkBonusAwarded(bookId);
                }
                Ledger = led;
                int total = (int)Math.Min(int.MaxValue / 2, (long)TotalXp + unitXp + bonus);
                TotalXp = total;
                return new XpCredit { UnitXp = unitXp, BonusXp = bonus, TotalXp = total };
            }
        }

        // مهاجرت یک‌باره از رتبه‌ی قدیمی (تعداد کتاب) به XP. نتیجه = بیشینه‌ی (برآورد کتابخونه، کف رتبه، XP ذخیره‌شده)
        public static int MigrateIfNeeded(IList<Book> books, int effectiveFinished)
        {
            if (Migrated)
            {
                return TotalXp;
            }
            XpLedger led = Ledger;
            int estimate = 0;
            if (books != null)
            {
                foreach (Book b in books)
                {
                    if (b.IsPrivate)
                    {
                        continue;
                    }
                    int unitsRead = b.IsCompleted ? b.TotalChapters : b.LastChapterIndex;
                    if (unitsRead <= 0)
                    {
                        continue;
                    }
                    int bonus = b.IsCompleted ? XpRank.BookBonus(unitsRead) : 0;
                    estimate += unitsRead + bonus;
                    led.SetLegacy(XpLedger.SafeId(b.Id), unitsRead, unitsRead, bonus > 0);
                }
            }
            int floorXp = Math.Max(XpRank.OldRankToMinXp(Math.Max(0, RankService.MaxRankReached)), XpRank.OldRankToMinXp(RankService.RankIndexFor(effectiveFinished)));
            int total = Math.Max(estimate, Math.Max(floorXp, TotalXp));
            Ledger = led;
            TotalXp = total;
            CelebratedXp = total;
            Migrated = true;
            return total;
        }
    }
}

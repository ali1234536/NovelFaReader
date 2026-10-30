using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EpubReader.Models;

namespace EpubReader.Services
{

// نتیجه‌ی اعمال بکاپ (برای دیالوگ نتیجه)
public sealed class BackupRestoreResult
{
	public int RankBefore;          // رنک (ایندکس) قبل از ادغام
	public int RankAfter;           // رنک بعد از ادغام
	public int BeforeCount;         // تعداد مؤثر قبل (max کتابخونه و کف)
	public int BackupCount;         // تعداد کتاب‌های بکاپ
	public int Total;               // مجموع نهایی
	public int FontsAdded;
	public int FontsDuplicate;
	public int FontsFailed;
	public bool AvatarPresent;
	public bool AvatarApplied;
	public int SecretsAdded;
	public int BooksInBackup;       // تعداد کتاب‌های دارای پیشرفت توی بکاپ
	public int BooksUpdated;        // پیشرفتشون روی کتاب‌های موجود اعمال شد
	public int BooksPending;        // کتابشون هنوز توی کتابخونه نیست؛ با اضافه شدن اعمال می‌شه
	public int XpBefore;            // XP کل قبل از بازیابی
	public int XpAfter;             // XP کل بعد از بازیابی (بیشینه‌ی دو طرف)
}

// بکاپ و بازیابی ناول‌فا (نسخه‌ی ویندوز): رنک، آواتار، اسم پروفایل، فونت‌های شخصی و رکورد کتاب‌های تمام‌شده.
// منطق فرمت (ZIP + manifest + integrity) توی BackupFormat است؛ اینجا فقط اتصال به سرویس‌های موجود برنامه:
// CustomFontService، ProfileService، RankService و AppSettings. از خود کتاب‌ها، کاورها و هش PIN هیچ‌چیزی داخل بکاپ نمی‌ره.
public static class BackupService
{
	public const string FileFilter = "Novelfa Backup (*.novelfabak)|*.novelfabak";

	private static readonly string[] avatarExts = { "jpg", "jpeg", "png", "bmp", "gif", "webp" };

	public static string DefaultFileName()
	{
		return "novelfa-backup-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + BackupFormat.Extension;
	}

	private static string AppVersion()
	{
		try
		{
			Version v = Assembly.GetExecutingAssembly().GetName().Version;
			return v == null ? "1.0" : v.ToString(3);
		}
		catch
		{
			return "1.0";
		}
	}

	// فایل رو حتی اگه ویندوز فونت رو برای برنامه ثبت کرده (و باز نگه داشته) می‌خونه
	private static byte[] ReadShared(string path)
	{
		using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		using (MemoryStream ms = new MemoryStream())
		{
			fs.CopyTo(ms);
			return ms.ToArray();
		}
	}

	// ---------- تشخیص تصویر (آواتار) ----------
	private static bool ImageLooksValid(byte[] bytes)
	{
		try
		{
			using (MemoryStream ms = new MemoryStream(bytes))
			{
				BitmapDecoder decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
				if (decoder.Frames.Count < 1)
				{
					return false;
				}
				int w = decoder.Frames[0].PixelWidth;
				int h = decoder.Frames[0].PixelHeight;
				// سقف ابعاد: جلوی تصویرهای فوق‌سنگین (decompression bomb) رو می‌گیره
				return w > 0 && h > 0 && w <= 16384 && h <= 16384 && (long)w * h <= 64000000L;
			}
		}
		catch
		{
			return false;
		}
	}

	// WPF شیء‌های تصویر رو به نخ خودشون می‌چسبونه؛ برای همین بررسی روی نخ رابط کاربری انجام می‌شه
	private static bool ImageCheckOnUi(byte[] bytes)
	{
		Dispatcher ui = (Application.Current != null) ? Application.Current.Dispatcher : null;
		if (ui == null || ui.CheckAccess())
		{
			return ImageLooksValid(bytes);
		}
		return (bool)ui.Invoke(new Func<bool>(() => ImageLooksValid(bytes)));
	}

	// ---------- ساخت بکاپ ----------
	// روی نخ رابط کاربری صدا زده بشه (از تنظیمات و سرویس‌های برنامه می‌خونه). libraryFinished = شمارش خام کتاب‌های تمام‌شده‌ی کتابخونه
	public static BackupData Collect(int libraryFinished, out bool avatarSkipped, out int fontsSkipped)
	{
		avatarSkipped = false;
		fontsSkipped = 0;

		BackupData d = new BackupData();
		d.Platform = "windows";
		d.AppVersion = AppVersion();
		d.CreatedAt = DateTimeOffset.UtcNow;
		d.Name = ProfileService.UserName ?? "";
		d.MaxRank = Math.Max(-1, Math.Min(RankService.Ranks.Length - 1, RankService.MaxRankReached));
		d.FinishedBooks = Math.Max(0, Math.Min(BackupFormat.MaxFinishedBooks, RankService.Effective(libraryFinished)));
		foreach (string id in RankService.SecretThemeIds())
		{
			if (BackupFormat.IsValidThemeId(id))
			{
				d.SecretThemes.Add(id);
			}
		}

		d.TotalXp = Math.Min(BackupFormat.MaxTotalXp, XpStore.TotalXp);
		d.XpLedger = XpStore.ContentLedgerSerialized();

		// رکوردهای پیشرفت: کتاب‌های غیرخصوصی کتابخونه (با SHA-256 و شناسه‌ی OPF خود فایل) + رکوردهای کتاب‌های حذف‌شده/بازیابی‌شده
		try
		{
			List<ReadingRecord> libraryRecords = new List<ReadingRecord>();
			foreach (Book book in LibraryService.LoadBooks())
			{
				if (book.IsPrivate || string.IsNullOrEmpty(book.FilePath) || !File.Exists(book.FilePath))
				{
					continue;
				}
				try
				{
					libraryRecords.Add(ReadingRecord.FromBook(book, ReadingRecordStore.Sha256OfFile(book.FilePath), ReadingRecordStore.ReadEpubIdentifier(book.FilePath)));
				}
				catch
				{
				}
			}
			List<ReadingRecord> all = ReadingRecord.MergeAll(ReadingRecordStore.All(), libraryRecords);
			all.Sort((ReadingRecord x, ReadingRecord y) =>
			{
				int c = string.CompareOrdinal((x.Title ?? "").ToLowerInvariant(), (y.Title ?? "").ToLowerInvariant());
				return c != 0 ? c : string.CompareOrdinal(x.Sha256, y.Sha256);
			});
			d.Records = all;
		}
		catch
		{
		}

		// آواتار شخصی (اگه تنظیم شده و سالمه)
		string avatarPath = ProfileService.AvatarPath;
		if (avatarPath != null)
		{
			try
			{
				string ext = Path.GetExtension(avatarPath).TrimStart('.').ToLowerInvariant();
				FileInfo fi = new FileInfo(avatarPath);
				if (Array.IndexOf(avatarExts, ext) < 0 || fi.Length > BackupFormat.MaxAvatarBytes)
				{
					avatarSkipped = true;
				}
				else
				{
					byte[] bytes = ReadShared(avatarPath);
					if (ImageCheckOnUi(bytes))
					{
						BackupAvatar a = new BackupAvatar();
						a.Ext = ext;
						a.Bytes = bytes;
						d.Avatar = a;
					}
					else
					{
						avatarSkipped = true;
					}
				}
			}
			catch
			{
				avatarSkipped = true;
			}
		}

		// فقط فونت‌هایی که خود کاربر اضافه کرده (فونت‌های همراه برنامه جداست و اصلاً اینجا نیست)
		foreach (CustomFontInfo info in CustomFontService.GetAll())
		{
			if (d.Fonts.Count >= BackupFormat.MaxFonts)
			{
				fontsSkipped++;
				continue;
			}
			try
			{
				string path = info.FullPath;
				string ext = Path.GetExtension(info.FileName).TrimStart('.').ToLowerInvariant();
				FileInfo fi = new FileInfo(path);
				if (!fi.Exists || fi.Length > BackupFormat.MaxFontBytes || (ext != "ttf" && ext != "otf"))
				{
					fontsSkipped++;
					continue;
				}
				byte[] bytes = ReadShared(path);
				if (!BackupFormat.HasFontMagic(bytes))
				{
					fontsSkipped++;
					continue;
				}
				BackupFont f = new BackupFont();
				f.DisplayName = info.Name ?? "";
				f.FamilyName = info.FamilyName ?? "";
				f.Ext = ext;
				f.Bytes = bytes;
				d.Fonts.Add(f);
			}
			catch
			{
				fontsSkipped++;
			}
		}
		return d;
	}

	// اول کامل توی فایل موقت ساخته و دوباره خونده و اعتبارسنجی می‌شه، بعد به مقصد کپی می‌شه
	// (بکاپ نیمه‌کاره یا ناسالم هیچ‌وقت به دست کاربر نمی‌رسه). روی نخ پس‌زمینه اجرا می‌شه.
	public static Task ExportAsync(string targetPath, BackupData data)
	{
		return Task.Run(() =>
		{
			string temp = Path.Combine(Path.GetTempPath(), "novelfa-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
			try
			{
				using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
				{
					BackupFormat.Write(fs, data);
				}
				using (FileStream check = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					BackupFormat.Read(check, ImageCheckOnUi);
				}
				File.Copy(temp, targetPath, true);
			}
			finally
			{
				try
				{
					if (File.Exists(temp))
					{
						File.Delete(temp);
					}
				}
				catch
				{
				}
			}
		});
	}

	// ---------- اعتبارسنجی (هیچ تغییری روی برنامه اعمال نمی‌شه) ----------
	public static Task<BackupData> InspectAsync(string path)
	{
		return Task.Run(() =>
		{
			try
			{
				FileInfo fi = new FileInfo(path);
				if (!fi.Exists)
				{
					throw new BackupException(BackupError.ReadFailed, "not found");
				}
				// فایل فشرده‌شده نمی‌تونه از کل حجم مجاز بزرگ‌تر باشه (با کمی فضای ساختار زیپ)
				if (fi.Length > BackupFormat.MaxTotalBytes + 4L * 1024L * 1024L)
				{
					throw new BackupException(BackupError.TooLarge);
				}
				using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				{
					return BackupFormat.Read(fs, ImageCheckOnUi);
				}
			}
			catch (UnauthorizedAccessException ex)
			{
				throw new BackupException(BackupError.ReadFailed, ex.Message);
			}
			catch (IOException ex)
			{
				throw new BackupException(BackupError.ReadFailed, ex.Message);
			}
		});
	}

	// ---------- اعمال (روی نخ رابط کاربری) ----------
	// ترتیب: فونت ← آواتار ← پروفایل/رنک. libraryFinished = شمارش خام کتاب‌های تمام‌شده‌ی کتابخونه‌ی فعلی
	public static BackupRestoreResult Apply(BackupData data, int libraryFinished)
	{
		BackupRestoreResult r = new BackupRestoreResult();

		// قانون جمع (الحاقیه‌ی نسخه ۳): تعداد کتاب‌های بکاپ روی رکورد فعلی اضافه می‌شه
		long before = Math.Min(BackupFormat.MaxFinishedBooks, (long)RankService.Effective(libraryFinished));
		long total = Math.Min(BackupFormat.MaxFinishedBooks, before + data.FinishedBooks);
		int rankBefore = Math.Max(RankService.MaxRankReached, RankService.RankIndexFor((int)before));
		int rankAfter = Math.Max(rankBefore, Math.Max(data.MaxRank, RankService.RankIndexFor((int)total)));
		r.BeforeCount = (int)before;
		r.BackupCount = data.FinishedBooks;
		r.Total = (int)total;
		r.RankBefore = rankBefore;
		r.RankAfter = rankAfter;

		ApplyFonts(data, r);

		// پیشرفت کتاب‌ها (جلو می‌بره، هیچ‌وقت عقب نمی‌بره)
		r.BooksInBackup = data.Records.Count;
		try
		{
			int updated = LibraryService.ApplyBackupRecords(data.Records);
			r.BooksUpdated = updated;
			r.BooksPending = Math.Max(0, data.Records.Count - updated);
		}
		catch
		{
		}

		// XP: فقط بیشینه‌ی دو طرف (جمع نمی‌شه، چون همون کتاب‌ها ممکنه روی دو دستگاه حساب شده باشن)
		r.XpBefore = XpStore.TotalXp;
		r.XpAfter = r.XpBefore;
		if (data.TotalXp > r.XpBefore)
		{
			XpStore.TotalXp = data.TotalXp;
			r.XpAfter = data.TotalXp;
		}
		// ledger قابل‌حمل: واحدهایی که روی دستگاه دیگه حساب شدن اینجا هم حساب‌شده می‌مونن و دوباره XP نمی‌دن (XP کل از ledger جمع نمی‌شه)
		if (data.TotalXp >= 0 && !string.IsNullOrEmpty(data.XpLedger))
		{
			XpStore.MergeContentLedger(data.XpLedger);
		}

		r.AvatarPresent = data.Avatar != null;
		if (data.Avatar != null)
		{
			string temp = Path.Combine(Path.GetTempPath(), "novelfa_avatar_" + Guid.NewGuid().ToString("N") + "." + data.Avatar.Ext);
			try
			{
				File.WriteAllBytes(temp, data.Avatar.Bytes);
				r.AvatarApplied = ProfileService.SaveAvatar(temp);
			}
			catch
			{
				r.AvatarApplied = false;
			}
			finally
			{
				TryDelete(temp);
			}
		}

		if (!string.IsNullOrWhiteSpace(data.Name))
		{
			ProfileService.SaveName(data.Name);
		}
		r.SecretsAdded = RankService.MergeSecretThemes(data.SecretThemes);

		// رکورد و رنک هیچ‌وقت کم نمی‌شن: rankAfter >= رنک ذخیره‌شده‌ی فعلی
		RankService.FinishedFloor = (int)total;
		if (rankAfter > RankService.MaxRankReached)
		{
			RankService.MaxRankReached = rankAfter;
		}
		return r;
	}

	private static void ApplyFonts(BackupData data, BackupRestoreResult r)
	{
		if (data.Fonts.Count == 0)
		{
			return;
		}

		// هش فونت‌های شخصی موجود؛ فونتی که عیناً هست دوباره اضافه نمی‌شه
		HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (CustomFontInfo info in CustomFontService.GetAll())
		{
			try
			{
				existing.Add(BackupFormat.Sha256Hex(ReadShared(info.FullPath)));
			}
			catch
			{
			}
		}

		int index = 0;
		foreach (BackupFont f in data.Fonts)
		{
			index++;
			if (existing.Contains(f.Sha256))
			{
				r.FontsDuplicate++;
				continue;
			}

			string temp = Path.Combine(Path.GetTempPath(), "novelfa_font_" + Guid.NewGuid().ToString("N") + "." + f.Ext);
			try
			{
				File.WriteAllBytes(temp, f.Bytes);
				string baseName = PickFontName(f, index);
				bool added = false;
				for (int attempt = 1; attempt <= 100 && !added; attempt++)
				{
					string candidate = attempt == 1 ? baseName : FitName(baseName, " (" + attempt + ")");
					if (CustomFontService.NameExists(candidate))
					{
						continue;
					}
					try
					{
						CustomFontService.Add(temp, candidate);
						added = true;
					}
					catch (FontNameException)
					{
						// اسم همین الان گرفته شده؛ با پسوند بعدی دوباره
					}
				}
				if (added)
				{
					r.FontsAdded++;
					existing.Add(f.Sha256);
				}
				else
				{
					r.FontsFailed++;
				}
			}
			catch
			{
				// فونتِ خراب (ReadFamilyName خالی / ویندوز نپذیرفت): فقط همین یکی رد می‌شه، بقیه ادامه دارن
				r.FontsFailed++;
			}
			finally
			{
				TryDelete(temp);
			}
		}
	}

	// اسم نمایشی: displayName ← familyName ← اسم فایل اصلی ← «Font N»
	private static string PickFontName(BackupFont f, int index)
	{
		string original = "";
		try
		{
			// اسم فایلِ بیرونی ممکنه کاراکتر نامعتبر مسیر داشته باشه؛ اون‌وقت Path خطا می‌ده
			original = Path.GetFileNameWithoutExtension(f.OriginalFileName ?? "");
		}
		catch (ArgumentException)
		{
			original = "";
		}
		string[] options = { f.DisplayName, f.FamilyName, original };
		foreach (string o in options)
		{
			string n = FitName(CleanControl(o), "");
			if (n.Length > 0)
			{
				return n;
			}
		}
		return "Font " + index.ToString(CultureInfo.InvariantCulture);
	}

	private const int FontNameMax = 60;

	// اسم رو تمیز می‌کنه و با پسوند توی سقف طول فونت جا می‌ده
	private static string FitName(string name, string suffix)
	{
		string n = CustomFontService.CleanName(name ?? "");
		int room = FontNameMax - suffix.Length;
		if (n.Length > room)
		{
			n = n.Substring(0, room).TrimEnd();
		}
		return n.Length == 0 ? "" : n + suffix;
	}

	private static string CleanControl(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		StringBuilder sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			sb.Append(char.IsControl(c) ? ' ' : c);
		}
		return sb.ToString();
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	// ---------- متن‌های رابط کاربری ----------
	public static string Describe(BackupException ex)
	{
		switch (ex.Code)
		{
			case BackupError.ReadFailed: return Loc.T("خوندن فایل بکاپ انجام نشد.");
			case BackupError.NotZip: return Loc.T("این فایل یک بکاپ ناول‌فا نیست (فایل زیپ سالم نیست).");
			case BackupError.TooLarge: return Loc.T("فایل بکاپ بیش از حد بزرگه.");
			case BackupError.ManifestMissing: return Loc.T("این فایل یک بکاپ ناول‌فا نیست (مانیفست پیدا نشد).");
			case BackupError.ManifestInvalid: return Loc.T("مانیفست بکاپ خراب یا نامعتبره.");
			case BackupError.WrongFormat: return Loc.T("این فایل یک بکاپ ناول‌فا نیست.");
			case BackupError.NewerVersion: return Loc.T("این بکاپ با نسخه‌ی جدیدتری از برنامه ساخته شده. برنامه رو به‌روز کن.");
			case BackupError.BadEntry: return Loc.T("ساختار فایل بکاپ معتبر نیست (فایل ناشناخته یا مسیر نامعتبر).");
			case BackupError.MissingFile: return Loc.T("بکاپ ناقصه؛ بعضی از فایل‌هاش نیست.");
			case BackupError.HashMismatch: return Loc.T("بکاپ خراب یا دست‌کاری شده (هش یکی از فایل‌ها نمی‌خونه).");
			case BackupError.IntegrityMismatch: return Loc.T("بکاپ خراب یا دست‌کاری شده (بررسی تمامیت ناموفق بود).");
			case BackupError.ProfileInvalid: return Loc.T("اطلاعات پروفایل داخل بکاپ نامعتبره.");
			case BackupError.FontInvalid: return Loc.T("یکی از فونت‌های داخل بکاپ، فونت معتبر نیست.");
			case BackupError.ImageInvalid: return Loc.T("تصویر آواتار داخل بکاپ نامعتبره.");
			default: return Loc.T("بکاپ نامعتبره.");
		}
	}

	// متنی که توی خلاصه‌ی بازیابی نشون داده می‌شه رو از کاراکترهای کنترلی پاک می‌کنه (مقدارش از فایل بیرونیه)
	private static string Safe(string s, int max)
	{
		string t = CleanControl(s).Trim();
		return t.Length > max ? t.Substring(0, max) : t;
	}

	public static string BuildPreviewText(BackupData d)
	{
		string platform = (d.Platform ?? "").Trim().ToLowerInvariant();
		string platformName = platform == "android" ? Loc.T("اندروید") : (platform == "windows" ? Loc.T("ویندوز") : Safe(d.Platform, 30));
		string version = Safe(d.AppVersion, 20);
		if (version.Length > 0)
		{
			platformName += " (" + version + ")";
		}
		int rank = Math.Max(0, d.MaxRank);
		StringBuilder sb = new StringBuilder();
		sb.Append(Loc.F("تاریخ ساخت بکاپ: {0}", d.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append('\n');
		sb.Append(Loc.F("ساخته‌شده روی: {0}", platformName)).Append('\n');
		sb.Append(Loc.F("رنک: {0}", Loc.Q(RankService.Get(rank).Name))).Append('\n');
		sb.Append(Loc.F("کتاب‌های تمام‌شده: {0}", d.FinishedBooks)).Append('\n');
		sb.Append(Loc.F("تعداد فونت‌ها: {0}", d.Fonts.Count)).Append('\n');
		if (d.TotalXp >= 0)
		{
			sb.Append(Loc.F("رتبه‌ی XP: {0}", Loc.Q(XpRank.LevelFor(d.TotalXp).DisplayName))).Append('\n');
		}
		sb.Append(Loc.F("کتاب‌های دارای پیشرفت: {0}", d.Records.Count)).Append('\n');
		sb.Append(Loc.T(d.Avatar != null ? "آواتار: دارد" : "آواتار: ندارد")).Append("\n\n");
		sb.Append(Loc.T("تعداد کتاب‌های بکاپ روی رکورد فعلی‌ت اضافه می‌شه و رنک از مجموع حساب می‌شه. پیشرفت کتاب‌ها فقط جلو می‌ره. فایل کتاب‌ها و کاورها دست نمی‌خورن."));
		return sb.ToString();
	}

	public static string BuildResultText(BackupRestoreResult r)
	{
		StringBuilder sb = new StringBuilder();
		if (r.RankAfter > r.RankBefore)
		{
			sb.Append(Loc.F("رنک از {0} به {1} ارتقا پیدا کرد.", Loc.Q(RankService.Get(r.RankBefore).Name), Loc.Q(RankService.Get(r.RankAfter).Name)));
		}
		else
		{
			sb.Append(Loc.F("رنک فعلی: {0}", Loc.Q(RankService.Get(r.RankAfter).Name)));
		}
		sb.Append('\n').Append(Loc.F("مجموع کتاب‌های خوانده‌شده: {0}", r.Total));
		if (r.XpAfter > r.XpBefore)
		{
			sb.Append('\n').Append(Loc.F("رتبه‌ی XP: {0}", Loc.Q(XpRank.LevelFor(r.XpAfter).DisplayName)));
		}

		string fonts = Loc.FN("{0} فونت اضافه شد", r.FontsAdded, r.FontsAdded);
		if (r.FontsDuplicate > 0)
		{
			fonts += Loc.ListSeparator + Loc.FN("{0} فونت تکراری رد شد", r.FontsDuplicate, r.FontsDuplicate);
		}
		if (r.FontsFailed > 0)
		{
			fonts += Loc.ListSeparator + Loc.FN("{0} فونت ناموفق بود", r.FontsFailed, r.FontsFailed);
		}
		sb.Append('\n').Append(fonts);

		if (r.BooksInBackup > 0)
		{
			sb.Append('\n').Append(Loc.FN("پیشرفت {0} کتاب اعمال شد", r.BooksUpdated, r.BooksUpdated));
			if (r.BooksPending > 0)
			{
				sb.Append(Loc.ListSeparator).Append(Loc.FN("{0} کتاب هنوز توی کتابخونه نیست؛ با اضافه شدن خودکار اعمال می‌شه", r.BooksPending, r.BooksPending));
			}
		}

		if (r.AvatarPresent)
		{
			sb.Append('\n').Append(Loc.T(r.AvatarApplied ? "آواتار جایگزین شد." : "آواتار اعمال نشد."));
		}
		return sb.ToString();
	}
}
}

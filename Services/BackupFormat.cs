using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EpubReader.Services
{

// کد خطای بکاپ؛ BackupService هر کد رو به یک پیام فارسی/انگلیسی تبدیل می‌کنه
public enum BackupError
{
	ReadFailed,
	NotZip,
	TooLarge,
	ManifestMissing,
	ManifestInvalid,
	WrongFormat,
	NewerVersion,
	BadEntry,
	MissingFile,
	HashMismatch,
	IntegrityMismatch,
	ProfileInvalid,
	FontInvalid,
	ImageInvalid
}

public sealed class BackupException : Exception
{
	public readonly BackupError Code;

	public BackupException(BackupError code, string detail) : base(code + (string.IsNullOrEmpty(detail) ? "" : ": " + detail))
	{
		Code = code;
	}

	public BackupException(BackupError code) : this(code, null)
	{
	}
}

// فونت داخل بکاپ
public sealed class BackupFont
{
	public string DisplayName = "";
	public string FamilyName = "";
	public string OriginalFileName = "";
	public string Ext = "ttf";          // ttf یا otf
	public byte[] Bytes;
	public string Sha256 = "";
	public string ZipPath = "";
}

// آواتار داخل بکاپ
public sealed class BackupAvatar
{
	public string Ext = "jpg";
	public byte[] Bytes;
	public string Sha256 = "";
	public string ZipPath = "";
}

// محتوای یک بکاپ (هم برای ساخت، هم نتیجه‌ی خوندن و اعتبارسنجی)
public sealed class BackupData
{
	public string Platform = "windows";
	public string AppVersion = "";
	public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
	public string Name = "";
	public int MaxRank = -1;
	public int FinishedBooks;
	// XP کل (-1 = توی فایل نبود). بیرون از تمامیت (integrity) می‌مونه و موقع بازیابی فقط بیشینه‌ی دو طرف گرفته می‌شه
	public int TotalXp = -1;
	// ledger قابل‌حمل XP (فقط ورودی‌های محتوایی)؛ "" = توی فایل نبود. مثل TotalXp بیرون از integrity می‌مونه
	public string XpLedger = "";
	public List<string> SecretThemes = new List<string>();
	public BackupAvatar Avatar;
	public List<BackupFont> Fonts = new List<BackupFont>();
	// رکوردهای پیشرفت کتاب‌ها (records.json؛ فقط نسخه‌ی ۲ به بعد)
	public List<ReadingRecord> Records = new List<ReadingRecord>();
}

// فرمت فایل .novelfabak — فقط منطق خالص (بدون WPF و بدون دسترسی به تنظیمات برنامه).
// طبق «پلن بکاپ ناول‌فا» (ZIP + manifest.json + integrity).
//
// نکته‌ی صداقت فنی: این اعتبارسنجی یعنی «فایل، یک بکاپ سالم و دست‌نخورده‌ی ناول‌فاست».
// چون کلید محرمانه‌ای بین دو اپ نیست، integrity فقط خرابی و دست‌کاری رو تشخیص می‌ده،
// نه هویت سازنده رو در برابر یک مهاجم حرفه‌ای. اگه بعداً لازم شد می‌شه روی همون رشته‌ی canonical
// یک HMAC-SHA256 با کلید مشترک گذاشت (فیلد integrity جاش رو داره).
public static class BackupFormat
{
	public const string FormatName = "novelfa-backup";
	// نسخه‌ی ۲ = نسخه‌ی ۱ + records.json (رکورد پیشرفت هر کتاب، هم‌فرمت با اندروید). بکاپ‌های نسخه‌ی ۱ همچنان خونده می‌شن.
	public const int FormatVersion = 2;
	public const int MaxBooks = 10000;
	public const long MaxRecordsBytes = 8L * 1024L * 1024L;
	public const int MaxTotalXp = 10000000;
	// مانیفست ۶۴ کیلوبایت سقف داره؛ ledgerِ بزرگ‌تر از این نوشته نمی‌شه (بازیابی فقط بیشینه‌ی XP کل رو می‌گیره)
	public const int MaxXpLedgerWriteChars = 30000;
	public const int MaxXpLedgerReadChars = 1000000;
	public const string Extension = ".novelfabak";

	public const int MaxFonts = 200;
	public const long MaxManifestBytes = 64L * 1024L;
	public const long MaxFontBytes = 25L * 1024L * 1024L;
	public const long MaxAvatarBytes = 15L * 1024L * 1024L;
	public const long MaxTotalBytes = 120L * 1024L * 1024L;
	public const int MaxFinishedBooks = 100000;
	public const int MaxSecretThemes = 256;
	public const int MaxNameChars = 100;
	public const int MaxTextChars = 200;

	private const string RecordsPath = "records.json";
	private static readonly Regex AvatarPattern = new Regex(@"^avatar\.(jpg|jpeg|png|bmp|gif|webp)\z", RegexOptions.CultureInvariant);
	private static readonly Regex FontPattern = new Regex(@"^fonts/[A-Za-z0-9_-]{1,32}\.(ttf|otf)\z", RegexOptions.CultureInvariant);
	private static readonly Regex ThemeIdPattern = new Regex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
	private static readonly Regex HexPattern = new Regex(@"^[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

	private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
	private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

	public static bool IsValidThemeId(string id)
	{
		return id != null && ThemeIdPattern.IsMatch(id);
	}

	public static string Sha256Hex(byte[] data)
	{
		using (SHA256 sha = SHA256.Create())
		{
			byte[] h = sha.ComputeHash(data);
			StringBuilder sb = new StringBuilder(64);
			foreach (byte b in h)
			{
				sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
			}
			return sb.ToString();
		}
	}

	// فونت واقعاً فونته؟ TTF: 00 01 00 00 / "true" / "ttcf" — OTF: "OTTO"
	public static bool HasFontMagic(byte[] d)
	{
		if (d == null || d.Length < 12)
		{
			return false;
		}
		bool ttf = d[0] == 0x00 && d[1] == 0x01 && d[2] == 0x00 && d[3] == 0x00;
		bool trueType = d[0] == (byte)'t' && d[1] == (byte)'r' && d[2] == (byte)'u' && d[3] == (byte)'e';
		bool collection = d[0] == (byte)'t' && d[1] == (byte)'t' && d[2] == (byte)'c' && d[3] == (byte)'f';
		bool otf = d[0] == (byte)'O' && d[1] == (byte)'T' && d[2] == (byte)'T' && d[3] == (byte)'O';
		return ttf || trueType || collection || otf;
	}

	// ---------- رشته‌ی canonical و integrity (بخش ۳.۳ پلن؛ باید بایت‌به‌بایت با اندروید یکی باشه) ----------
	// payload: جفت‌های (مسیر داخل زیپ، sha256 با حروف کوچک). records.json هم مثل بقیه‌ی فایل‌ها توی payload میاد
	public static string CanonicalString(string name, long maxRank, long finishedBooks, IEnumerable<string> secretThemes, IEnumerable<KeyValuePair<string, string>> payload)
	{
		List<KeyValuePair<string, string>> files = new List<KeyValuePair<string, string>>(payload);
		files.Sort((KeyValuePair<string, string> a, KeyValuePair<string, string> b) => string.CompareOrdinal(a.Key, b.Key));

		List<string> themes = new List<string>(secretThemes);
		themes.Sort(StringComparer.Ordinal);

		StringBuilder sb = new StringBuilder();
		foreach (KeyValuePair<string, string> f in files)
		{
			sb.Append(f.Key).Append(':').Append(f.Value.ToLowerInvariant()).Append('\n');
		}
		sb.Append("profile:")
			.Append(name ?? "").Append('|')
			.Append(maxRank.ToString(CultureInfo.InvariantCulture)).Append('|')
			.Append(finishedBooks.ToString(CultureInfo.InvariantCulture)).Append('|')
			.Append(string.Join(",", themes.ToArray()));
		return sb.ToString();
	}

	private static string CleanText(string t, int max)
	{
		if (string.IsNullOrEmpty(t))
		{
			return "";
		}
		StringBuilder sb = new StringBuilder(t.Length);
		foreach (char c in t)
		{
			sb.Append(char.IsControl(c) ? ' ' : c);
		}
		string r = sb.ToString().Trim();
		return r.Length > max ? r.Substring(0, max) : r;
	}

	public static string IntegrityOf(string canonical)
	{
		return Sha256Hex(Utf8.GetBytes(canonical));
	}

	// ---------- ساخت ----------
	public static void Write(Stream target, BackupData d)
	{
		List<KeyValuePair<string, string>> payload = new List<KeyValuePair<string, string>>();

		if (d.Avatar != null)
		{
			string ext = (d.Avatar.Ext ?? "").ToLowerInvariant();
			d.Avatar.ZipPath = "avatar." + ext;
			if (!AvatarPattern.IsMatch(d.Avatar.ZipPath))
			{
				throw new ArgumentException("unsupported avatar type: " + ext);
			}
			d.Avatar.Sha256 = Sha256Hex(d.Avatar.Bytes);
			payload.Add(new KeyValuePair<string, string>(d.Avatar.ZipPath, d.Avatar.Sha256));
		}
		for (int i = 0; i < d.Fonts.Count; i++)
		{
			BackupFont f = d.Fonts[i];
			string ext = (f.Ext ?? "").ToLowerInvariant();
			f.ZipPath = "fonts/" + (i + 1).ToString("D3", CultureInfo.InvariantCulture) + "." + ext;
			if (!FontPattern.IsMatch(f.ZipPath))
			{
				throw new ArgumentException("unsupported font type: " + ext);
			}
			f.Sha256 = Sha256Hex(f.Bytes);
			payload.Add(new KeyValuePair<string, string>(f.ZipPath, f.Sha256));
		}

		List<string> themes = new List<string>();
		foreach (string t in d.SecretThemes)
		{
			if (IsValidThemeId(t) && !themes.Contains(t))
			{
				themes.Add(t);
			}
		}
		themes.Sort(StringComparer.Ordinal);

		// رکوردهای پیشرفت (records.json)؛ هش‌اش مثل بقیه‌ی فایل‌ها زیر تمامیت می‌ره
		byte[] recordsBytes = null;
		string recordsSha = "";
		if (d.Records.Count > 0)
		{
			List<ReadingRecord> recs = d.Records.Count > MaxBooks ? d.Records.GetRange(0, MaxBooks) : d.Records;
			recordsBytes = ReadingRecordStore.Serialize(recs);
			if (recordsBytes.LongLength <= MaxRecordsBytes)
			{
				recordsSha = Sha256Hex(recordsBytes);
				payload.Add(new KeyValuePair<string, string>(RecordsPath, recordsSha));
			}
			else
			{
				recordsBytes = null;
			}
		}

		string integrity = IntegrityOf(CanonicalString(d.Name, d.MaxRank, d.FinishedBooks, themes, payload));

		JObject profile = new JObject();
		profile.Add("name", d.Name ?? "");
		profile.Add("maxRank", (long)d.MaxRank);
		profile.Add("finishedBooks", (long)d.FinishedBooks);
		if (d.TotalXp >= 0)
		{
			profile.Add("totalXp", (long)Math.Min(MaxTotalXp, d.TotalXp));
		}
		if (!string.IsNullOrEmpty(d.XpLedger) && d.XpLedger.Length <= MaxXpLedgerWriteChars)
		{
			profile.Add("xpLedger", d.XpLedger);
		}
		List<object> themeList = new List<object>();
		foreach (string t in themes)
		{
			themeList.Add(t);
		}
		profile.Add("secretThemes", themeList);

		object avatarNode = null;
		if (d.Avatar != null)
		{
			JObject a = new JObject();
			a.Add("file", d.Avatar.ZipPath);
			a.Add("sha256", d.Avatar.Sha256);
			a.Add("bytes", (long)d.Avatar.Bytes.Length);
			avatarNode = a;
		}

		List<object> fontList = new List<object>();
		foreach (BackupFont f in d.Fonts)
		{
			JObject fo = new JObject();
			fo.Add("file", f.ZipPath);
			fo.Add("displayName", f.DisplayName ?? "");
			fo.Add("familyName", f.FamilyName ?? "");
			if (!string.IsNullOrEmpty(f.OriginalFileName))
			{
				fo.Add("originalFileName", f.OriginalFileName);
			}
			fo.Add("sha256", f.Sha256);
			fo.Add("bytes", (long)f.Bytes.Length);
			fontList.Add(fo);
		}

		JObject root = new JObject();
		root.Add("format", FormatName);
		root.Add("formatVersion", (long)FormatVersion);
		root.Add("app", "Novelfa");
		root.Add("platform", d.Platform ?? "windows");
		root.Add("appVersion", d.AppVersion ?? "");
		root.Add("createdAt", d.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
		root.Add("profile", profile);
		root.Add("avatar", avatarNode);
		root.Add("fonts", fontList);
		if (recordsBytes != null)
		{
			JObject rn = new JObject();
			rn.Add("file", RecordsPath);
			rn.Add("sha256", recordsSha);
			rn.Add("bytes", (long)recordsBytes.Length);
			root.Add("records", rn);
		}
		else
		{
			root.Add("records", null);
		}
		root.Add("integrity", integrity);

		using (ZipArchive zip = new ZipArchive(target, ZipArchiveMode.Create, true))
		{
			AddEntry(zip, "manifest.json", Utf8.GetBytes(MiniJson.Write(root)));
			if (d.Avatar != null)
			{
				AddEntry(zip, d.Avatar.ZipPath, d.Avatar.Bytes);
			}
			foreach (BackupFont f in d.Fonts)
			{
				AddEntry(zip, f.ZipPath, f.Bytes);
			}
			if (recordsBytes != null)
			{
				AddEntry(zip, RecordsPath, recordsBytes);
			}
		}
	}

	private static void AddEntry(ZipArchive zip, string path, byte[] data)
	{
		ZipArchiveEntry e = zip.CreateEntry(path, CompressionLevel.Optimal);
		using (Stream s = e.Open())
		{
			s.Write(data, 0, data.Length);
		}
	}

	// ---------- خوندن و اعتبارسنجی (بخش ۵ پلن؛ هیچ چیزی اعمال نمی‌شه، فقط BackupData برمی‌گرده یا BackupException) ----------
	// imageCheck: بررسی اینکه بایت‌های آواتار یک تصویر قابل‌خوندنه (WPF توی لایه‌ی بالا)
	public static BackupData Read(Stream source, Func<byte[], bool> imageCheck)
	{
		ZipArchive zip;
		try
		{
			zip = new ZipArchive(source, ZipArchiveMode.Read, true);
		}
		catch (InvalidDataException)
		{
			throw new BackupException(BackupError.NotZip);
		}
		catch (NotSupportedException)
		{
			throw new BackupException(BackupError.NotZip);
		}
		catch (IOException ex)
		{
			throw new BackupException(BackupError.ReadFailed, ex.Message);
		}
		catch (UnauthorizedAccessException ex)
		{
			throw new BackupException(BackupError.ReadFailed, ex.Message);
		}

		using (zip)
		{
			try
			{
				return ReadArchive(zip, imageCheck);
			}
			catch (InvalidDataException)
			{
				// زیپِ خراب (هدر یا داده‌ی فشرده‌ی ناسالم)
				throw new BackupException(BackupError.NotZip);
			}
			catch (NotSupportedException)
			{
				// زیپ رمزدار یا با روش فشرده‌سازی پشتیبانی‌نشده
				throw new BackupException(BackupError.NotZip);
			}
			catch (IOException ex)
			{
				throw new BackupException(BackupError.ReadFailed, ex.Message);
			}
		}
	}

	private static int Kind(string name)
	{
		if (name == "manifest.json") return 1;
		if (AvatarPattern.IsMatch(name)) return 2;
		if (FontPattern.IsMatch(name)) return 3;
		if (name == RecordsPath) return 4;
		return 0;
	}

	private static BackupData ReadArchive(ZipArchive zip, Func<byte[], bool> imageCheck)
	{
		// 1) سقف تعداد، وجود مانیفست، الگوی مسیرها (zip-slip و مسیر ناشناخته)، تکراری نبودن اسم‌ها
		if (zip.Entries.Count > MaxFonts + 3)
		{
			throw new BackupException(BackupError.TooLarge, "too many entries");
		}
		bool hasManifest = false;
		foreach (ZipArchiveEntry e in zip.Entries)
		{
			if (e.FullName == "manifest.json")
			{
				hasManifest = true;
			}
		}
		if (!hasManifest)
		{
			throw new BackupException(BackupError.ManifestMissing);
		}
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (ZipArchiveEntry e in zip.Entries)
		{
			string entryName = e.FullName;
			if (Kind(entryName) == 0)
			{
				throw new BackupException(BackupError.BadEntry, "unexpected path");
			}
			if (!seen.Add(entryName))
			{
				throw new BackupException(BackupError.BadEntry, "duplicate path");
			}
		}

		// 2) خوندن بایت‌ها با شمارنده‌ی واقعی (به هدر زیپ اعتماد نمی‌کنیم)
		Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		long total = 0;
		foreach (ZipArchiveEntry e in zip.Entries)
		{
			int kind = Kind(e.FullName);
			long limit = kind == 1 ? MaxManifestBytes : (kind == 2 ? MaxAvatarBytes : (kind == 4 ? MaxRecordsBytes : MaxFontBytes));
			if (e.Length > limit)
			{
				throw new BackupException(BackupError.TooLarge, e.FullName);
			}
			files[e.FullName] = ReadLimited(e, limit, ref total);
		}

		// 3) مانیفست: خوندن UTF-8 سخت‌گیرانه و پارس JSON
		JObject root;
		try
		{
			byte[] mb = files["manifest.json"];
			int skip = (mb.Length >= 3 && mb[0] == 0xEF && mb[1] == 0xBB && mb[2] == 0xBF) ? 3 : 0;
			string text = Utf8Strict.GetString(mb, skip, mb.Length - skip);
			root = MiniJson.Parse(text) as JObject;
		}
		catch (FormatException ex)
		{
			throw new BackupException(BackupError.ManifestInvalid, ex.Message);
		}
		catch (ArgumentException ex)
		{
			throw new BackupException(BackupError.ManifestInvalid, ex.Message);
		}
		if (root == null)
		{
			throw new BackupException(BackupError.ManifestInvalid, "root is not an object");
		}

		// 4) فرمت و نسخه (قبل از بقیه‌ی فیلدها، چون نسخه‌ی آینده ممکنه شکل دیگه‌ای داشته باشه)
		if (!(root.Get("format") is string) || (string)root.Get("format") != FormatName)
		{
			throw new BackupException(BackupError.WrongFormat);
		}
		if (!(root.Get("formatVersion") is long))
		{
			throw new BackupException(BackupError.ManifestInvalid, "formatVersion");
		}
		long version = (long)root.Get("formatVersion");
		if (version > FormatVersion)
		{
			throw new BackupException(BackupError.NewerVersion);
		}
		if (version < 1)
		{
			throw new BackupException(BackupError.ManifestInvalid, "formatVersion");
		}

		BackupData d = new BackupData();
		d.Platform = RequireString(root, "platform", MaxTextChars);
		d.AppVersion = RequireString(root, "appVersion", MaxTextChars);
		string created = RequireString(root, "createdAt", MaxTextChars);
		DateTimeOffset createdAt;
		if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createdAt))
		{
			throw new BackupException(BackupError.ManifestInvalid, "createdAt");
		}
		d.CreatedAt = createdAt;
		string integrity = RequireString(root, "integrity", 128);

		// profile
		JObject profile = root.Get("profile") as JObject;
		if (profile == null)
		{
			throw new BackupException(BackupError.ManifestInvalid, "profile");
		}
		object nameNode = profile.Get("name");
		if (nameNode != null && !(nameNode is string))
		{
			throw new BackupException(BackupError.ManifestInvalid, "profile.name");
		}
		string name = (string)nameNode ?? "";
		long maxRank = RequireLong(profile, "maxRank");
		long finished = RequireLong(profile, "finishedBooks");
		List<object> themeNodes = profile.Get("secretThemes") as List<object>;
		if (themeNodes == null)
		{
			throw new BackupException(BackupError.ManifestInvalid, "profile.secretThemes");
		}
		List<string> themes = new List<string>();
		foreach (object t in themeNodes)
		{
			if (!(t is string))
			{
				throw new BackupException(BackupError.ManifestInvalid, "profile.secretThemes");
			}
			themes.Add((string)t);
		}

		// XP کل (اختیاری و بیرون از integrity؛ بکاپ‌های قدیمی ندارنش)
		int totalXp = -1;
		object xpNode = profile.Get("totalXp");
		if (xpNode is long)
		{
			long xv = (long)xpNode;
			if (xv < 0 || xv > MaxTotalXp)
			{
				throw new BackupException(BackupError.ProfileInvalid, "totalXp");
			}
			totalXp = (int)xv;
		}
		string xpLedger = "";
		object ledgerNode = profile.Get("xpLedger");
		if (ledgerNode is string && ((string)ledgerNode).Length <= MaxXpLedgerReadChars)
		{
			xpLedger = (string)ledgerNode;
		}

		// avatar + fonts: لیست فایل‌هایی که مانیفست می‌گه
		if (!root.Has("avatar"))
		{
			throw new BackupException(BackupError.ManifestInvalid, "avatar");
		}
		object avatarNode = root.Get("avatar");
		List<object> fontNodes = root.Get("fonts") as List<object>;
		if (fontNodes == null)
		{
			throw new BackupException(BackupError.ManifestInvalid, "fonts");
		}
		if (fontNodes.Count > MaxFonts)
		{
			throw new BackupException(BackupError.TooLarge, "fonts");
		}

		List<KeyValuePair<string, string>> payload = new List<KeyValuePair<string, string>>();
		HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);

		if (avatarNode != null)
		{
			JObject a = avatarNode as JObject;
			if (a == null)
			{
				throw new BackupException(BackupError.ManifestInvalid, "avatar");
			}
			string file = RequireString(a, "file", 64);
			if (Kind(file) != 2)
			{
				throw new BackupException(BackupError.BadEntry, "avatar path");
			}
			BackupAvatar av = new BackupAvatar();
			av.ZipPath = file;
			av.Ext = file.Substring(file.LastIndexOf('.') + 1);
			CheckPayload(files, declared, payload, file, RequireString(a, "sha256", 64), RequireLong(a, "bytes"), out av.Bytes, out av.Sha256);
			d.Avatar = av;
		}
		foreach (object fn in fontNodes)
		{
			JObject fo = fn as JObject;
			if (fo == null)
			{
				throw new BackupException(BackupError.ManifestInvalid, "fonts[]");
			}
			string file = RequireString(fo, "file", 64);
			if (Kind(file) != 3)
			{
				throw new BackupException(BackupError.BadEntry, "font path");
			}
			BackupFont bf = new BackupFont();
			bf.ZipPath = file;
			bf.Ext = file.Substring(file.LastIndexOf('.') + 1);
			bf.DisplayName = RequireString(fo, "displayName", MaxTextChars);
			bf.FamilyName = RequireString(fo, "familyName", MaxTextChars);
			object orig = fo.Get("originalFileName");
			if (orig != null)
			{
				if (!(orig is string) || ((string)orig).Length > MaxTextChars)
				{
					throw new BackupException(BackupError.ManifestInvalid, "originalFileName");
				}
				bf.OriginalFileName = (string)orig;
			}
			CheckPayload(files, declared, payload, file, RequireString(fo, "sha256", 64), RequireLong(fo, "bytes"), out bf.Bytes, out bf.Sha256);
			d.Fonts.Add(bf);
		}

		// records.json (اختیاری؛ نسخه‌ی ۲ به بعد)
		byte[] recordsData = null;
		if (version >= 2 && root.Has("records") && root.Get("records") != null)
		{
			JObject rn = root.Get("records") as JObject;
			if (rn == null)
			{
				throw new BackupException(BackupError.ManifestInvalid, "records");
			}
			string rfile = RequireString(rn, "file", 64);
			if (Kind(rfile) != 4)
			{
				throw new BackupException(BackupError.BadEntry, "records path");
			}
			string rsha;
			CheckPayload(files, declared, payload, rfile, RequireString(rn, "sha256", 64), RequireLong(rn, "bytes"), out recordsData, out rsha);
		}

		// هر فایلی که توی زیپ هست و مانیفست ازش خبر نداره = رد
		foreach (string path in files.Keys)
		{
			if (path != "manifest.json" && !declared.Contains(path))
			{
				throw new BackupException(BackupError.BadEntry, "undeclared file");
			}
		}

		// 6) integrity دوباره حساب و مقایسه می‌شه (با مقادیر خامِ مانیفست)
		string canonical = CanonicalString(name, maxRank, finished, themes, payload);
		if (!string.Equals(IntegrityOf(canonical), integrity, StringComparison.OrdinalIgnoreCase))
		{
			throw new BackupException(BackupError.IntegrityMismatch);
		}

		// 7) بازه‌ی مقادیر پروفایل
		if (maxRank < -1 || maxRank > 6 || finished < 0 || finished > MaxFinishedBooks)
		{
			throw new BackupException(BackupError.ProfileInvalid, "range");
		}
		if (name.Length > MaxNameChars || themes.Count > MaxSecretThemes)
		{
			throw new BackupException(BackupError.ProfileInvalid, "size");
		}
		foreach (string t in themes)
		{
			if (!IsValidThemeId(t))
			{
				throw new BackupException(BackupError.ProfileInvalid, "secret theme id");
			}
		}
		d.Name = name;
		d.MaxRank = (int)maxRank;
		d.FinishedBooks = (int)finished;
		d.SecretThemes = themes;
		d.TotalXp = totalXp;
		d.XpLedger = xpLedger;
		if (recordsData != null)
		{
			d.Records = ReadingRecordStore.Parse(recordsData, MaxBooks);
		}

		// 8) هر فونت واقعاً فونت باشه
		foreach (BackupFont f in d.Fonts)
		{
			if (!HasFontMagic(f.Bytes))
			{
				throw new BackupException(BackupError.FontInvalid, f.ZipPath);
			}
		}

		// 9) آواتار واقعاً تصویر قابل‌خوندن باشه
		if (d.Avatar != null)
		{
			bool ok = false;
			try
			{
				ok = imageCheck != null && imageCheck(d.Avatar.Bytes);
			}
			catch
			{
				ok = false;
			}
			if (!ok)
			{
				throw new BackupException(BackupError.ImageInvalid);
			}
		}
		return d;
	}

	// مسیر موجود باشه، حجم و هش واقعی با مانیفست بخونه
	private static void CheckPayload(Dictionary<string, byte[]> files, HashSet<string> declared, List<KeyValuePair<string, string>> payload,
		string path, string sha, long bytes, out byte[] data, out string shaOut)
	{
		if (!declared.Add(path))
		{
			throw new BackupException(BackupError.ManifestInvalid, "duplicate file in manifest");
		}
		if (!files.TryGetValue(path, out data))
		{
			throw new BackupException(BackupError.MissingFile, path);
		}
		if (!HexPattern.IsMatch(sha))
		{
			throw new BackupException(BackupError.ManifestInvalid, "sha256");
		}
		if (data.LongLength != bytes)
		{
			throw new BackupException(BackupError.HashMismatch, path);
		}
		string actual = Sha256Hex(data);
		if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
		{
			throw new BackupException(BackupError.HashMismatch, path);
		}
		shaOut = actual;
		payload.Add(new KeyValuePair<string, string>(path, actual));
	}

	private static string RequireString(JObject o, string key, int maxChars)
	{
		object v = o.Get(key);
		if (!(v is string))
		{
			throw new BackupException(BackupError.ManifestInvalid, key);
		}
		string s = (string)v;
		if (s.Length > maxChars)
		{
			throw new BackupException(BackupError.ManifestInvalid, key + " too long");
		}
		return s;
	}

	private static long RequireLong(JObject o, string key)
	{
		object v = o.Get(key);
		if (!(v is long))
		{
			throw new BackupException(BackupError.ManifestInvalid, key);
		}
		return (long)v;
	}

	private static byte[] ReadLimited(ZipArchiveEntry e, long limit, ref long total)
	{
		using (Stream s = e.Open())
		using (MemoryStream ms = new MemoryStream())
		{
			byte[] buf = new byte[81920];
			int n;
			while ((n = s.Read(buf, 0, buf.Length)) > 0)
			{
				total += n;
				if (total > MaxTotalBytes || ms.Length + n > limit)
				{
					throw new BackupException(BackupError.TooLarge, e.FullName);
				}
				ms.Write(buf, 0, n);
			}
			return ms.ToArray();
		}
	}
}
}

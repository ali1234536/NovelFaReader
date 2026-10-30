using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using EpubReader.Models;

namespace EpubReader.Services
{

// رکورد پیشرفت یک کتاب (هم‌فرمت با records.json اندروید). هویت کتاب: اول SHA-256 خود فایل، بعد شناسه‌ی OPF
public sealed class ReadingRecord
{
	public string Sha256 = "";
	public string EpubId = "";
	public string Title = "";
	public string FileName = "";
	public int Chapter;
	public int Chapters;
	public double Percent;
	public bool Finished;
	public bool Favorite;
	public string Collection;
	public long UpdatedAt;

	public bool SameBookAs(ReadingRecord o)
	{
		return (Sha256.Length > 0 && Sha256 == o.Sha256) || (EpubId.Length > 0 && EpubId == o.EpubId);
	}

	// ادغام دو رکورد یک کتاب: جلوتر برنده است، علاقه‌مندی و تمام‌شده چسبنده‌ان، کلیدهای هویت اجتماع می‌شن
	public static ReadingRecord Merge(ReadingRecord a, ReadingRecord b)
	{
		ReadingRecord winner;
		if (a.Finished != b.Finished)
		{
			winner = a.Finished ? a : b;
		}
		else if (a.Percent != b.Percent)
		{
			winner = a.Percent > b.Percent ? a : b;
		}
		else
		{
			winner = a.UpdatedAt >= b.UpdatedAt ? a : b;
		}
		ReadingRecord loser = ReferenceEquals(winner, a) ? b : a;
		ReadingRecord r = new ReadingRecord();
		r.Sha256 = winner.Sha256.Length > 0 ? winner.Sha256 : loser.Sha256;
		r.EpubId = winner.EpubId.Length > 0 ? winner.EpubId : loser.EpubId;
		r.Title = winner.Title.Length > 0 ? winner.Title : loser.Title;
		r.FileName = winner.FileName.Length > 0 ? winner.FileName : loser.FileName;
		r.Chapter = winner.Chapter;
		r.Chapters = winner.Chapters;
		r.Percent = winner.Percent;
		r.Finished = a.Finished || b.Finished;
		r.Favorite = a.Favorite || b.Favorite;
		r.Collection = !string.IsNullOrWhiteSpace(winner.Collection) ? winner.Collection : loser.Collection;
		r.UpdatedAt = Math.Max(a.UpdatedAt, b.UpdatedAt);
		return r;
	}

	public static List<ReadingRecord> MergeAll(IEnumerable<ReadingRecord> baseList, IEnumerable<ReadingRecord> incoming)
	{
		List<ReadingRecord> outList = new List<ReadingRecord>(baseList);
		foreach (ReadingRecord rec in incoming)
		{
			int idx = outList.FindIndex((ReadingRecord x) => x.SameBookAs(rec));
			if (idx >= 0)
			{
				outList[idx] = Merge(outList[idx], rec);
			}
			else
			{
				outList.Add(rec);
			}
		}
		return outList;
	}

	private static string Clean(string s, int max)
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
		string r = sb.ToString().Trim();
		return r.Length > max ? r.Substring(0, max) : r;
	}

	private static bool IsHex64(string s)
	{
		if (s == null || s.Length != 64)
		{
			return false;
		}
		foreach (char c in s)
		{
			if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
			{
				return false;
			}
		}
		return true;
	}

	public JObject ToJson()
	{
		JObject o = new JObject();
		o.Add("sha256", Sha256);
		o.Add("epubId", EpubId);
		o.Add("title", Title);
		o.Add("fileName", FileName);
		o.Add("chapter", (long)Chapter);
		o.Add("chapters", (long)Chapters);
		o.Add("percent", Percent);
		o.Add("finished", Finished);
		o.Add("favorite", Favorite);
		o.Add("collection", string.IsNullOrWhiteSpace(Collection) ? null : Collection);
		o.Add("updatedAt", UpdatedAt);
		return o;
	}

	// پارس سخت‌گیر-نه: رکورد ناقص null برمی‌گردونه و رد می‌شه
	public static ReadingRecord FromJson(JObject o)
	{
		string sha = (o.Get("sha256") as string ?? "").ToLowerInvariant();
		if (!IsHex64(sha))
		{
			sha = "";
		}
		string id = Clean(o.Get("epubId") as string, 256);
		if (sha.Length == 0 && id.Length == 0)
		{
			return null;
		}
		ReadingRecord r = new ReadingRecord();
		r.Sha256 = sha;
		r.EpubId = id;
		r.Title = Clean(o.Get("title") as string, 512);
		r.FileName = Clean(o.Get("fileName") as string, 256);
		r.Chapters = (int)Math.Max(0, Math.Min(100000, AsLong(o.Get("chapters"))));
		r.Chapter = (int)Math.Max(0, Math.Min(Math.Max(0, r.Chapters - 1), AsLong(o.Get("chapter"))));
		object p = o.Get("percent");
		double pv = p is double ? (double)p : (p is long ? (double)(long)p : 0.0);
		r.Percent = Math.Max(0.0, Math.Min(100.0, pv));
		r.Finished = (o.Get("finished") is bool) && (bool)o.Get("finished");
		r.Favorite = (o.Get("favorite") is bool) && (bool)o.Get("favorite");
		string col = o.Get("collection") as string;
		r.Collection = string.IsNullOrWhiteSpace(col) ? null : Clean(col, 128);
		r.UpdatedAt = Math.Max(0, AsLong(o.Get("updatedAt")));
		return r;
	}

	private static long AsLong(object v)
	{
		if (v is long)
		{
			return (long)v;
		}
		if (v is double)
		{
			return (long)(double)v;
		}
		return 0;
	}

	// رکورد یک کتاب کتابخونه (هیچ‌وقت برای کتاب خصوصی صدا زده نشه)
	public static ReadingRecord FromBook(Book b, string sha256, string epubId)
	{
		ReadingRecord r = new ReadingRecord();
		r.Sha256 = sha256 ?? "";
		r.EpubId = (epubId ?? "").Trim();
		r.Title = b.Title ?? "";
		r.FileName = string.IsNullOrEmpty(b.FilePath) ? "" : Path.GetFileName(b.FilePath);
		r.Chapter = Math.Max(0, b.LastChapterIndex);
		r.Chapters = Math.Max(0, b.TotalChapters);
		r.Percent = b.TotalChapters > 0 ? Math.Min(100.0, (b.LastChapterIndex + 1) * 100.0 / b.TotalChapters) : 0.0;
		r.Finished = b.IsCompleted;
		r.Favorite = b.IsFavorite;
		r.Collection = string.IsNullOrWhiteSpace(b.CollectionName) ? null : b.CollectionName;
		r.UpdatedAt = ToMillis(b.LastOpenedDate);
		if (r.UpdatedAt == 0)
		{
			r.UpdatedAt = ToMillis(b.DateAdded);
		}
		return r;
	}

	public static long ToMillis(string text)
	{
		DateTime dt;
		if (string.IsNullOrWhiteSpace(text) || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
		{
			return 0;
		}
		return (long)(dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
	}

	public static string FromMillis(long ms)
	{
		return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
	}

	// پیشرفت رو فقط جلو می‌بره؛ علاقه‌مندی و مجموعه فقط پر می‌شن، هیچ‌وقت پاک نمی‌شن. true = چیزی عوض شد
	public bool ApplyTo(Book book)
	{
		bool changed = false;
		int total = book.TotalChapters;
		if (total > 0)
		{
			int target;
			if (Finished)
			{
				target = total - 1;
			}
			else if (Chapters == total)
			{
				target = Chapter;
			}
			else if (Chapters > 0)
			{
				target = (int)Math.Round(Percent / 100.0 * total) - 1;
			}
			else
			{
				target = Chapter;
			}
			target = Math.Max(0, Math.Min(total - 1, target));
			int before = book.LastChapterIndex;
			if (target > before)
			{
				book.LastChapterIndex = target;
				changed = true;
			}
			bool needsOpened = Finished || book.LastChapterIndex > before;
			if (needsOpened && string.IsNullOrEmpty(book.LastOpenedDate) && UpdatedAt > 0)
			{
				book.LastOpenedDate = FromMillis(UpdatedAt);
				changed = true;
			}
		}
		if (Favorite && !book.IsFavorite)
		{
			book.IsFavorite = true;
			changed = true;
		}
		if (string.IsNullOrWhiteSpace(book.CollectionName) && !string.IsNullOrWhiteSpace(Collection))
		{
			book.CollectionName = Collection;
			CollectionRegistry.Add(Collection);
			changed = true;
		}
		return changed;
	}
}

// ذخیره‌ی محلی رکوردهای کتاب‌هایی که الان توی کتابخونه نیستن (بعد از بازیابی بکاپ یا حذف کتاب)
public static class ReadingRecordStore
{
	private static List<ReadingRecord> cache;
	private static readonly object gate = new object();

	private static string FilePath
	{
		get
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
			if (!Directory.Exists(folder))
			{
				Directory.CreateDirectory(folder);
			}
			return Path.Combine(folder, "reading_records.json");
		}
	}

	// پوشش فرمت envelope: {"v":1,"books":[...]}
	public static byte[] Serialize(IEnumerable<ReadingRecord> records)
	{
		List<object> arr = new List<object>();
		foreach (ReadingRecord r in records)
		{
			arr.Add(r.ToJson());
		}
		JObject root = new JObject();
		root.Add("v", 1L);
		root.Add("books", arr);
		return new UTF8Encoding(false).GetBytes(MiniJson.Write(root));
	}

	public static List<ReadingRecord> Parse(byte[] bytes, int max)
	{
		List<ReadingRecord> list = new List<ReadingRecord>();
		try
		{
			int skip = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
			JObject root = MiniJson.Parse(Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip)) as JObject;
			List<object> arr = root == null ? null : root.Get("books") as List<object>;
			if (arr == null)
			{
				return list;
			}
			for (int i = 0; i < arr.Count && i < max; i++)
			{
				JObject o = arr[i] as JObject;
				ReadingRecord r = o == null ? null : ReadingRecord.FromJson(o);
				if (r != null)
				{
					list.Add(r);
				}
			}
		}
		catch
		{
			list.Clear();
		}
		return list;
	}

	private static List<ReadingRecord> Loaded()
	{
		if (cache == null)
		{
			try
			{
				cache = File.Exists(FilePath) ? Parse(File.ReadAllBytes(FilePath), 10000) : new List<ReadingRecord>();
			}
			catch
			{
				cache = new List<ReadingRecord>();
			}
		}
		return cache;
	}

	private static void Persist()
	{
		try
		{
			File.WriteAllBytes(FilePath, Serialize(cache ?? new List<ReadingRecord>()));
		}
		catch
		{
		}
	}

	public static List<ReadingRecord> All()
	{
		lock (gate)
		{
			return new List<ReadingRecord>(Loaded());
		}
	}

	public static bool IsEmpty()
	{
		lock (gate)
		{
			return Loaded().Count == 0;
		}
	}

	public static ReadingRecord Find(string sha256, string epubId)
	{
		lock (gate)
		{
			List<ReadingRecord> list = Loaded();
			if (!string.IsNullOrEmpty(sha256))
			{
				ReadingRecord bySha = list.Find((ReadingRecord x) => x.Sha256.Length > 0 && x.Sha256 == sha256);
				if (bySha != null)
				{
					return bySha;
				}
			}
			if (!string.IsNullOrEmpty(epubId))
			{
				return list.Find((ReadingRecord x) => x.EpubId.Length > 0 && x.EpubId == epubId);
			}
			return null;
		}
	}

	public static void Upsert(ReadingRecord rec)
	{
		MergeAll(new ReadingRecord[] { rec });
	}

	public static void MergeAll(IEnumerable<ReadingRecord> incoming)
	{
		lock (gate)
		{
			cache = ReadingRecord.MergeAll(Loaded(), incoming);
			Persist();
		}
	}

	// ---------- هویت فایل ----------
	public static string Sha256OfFile(string path)
	{
		using (FileStream fs = File.OpenRead(path))
		using (SHA256 sha = SHA256.Create())
		{
			byte[] h = sha.ComputeHash(fs);
			StringBuilder sb = new StringBuilder(64);
			foreach (byte b in h)
			{
				sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
			}
			return sb.ToString();
		}
	}

	private static byte[] EntryBytes(ZipArchive zip, string name, long cap)
	{
		ZipArchiveEntry e = zip.GetEntry(name);
		if (e == null)
		{
			foreach (ZipArchiveEntry x in zip.Entries)
			{
				if (string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase))
				{
					e = x;
					break;
				}
			}
		}
		if (e == null || e.Length > cap)
		{
			return null;
		}
		using (Stream s = e.Open())
		using (MemoryStream ms = new MemoryStream())
		{
			s.CopyTo(ms);
			return ms.ToArray();
		}
	}

	private static XmlDocument ParseXml(byte[] bytes)
	{
		try
		{
			XmlReaderSettings st = new XmlReaderSettings();
			st.DtdProcessing = DtdProcessing.Prohibit;
			st.XmlResolver = null;
			XmlDocument doc = new XmlDocument();
			doc.XmlResolver = null;
			using (XmlReader xr = XmlReader.Create(new MemoryStream(bytes), st))
			{
				doc.Load(xr);
			}
			return doc;
		}
		catch
		{
			return null;
		}
	}

	// dc:identifier از OPF (شناسه‌ی شبیه URN/UUID ترجیح داده می‌شه)؛ null اگه نشد
	public static string ReadEpubIdentifier(string epubPath)
	{
		try
		{
			using (ZipArchive zip = ZipFile.OpenRead(epubPath))
			{
				byte[] cb = EntryBytes(zip, "META-INF/container.xml", 1024 * 1024);
				XmlDocument container = cb == null ? null : ParseXml(cb);
				if (container == null)
				{
					return null;
				}
				string opfPath = null;
				foreach (XmlNode n in container.GetElementsByTagName("rootfile"))
				{
					XmlAttribute a = n.Attributes == null ? null : n.Attributes["full-path"];
					if (a != null && !string.IsNullOrWhiteSpace(a.Value))
					{
						opfPath = a.Value.Trim();
						break;
					}
				}
				byte[] ob = opfPath == null ? null : EntryBytes(zip, opfPath, 10L * 1024 * 1024);
				XmlDocument opf = ob == null ? null : ParseXml(ob);
				if (opf == null)
				{
					return null;
				}
				string fallback = null;
				foreach (XmlNode n in opf.GetElementsByTagName("*"))
				{
					if (n.NamespaceURI != "http://purl.org/dc/elements/1.1/" || n.LocalName != "identifier")
					{
						continue;
					}
					string text = (n.InnerText ?? "").Trim();
					if (text.Length == 0)
					{
						continue;
					}
					if (text.IndexOf(':') >= 0 || (text.IndexOf('-') >= 0 && text.Length >= 32))
					{
						return text.Length > 256 ? text.Substring(0, 256) : text;
					}
					if (fallback == null)
					{
						fallback = text;
					}
				}
				return fallback == null ? null : (fallback.Length > 256 ? fallback.Substring(0, 256) : fallback);
			}
		}
		catch
		{
			return null;
		}
	}
}
}

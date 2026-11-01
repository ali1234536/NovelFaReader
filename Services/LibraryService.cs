using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EpubReader.Models;

namespace EpubReader.Services
{

/// <summary>مرتب‌سازی طبیعی: «جلد ۲» قبل از «جلد ۱۰» میاد.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
	[System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int StrCmpLogicalW(string x, string y);

	public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

	public int Compare(string x, string y)
	{
		return StrCmpLogicalW(x ?? "", y ?? "");
	}
}

public static class LibraryService
{
	private static string LibraryFilePath
	{
		get
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
			if (!Directory.Exists(folder))
			{
				Directory.CreateDirectory(folder);
			}
			return Path.Combine(folder, "library.json");
		}
	}

	// اگه خوندن کتابخونه خطا بده، نباید یه لیست خالی جاش ذخیره بشه (وگرنه همه‌ی کتاب‌ها پاک می‌شن)
	private static bool lastLoadFailed = false;

	public static List<Book> LoadBooks()
	{
		lastLoadFailed = false;
		if (!File.Exists(LibraryFilePath))
		{
			return new List<Book>();
		}
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				string json = File.ReadAllText(LibraryFilePath, Encoding.UTF8);
				List<Book> loaded = SimpleJson.DeserializeBooks(json);
				AssignDisplayTitles(loaded);
				return loaded;
			}
			catch (IOException)
			{
				// ممکنه فایل لحظه‌ای قفل باشه (مثلاً آنتی‌ویروس)؛ دوباره تلاش کن
				System.Threading.Thread.Sleep(150);
			}
			catch
			{
				break;
			}
		}
		lastLoadFailed = true;
		BackupBrokenLibrary();
		return new List<Book>();
	}

	// فایل تشخیصی: هر بار که کتابی باز می‌شه اینجا ثبت می‌شه (برای پیدا کردن باگ باز شدن کتاب اشتباه)
	public static void LogOpen(string message)
	{
		try
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
			string path = Path.Combine(folder, "open.log");
			FileInfo fi = new FileInfo(path);
			if (fi.Exists && fi.Length > 200 * 1024)
			{
				File.Delete(path);
			}
			File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
		}
		catch
		{
		}
	}

	private static bool IsGenericTitle(string title)
	{
		if (string.IsNullOrWhiteSpace(title))
		{
			return true;
		}
		string t = title.Trim().Replace("\u200c", " ");
		return t == "کتاب ترجمه شده" || t.Equals("unknown", StringComparison.OrdinalIgnoreCase) || t.Equals("untitled", StringComparison.OrdinalIgnoreCase);
	}

	// کتاب‌هایی که اسم یکسان یا کلی (مثل «کتاب ترجمه‌شده») دارن، با اسم فایلشون نشون داده می‌شن تا از هم قابل تشخیص باشن
	// اسم فایل تمیز: خط‌تیره‌ی زیرین → فاصله، حذف برچسب «@NovelFa» و فاصله/خط‌تیره‌ی اضافه‌ی آخر
	public static string CleanFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return name;
		}
		string t = System.Text.RegularExpressions.Regex.Replace(name, "[_\\s]*@NovelFa", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		t = t.Replace('_', ' ');
		t = System.Text.RegularExpressions.Regex.Replace(t, "\\s{2,}", " ");
		t = t.Trim().Trim('-', ' ', '.');
		return t.Length == 0 ? name : t;
	}

	private static void AssignDisplayTitles(List<Book> list)
	{
		foreach (Book b in list)
		{
			// اسم کتاب = اسم فایل (همون چیزی که کاربر توی پوشه می‌بینه)؛ عنوان داخل فایل اغلب شلوغ یا یکسانه (مثل اسم سایت‌ها یا چند جلد با یک عنوان)
			if (!string.IsNullOrEmpty(b.FilePath))
			{
				string name = Path.GetFileNameWithoutExtension(b.FilePath);
				string cleaned = CleanFileName(name);
				b.DisplayTitle = string.IsNullOrWhiteSpace(cleaned) ? b.Title : cleaned;
			}
		}
	}

	private static void BackupBrokenLibrary()
	{
		try
		{
			File.Copy(LibraryFilePath, LibraryFilePath + ".broken", true);
		}
		catch
		{
		}
	}

	public static void SaveBooks(List<Book> books)
	{
		if (lastLoadFailed)
		{
			return;
		}
		string json = SimpleJson.SerializeBooks(books);
		string path = LibraryFilePath;
		string tempPath = path + ".tmp";
		File.WriteAllText(tempPath, json, Encoding.UTF8);
		if (File.Exists(path))
		{
			File.Replace(tempPath, path, path + ".bak");
		}
		else
		{
			File.Move(tempPath, path);
		}
	}

	public static void RemoveBook(string filePath)
	{
		List<Book> books = LoadBooks();
		CaptureRecord(books.Find((Book b) => b.FilePath == filePath));
		books.RemoveAll((Book b) => b.FilePath == filePath);
		SaveBooks(books);
	}

	public static void AddOrUpdateBook(Book book)
	{
		List<Book> books = LoadBooks();
		Book existing = books.Find((Book b) => b.FilePath == book.FilePath);
		if (existing != null)
		{
			existing.LastChapterIndex = book.LastChapterIndex;
			existing.Title = book.Title;
			existing.TotalChapters = book.TotalChapters;
			existing.FontFamily = book.FontFamily;
			existing.FontSize = book.FontSize;
			existing.IsDarkMode = book.IsDarkMode;
			existing.Theme = book.Theme;
			existing.LineHeight = book.LineHeight;
			existing.SideMargin = book.SideMargin;
			existing.ParagraphSpacing = book.ParagraphSpacing;
			if (!string.IsNullOrEmpty(book.CoverImagePath))
			{
				existing.CoverImagePath = book.CoverImagePath;
			}
		}
		else
		{
			ApplyPendingProgress(book);
			books.Add(book);
		}
		SaveBooks(books);
	}

	// ---------- رکوردهای پیشرفت (هم‌فرمت با اندروید) ----------
	// موقع اضافه شدن کتاب تازه: اگه رکوردی برای همین فایل (SHA-256 یا شناسه‌ی OPF) هست اعمال می‌شه
	private static void ApplyPendingProgress(Book book)
	{
		if (book == null || book.IsPrivate || string.IsNullOrEmpty(book.FilePath) || ReadingRecordStore.IsEmpty() || !File.Exists(book.FilePath))
		{
			return;
		}
		try
		{
			ReadingRecord rec = ReadingRecordStore.Find(ReadingRecordStore.Sha256OfFile(book.FilePath), ReadingRecordStore.ReadEpubIdentifier(book.FilePath));
			if (rec != null)
			{
				rec.ApplyTo(book);
			}
		}
		catch
		{
		}
	}

	// قبل از حذف کتاب، جاش توی رکوردها نگه داشته می‌شه (کتاب خصوصی هرگز)
	private static void CaptureRecord(Book book)
	{
		if (book == null || book.IsPrivate || string.IsNullOrEmpty(book.FilePath) || !File.Exists(book.FilePath))
		{
			return;
		}
		try
		{
			ReadingRecordStore.Upsert(ReadingRecord.FromBook(book, ReadingRecordStore.Sha256OfFile(book.FilePath), ReadingRecordStore.ReadEpubIdentifier(book.FilePath)));
		}
		catch
		{
		}
	}

	// بازیابی بکاپ: رکوردها توی فروشگاه محلی ادغام و روی کتاب‌های فعلی اعمال می‌شن. تعداد کتاب‌های اعمال‌شده برمی‌گرده
	public static int ApplyBackupRecords(List<ReadingRecord> items)
	{
		if (items == null || items.Count == 0)
		{
			return 0;
		}
		ReadingRecordStore.MergeAll(items);
		List<Book> books = LoadBooks();
		bool canSave = !lastLoadFailed;
		int updated = 0;
		bool changed = false;
		foreach (Book book in books)
		{
			if (book.IsPrivate || string.IsNullOrEmpty(book.FilePath) || !File.Exists(book.FilePath))
			{
				continue;
			}
			try
			{
				ReadingRecord rec = ReadingRecordStore.Find(ReadingRecordStore.Sha256OfFile(book.FilePath), ReadingRecordStore.ReadEpubIdentifier(book.FilePath));
				if (rec != null && rec.ApplyTo(book))
				{
					updated++;
					changed = true;
				}
			}
			catch
			{
			}
		}
		if (changed && canSave)
		{
			SaveBooks(books);
		}
		return updated;
	}

	private static readonly HashSet<string> openedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public static bool WasOpenedThisSession(string filePath)
	{
		return filePath != null && openedThisSession.Contains(filePath);
	}

	public static void MarkAsOpened(string filePath)
	{
		if (filePath != null)
		{
			openedThisSession.Add(filePath);
		}
		List<Book> books = LoadBooks();
		Book existing = books.Find((Book b) => b.FilePath == filePath);
		if (existing != null)
		{
			existing.LastOpenedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			SaveBooks(books);
		}
	}

	public static void ResetProgress(string filePath)
	{
		List<Book> books = LoadBooks();
		Book existing = books.Find((Book b) => b.FilePath == filePath);
		if (existing != null)
		{
			existing.LastChapterIndex = 0;
			existing.LastOpenedDate = "";
			SaveBooks(books);
		}
	}

	public static void TogglePrivate(string filePath)
	{
		List<Book> books = LoadBooks();
		Book existing = books.Find((Book b) => b.FilePath == filePath);
		if (existing != null)
		{
			existing.IsPrivate = !existing.IsPrivate;
			SaveBooks(books);
		}
	}

	public static void ToggleFavorite(string filePath)
	{
		List<Book> books = LoadBooks();
		Book existing = books.Find((Book b) => b.FilePath == filePath);
		if (existing != null)
		{
			existing.IsFavorite = !existing.IsFavorite;
			SaveBooks(books);
		}
	}

	public static void SetCollection(string filePath, string collectionName)
	{
		List<Book> books = LoadBooks();
		Book existing = books.Find((Book b) => b.FilePath == filePath);
		if (existing != null)
		{
			existing.CollectionName = collectionName;
			SaveBooks(books);
		}
	}

	public static void RemoveFromCollection(string filePath)
	{
		List<Book> books = LoadBooks();
		Book existing = books.Find((Book b) => b.FilePath == filePath);
		if (existing != null)
		{
			existing.CollectionName = "";
			SaveBooks(books);
		}
	}

	public static void RenameCollection(string oldName, string newName)
	{
		CollectionRegistry.Rename(oldName, newName);
		List<Book> books = LoadBooks();
		bool changed = false;
		foreach (Book b in books.Where((Book book) => book.CollectionName == oldName))
		{
			b.CollectionName = newName;
			changed = true;
		}
		if (changed)
		{
			SaveBooks(books);
		}
	}

	public static List<string> GetAllCollectionNames()
	{
		List<Book> books = LoadBooks();
		List<string> names = (from b in books
			where !string.IsNullOrEmpty(b.CollectionName)
			select b.CollectionName).Distinct().ToList();
		// مجموعه‌هایی که دستی ساخته شدن و هنوز خالین هم باید برای انتخاب دیده بشن
		foreach (string n in CollectionRegistry.Load())
		{
			if (!names.Contains(n, StringComparer.OrdinalIgnoreCase))
			{
				names.Add(n);
			}
		}
		return names;
	}

	// مجموعه رو حذف می‌کنه؛ کتاب‌ها می‌مونن و فقط از مجموعه بیرون میان
	public static void DeleteCollection(string name)
	{
		CollectionRegistry.Remove(name);
		List<Book> books = LoadBooks();
		bool changed = false;
		foreach (Book b in books.Where((Book book) => string.Equals(book.CollectionName, name, StringComparison.OrdinalIgnoreCase)))
		{
			b.CollectionName = "";
			changed = true;
		}
		if (changed)
		{
			SaveBooks(books);
		}
	}

	public static void CleanupUnusedCovers()
	{
		try
		{
			List<Book> books = LoadBooks();
			if (books.Count == 0)
			{
				return;
			}
			string coversFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp", "Covers");
			if (!Directory.Exists(coversFolder))
			{
				return;
			}
			HashSet<string> usedCovers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (Book b in books)
			{
				if (!string.IsNullOrEmpty(b.CoverImagePath))
				{
					usedCovers.Add(Path.GetFullPath(b.CoverImagePath));
				}
			}
			string[] files = Directory.GetFiles(coversFolder);
			foreach (string file in files)
			{
				if (!usedCovers.Contains(Path.GetFullPath(file)))
				{
					try
					{
						File.Delete(file);
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}
}
}

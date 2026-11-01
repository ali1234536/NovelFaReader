using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EpubReader.Models;

namespace EpubReader.Services
{

// فرمت‌های غیر EPUB (PDF، CBZ/ZIP، TXT، Markdown، DOCX) بدون ساختن هیچ فایل کتاب و مستقیم از خود فایل باز می‌شن.
// کتابخونه مسیر فایل اصلی رو نگه می‌داره؛ این کلاس فقط یه «کتاب» سبک برای نمایشگر اختصاصی اون فرمت آماده می‌کنه.
public static class NativeBooks
{
	public static bool IsNative(string path)
	{
		string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
		return ext == ".pdf" || ext == ".cbz" || ext == ".zip" || ext == ".txt" || ext == ".text" || ext == ".md" || ext == ".markdown" || ext == ".mdown" || ext == ".docx";
	}

	// EPUB معمولی مثل قبل؛ بقیه‌ی فرمت‌ها مستقیم
	public static EpubBook Open(string path)
	{
		if (IsNative(path))
		{
			return LoadNative(path);
		}
		return EpubParser.Load(path);
	}

	public static string DocKindOf(string path)
	{
		switch (Path.GetExtension(path ?? "").ToLowerInvariant())
		{
			case ".txt":
			case ".text":
				return "txt";
			case ".md":
			case ".markdown":
			case ".mdown":
				return "md";
			case ".docx":
				return "docx";
		}
		return null;
	}

	private static void WritePlaceholder(string work, int index)
	{
		File.WriteAllText(Path.Combine(work, "ch" + (index + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".xhtml"), "<html><head><meta charset=\"utf-8\"></head><body><p></p></body></html>", new UTF8Encoding(false));
	}

	private static EpubBook LoadNative(string path)
	{
		string work = Path.Combine(Path.GetTempPath(), "EpubNative_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try
		{
			EpubBook book = new EpubBook();
			book.ExtractPath = work;
			book.NativePath = path;
			string ext = Path.GetExtension(path).ToLowerInvariant();
			string title = TextBookParser.NameWithoutExtension(Path.GetFileName(path));
			if (ext == ".pdf")
			{
				book.NativeKind = "pdf";
				book.Title = title;
				WritePlaceholder(work, 0);
				book.Chapters.Add(new ChapterItem { Title = title, FilePath = Path.Combine(work, "ch0001.xhtml") });
			}
			else if (ext == ".cbz" || ext == ".zip")
			{
				LoadComic(path, work, book, title);
			}
			else
			{
				book.NativeKind = DocKindOf(path);
				ParsedBook parsed = BookConverter.ParseDocument(path, work);
				book.Title = string.IsNullOrWhiteSpace(parsed.Title) ? title : parsed.Title;
				foreach (ParsedChapter ch in parsed.Chapters)
				{
					book.Chapters.Add(new ChapterItem { Title = ch.Title, FilePath = ch.File });
				}
			}
			return book;
		}
		catch
		{
			try
			{
				Directory.Delete(work, true);
			}
			catch
			{
			}
			throw;
		}
	}

	private static void LoadComic(string path, string work, EpubBook book, string title)
	{
		book.NativeKind = "comic";
		book.Title = title;
		List<string> sorted;
		List<string> files = new List<string>();
		try
		{
			using (ZipArchive zip = ZipFile.OpenRead(path))
			{
				Dictionary<string, ZipArchiveEntry> byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
				foreach (ZipArchiveEntry e in zip.Entries)
				{
					string n = e.FullName.Replace('\\', '/');
					if (!byName.ContainsKey(n))
					{
						byName[n] = e;
					}
				}
				sorted = CbzParser.SortedImageNames(byName.Keys);
				if (sorted.Count > 0)
				{
					// عکس‌ها توی پوشه‌ی موقت باز می‌شن و با هاست مجازیِ پوشه‌ای (بدون نیاز به اینترنت) نشون داده می‌شن؛ بعد از بستن کتاب پاک می‌شن
					string mediaDir = Path.Combine(work, "media");
					Directory.CreateDirectory(mediaDir);
					for (int i = 0; i < sorted.Count; i++)
					{
						string ext = Path.GetExtension(sorted[i]).ToLowerInvariant();
						string rel = "media/p" + (i + 1).ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ext;
						string full = Path.Combine(work, rel.Replace('/', Path.DirectorySeparatorChar));
						using (Stream src = byName[sorted[i]].Open())
						using (FileStream dst = new FileStream(full, FileMode.Create, FileAccess.Write))
						{
							src.CopyTo(dst);
						}
						files.Add(rel);
					}
					book.CoverImagePath = SaveComicCover(Path.Combine(work, files[0].Replace('/', Path.DirectorySeparatorChar)), path);
				}
			}
		}
		catch (InvalidDataException)
		{
			throw new InvalidDataException(Loc.T("این فایل ZIP/CBZ خراب یا ناخوانده."));
		}
		if (sorted.Count == 0)
		{
			throw new InvalidDataException(Loc.T("این ZIP عکسی نداره (فقط CBZ/ZIPِ تصویری پشتیبانی می‌شه)."));
		}
		book.NativePages = files;
		List<KeyValuePair<string, int>> starts = new List<KeyValuePair<string, int>>();
		List<CbzParser.CbzChapter> chapters = CbzParser.ChaptersForEntries(sorted);
		if (chapters.Count > 0)
		{
			for (int c = 0; c < chapters.Count; c++)
			{
				starts.Add(new KeyValuePair<string, int>(chapters[c].Title, c == 0 ? 0 : chapters[c].FirstPageIndex));
			}
		}
		else
		{
			for (int s = 0; s < sorted.Count; s += CbzParser.PagesPerSection)
			{
				starts.Add(new KeyValuePair<string, int>(Loc.F("بخش {0}", starts.Count + 1), s));
			}
		}
		book.NativeStarts = starts;
		for (int i = 0; i < starts.Count; i++)
		{
			WritePlaceholder(work, i);
			book.Chapters.Add(new ChapterItem { Title = starts[i].Key, FilePath = Path.Combine(work, "ch" + (i + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".xhtml") });
		}
	}

	// جلد کمیک = صفحه‌ی اول؛ یک‌بار توی پوشه‌ی جلدها ذخیره می‌شه
	private static string SaveComicCover(string firstImageFile, string sourcePath)
	{
		try
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp", "Covers");
			Directory.CreateDirectory(folder);
			string key;
			using (MD5 md5 = MD5.Create())
			{
				key = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath).ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
			}
			string ext = Path.GetExtension(firstImageFile);
			string dest = Path.Combine(folder, "cover_" + key + (string.IsNullOrEmpty(ext) ? ".jpg" : ext));
			if (!File.Exists(dest))
			{
				File.Copy(firstImageFile, dest, true);
			}
			return dest;
		}
		catch
		{
			return null;
		}
	}
}
}

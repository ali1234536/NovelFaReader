using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EpubReader.Services
{

// تبدیل TXT، Markdown و DOCX به یک فایل EPUB واقعی؛ بعدش مثل هر EPUB دیگه‌ای وارد کتابخونه می‌شه
// (خوندن، پیشرفت و بکاپ بدون هیچ تغییری کار می‌کنن). فایل تبدیل‌شده توی پوشه‌ی Converted کنار تنظیمات برنامه می‌مونه.
public static class BookConverter
{
	private static readonly string[] Extensions = { ".txt", ".text", ".md", ".markdown", ".mdown", ".docx", ".pdf", ".cbz", ".zip" };

	// الگوی دیالوگ انتخاب فایل
	public const string DialogPatterns = "*.epub;*.pdf;*.txt;*.text;*.md;*.markdown;*.mdown;*.docx;*.cbz;*.zip";

	public static bool IsConvertible(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		string ext = Path.GetExtension(path);
		foreach (string e in Extensions)
		{
			if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static string ConvertedFolder
	{
		get
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp", "Converted");
			if (!Directory.Exists(folder))
			{
				Directory.CreateDirectory(folder);
			}
			return folder;
		}
	}

	private static string Md5Hex(string text, int chars)
	{
		using (MD5 md5 = MD5.Create())
		{
			byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
			StringBuilder sb = new StringBuilder();
			foreach (byte b in h)
			{
				sb.Append(b.ToString("x2"));
			}
			return sb.ToString().Substring(0, chars);
		}
	}

	// مسیر EPUBِ تبدیل‌شده‌ی یک فایل (ثابت برای هر مسیر مبدأ؛ برای تشخیص تکراری بودن)
	public static string TargetPathFor(string sourcePath)
	{
		string name = TextBookParser.NameWithoutExtension(sourcePath);
		StringBuilder safe = new StringBuilder();
		foreach (char c in name)
		{
			safe.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
		}
		string baseName = safe.ToString().Trim();
		if (baseName.Length == 0)
		{
			baseName = "book";
		}
		if (baseName.Length > 60)
		{
			baseName = baseName.Substring(0, 60);
		}
		return Path.Combine(ConvertedFolder, baseName + "_" + Md5Hex(Path.GetFullPath(sourcePath).ToLowerInvariant(), 8) + ".epub");
	}

	// فایل مبدأ رو به EPUB تبدیل می‌کنه و مسیر EPUB رو برمی‌گردونه. اگه قبلاً تبدیل شده و فایل مبدأ عوض نشده، همونو برمی‌گردونه
	public static string ConvertToEpub(string sourcePath)
	{
		string target = TargetPathFor(sourcePath);
		bool isPdf = string.Equals(Path.GetExtension(sourcePath), ".pdf", StringComparison.OrdinalIgnoreCase);
		if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(sourcePath) && (!isPdf || File.Exists(target + ".src")))
		{
			if (isPdf)
			{
				WritePdfSidecar(target, sourcePath);
			}
			return target;
		}
		string work = Path.Combine(Path.GetTempPath(), "EpubConv_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try
		{
			Func<int, string> fallback = (int i) => Loc.F("فصل {0}", i);
			string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
			ParsedBook parsed;
			if (ext == ".pdf")
			{
				parsed = PdfPlaceholder(sourcePath, work);
			}
			else if (ext == ".cbz" || ext == ".zip")
			{
				parsed = ParseComic(sourcePath, work);
			}
			else if (ext == ".docx")
			{
				parsed = DocxParser.ParseFile(sourcePath, work, fallback, Path.GetFileName(sourcePath));
			}
			else if (ext == ".md" || ext == ".markdown" || ext == ".mdown")
			{
				parsed = TextBookParser.ParseMarkdown(File.ReadAllBytes(sourcePath), Path.GetFileName(sourcePath), work, fallback);
			}
			else
			{
				parsed = TextBookParser.ParseText(File.ReadAllBytes(sourcePath), Path.GetFileName(sourcePath), work, fallback);
			}
			if (parsed.Chapters.Count == 0)
			{
				throw new InvalidDataException(Loc.T("این فایل متن قابل‌خوندن نداره."));
			}
			string temp = target + ".tmp";
			if (File.Exists(temp))
			{
				File.Delete(temp);
			}
			PackEpub(temp, parsed, work, Path.GetFullPath(sourcePath));
			if (File.Exists(target))
			{
				File.Delete(target);
			}
			File.Move(temp, target);
			if (isPdf)
			{
				WritePdfSidecar(target, sourcePath);
			}
			return target;
		}
		finally
		{
			try
			{
				Directory.Delete(work, true);
			}
			catch
			{
			}
		}
	}

	// PDF با نمایشگر خود PDF (WebView2) نشون داده می‌شه تا ظاهر اصلیش (فونت، فارسی، جدول، عکس) سالم بمونه.
	// توی کتابخونه یه کتاب EPUB خیلی کوچیک (یک فصل) ثبت می‌شه و کنارش یه فایل «.src» مسیر PDF اصلی رو نگه می‌داره
	private static ParsedBook PdfPlaceholder(string sourcePath, string work)
	{
		string title = TextBookParser.NameWithoutExtension(Path.GetFileName(sourcePath));
		List<ConvertedSection> sections = new List<ConvertedSection>();
		sections.Add(new ConvertedSection(title, "<p>" + ConvertedBook.Escape(Loc.T("این کتاب یک فایل PDF است و با نمایشگر PDF نشون داده می‌شه.")) + "</p>"));
		return ConvertedBook.Write(work, title, sections);
	}

	private static void WritePdfSidecar(string epubPath, string pdfPath)
	{
		try
		{
			File.WriteAllText(epubPath + ".src", Path.GetFullPath(pdfPath), new UTF8Encoding(false));
		}
		catch
		{
		}
	}

	// اگه این EPUB در اصل یه PDF بوده و فایل PDF هنوز هست، مسیرش رو برمی‌گردونه؛ وگرنه null
	public static string PdfSourceFor(string epubPath)
	{
		try
		{
			string side = epubPath + ".src";
			if (!File.Exists(side))
			{
				return null;
			}
			string pdf = File.ReadAllText(side, Encoding.UTF8).Trim();
			if (pdf.Length > 0 && File.Exists(pdf) && string.Equals(Path.GetExtension(pdf), ".pdf", StringComparison.OrdinalIgnoreCase))
			{
				return pdf;
			}
		}
		catch
		{
		}
		return null;
	}

	// PDF: اگه رمز داشته باشه از کاربر رمز می‌پرسه (تا ۳ بار). PDFِ اسکن‌شده (بدون متن) قابل‌خوندن نیست
	// CBZ/ZIP تصویری؛ ZIPِ بدون عکس خطای روشن می‌ده
	private static ParsedBook ParseComic(string sourcePath, string work)
	{
		try
		{
			return CbzParser.ParseFile(sourcePath, work, (int i) => Loc.F("بخش {0}", i), Path.GetFileName(sourcePath));
		}
		catch (InvalidDataException ex)
		{
			if (ex.Message == "CBZ_NO_IMAGES")
			{
				throw new InvalidDataException(Loc.T("این ZIP عکسی نداره (فقط CBZ/ZIPِ تصویری پشتیبانی می‌شه)."));
			}
			throw new InvalidDataException(Loc.T("این فایل ZIP/CBZ خراب یا ناخوانده."));
		}
	}

	// ZIP ساده؛ توی اسکن پوشه‌ها نادیده گرفته می‌شه (فقط وقتی خودت انتخابش کنی اضافه می‌شه)، چون ZIPهای معمولی کتاب نیستن
	public static bool IsPlainZip(string path)
	{
		return string.Equals(Path.GetExtension(path ?? ""), ".zip", StringComparison.OrdinalIgnoreCase);
	}

	private static ParsedBook ParsePdf(string sourcePath, string work, Func<int, string> fallback)
	{
		byte[] bytes = File.ReadAllBytes(sourcePath);
		string name = Path.GetFileName(sourcePath);
		string password = null;
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				ParsedBook parsed;
				try
				{
					parsed = PdfParser.Parse(bytes, name, work, fallback, password);
				}
				catch (InvalidDataException ex)
				{
					if (ex.Message == "PDF_NO_TEXT")
					{
						throw new InvalidDataException(Loc.T("این PDF متن قابل‌خوندن نداره (احتمالاً اسکن‌شده و فقط تصویره)."));
					}
					throw;
				}
				if (parsed.Chapters.Count == 0)
				{
					throw new InvalidDataException(Loc.T("این PDF متن قابل‌خوندن نداره (احتمالاً اسکن‌شده و فقط تصویره)."));
				}
				return parsed;
			}
			catch (PdfPasswordRequiredException)
			{
				password = PdfPasswordWindow.Ask(name, false);
			}
			catch (PdfPasswordWrongException)
			{
				password = PdfPasswordWindow.Ask(name, true);
			}
			if (password == null)
			{
				throw new OperationCanceledException(Loc.T("رمز PDF وارد نشد."));
			}
		}
		throw new InvalidDataException(Loc.T("رمز PDF درست نبود."));
	}

	private static void AddText(ZipArchive zip, string path, string content, CompressionLevel level)
	{
		ZipArchiveEntry e = zip.CreateEntry(path, level);
		using (Stream s = e.Open())
		{
			byte[] data = new UTF8Encoding(false).GetBytes(content);
			s.Write(data, 0, data.Length);
		}
	}

	private static string MediaTypeOf(string path)
	{
		switch (Path.GetExtension(path).ToLowerInvariant())
		{
			case ".png": return "image/png";
			case ".jpg":
			case ".jpeg": return "image/jpeg";
			case ".gif": return "image/gif";
			case ".bmp": return "image/bmp";
			case ".webp": return "image/webp";
			case ".svg": return "image/svg+xml";
			default: return "application/octet-stream";
		}
	}

	// پوشه‌ی فصل‌ها (+ media/) رو به یک EPUB معتبر بسته‌بندی می‌کنه
	private static void PackEpub(string epubPath, ParsedBook parsed, string workDir, string sourceFullPath)
	{
		string uid = "urn:uuid:" + new Guid(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(sourceFullPath.ToLowerInvariant()))).ToString();
		StringBuilder manifest = new StringBuilder();
		StringBuilder spine = new StringBuilder();
		StringBuilder navList = new StringBuilder();
		StringBuilder ncxPoints = new StringBuilder();
		for (int i = 0; i < parsed.Chapters.Count; i++)
		{
			string name = Path.GetFileName(parsed.Chapters[i].File);
			string id = "ch" + (i + 1);
			string title = ConvertedBook.Escape(parsed.Chapters[i].Title ?? ("" + (i + 1)));
			manifest.Append("    <item id=\"").Append(id).Append("\" href=\"").Append(name).Append("\" media-type=\"application/xhtml+xml\"/>\n");
			spine.Append("    <itemref idref=\"").Append(id).Append("\"/>\n");
			navList.Append("      <li><a href=\"").Append(name).Append("\">").Append(title).Append("</a></li>\n");
			ncxPoints.Append("    <navPoint id=\"n").Append(i + 1).Append("\" playOrder=\"").Append(i + 1).Append("\"><navLabel><text>").Append(title)
				.Append("</text></navLabel><content src=\"").Append(name).Append("\"/></navPoint>\n");
		}
		string bookTitle = ConvertedBook.Escape(string.IsNullOrWhiteSpace(parsed.Title) ? "Book" : parsed.Title);
		// جلد (مثلاً صفحه‌ی اول کمیک) با meta cover و آیتم manifest معرفی می‌شه؛ بقیه‌ی عکس‌ها تو manifest نمیان
		string coverMeta = "";
		string coverItem = "";
		if (!string.IsNullOrEmpty(parsed.CoverFile))
		{
			coverMeta = "    <meta name=\"cover\" content=\"cover-image\"/>\n";
			coverItem = "    <item id=\"cover-image\" href=\"" + parsed.CoverFile + "\" media-type=\"" + MediaTypeOf(parsed.CoverFile) + "\" properties=\"cover-image\"/>\n";
		}

		using (FileStream fs = new FileStream(epubPath, FileMode.Create, FileAccess.ReadWrite))
		using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
		{
			AddText(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
			AddText(zip, "META-INF/container.xml",
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n  <rootfiles>\n    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n  </rootfiles>\n</container>\n",
				CompressionLevel.Optimal);
			AddText(zip, "OEBPS/content.opf",
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">\n  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n    <dc:identifier id=\"bookid\">" + uid + "</dc:identifier>\n    <dc:title>" + bookTitle + "</dc:title>\n    <dc:language>fa</dc:language>\n" + coverMeta + "  </metadata>\n  <manifest>\n    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n" + manifest + coverItem + "  </manifest>\n  <spine toc=\"ncx\">\n" + spine + "  </spine>\n</package>\n",
				CompressionLevel.Optimal);
			AddText(zip, "OEBPS/nav.xhtml",
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>" + bookTitle + "</title></head><body>\n  <nav epub:type=\"toc\"><ol>\n" + navList + "  </ol></nav>\n</body></html>\n",
				CompressionLevel.Optimal);
			AddText(zip, "OEBPS/toc.ncx",
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">\n  <head><meta name=\"dtb:uid\" content=\"" + uid + "\"/></head>\n  <docTitle><text>" + bookTitle + "</text></docTitle>\n  <navMap>\n" + ncxPoints + "  </navMap>\n</ncx>\n",
				CompressionLevel.Optimal);

			foreach (ParsedChapter ch in parsed.Chapters)
			{
				zip.CreateEntryFromFile(ch.File, "OEBPS/" + Path.GetFileName(ch.File), CompressionLevel.Optimal);
			}

			// تصویرهای DOCX (media/…) کنار فصل‌ها؛ توی manifest نمیان تا اولین عکس به‌اشتباه جلد کتاب نشه
			string mediaDir = Path.Combine(workDir, "media");
			if (Directory.Exists(mediaDir))
			{
				foreach (string f in Directory.GetFiles(mediaDir, "*", SearchOption.AllDirectories))
				{
					string rel = f.Substring(workDir.Length).TrimStart('\\', '/').Replace('\\', '/');
					zip.CreateEntryFromFile(f, "OEBPS/" + rel, MediaTypeOf(f) == "application/octet-stream" || f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
				}
			}
		}
	}
}
}

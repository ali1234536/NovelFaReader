using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using EpubReader.Models;

namespace EpubReader.Services
{

public static class EpubParser
{
	private class ManifestEntry
	{
		public string Href;
		public string MediaType;
		public string Properties;
	}

	private static string NormKey(string path)
	{
		try
		{
			return Path.GetFullPath(path).ToLowerInvariant();
		}
		catch
		{
			return (path ?? "").ToLowerInvariant();
		}
	}

	private static string CleanTocText(string t)
	{
		if (string.IsNullOrWhiteSpace(t))
		{
			return null;
		}
		return System.Text.RegularExpressions.Regex.Replace(t, "\\s+", " ").Trim();
	}

	// فهرست مطالب کتاب (EPUB3 nav یا EPUB2 ncx) رو می‌خونه: مسیر کامل فایل → عنوان اولین ورودی اون فایل
	private static Dictionary<string, string> ReadToc(XDocument opfDoc, XNamespace nsOpf, Dictionary<string, ManifestEntry> manifest, string opfFolder)
	{
		Dictionary<string, string> map = new Dictionary<string, string>();
		try
		{
			// EPUB3: <item properties="nav">
			foreach (ManifestEntry m in manifest.Values)
			{
				if (m.Properties != null && (" " + m.Properties + " ").Contains(" nav "))
				{
					string navPath = ResolveHref(opfFolder, m.Href);
					if (File.Exists(navPath))
					{
						XDocument nav = XDocument.Load(navPath);
						string navFolder = Path.GetDirectoryName(navPath);
						foreach (XElement navEl in nav.Descendants().Where((XElement e) => e.Name.LocalName == "nav"))
						{
							bool isToc = navEl.Attributes().Any((XAttribute a) => a.Name.LocalName == "type" && a.Value.Contains("toc"));
							if (!isToc)
							{
								continue;
							}
							foreach (XElement a in navEl.Descendants().Where((XElement e) => e.Name.LocalName == "a"))
							{
								XAttribute href = a.Attribute("href");
								string text = CleanTocText(a.Value);
								if (href == null || text == null)
								{
									continue;
								}
								string key = NormKey(ResolveHref(navFolder, href.Value));
								if (!map.ContainsKey(key))
								{
									map[key] = text;
								}
							}
							break;
						}
					}
					if (map.Count > 0)
					{
						return map;
					}
				}
			}
			// EPUB2: toc.ncx
			ManifestEntry ncx = manifest.Values.FirstOrDefault((ManifestEntry m) => m.MediaType == "application/x-dtbncx+xml");
			XElement spine = opfDoc.Descendants(nsOpf + "spine").FirstOrDefault();
			if (ncx == null && spine != null && spine.Attribute("toc") != null && manifest.ContainsKey(spine.Attribute("toc").Value))
			{
				ncx = manifest[spine.Attribute("toc").Value];
			}
			if (ncx != null)
			{
				string ncxPath = ResolveHref(opfFolder, ncx.Href);
				if (File.Exists(ncxPath))
				{
					XDocument doc = XDocument.Load(ncxPath);
					string ncxFolder = Path.GetDirectoryName(ncxPath);
					foreach (XElement np in doc.Descendants().Where((XElement e) => e.Name.LocalName == "navPoint"))
					{
						XElement label = np.Elements().FirstOrDefault((XElement e) => e.Name.LocalName == "navLabel");
						XElement content = np.Elements().FirstOrDefault((XElement e) => e.Name.LocalName == "content");
						if (label == null || content == null || content.Attribute("src") == null)
						{
							continue;
						}
						string text = CleanTocText(label.Value);
						if (text == null)
						{
							continue;
						}
						string key = NormKey(ResolveHref(ncxFolder, content.Attribute("src").Value));
						if (!map.ContainsKey(key))
						{
							map[key] = text;
						}
					}
				}
			}
		}
		catch
		{
		}
		return map;
	}

	// آدرس فایل داخل کتاب (با %20 و حروف فارسی کدشده) رو به مسیر واقعی تبدیل می‌کنه
	private static string ResolveHref(string folder, string href)
	{
		string clean = href;
		int hash = clean.IndexOf('#');
		if (hash >= 0)
		{
			clean = clean.Substring(0, hash);
		}
		string raw = Path.Combine(folder, clean.Replace('/', Path.DirectorySeparatorChar));
		try
		{
			string decoded = Path.Combine(folder, Uri.UnescapeDataString(clean).Replace('/', Path.DirectorySeparatorChar));
			if (File.Exists(decoded))
			{
				return decoded;
			}
		}
		catch
		{
		}
		return raw;
	}

	private static ZipArchiveEntry FindEntry(ZipArchive zip, string name)
	{
		string want = (name ?? "").Replace('\\', '/').TrimStart('/');
		foreach (ZipArchiveEntry e in zip.Entries)
		{
			if (string.Equals(e.FullName.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase))
			{
				return e;
			}
		}
		return null;
	}

	private static string JoinZipPath(string folder, string href)
	{
		string clean = href;
		int hash = clean.IndexOf('#');
		if (hash >= 0)
		{
			clean = clean.Substring(0, hash);
		}
		try
		{
			clean = Uri.UnescapeDataString(clean);
		}
		catch
		{
		}
		List<string> parts = new List<string>();
		string all = (string.IsNullOrEmpty(folder) ? "" : folder + "/") + clean;
		foreach (string p in all.Replace('\\', '/').Split('/'))
		{
			if (p.Length == 0 || p == ".")
			{
				continue;
			}
			if (p == "..")
			{
				if (parts.Count > 0)
				{
					parts.RemoveAt(parts.Count - 1);
				}
				continue;
			}
			parts.Add(p);
		}
		return string.Join("/", parts.ToArray());
	}

	// برای افزودن به کتابخونه: فقط عنوان، تعداد فصل‌ها و جلد از داخل zip خونده می‌شه (بدون باز کردن کل کتاب روی دیسک)؛ اگه نشد همون Load کامل
	public static EpubBook LoadMeta(string epubFilePath)
	{
		try
		{
			using (ZipArchive zip = ZipFile.OpenRead(epubFilePath))
			{
				ZipArchiveEntry cont = FindEntry(zip, "META-INF/container.xml");
				if (cont == null)
				{
					return Load(epubFilePath);
				}
				XDocument containerDoc;
				using (Stream cs = cont.Open())
				{
					containerDoc = XDocument.Load(cs);
				}
				XNamespace nsContainer = "urn:oasis:names:tc:opendocument:xmlns:container";
				string opfPath = containerDoc.Descendants(nsContainer + "rootfile").First().Attribute("full-path").Value.Replace('\\', '/');
				string opfFolder = opfPath.Contains("/") ? opfPath.Substring(0, opfPath.LastIndexOf('/')) : "";
				ZipArchiveEntry opfEntry = FindEntry(zip, opfPath);
				if (opfEntry == null)
				{
					return Load(epubFilePath);
				}
				XDocument opfDoc;
				using (Stream os = opfEntry.Open())
				{
					opfDoc = XDocument.Load(os);
				}
				XNamespace nsOpf = "http://www.idpf.org/2007/opf";
				XNamespace nsDc = "http://purl.org/dc/elements/1.1/";
				EpubBook book = new EpubBook();
				XElement titleEl = opfDoc.Descendants(nsDc + "title").FirstOrDefault();
				book.Title = ((titleEl != null && !string.IsNullOrWhiteSpace(titleEl.Value)) ? titleEl.Value.Trim() : Path.GetFileNameWithoutExtension(epubFilePath));
				Dictionary<string, ManifestEntry> manifestItems = new Dictionary<string, ManifestEntry>();
				foreach (XElement item in opfDoc.Descendants(nsOpf + "item"))
				{
					XAttribute idAttr = item.Attribute("id");
					XAttribute hrefAttr = item.Attribute("href");
					if (idAttr == null || hrefAttr == null || manifestItems.ContainsKey(idAttr.Value))
					{
						continue;
					}
					XAttribute typeAttr = item.Attribute("media-type");
					manifestItems[idAttr.Value] = new ManifestEntry { Href = hrefAttr.Value, MediaType = ((typeAttr != null) ? typeAttr.Value : null) };
				}
				string coverHref = null;
				XElement coverMeta = opfDoc.Descendants(nsOpf + "meta").FirstOrDefault((XElement m) => m.Attribute("name") != null && m.Attribute("name").Value == "cover");
				if (coverMeta != null)
				{
					string coverId = ((coverMeta.Attribute("content") != null) ? coverMeta.Attribute("content").Value : null);
					if (coverId != null && manifestItems.ContainsKey(coverId))
					{
						coverHref = manifestItems[coverId].Href;
					}
				}
				if (coverHref == null)
				{
					var coverItem = manifestItems.Values.FirstOrDefault(v => v.MediaType != null && v.MediaType.StartsWith("image") && v.Href.ToLower().Contains("cover"));
					if (coverItem != null)
					{
						coverHref = coverItem.Href;
					}
				}
				if (coverHref == null)
				{
					var firstImage = manifestItems.Values.FirstOrDefault(v => v.MediaType != null && v.MediaType.StartsWith("image"));
					if (firstImage != null)
					{
						coverHref = firstImage.Href;
					}
				}
				if (coverHref != null)
				{
					ZipArchiveEntry coverEntry = FindEntry(zip, JoinZipPath(opfFolder, coverHref));
					if (coverEntry != null)
					{
						string tmp = Path.Combine(Path.GetTempPath(), "EpubCover_" + Guid.NewGuid().ToString("N") + Path.GetExtension(coverEntry.FullName));
						try
						{
							using (Stream src = coverEntry.Open())
							using (FileStream dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
							{
								src.CopyTo(dst);
							}
							book.CoverImagePath = CopyToPermanentCoverStorage(tmp, epubFilePath);
						}
						finally
						{
							try
							{
								File.Delete(tmp);
							}
							catch
							{
							}
						}
					}
				}
				int count = 0;
				foreach (XElement itemref in opfDoc.Descendants(nsOpf + "itemref"))
				{
					XAttribute idref = itemref.Attribute("idref");
					if (idref != null && manifestItems.ContainsKey(idref.Value))
					{
						count++;
						book.Chapters.Add(new ChapterItem { Title = Loc.F("بخش {0}", count), FilePath = null });
					}
				}
				if (count == 0)
				{
					return Load(epubFilePath);
				}
				return book;
			}
		}
		catch
		{
			return Load(epubFilePath);
		}
	}

	// باز کردن zip با چند رشته‌ی هم‌زمان (هر رشته نمونه‌ی zip مخصوص خودش رو داره): برای کتاب‌های پر از عکس خیلی سریع‌تر از ExtractToDirectory
	private static void ExtractParallel(string zipPath, string destFolder)
	{
		string root = Path.GetFullPath(destFolder);
		if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
		{
			root += Path.DirectorySeparatorChar;
		}
		List<string> names = new List<string>();
		using (ZipArchive zip = ZipFile.OpenRead(zipPath))
		{
			foreach (ZipArchiveEntry e in zip.Entries)
			{
				if (!string.IsNullOrEmpty(e.Name))
				{
					names.Add(e.FullName);
				}
			}
		}
		if (names.Count < 12)
		{
			ZipFile.ExtractToDirectory(zipPath, destFolder);
			return;
		}
		int workers = Math.Min(4, Math.Max(2, Environment.ProcessorCount));
		Exception failure = null;
		System.Threading.Tasks.Task[] tasks = new System.Threading.Tasks.Task[workers];
		for (int w = 0; w < workers; w++)
		{
			int mine = w;
			tasks[w] = System.Threading.Tasks.Task.Run(() =>
			{
				try
				{
					using (ZipArchive zip = ZipFile.OpenRead(zipPath))
					{
						int i = 0;
						foreach (ZipArchiveEntry e in zip.Entries)
						{
							if (string.IsNullOrEmpty(e.Name))
							{
								continue;
							}
							if ((i++ % workers) != mine)
							{
								continue;
							}
							string target = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
							if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}
							Directory.CreateDirectory(Path.GetDirectoryName(target));
							e.ExtractToFile(target, true);
						}
					}
				}
				catch (Exception ex)
				{
					failure = ex;
				}
			});
		}
		System.Threading.Tasks.Task.WaitAll(tasks);
		if (failure != null)
		{
			throw failure;
		}
	}

	public static EpubBook Load(string epubFilePath)
	{
		EpubBook book = new EpubBook();
		string tempFolder = Path.Combine(Path.GetTempPath(), "EpubReader_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempFolder);
		book.ExtractPath = tempFolder;
		try
		{
		ExtractParallel(epubFilePath, tempFolder);
		string containerPath = Path.Combine(tempFolder, "META-INF", "container.xml");
		XDocument containerDoc = XDocument.Load(containerPath);
		XNamespace nsContainer = "urn:oasis:names:tc:opendocument:xmlns:container";
		string opfRelativePath = containerDoc.Descendants(nsContainer + "rootfile").First().Attribute("full-path")
			.Value;
		string opfFullPath = Path.Combine(tempFolder, opfRelativePath.Replace('/', Path.DirectorySeparatorChar));
		string opfFolder = Path.GetDirectoryName(opfFullPath);
		XDocument opfDoc = XDocument.Load(opfFullPath);
		XNamespace nsOpf = "http://www.idpf.org/2007/opf";
		XNamespace nsDc = "http://purl.org/dc/elements/1.1/";
		XElement titleEl = opfDoc.Descendants(nsDc + "title").FirstOrDefault();
		book.Title = ((titleEl != null && !string.IsNullOrWhiteSpace(titleEl.Value)) ? titleEl.Value.Trim() : Path.GetFileNameWithoutExtension(epubFilePath));
		Dictionary<string, ManifestEntry> manifestItems = new Dictionary<string, ManifestEntry>();
		foreach (XElement item in opfDoc.Descendants(nsOpf + "item"))
		{
			XAttribute idAttr = item.Attribute("id");
			XAttribute hrefAttr = item.Attribute("href");
			if (idAttr == null || hrefAttr == null || manifestItems.ContainsKey(idAttr.Value))
			{
				continue;
			}
			XAttribute typeAttr = item.Attribute("media-type");
			XAttribute propAttr = item.Attribute("properties");
			manifestItems[idAttr.Value] = new ManifestEntry
			{
				Href = hrefAttr.Value,
				MediaType = ((typeAttr != null) ? typeAttr.Value : null),
				Properties = ((propAttr != null) ? propAttr.Value : null)
			};
		}
		string coverHref = null;
		XElement coverMeta = opfDoc.Descendants(nsOpf + "meta").FirstOrDefault((XElement m) => m.Attribute("name") != null && m.Attribute("name").Value == "cover");
		if (coverMeta != null)
		{
			string coverId = ((coverMeta.Attribute("content") != null) ? coverMeta.Attribute("content").Value : null);
			if (coverId != null && manifestItems.ContainsKey(coverId))
			{
				coverHref = manifestItems[coverId].Href;
			}
		}
		if (coverHref == null)
		{
			var coverItem = manifestItems.Values.FirstOrDefault(v => v.MediaType != null && v.MediaType.StartsWith("image") && v.Href.ToLower().Contains("cover"));
			if (coverItem != null)
			{
				coverHref = coverItem.Href;
			}
		}
		if (coverHref == null)
		{
			var firstImage = manifestItems.Values.FirstOrDefault(v => v.MediaType != null && v.MediaType.StartsWith("image"));
			if (firstImage != null)
			{
				coverHref = firstImage.Href;
			}
		}
		if (coverHref != null)
		{
			string sourcePath = ResolveHref(opfFolder, coverHref);
			if (File.Exists(sourcePath))
			{
				book.CoverImagePath = CopyToPermanentCoverStorage(sourcePath, epubFilePath);
			}
		}
		List<string> spineItems = (from item in opfDoc.Descendants(nsOpf + "itemref")
			where item.Attribute("idref") != null
		select item.Attribute("idref").Value).ToList();
		int index = 1;
		foreach (string idref in spineItems)
		{
			if (manifestItems.ContainsKey(idref))
			{
				string href = manifestItems[idref].Href;
				string fullPath = ResolveHref(opfFolder, href);
				book.Chapters.Add(new ChapterItem
				{
					Title = Loc.F("بخش {0}", index),
					FilePath = fullPath
				});
				index++;
			}
		}
		return book;
		}
		catch
		{
			try
			{
				Directory.Delete(tempFolder, true);
			}
			catch
			{
			}
			throw;
		}
	}

	private static string CopyToPermanentCoverStorage(string sourceImagePath, string epubFilePath)
	{
		string coversFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp", "Covers");
		if (!Directory.Exists(coversFolder))
		{
			Directory.CreateDirectory(coversFolder);
		}
		string extension = Path.GetExtension(sourceImagePath);
		if (string.IsNullOrEmpty(extension))
		{
			extension = ".jpg";
		}
		string key;
		using (MD5 md5 = MD5.Create())
		{
			byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(epubFilePath.ToLowerInvariant()));
			key = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
		}
		string destPath = Path.Combine(coversFolder, "cover_" + key + extension);
		if (!File.Exists(destPath))
		{
			File.Copy(sourceImagePath, destPath, overwrite: true);
		}
		return destPath;
	}
}
}

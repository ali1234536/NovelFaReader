using System;
using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace EpubReader.Services
{

// نمایشگر PDF خود برنامه (pdf.js داخل WebView2): هم‌رنگ تم خوانش، با شماره‌ی صفحه، زوم و حالت شب.
// فایل‌های نمایشگر (viewer.html + pdf.js) داخل exe جاسازی شدن و موقع نیاز توی %AppData%\EpubReaderApp\PdfViewer باز می‌شن
public static class PdfViewerHost
{
	public const string ViewerHost = "pdfview.local";

	public const string DocHost = "pdfdoc.local";

	public const string ComicHost = "comicdoc.local";

	// ده صفحه = یک «واحد» XP (حدود یه بخش کوچیک)
	public const int PagesPerXpUnit = 10;

	private static readonly string[] Files = { "viewer.html", "comic.html", "doc.html", "pdf.min.js", "pdf.worker.min.js" };

	private static string viewerFolder;

	public static string EnsureViewerFolder()
	{
		if (viewerFolder != null && File.Exists(Path.Combine(viewerFolder, "viewer.html")))
		{
			return viewerFolder;
		}
		string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp", "PdfViewer");
		Directory.CreateDirectory(folder);
		Assembly asm = Assembly.GetExecutingAssembly();
		string[] names = asm.GetManifestResourceNames();
		foreach (string file in Files)
		{
			string resource = null;
			foreach (string n in names)
			{
				if (n.EndsWith("PdfViewer." + file, StringComparison.Ordinal))
				{
					resource = n;
					break;
				}
			}
			if (resource == null)
			{
				throw new FileNotFoundException("PdfViewer resource missing: " + file);
			}
			string dest = Path.Combine(folder, file);
			using (Stream s = asm.GetManifestResourceStream(resource))
			{
				if (File.Exists(dest) && new FileInfo(dest).Length == s.Length)
				{
					continue;
				}
				using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
				{
					s.CopyTo(fs);
				}
			}
		}
		// فونت‌های استاندارد و cmapها (برای PDFهایی که فونتشون داخل فایل نیست یا CJK ان) یک‌بار از pdfdata.zip باز می‌شن
		if (!Directory.Exists(Path.Combine(folder, "cmaps")) || !Directory.Exists(Path.Combine(folder, "standard_fonts")))
		{
			string dataRes = null;
			foreach (string n in names)
			{
				if (n.EndsWith("PdfViewer.pdfdata.zip", StringComparison.Ordinal))
				{
					dataRes = n;
					break;
				}
			}
			if (dataRes != null)
			{
				try
				{
					using (Stream zs = asm.GetManifestResourceStream(dataRes))
					using (System.IO.Compression.ZipArchive za = new System.IO.Compression.ZipArchive(zs, System.IO.Compression.ZipArchiveMode.Read))
					{
						foreach (System.IO.Compression.ZipArchiveEntry en in za.Entries)
						{
							if (string.IsNullOrEmpty(en.Name))
							{
								continue;
							}
							string rel = en.FullName.Replace('/', Path.DirectorySeparatorChar);
							string target = Path.GetFullPath(Path.Combine(folder, rel));
							if (!target.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}
							Directory.CreateDirectory(Path.GetDirectoryName(target));
							using (Stream es = en.Open())
							using (FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write))
							{
								es.CopyTo(fs);
							}
						}
					}
				}
				catch
				{
					// بدون فونت‌های اضافه هم PDFهای معمولی نمایش داده می‌شن
				}
			}
		}
		viewerFolder = folder;
		return folder;
	}

	// نگاشت هاست مجازی: صفحه‌ی نمایشگر + پوشه‌ی PDF (فقط برای همین نمایشگر)
	public static void Attach(CoreWebView2 core, string pdfPath)
	{
		string folder = EnsureViewerFolder();
		try
		{
			core.ClearVirtualHostNameToFolderMapping(ViewerHost);
			core.ClearVirtualHostNameToFolderMapping(DocHost);
		}
		catch
		{
		}
		core.SetVirtualHostNameToFolderMapping(ViewerHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
		core.SetVirtualHostNameToFolderMapping(DocHost, Path.GetDirectoryName(Path.GetFullPath(pdfPath)), CoreWebView2HostResourceAccessKind.Allow);
	}

	// نمایشگر کمیک: صفحه‌ها همون عکس‌های باز‌شده‌ی EPUB تبدیل‌شده‌ان (به ترتیب بخش‌ها)
	public static void AttachComic(CoreWebView2 core, string extractRoot)
	{
		string folder = EnsureViewerFolder();
		CloseComic();
		try
		{
			core.ClearVirtualHostNameToFolderMapping(ViewerHost);
			core.ClearVirtualHostNameToFolderMapping(DocHost);
			core.ClearVirtualHostNameToFolderMapping(ComicHost);
		}
		catch
		{
		}
		core.SetVirtualHostNameToFolderMapping(ViewerHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
		core.SetVirtualHostNameToFolderMapping(ComicHost, Path.GetFullPath(extractRoot), CoreWebView2HostResourceAccessKind.Allow);
	}

	// کمیک مستقیم از خود CBZ/ZIP: عکس‌ها بدون استخراج روی دیسک، لحظه‌ای از داخل آرشیو به نمایشگر داده می‌شن
	private static System.IO.Compression.ZipArchive comicZip;

	private static System.Collections.Generic.List<System.IO.Compression.ZipArchiveEntry> comicEntries;

	private static bool comicFilterAdded;

	public static string ComicPageName(int index, string entryName)
	{
		string ext = Path.GetExtension(entryName ?? "");
		return "p" + (index + 1) + (string.IsNullOrEmpty(ext) ? ".jpg" : ext.ToLowerInvariant());
	}

	public static void CloseComic()
	{
		try
		{
			if (comicZip != null)
			{
				comicZip.Dispose();
			}
		}
		catch
		{
		}
		comicZip = null;
		comicEntries = null;
	}

	public static void AttachComicZip(CoreWebView2 core, string zipPath, System.Collections.Generic.List<string> names)
	{
		string folder = EnsureViewerFolder();
		CloseComic();
		try
		{
			core.ClearVirtualHostNameToFolderMapping(ViewerHost);
			core.ClearVirtualHostNameToFolderMapping(DocHost);
			core.ClearVirtualHostNameToFolderMapping(ComicHost);
		}
		catch
		{
		}
		core.SetVirtualHostNameToFolderMapping(ViewerHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
		System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
		System.Collections.Generic.Dictionary<string, System.IO.Compression.ZipArchiveEntry> byName = new System.Collections.Generic.Dictionary<string, System.IO.Compression.ZipArchiveEntry>(StringComparer.Ordinal);
		foreach (System.IO.Compression.ZipArchiveEntry e in zip.Entries)
		{
			string n = e.FullName.Replace('\\', '/');
			if (!byName.ContainsKey(n))
			{
				byName[n] = e;
			}
		}
		System.Collections.Generic.List<System.IO.Compression.ZipArchiveEntry> list = new System.Collections.Generic.List<System.IO.Compression.ZipArchiveEntry>();
		foreach (string n in names)
		{
			System.IO.Compression.ZipArchiveEntry en;
			list.Add(byName.TryGetValue(n, out en) ? en : null);
		}
		comicZip = zip;
		comicEntries = list;
		if (!comicFilterAdded)
		{
			core.AddWebResourceRequestedFilter("https://" + ComicHost + "/*", CoreWebView2WebResourceContext.All);
			core.WebResourceRequested += OnComicRequest;
			comicFilterAdded = true;
		}
	}

	private static void OnComicRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		try
		{
			if (comicZip == null || comicEntries == null)
			{
				return;
			}
			Uri u;
			if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out u) || !string.Equals(u.Host, ComicHost, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			string seg = u.AbsolutePath.TrimStart('/');
			int dot = seg.IndexOf('.');
			int idx;
			if (seg.Length < 2 || seg[0] != 'p' || dot < 2 || !int.TryParse(seg.Substring(1, dot - 1), out idx) || idx < 1 || idx > comicEntries.Count || comicEntries[idx - 1] == null)
			{
				return;
			}
			System.IO.MemoryStream ms = new System.IO.MemoryStream();
			using (Stream s = comicEntries[idx - 1].Open())
			{
				s.CopyTo(ms);
			}
			ms.Position = 0;
			string ext = Path.GetExtension(seg).ToLowerInvariant();
			string mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".gif" ? "image/gif" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
			CoreWebView2 core = sender as CoreWebView2;
			if (core != null)
			{
				e.Response = core.Environment.CreateWebResourceResponse(ms, 200, "OK", "Content-Type: " + mime + "\nAccess-Control-Allow-Origin: *\nCache-Control: max-age=600");
			}
		}
		catch
		{
		}
	}

	private static readonly System.Text.RegularExpressions.Regex ImgSrc = new System.Text.RegularExpressions.Regex("<img\\b[^>]*?\\bsrc\\s*=\\s*[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	// لیست آدرس نسبی صفحه‌ها + شروع هر بخش (عنوان، شماره‌ی صفحه‌ی صفرمبنا)
	public static System.Collections.Generic.List<string> CollectComicPages(string extractRoot, System.Collections.Generic.IList<EpubReader.Models.ChapterItem> chapters, out System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>> starts)
	{
		System.Collections.Generic.List<string> pages = new System.Collections.Generic.List<string>();
		starts = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>();
		string root = Path.GetFullPath(extractRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		foreach (EpubReader.Models.ChapterItem ch in chapters)
		{
			int before = pages.Count;
			try
			{
				string file = Path.GetFullPath(ch.FilePath);
				string dir = Path.GetDirectoryName(file);
				string html = File.ReadAllText(file);
				foreach (System.Text.RegularExpressions.Match m in ImgSrc.Matches(html))
				{
					string src = m.Groups[1].Value;
					int cut = src.IndexOfAny(new char[] { '?', '#' });
					if (cut >= 0)
					{
						src = src.Substring(0, cut);
					}
					string full = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(src).Replace('/', Path.DirectorySeparatorChar)));
					if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
					{
						continue;
					}
					string[] parts = full.Substring(root.Length).Split(Path.DirectorySeparatorChar);
					for (int i = 0; i < parts.Length; i++)
					{
						parts[i] = Uri.EscapeDataString(parts[i]);
					}
					pages.Add(string.Join("/", parts));
				}
			}
			catch
			{
			}
			if (pages.Count > before)
			{
				starts.Add(new System.Collections.Generic.KeyValuePair<string, int>(ch.Title ?? "", before));
			}
		}
		return pages;
	}

	public static string BuildDocUrl(string kind, string extractRoot, System.Collections.Generic.IList<EpubReader.Models.ChapterItem> chapters, string bookId, string bg, string fg, bool dark, int page, double zoom)
	{
		string root = Path.GetFullPath(extractRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		System.Text.StringBuilder d = new System.Text.StringBuilder("{\"c\":[");
		bool first = true;
		foreach (EpubReader.Models.ChapterItem ch in chapters)
		{
			string full = Path.GetFullPath(ch.FilePath);
			if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string[] parts = full.Substring(root.Length).Split(Path.DirectorySeparatorChar);
			for (int i = 0; i < parts.Length; i++)
			{
				parts[i] = Uri.EscapeDataString(parts[i]);
			}
			if (!first)
			{
				d.Append(',');
			}
			first = false;
			d.Append("{\"f\":").Append(JsonStr(string.Join("/", parts))).Append(",\"t\":").Append(JsonStr(ch.Title ?? "")).Append('}');
		}
		d.Append("]}");
		string tl = "{" + "\"zout\":" + JsonStr(Loc.T("کوچک‌کردن متن"))
			+ ",\"zin\":" + JsonStr(Loc.T("بزرگ‌کردن متن"))
			+ ",\"width\":" + JsonStr(Loc.T("عرض ستون متن"))
			+ ",\"lh\":" + JsonStr(Loc.T("فاصله‌ی خط‌ها"))
			+ ",\"ff\":" + JsonStr(Loc.T("فونت با پایه / بدون پایه"))
			+ ",\"find\":" + JsonStr(Loc.T("جستجو (Ctrl+F)"))
			+ ",\"search\":" + JsonStr(Loc.T("جستجو…"))
			+ ",\"none\":" + JsonStr(Loc.T("پیدا نشد"))
			+ ",\"min\":" + JsonStr(Loc.T("دقیقه")) + "}";
		return "https://" + ViewerHost + "/doc.html?kind=" + Uri.EscapeDataString(kind)
			+ "&base=" + Uri.EscapeDataString("https://" + ComicHost + "/")
			+ "&d=" + Uri.EscapeDataString(d.ToString())
			+ "&id=" + Uri.EscapeDataString(bookId ?? "")
			+ "&bg=" + Uri.EscapeDataString(bg ?? "#1c1c1e")
			+ "&fg=" + Uri.EscapeDataString(fg ?? "#e8e8e8")
			+ "&dark=" + (dark ? "1" : "0")
			+ "&page=" + Math.Max(1, page)
			+ (zoom > 0.3 ? "&zoom=" + zoom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "")
			+ "&terr=" + Uri.EscapeDataString(Loc.T("این فایل متن قابل‌خوندن نداره."))
			+ "&tl=" + Uri.EscapeDataString(tl);
	}

	private static string JsonStr(string s)
	{
		System.Text.StringBuilder sb = new System.Text.StringBuilder("\"");
		foreach (char c in s ?? "")
		{
			if (c == '"' || c == '\\')
			{
				sb.Append('\\').Append(c);
			}
			else if (c < ' ')
			{
				sb.Append(' ');
			}
			else
			{
				sb.Append(c);
			}
		}
		return sb.Append('"').ToString();
	}

	public static string BuildComicUrl(System.Collections.Generic.List<string> pages, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>> starts, string bookId, string bg, string fg, bool dark, int page, double zoom)
	{
		System.Text.StringBuilder d = new System.Text.StringBuilder("{\"p\":[");
		for (int i = 0; i < pages.Count; i++)
		{
			if (i > 0)
			{
				d.Append(',');
			}
			d.Append(JsonStr(pages[i]));
		}
		d.Append("],\"c\":[");
		for (int i = 0; i < starts.Count; i++)
		{
			if (i > 0)
			{
				d.Append(',');
			}
			d.Append('[').Append(JsonStr(starts[i].Key)).Append(',').Append(starts[i].Value).Append(']');
		}
		d.Append("]}");
		string tl = "{" + "\"thumbs\":" + JsonStr(Loc.T("صفحه‌ها (T)"))
			+ ",\"zout\":" + JsonStr(Loc.T("کوچک‌نمایی"))
			+ ",\"zin\":" + JsonStr(Loc.T("بزرگ‌نمایی"))
			+ ",\"fit\":" + JsonStr(Loc.T("اندازه‌ی صفحه / عرض"))
			+ ",\"view\":" + JsonStr(Loc.T("حالت نمایش"))
			+ ",\"rtl\":" + JsonStr(Loc.T("راست‌به‌چپ (مانگا)"))
			+ ",\"gap\":" + JsonStr(Loc.T("فاصله‌ی بین صفحه‌ها"))
			+ ",\"bm\":" + JsonStr(Loc.T("نشانک (B)"))
			+ ",\"m_scroll\":" + JsonStr(Loc.T("پیوسته"))
			+ ",\"m_single\":" + JsonStr(Loc.T("تک‌صفحه"))
			+ ",\"m_spread\":" + JsonStr(Loc.T("دوصفحه")) + "}";
		return "https://" + ViewerHost + "/comic.html?base=" + Uri.EscapeDataString("https://" + ComicHost + "/")
			+ "&d=" + Uri.EscapeDataString(d.ToString())
			+ "&id=" + Uri.EscapeDataString(bookId ?? "")
			+ "&bg=" + Uri.EscapeDataString(bg ?? "#1c1c1e")
			+ "&fg=" + Uri.EscapeDataString(fg ?? "#e8e8e8")
			+ "&dark=" + (dark ? "1" : "0")
			+ "&page=" + Math.Max(1, page)
			+ (zoom > 0.3 ? "&zoom=" + zoom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "")
			+ "&terr=" + Uri.EscapeDataString(Loc.T("صفحه‌ای پیدا نشد"))
			+ "&tl=" + Uri.EscapeDataString(tl);
	}

	public static string BuildUrl(string pdfPath, string bg, string fg, bool dark, int page, double zoom = 0, bool wantCover = false)
	{
		string docUrl = "https://" + DocHost + "/" + Uri.EscapeDataString(Path.GetFileName(pdfPath));
		return "https://" + ViewerHost + "/viewer.html?file=" + Uri.EscapeDataString(docUrl)
			+ "&bg=" + Uri.EscapeDataString(bg ?? "#1c1c1e")
			+ "&fg=" + Uri.EscapeDataString(fg ?? "#e8e8e8")
			+ "&dark=" + (dark ? "1" : "0")
			+ "&page=" + Math.Max(1, page)
			+ (wantCover ? "&cover=1" : "")
			+ (zoom > 0.3 ? "&zoom=" + zoom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "")
			+ "&twrong=" + Uri.EscapeDataString(Loc.T("رمز اشتباهه. دوباره وارد کن"))
			+ "&tpw=" + Uri.EscapeDataString(Loc.T("این PDF رمز داره. رمزش رو وارد کن"))
			+ "&tok=" + Uri.EscapeDataString(Loc.T("تأیید"))
			+ "&terr=" + Uri.EscapeDataString(Loc.T("این PDF باز نشد"))
			+ "&tload=" + Uri.EscapeDataString(Loc.T("در حال باز کردن PDF…"))
			+ "&tsearch=" + Uri.EscapeDataString(Loc.T("جستجو در PDF…"))
			+ "&tnone=" + Uri.EscapeDataString(Loc.T("پیدا نشد"))
			+ "&tdark=" + Uri.EscapeDataString(Loc.T("حالت شب برای صفحه‌ها"));
	}

	public static bool IsAllowedUri(string uri)
	{
		return uri != null && (uri.StartsWith("https://" + ViewerHost + "/", StringComparison.OrdinalIgnoreCase)
			|| uri.StartsWith("https://" + DocHost + "/", StringComparison.OrdinalIgnoreCase)
			|| uri.StartsWith("https://" + ComicHost + "/", StringComparison.OrdinalIgnoreCase));
	}
}
}

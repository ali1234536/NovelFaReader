using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EpubReader.Services
{

// CBZ / ZIP تصویری (مانهوا، مانگا، کمیک) — پورت CbzBook.kt اندروید.
// یه CBZ فقط یه ZIP از عکس‌های صفحه‌هاست به ترتیب خوندن. قاعده‌ها:
// - فقط عکس (jpg, jpeg, png, webp, gif, bmp) صفحه حساب می‌شه؛ بقیه‌ی فایل‌ها، پوشه‌ها، __MACOSX و فایل‌های نقطه‌دار نادیده‌ان
// - ترتیب «طبیعی»: page-2 قبل از page-10، و پوشه‌ها قبل از محتواشون
// - پوشه‌ها فصل می‌شن (بعد از حذف مسیری که همه‌ی پوشه‌ها مشترک دارن)؛ آرشیو بدون پوشه به بخش‌های ۲۰ صفحه‌ای تقسیم می‌شه
// - CBR (RAR) پشتیبانی نمی‌شه
public static class CbzParser
{
	public const int PagesPerSection = 20;

	private static readonly string[] ImageExtensions = { "jpg", "jpeg", "png", "webp", "gif", "bmp" };

	public sealed class CbzChapter
	{
		public string Title;
		public int FirstPageIndex;
	}

	public static bool IsImageEntry(string name)
	{
		if (string.IsNullOrEmpty(name) || name.EndsWith("/", StringComparison.Ordinal))
		{
			return false;
		}
		int slash = name.LastIndexOf('/');
		string baseName = slash >= 0 ? name.Substring(slash + 1) : name;
		if (baseName.Length == 0 || baseName[0] == '.')
		{
			return false;
		}
		if (name.StartsWith("__MACOSX/", StringComparison.Ordinal))
		{
			return false;
		}
		int dot = baseName.LastIndexOf('.');
		if (dot < 0)
		{
			return false;
		}
		string ext = baseName.Substring(dot + 1).ToLowerInvariant();
		return Array.IndexOf(ImageExtensions, ext) >= 0;
	}

	// نام ورودی‌های عکس به ترتیب طبیعی
	public static List<string> SortedImageNames(IEnumerable<string> names)
	{
		List<string> pages = new List<string>();
		foreach (string n in names)
		{
			string fixedName = (n ?? "").Replace('\\', '/');
			if (IsImageEntry(fixedName))
			{
				pages.Add(fixedName);
			}
		}
		pages.Sort(CompareNatural);
		return pages;
	}

	public static List<CbzChapter> ChaptersForEntries(List<string> sortedPages)
	{
		List<CbzChapter> result = new List<CbzChapter>();
		if (sortedPages.Count == 0)
		{
			return result;
		}
		List<string> parents = new List<string>();
		int foldered = 0;
		foreach (string p in sortedPages)
		{
			int slash = p.LastIndexOf('/');
			string parent = slash >= 0 ? p.Substring(0, slash) : "";
			parents.Add(parent);
			if (parent.Length > 0)
			{
				foldered++;
			}
		}
		// مسیر مشترک همه‌ی صفحه‌ها (فقط وقتی همه توی پوشه‌ان)
		List<string> common = new List<string>();
		if (foldered > 0 && foldered == parents.Count)
		{
			string[] acc = parents[0].Split('/');
			int len = acc.Length;
			for (int i = 1; i < parents.Count && len > 0; i++)
			{
				string[] parts = parents[i].Split('/');
				int k = 0;
				while (k < len && k < parts.Length && acc[k] == parts[k])
				{
					k++;
				}
				len = k;
			}
			for (int i = 0; i < len; i++)
			{
				common.Add(acc[i]);
			}
		}
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		for (int index = 0; index < parents.Count; index++)
		{
			string parent = parents[index];
			string stripped = parent;
			if (common.Count > 0)
			{
				string[] parts = parent.Split('/');
				List<string> rest = new List<string>();
				for (int i = common.Count; i < parts.Length; i++)
				{
					rest.Add(parts[i]);
				}
				stripped = string.Join("/", rest.ToArray());
			}
			int cut = stripped.IndexOf('/');
			string key = cut >= 0 ? stripped.Substring(0, cut) : stripped;
			if (key.Length > 0 && seen.Add(key))
			{
				CbzChapter ch = new CbzChapter();
				ch.Title = key;
				ch.FirstPageIndex = index;
				result.Add(ch);
			}
		}
		// آرشیوی که فقط یک پوشه داره (یا هیچ پوشه‌ای) فصل جداگانه نمی‌خواد
		if (result.Count < 2)
		{
			result.Clear();
		}
		return result;
	}

	public static ParsedBook ParseFile(string zipFile, string outDir, Func<int, string> sectionFallback, string titleFallback)
	{
		string mediaDir = Path.Combine(outDir, "media");
		Directory.CreateDirectory(mediaDir);
		List<string> pageFiles = new List<string>();
		List<string> sortedNames;
		using (ZipArchive zip = ZipFile.OpenRead(zipFile))
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
			sortedNames = SortedImageNames(byName.Keys);
			if (sortedNames.Count == 0)
			{
				throw new InvalidDataException("CBZ_NO_IMAGES");
			}
			for (int i = 0; i < sortedNames.Count; i++)
			{
				string name = sortedNames[i];
				string ext = Path.GetExtension(name).ToLowerInvariant();
				string rel = "media/p" + (i + 1).ToString("D5", CultureInfo.InvariantCulture) + ext;
				string full = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
				using (Stream src = byName[name].Open())
				using (FileStream dst = new FileStream(full, FileMode.Create, FileAccess.Write))
				{
					src.CopyTo(dst);
				}
				pageFiles.Add(rel);
			}
		}

		List<CbzChapter> chapters = ChaptersForEntries(sortedNames);
		List<ConvertedSection> sections = new List<ConvertedSection>();
		if (chapters.Count > 0)
		{
			for (int c = 0; c < chapters.Count; c++)
			{
				int start = chapters[c].FirstPageIndex;
				int end = c + 1 < chapters.Count ? chapters[c + 1].FirstPageIndex : pageFiles.Count;
				// صفحه‌های ریشه که قبل از اولین پوشه‌ان به اولین فصل می‌چسبن تا هیچ صفحه‌ای گم نشه
				if (c == 0)
				{
					start = 0;
				}
				sections.Add(new ConvertedSection(chapters[c].Title, PagesHtml(pageFiles, start, end)));
			}
		}
		else
		{
			for (int start = 0; start < pageFiles.Count; start += PagesPerSection)
			{
				int end = Math.Min(pageFiles.Count, start + PagesPerSection);
				sections.Add(new ConvertedSection(sectionFallback(sections.Count + 1), PagesHtml(pageFiles, start, end)));
			}
		}
		string title = TextBookParser.NameWithoutExtension(string.IsNullOrEmpty(titleFallback) ? zipFile : titleFallback);
		ParsedBook book = ConvertedBook.Write(outDir, title, sections, pageFiles[0]);
		return book;
	}

	private static string PagesHtml(List<string> pageFiles, int start, int end)
	{
		StringBuilder sb = new StringBuilder();
		for (int i = start; i < end; i++)
		{
			sb.Append("<p><img src=\"").Append(pageFiles[i]).Append("\" alt=\"\"/></p>");
		}
		return sb.ToString();
	}

	// ترتیب طبیعی: دنباله‌ی ارقام مثل عدد مقایسه می‌شه (۲ < ۱۰) و بقیه بدون توجه به بزرگی/کوچکی حروف
	public static int CompareNatural(string a, string b)
	{
		List<object> pa = SplitNatural(a);
		List<object> pb = SplitNatural(b);
		int n = Math.Min(pa.Count, pb.Count);
		for (int i = 0; i < n; i++)
		{
			object x = pa[i];
			object y = pb[i];
			int cmp;
			if (x is long && y is long)
			{
				cmp = ((long)x).CompareTo((long)y);
			}
			else
			{
				cmp = string.Compare(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);
			}
			if (cmp != 0)
			{
				return cmp;
			}
		}
		return pa.Count.CompareTo(pb.Count);
	}

	private static List<object> SplitNatural(string s)
	{
		List<object> parts = new List<object>();
		int i = 0;
		while (i < s.Length)
		{
			int j = i;
			if (s[i] >= '0' && s[i] <= '9')
			{
				while (j < s.Length && s[j] >= '0' && s[j] <= '9')
				{
					j++;
				}
				string digits = s.Substring(i, j - i);
				long v;
				if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out v))
				{
					parts.Add(v);
				}
				else
				{
					parts.Add(digits);
				}
			}
			else
			{
				while (j < s.Length && !(s[j] >= '0' && s[j] <= '9'))
				{
					j++;
				}
				parts.Add(s.Substring(i, j - i));
			}
			i = j;
		}
		return parts;
	}
}
}

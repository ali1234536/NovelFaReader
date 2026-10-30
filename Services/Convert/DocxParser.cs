using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace EpubReader.Services
{

// Word (.docx) books (port of DocxParser.kt). A docx is a zip: word/document.xml holds the text,
// word/_rels/document.xml.rels maps image ids to word/media/, docProps/core.xml may hold the title,
// and word/numbering.xml decides bullet vs. numbered lists.
public static class DocxParser
{
	private const string DcNs = "http://purl.org/dc/elements/1.1/";
	private const int MaxEntries = 10000;
	private const long MaxTotalBytes = 500L * 1024 * 1024;

	// Unzips the .docx into outDir (zip-bomb and path-traversal guards) and parses it.
	// The extracted word/ tree stays beside the generated chapters; it is harmless.
	public static ParsedBook ParseFile(string docxFile, string outDir, Func<int, string> chapterFallback, string titleFallback = null)
	{
		Directory.CreateDirectory(outDir);
		Unzip(docxFile, outDir);
		string fileName = !string.IsNullOrWhiteSpace(titleFallback) ? titleFallback : docxFile;
		return Parse(outDir, fileName, outDir, chapterFallback);
	}

	private static void Unzip(string zipPath, string outDir)
	{
		string canonicalDir = Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string prefix = canonicalDir + Path.DirectorySeparatorChar;
		using (FileStream fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
		using (ZipArchive zf = new ZipArchive(fs, ZipArchiveMode.Read))
		{
			int entryCount = 0;
			long totalBytes = 0;
			foreach (ZipArchiveEntry entry in zf.Entries)
			{
				entryCount++;
				if (entryCount > MaxEntries)
				{
					throw new IOException("Zip entry count exceeds limit: " + entry.FullName);
				}
				string outPath;
				try
				{
					outPath = Path.GetFullPath(Path.Combine(canonicalDir, entry.FullName));
				}
				catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
				{
					throw new IOException("Zip entry has an invalid path: " + entry.FullName);
				}
				if (!outPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("Zip entry outside target dir: " + entry.FullName);
				}
				bool isDir = entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
				if (isDir)
				{
					Directory.CreateDirectory(outPath);
					continue;
				}
				string parent = Path.GetDirectoryName(outPath);
				if (!string.IsNullOrEmpty(parent))
				{
					Directory.CreateDirectory(parent);
				}
				if (totalBytes + entry.Length > MaxTotalBytes)
				{
					throw new IOException("Zip uncompressed size exceeds limit");
				}
				using (Stream input = entry.Open())
				using (FileStream output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					byte[] buffer = new byte[81920];
					int n;
					while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
					{
						output.Write(buffer, 0, n);
						totalBytes += n;
						if (totalBytes > MaxTotalBytes)
						{
							throw new IOException("Zip uncompressed size exceeds limit");
						}
					}
				}
			}
		}
	}

	public static ParsedBook Parse(string extractDir, string fileName, string outDir, Func<int, string> chapterFallback)
	{
		string documentPath = Path.Combine(extractDir, "word", "document.xml");
		if (!File.Exists(documentPath))
		{
			throw new ArgumentException("Not a valid DOCX: word/document.xml missing");
		}
		XmlDocument doc = LoadXml(documentPath);
		Dictionary<string, string> rels = ReadRels(extractDir);
		DocxNumbering numbering = ReadNumbering(extractDir);
		string coreTitle = ReadCoreTitle(extractDir);
		string mediaDir = Path.Combine(outDir, "media");
		Directory.CreateDirectory(mediaDir);
		DocxState state = new DocxState(extractDir, rels, numbering, mediaDir);

		XmlElement body = doc.DocumentElement != null ? FirstChild(doc.DocumentElement, "body") : null;
		if (body == null)
		{
			throw new ArgumentException("Not a valid DOCX: no document body");
		}
		List<ConvertedSection> sections = new List<ConvertedSection>();
		string currentTitle = null;
		StringBuilder currentBody = new StringBuilder();
		List<DocxListItem> pendingList = null;
		Action flushList = () =>
		{
			if (pendingList != null)
			{
				currentBody.Append(RenderList(pendingList));
				pendingList = null;
			}
		};
		Action flushSection = () =>
		{
			flushList();
			if (currentBody.Length > 0 || currentTitle != null)
			{
				sections.Add(new ConvertedSection(currentTitle ?? chapterFallback(sections.Count + 1), currentBody.ToString()));
				currentBody = new StringBuilder();
				currentTitle = null;
			}
		};
		string title = coreTitle ?? TextBookParser.NameWithoutExtension(fileName);
		bool seenContent = false;
		foreach (XmlElement el in ChildElements(body))
		{
			string name = el.LocalName;
			if (name == "p")
			{
				DocxPara para = ParseParagraph(el, state);
				string style = para.Style;
				if (style == "Title" && !seenContent && coreTitle == null)
				{
					title = para.Text;
					continue;
				}
				if (style == "Heading1" || style == "Heading2")
				{
					flushSection();
					currentTitle = para.Text;
					int hl = style == "Heading1" ? 1 : 2;
					currentBody.Append("<h").Append(hl).Append(">").Append(ConvertedBook.Escape(para.Text)).Append("</h").Append(hl).Append(">");
					seenContent = true;
					continue;
				}
				if (para.IsList)
				{
					if (pendingList == null)
					{
						pendingList = new List<DocxListItem>();
					}
					DocxListItem li = new DocxListItem();
					li.Level = para.ListLevel;
					li.Ordered = numbering.IsOrdered(para.NumId);
					li.Html = para.Html;
					pendingList.Add(li);
					seenContent = true;
					continue;
				}
				flushList();
				if (style != null && style.StartsWith("Heading", StringComparison.Ordinal))
				{
					int level = 3;
					int? parsed = ParseIntOrNull(style.Substring("Heading".Length));
					if (parsed.HasValue)
					{
						level = Math.Max(3, Math.Min(6, parsed.Value));
					}
					currentBody.Append("<h").Append(level).Append(">").Append(para.Html).Append("</h").Append(level).Append(">");
				}
				else if (!string.IsNullOrWhiteSpace(para.Html))
				{
					currentBody.Append("<p>").Append(para.Html).Append("</p>");
				}
				if (!string.IsNullOrWhiteSpace(para.Html))
				{
					seenContent = true;
				}
			}
			else if (name == "tbl")
			{
				flushList();
				currentBody.Append(RenderTable(el, state));
				seenContent = true;
			}
			// sectPr and anything else: layout metadata, not content.
		}
		flushSection();
		if (sections.Count == 0)
		{
			return ConvertedBook.Write(outDir, title, new List<ConvertedSection>());
		}
		return ConvertedBook.Write(outDir, title, ConvertedBook.ChunkSections(sections), state.FirstImage);
	}

	private sealed class DocxState
	{
		public readonly string ExtractDir;
		public readonly Dictionary<string, string> Rels;
		public readonly DocxNumbering Numbering;
		public readonly string MediaDir;
		public string FirstImage;

		public DocxState(string extractDir, Dictionary<string, string> rels, DocxNumbering numbering, string mediaDir)
		{
			ExtractDir = extractDir;
			Rels = rels;
			Numbering = numbering;
			MediaDir = mediaDir;
		}
	}

	private sealed class DocxPara
	{
		public string Text;
		public string Html;
		public string Style;
		public bool IsList;
		public int ListLevel;
		public int? NumId;
	}

	private sealed class DocxListItem
	{
		public int Level;
		public bool Ordered;
		public string Html;
	}

	// numId -> abstract numbering -> ordered? (bullet formats are unordered).
	private sealed class DocxNumbering
	{
		private readonly Dictionary<int, bool> _orderedByNum;

		public DocxNumbering(Dictionary<int, bool> orderedByNum)
		{
			_orderedByNum = orderedByNum;
		}

		public bool IsOrdered(int? numId)
		{
			if (!numId.HasValue)
			{
				return false;
			}
			bool ordered;
			if (_orderedByNum.TryGetValue(numId.Value, out ordered))
			{
				return ordered;
			}
			return true;
		}
	}

	// One paragraph: runs -> HTML, plus style / list metadata.
	private static DocxPara ParseParagraph(XmlElement p, DocxState state)
	{
		string style = null;
		int? numId = null;
		int ilvl = 0;
		XmlElement pPr = FirstChild(p, "pPr");
		if (pPr != null)
		{
			XmlElement pStyle = FirstChild(pPr, "pStyle");
			if (pStyle != null)
			{
				style = pStyle.GetAttribute("w:val");
			}
			XmlElement numPr = FirstChild(pPr, "numPr");
			if (numPr != null)
			{
				XmlElement numIdEl = FirstChild(numPr, "numId");
				if (numIdEl != null)
				{
					numId = ParseIntOrNull(WVal(numIdEl));
				}
				XmlElement ilvlEl = FirstChild(numPr, "ilvl");
				if (ilvlEl != null)
				{
					int? v = ParseIntOrNull(WVal(ilvlEl));
					if (v.HasValue)
					{
						ilvl = v.Value;
					}
				}
			}
		}
		StringBuilder html = new StringBuilder();
		StringBuilder plain = new StringBuilder();
		foreach (XmlElement el in ChildElements(p))
		{
			string name = el.LocalName;
			if (name == "r")
			{
				string h;
				string t;
				RenderRun(el, state, out h, out t);
				html.Append(h);
				plain.Append(t);
			}
			else if (name == "hyperlink")
			{
				// Hyperlink runs render like normal runs; the href survives only for external relationships.
				string href = null;
				string rid = el.GetAttribute("r:id");
				if (rid.Length > 0)
				{
					string target;
					if (state.Rels.TryGetValue(rid, out target))
					{
						href = target;
					}
				}
				StringBuilder innerH = new StringBuilder();
				StringBuilder innerT = new StringBuilder();
				foreach (XmlElement hn in ChildElements(el))
				{
					if (hn.LocalName == "r")
					{
						string h;
						string t;
						RenderRun(hn, state, out h, out t);
						innerH.Append(h);
						innerT.Append(t);
					}
				}
				if (href != null && (href.StartsWith("http://", StringComparison.Ordinal) || href.StartsWith("https://", StringComparison.Ordinal)))
				{
					html.Append("<a href=\"").Append(ConvertedBook.Escape(href)).Append("\">").Append(innerH.ToString()).Append("</a>");
				}
				else
				{
					html.Append(innerH.ToString());
				}
				plain.Append(innerT.ToString());
			}
			// bookmarkStart/End, proofErr, etc.: no visible text.
		}
		DocxPara result = new DocxPara();
		result.Text = plain.ToString().Trim();
		result.Html = html.ToString();
		result.Style = style;
		result.IsList = numId.HasValue;
		result.ListLevel = Math.Max(0, Math.Min(8, ilvl));
		result.NumId = numId;
		return result;
	}

	// One run: text nodes + inline formatting + embedded images.
	private static void RenderRun(XmlElement r, DocxState state, out string htmlOut, out string plainOut)
	{
		bool bold = false;
		bool italic = false;
		bool underline = false;
		bool strike = false;
		XmlElement rPr = FirstChild(r, "rPr");
		if (rPr != null)
		{
			foreach (XmlElement n in ChildElements(rPr))
			{
				string ln = n.LocalName;
				if (ln == "b")
				{
					bold = true;
				}
				else if (ln == "i")
				{
					italic = true;
				}
				else if (ln == "u")
				{
					underline = true;
				}
				else if (ln == "strike")
				{
					strike = true;
				}
			}
		}
		StringBuilder html = new StringBuilder();
		StringBuilder plain = new StringBuilder();
		foreach (XmlElement el in ChildElements(r))
		{
			string name = el.LocalName;
			if (name == "t")
			{
				string t = el.InnerText ?? "";
				plain.Append(t);
				html.Append(ConvertedBook.Escape(t));
			}
			else if (name == "tab")
			{
				plain.Append(' ');
				html.Append(' ');
			}
			else if (name == "br")
			{
				html.Append("<br/>");
			}
			else if (name == "drawing" || name == "pict")
			{
				// Embedded image: blip/imageData r:embed -> media file.
				string embed = FindEmbedId(el);
				string target = null;
				if (embed == null || !state.Rels.TryGetValue(embed, out target))
				{
					continue;
				}
				string srcPath = ResolveInside(state.ExtractDir, "word/" + target);
				if (srcPath == null || !File.Exists(srcPath))
				{
					continue;
				}
				string destName = Path.GetFileName(srcPath);
				string dest = Path.Combine(state.MediaDir, destName);
				if (!File.Exists(dest))
				{
					try
					{
						File.Copy(srcPath, dest);
					}
					catch (Exception)
					{
					}
				}
				bool destExists = File.Exists(dest);
				if (state.FirstImage == null && destExists)
				{
					state.FirstImage = dest;
				}
				if (destExists)
				{
					html.Append("<img src=\"media/").Append(ConvertedBook.Escape(destName)).Append("\" />");
				}
			}
		}
		string outHtml = html.ToString();
		if (strike)
		{
			outHtml = "<del>" + outHtml + "</del>";
		}
		if (underline)
		{
			outHtml = "<u>" + outHtml + "</u>";
		}
		if (italic)
		{
			outHtml = "<em>" + outHtml + "</em>";
		}
		if (bold)
		{
			outHtml = "<strong>" + outHtml + "</strong>";
		}
		htmlOut = outHtml;
		plainOut = plain.ToString();
	}

	// Full path of rel under baseDir, or null when it does not resolve to a location inside baseDir.
	private static string ResolveInside(string baseDir, string rel)
	{
		try
		{
			string root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string full = Path.GetFullPath(Path.Combine(root, rel));
			if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return full;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static void CollectByLocalName(XmlNode parent, string local, List<XmlElement> into)
	{
		foreach (XmlNode n in parent.ChildNodes)
		{
			XmlElement e = n as XmlElement;
			if (e == null)
			{
				continue;
			}
			if (e.LocalName == local)
			{
				into.Add(e);
			}
			CollectByLocalName(e, local, into);
		}
	}

	private static string FindEmbedId(XmlElement el)
	{
		// drawing -> blip/@r:embed; pict -> imagedata/@r:id.
		List<XmlElement> blips = new List<XmlElement>();
		CollectByLocalName(el, "blip", blips);
		foreach (XmlElement e in blips)
		{
			string v = e.GetAttribute("r:embed");
			if (v.Length == 0)
			{
				v = e.GetAttribute("embed");
			}
			if (v.Length > 0)
			{
				return v;
			}
		}
		List<XmlElement> imgs = new List<XmlElement>();
		CollectByLocalName(el, "imagedata", imgs);
		foreach (XmlElement e in imgs)
		{
			string v = e.GetAttribute("r:id");
			if (v.Length == 0)
			{
				v = e.GetAttribute("id");
			}
			if (v.Length > 0)
			{
				return v;
			}
		}
		return null;
	}

	private static string RenderTable(XmlElement tbl, DocxState state)
	{
		StringBuilder sb = new StringBuilder("<table>");
		foreach (XmlElement row in ChildElements(tbl))
		{
			if (row.LocalName != "tr")
			{
				continue;
			}
			sb.Append("<tr>");
			foreach (XmlElement cell in ChildElements(row))
			{
				if (cell.LocalName != "tc")
				{
					continue;
				}
				List<string> parts = new List<string>();
				foreach (XmlElement cn in ChildElements(cell))
				{
					if (cn.LocalName != "p")
					{
						continue;
					}
					DocxPara para = ParseParagraph(cn, state);
					if (!string.IsNullOrWhiteSpace(para.Html))
					{
						parts.Add(para.Html);
					}
				}
				sb.Append("<td>").Append(string.Join("<br/>", parts.ToArray())).Append("</td>");
			}
			sb.Append("</tr>");
		}
		return sb.Append("</table>").ToString();
	}

	// Flat list items -> nested <ul>/<ol> by level.
	private static string RenderList(List<DocxListItem> items)
	{
		int next;
		return RenderListAt(items, 0, items.Count > 0 ? items[0].Level : 0, out next);
	}

	// Renders items from 'from' at baseLevel; returns the HTML and (via next) the index of the first unconsumed item.
	// A deeper run nests inside the previous <li> (its closing tag is stripped, the nest appended, the tag restored).
	private static string RenderListAt(List<DocxListItem> items, int from, int baseLevel, out int next)
	{
		const string tail = "</li>";
		StringBuilder sb = new StringBuilder();
		int idx = from;
		string openTag = null;
		while (idx < items.Count)
		{
			DocxListItem item = items[idx];
			if (item.Level < baseLevel)
			{
				break;
			}
			if (item.Level > baseLevel)
			{
				int nestedNext;
				string nested = RenderListAt(items, idx, item.Level, out nestedNext);
				if (EndsWith(sb, tail))
				{
					sb.Length = sb.Length - tail.Length;
				}
				sb.Append(nested).Append(tail);
				idx = nestedNext;
				continue;
			}
			string tag = item.Ordered ? "ol" : "ul";
			if (openTag != tag)
			{
				if (openTag != null)
				{
					sb.Append("</").Append(openTag).Append(">");
				}
				sb.Append("<").Append(tag).Append(">");
				openTag = tag;
			}
			sb.Append("<li>").Append(item.Html).Append("</li>");
			idx++;
		}
		if (openTag != null)
		{
			sb.Append("</").Append(openTag).Append(">");
		}
		next = idx;
		return sb.ToString();
	}

	private static bool EndsWith(StringBuilder sb, string tail)
	{
		if (sb.Length < tail.Length)
		{
			return false;
		}
		int off = sb.Length - tail.Length;
		for (int k = 0; k < tail.Length; k++)
		{
			if (sb[off + k] != tail[k])
			{
				return false;
			}
		}
		return true;
	}

	private static DocxNumbering ReadNumbering(string extractDir)
	{
		string file = Path.Combine(extractDir, "word", "numbering.xml");
		if (!File.Exists(file))
		{
			return new DocxNumbering(new Dictionary<int, bool>());
		}
		try
		{
			XmlDocument doc = LoadXml(file);
			XmlElement root = doc.DocumentElement;
			if (root == null)
			{
				return new DocxNumbering(new Dictionary<int, bool>());
			}
			Dictionary<int, bool> abstractFmt = new Dictionary<int, bool>();
			foreach (XmlElement el in ChildElements(root))
			{
				if (el.LocalName != "abstractNum")
				{
					continue;
				}
				int? id = ParseIntOrNull(AttrOrBare(el, "w:abstractNumId", "abstractNumId"));
				if (!id.HasValue)
				{
					continue;
				}
				// Level 0 decides the list kind.
				bool ordered = true;
				foreach (XmlElement lvlEl in ChildElements(el))
				{
					if (lvlEl.LocalName != "lvl")
					{
						continue;
					}
					int? ilvlParsed = ParseIntOrNull(AttrOrBare(lvlEl, "w:ilvl", "ilvl"));
					int ilvl = ilvlParsed.HasValue ? ilvlParsed.Value : 0;
					if (ilvl != 0)
					{
						continue;
					}
					string fmt = null;
					XmlElement fmtEl = FirstChild(lvlEl, "numFmt");
					if (fmtEl != null)
					{
						fmt = WVal(fmtEl);
					}
					ordered = fmt != null && fmt != "bullet" && fmt != "none";
					break;
				}
				abstractFmt[id.Value] = ordered;
			}
			Dictionary<int, bool> orderedByNum = new Dictionary<int, bool>();
			foreach (XmlElement el in ChildElements(root))
			{
				if (el.LocalName != "num")
				{
					continue;
				}
				int? numId = ParseIntOrNull(AttrOrBare(el, "w:numId", "numId"));
				if (!numId.HasValue)
				{
					continue;
				}
				XmlElement absEl = FirstChild(el, "abstractNumId");
				if (absEl == null)
				{
					continue;
				}
				int? abs = ParseIntOrNull(WVal(absEl));
				if (!abs.HasValue)
				{
					continue;
				}
				bool fmtOrdered;
				if (!abstractFmt.TryGetValue(abs.Value, out fmtOrdered))
				{
					fmtOrdered = true;
				}
				orderedByNum[numId.Value] = fmtOrdered;
			}
			return new DocxNumbering(orderedByNum);
		}
		catch (Exception)
		{
			return new DocxNumbering(new Dictionary<int, bool>());
		}
	}

	private static Dictionary<string, string> ReadRels(string extractDir)
	{
		Dictionary<string, string> result = new Dictionary<string, string>();
		string file = Path.Combine(extractDir, "word", "_rels", "document.xml.rels");
		if (!File.Exists(file))
		{
			return result;
		}
		try
		{
			XmlDocument doc = LoadXml(file);
			if (doc.DocumentElement == null)
			{
				return result;
			}
			foreach (XmlElement el in ChildElements(doc.DocumentElement))
			{
				string id = el.GetAttribute("Id");
				string target = el.GetAttribute("Target");
				if (id.Length > 0 && target.Length > 0 &&
					!target.StartsWith("http://", StringComparison.Ordinal) && !target.StartsWith("https://", StringComparison.Ordinal))
				{
					result[id] = target.StartsWith("/", StringComparison.Ordinal) ? target.Substring(1) : target;
				}
				else if (id.Length > 0 && target.Length > 0)
				{
					result[id] = target;
				}
			}
			return result;
		}
		catch (Exception)
		{
			return new Dictionary<string, string>();
		}
	}

	private static string ReadCoreTitle(string extractDir)
	{
		string file = Path.Combine(extractDir, "docProps", "core.xml");
		if (!File.Exists(file))
		{
			return null;
		}
		try
		{
			XmlDocument doc = LoadXml(file);
			XmlNodeList titles = doc.GetElementsByTagName("title", DcNs);
			if (titles.Count == 0)
			{
				return null;
			}
			string text = (titles[0].InnerText ?? "").Trim();
			return text.Length > 0 ? text : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	// DTD processing is prohibited and no resolver is set; whitespace-only w:t nodes are preserved.
	private static XmlDocument LoadXml(string path)
	{
		XmlReaderSettings settings = new XmlReaderSettings();
		settings.DtdProcessing = DtdProcessing.Prohibit;
		settings.XmlResolver = null;
		settings.IgnoreWhitespace = false;
		XmlDocument doc = new XmlDocument();
		doc.PreserveWhitespace = true;
		doc.XmlResolver = null;
		using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
		using (XmlReader xr = XmlReader.Create(fs, settings))
		{
			doc.Load(xr);
		}
		return doc;
	}

	private static IEnumerable<XmlElement> ChildElements(XmlNode parent)
	{
		foreach (XmlNode n in parent.ChildNodes)
		{
			XmlElement e = n as XmlElement;
			if (e != null)
			{
				yield return e;
			}
		}
	}

	private static XmlElement FirstChild(XmlElement parent, string local)
	{
		foreach (XmlNode n in parent.ChildNodes)
		{
			XmlElement e = n as XmlElement;
			if (e != null && e.LocalName == local)
			{
				return e;
			}
		}
		return null;
	}

	// getAttribute("w:val") falling back to getAttribute("val") when empty.
	private static string WVal(XmlElement el)
	{
		return AttrOrBare(el, "w:val", "val");
	}

	private static string AttrOrBare(XmlElement el, string qualified, string bare)
	{
		string v = el.GetAttribute(qualified);
		if (v.Length == 0)
		{
			v = el.GetAttribute(bare);
		}
		return v;
	}

	private static int? ParseIntOrNull(string s)
	{
		int v;
		if (!string.IsNullOrEmpty(s) && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v))
		{
			return v;
		}
		return null;
	}
}
}

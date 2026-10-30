using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace EpubReader.Services
{

// Plain-text and Markdown books (port of TextBookParser.kt).
// Direction is not decided here: chapters are plain HTML, the reader stamps each paragraph with its own direction.
public static class TextBookParser
{
	// ASCII-only whitespace class: Java's \s is ASCII-only, .NET's is Unicode-aware.
	private const string Ws = "[ \\t\\n\\x0B\\f\\r]";

	private static readonly HashSet<string> HeadingWordsFa = new HashSet<string>(StringComparer.Ordinal)
	{
		"مقدمه", "پیش‌گفتار", "پیشگفتار", "مؤخره", "پس‌گفتار", "دیباچه",
		"سرآغاز", "فهرست", "درباره‌ی نویسنده", "درباره نویسنده",
		"درباره‌ی کتاب", "درباره کتاب", "سپاسگزاری", "یادداشت مترجم"
	};

	private static readonly HashSet<string> HeadingWordsEn = new HashSet<string>(StringComparer.Ordinal)
	{
		"introduction", "preface", "prologue", "epilogue", "foreword",
		"contents", "acknowledgements", "acknowledgments", "about the author"
	};

	private const string TitleStopChars = ".!؟?،؛:";

	private static readonly Regex BlankSplit = new Regex("\\n" + Ws + "*\\n", RegexOptions.CultureInvariant);
	private static readonly Regex HeadingFaRe = new Regex("^(فصل|بخش|قسمت|کتاب)" + Ws + "+[\\p{L}0-9۰-۹]+", RegexOptions.CultureInvariant);
	private static readonly Regex HeadingEnRe = new Regex("^(chapter|part|section)" + Ws + "+[\\p{L}0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	// Kotlin File(name).nameWithoutExtension, tolerant of any characters (Path.GetFileNameWithoutExtension throws on invalid ones).
	internal static string NameWithoutExtension(string path)
	{
		if (path == null)
		{
			return "";
		}
		string p = path.TrimEnd('/', '\\');
		int cut = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
		string name = cut >= 0 ? p.Substring(cut + 1) : p;
		int dot = name.LastIndexOf('.');
		return dot >= 0 ? name.Substring(0, dot) : name;
	}

	private static string Normalize(string decoded)
	{
		return decoded.Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿', '\n');
	}

	private static string ParagraphsHtml(List<string> blocks)
	{
		StringBuilder sb = new StringBuilder();
		foreach (string b in blocks)
		{
			sb.Append("<p>").Append(ConvertedBook.Escape(b)).Append("</p>");
		}
		return sb.ToString();
	}

	public static ParsedBook ParseText(byte[] bytes, string fileName, string outDir, Func<int, string> chapterFallback)
	{
		string raw = Normalize(DecodeText(bytes));
		List<string> blocks = SplitParagraphs(raw);
		if (blocks.Count == 0)
		{
			return ConvertedBook.Write(outDir, NameWithoutExtension(fileName), new List<ConvertedSection>());
		}
		// Book title: a short first line with no sentence punctuation, followed by a blank line, in a document that has more to say.
		string title = NameWithoutExtension(fileName);
		List<string> bodyBlocks = blocks;
		string first = blocks[0];
		if (blocks.Count >= 3 && first.IndexOf('\n') < 0 && first.Length <= 60 &&
			TitleStopChars.IndexOf(first[first.Length - 1]) < 0 && !IsHeading(first))
		{
			title = first.Trim();
			bodyBlocks = blocks.GetRange(1, blocks.Count - 1);
		}
		List<ConvertedSection> sections = new List<ConvertedSection>();
		List<string> current = new List<string>();
		string currentTitle = null;
		Action flush = () =>
		{
			if (current.Count == 0)
			{
				return;
			}
			string body = ParagraphsHtml(current);
			sections.Add(new ConvertedSection(currentTitle ?? chapterFallback(sections.Count + 1), body));
			current = new List<string>();
			currentTitle = null;
		};
		foreach (string block in bodyBlocks)
		{
			if (IsHeading(block) && block.IndexOf('\n') < 0)
			{
				flush();
				currentTitle = block.Trim();
			}
			else
			{
				current.Add(block);
			}
		}
		flush();
		if (sections.Count == 0)
		{
			sections.Add(new ConvertedSection(chapterFallback(1), ParagraphsHtml(bodyBlocks)));
		}
		return ConvertedBook.Write(outDir, title, ConvertedBook.ChunkSections(sections));
	}

	public static ParsedBook ParseMarkdown(byte[] bytes, string fileName, string outDir, Func<int, string> chapterFallback)
	{
		string raw = Normalize(DecodeText(bytes));
		MdParser md = new MdParser(raw);
		string title = md.Title ?? NameWithoutExtension(fileName);
		List<ConvertedSection> sections = new List<ConvertedSection>();
		for (int i = 0; i < md.Sections.Count; i++)
		{
			MdSection s = md.Sections[i];
			sections.Add(new ConvertedSection(s.Title ?? chapterFallback(i + 1), s.BodyHtml));
		}
		return ConvertedBook.Write(outDir, title, sections.Count == 0 ? new List<ConvertedSection>() : ConvertedBook.ChunkSections(sections));
	}

	// Encoding, in order: BOM (UTF-8 / UTF-16 LE / BE), strict UTF-8, then Windows-1256, then lenient UTF-8.
	public static string DecodeText(byte[] bytes)
	{
		if (bytes == null || bytes.Length == 0)
		{
			return "";
		}
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
		{
			return new UTF8Encoding(false, false).GetString(bytes, 3, bytes.Length - 3);
		}
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
		{
			return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
		}
		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
		{
			return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
		}
		try
		{
			return new UTF8Encoding(false, true).GetString(bytes);
		}
		catch (Exception)
		{
		}
		try
		{
			return Encoding.GetEncoding(1256).GetString(bytes);
		}
		catch (Exception)
		{
			return new UTF8Encoding(false, false).GetString(bytes);
		}
	}

	// Blank-line paragraphs; with no blank lines, every line is one.
	private static List<string> SplitParagraphs(string text)
	{
		List<string> byBlank = new List<string>();
		foreach (string part in BlankSplit.Split(text))
		{
			string t = part.Trim();
			if (t.Length > 0)
			{
				byBlank.Add(t);
			}
		}
		if (byBlank.Count > 1)
		{
			return byBlank;
		}
		List<string> byLine = new List<string>();
		foreach (string line in text.Split('\n'))
		{
			string t = line.Trim();
			if (t.Length > 0)
			{
				byLine.Add(t);
			}
		}
		return byLine;
	}

	private static bool IsHeading(string block)
	{
		string line = block.Trim();
		if (line.Length == 0 || line.Length > 80)
		{
			return false;
		}
		if (HeadingWordsFa.Contains(line))
		{
			return true;
		}
		if (HeadingWordsEn.Contains(line.ToLowerInvariant()))
		{
			return true;
		}
		if (HeadingFaRe.IsMatch(line))
		{
			return true;
		}
		if (HeadingEnRe.IsMatch(line))
		{
			return true;
		}
		// ALL-CAPS Latin line.
		if (line.Length >= 3)
		{
			bool anyLower = false;
			bool anyUpper = false;
			foreach (char c in line)
			{
				if (char.IsLower(c))
				{
					anyLower = true;
					break;
				}
				if (char.IsUpper(c))
				{
					anyUpper = true;
				}
			}
			if (!anyLower && anyUpper)
			{
				return true;
			}
		}
		return false;
	}

	private sealed class MdSection
	{
		public string Title;
		public string BodyHtml;

		public MdSection(string title, string bodyHtml)
		{
			Title = title;
			BodyHtml = bodyHtml;
		}
	}

	private sealed class MdItem
	{
		public int Indent;
		public bool Ordered;
		public string Text;
	}

	// Small CommonMark-subset renderer: headings, fenced code, lists (nested by indent), blockquotes, tables, rules, inline formatting.
	private sealed class MdParser
	{
		private const string Dot = "[^\\n\\r\\u0085\\u2028\\u2029]";

		private static readonly Regex AtxRe = new Regex("\\A(#{1,6})" + Ws + "+(" + Dot + "*)\\z", RegexOptions.CultureInvariant);
		private static readonly Regex HrRe = new Regex("\\A" + Ws + "*([-*_])(" + Ws + "*\\1){2,}" + Ws + "*\\z", RegexOptions.CultureInvariant);
		private static readonly Regex TableDelimRe = new Regex("\\A" + Ws + "*\\|?[ \\t\\n\\x0B\\f\\r:|-]+\\|" + Ws + "*\\z", RegexOptions.CultureInvariant);
		private static readonly Regex ListRe = new Regex("\\A(" + Ws + "*)([-*+]|[0-9]+[.)])" + Ws + "+(" + Dot + "*)\\z", RegexOptions.CultureInvariant);
		private static readonly Regex ListMarkerOnlyRe = new Regex("\\A(" + Ws + "*)([-*+]|[0-9]+[.)])" + Ws + "+\\z", RegexOptions.CultureInvariant);

		private static readonly Regex CodeSpanRe = new Regex("`([^`]+)`", RegexOptions.CultureInvariant);
		private static readonly Regex ImageRe = new Regex("!\\[([^\\]]*)\\]\\([^)]*\\)", RegexOptions.CultureInvariant);
		private static readonly Regex LinkRe = new Regex("\\[([^\\]]+)\\]\\(([^) \\t\\n\\x0B\\f\\r]+)(?:" + Ws + "+\"[^\"]*\")?\\)", RegexOptions.CultureInvariant);
		private static readonly Regex BoldStarRe = new Regex("\\*\\*([^*]+)\\*\\*", RegexOptions.CultureInvariant);
		private static readonly Regex BoldUnderRe = new Regex("__([^_]+)__", RegexOptions.CultureInvariant);
		private static readonly Regex StrikeRe = new Regex("~~([^~]+)~~", RegexOptions.CultureInvariant);
		private static readonly Regex ItalicStarRe = new Regex("\\*([^*]+)\\*", RegexOptions.CultureInvariant);
		private static readonly Regex ItalicUnderRe = new Regex("(?<![A-Za-z0-9_])_([^_]+)_(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
		private static readonly Regex CodeRestoreRe = new Regex("\\x00([0-9]+)\\x00", RegexOptions.CultureInvariant);
		private static readonly Regex TagRe = new Regex("<[^>]+>", RegexOptions.CultureInvariant);

		public string Title;
		public readonly List<MdSection> Sections = new List<MdSection>();

		private readonly string _raw;
		private readonly StringBuilder _out = new StringBuilder();
		private string _sectionTitle;

		public MdParser(string raw)
		{
			_raw = raw;
			Parse();
		}

		private void FlushSection()
		{
			if (_out.Length > 0 || _sectionTitle != null)
			{
				Sections.Add(new MdSection(_sectionTitle, _out.ToString()));
				_out.Length = 0;
				_sectionTitle = null;
			}
		}

		private static bool AllOf(string s, char c)
		{
			foreach (char x in s)
			{
				if (x != c)
				{
					return false;
				}
			}
			return true;
		}

		private static bool AllOf(string s, char a, char b)
		{
			foreach (char x in s)
			{
				if (x != a && x != b)
				{
					return false;
				}
			}
			return true;
		}

		private static bool IsTableStart(string[] lines, int i)
		{
			return lines[i].IndexOf('|') >= 0 && i + 1 < lines.Length &&
				TableDelimRe.IsMatch(lines[i + 1]) &&
				lines[i + 1].IndexOf('-') >= 0;
		}

		private void Parse()
		{
			string[] lines = _raw.Split('\n');
			int i = 0;
			while (i < lines.Length)
			{
				string line = lines[i];
				if (string.IsNullOrWhiteSpace(line))
				{
					i++;
					continue;
				}
				string trimmed = line.Trim();
				// A stray === underline: markup, not text.
				if (trimmed.Length > 0 && AllOf(trimmed, '='))
				{
					i++;
					continue;
				}
				// Fenced code block.
				if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
				{
					StringBuilder buf = new StringBuilder();
					i++;
					while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
					{
						buf.Append(lines[i]).Append('\n');
						i++;
					}
					i++; // closing fence
					_out.Append("<pre><code>")
						.Append(ConvertedBook.Escape(buf.ToString().TrimEnd('\n')))
						.Append("</code></pre>");
					continue;
				}
				// ATX heading.
				Match atx = AtxRe.Match(trimmed);
				if (atx.Success)
				{
					int level = atx.Groups[1].Value.Length;
					string text = atx.Groups[2].Value.Trim().TrimEnd('#').Trim();
					if (level == 1 && Title == null && Sections.Count == 0 && _out.Length == 0)
					{
						Title = StripInline(text);
						i++;
						continue;
					}
					if (level <= 2)
					{
						FlushSection();
						_sectionTitle = StripInline(text);
					}
					_out.Append("<h").Append(level).Append(">").Append(Inline(text)).Append("</h").Append(level).Append(">");
					i++;
					continue;
				}
				// Setext heading (text line followed by === or ---).
				if (i + 1 < lines.Length)
				{
					string under = lines[i + 1].Trim();
					if (under.Length > 0 && AllOf(under, '='))
					{
						if (Title == null && Sections.Count == 0 && _out.Length == 0)
						{
							Title = StripInline(trimmed);
						}
						else
						{
							FlushSection();
							_sectionTitle = StripInline(trimmed);
							_out.Append("<h1>").Append(Inline(trimmed)).Append("</h1>");
						}
						i += 2;
						continue;
					}
					if (under.Length > 0 && AllOf(under, '-') && !string.IsNullOrWhiteSpace(line))
					{
						FlushSection();
						_sectionTitle = StripInline(trimmed);
						_out.Append("<h2>").Append(Inline(trimmed)).Append("</h2>");
						i += 2;
						continue;
					}
				}
				// Horizontal rule.
				if (HrRe.IsMatch(line))
				{
					_out.Append("<hr/>");
					i++;
					continue;
				}
				// Table (header + delimiter row).
				if (IsTableStart(lines, i))
				{
					List<string> headerCells = SplitRow(line);
					_out.Append("<table><thead><tr>");
					foreach (string c in headerCells)
					{
						_out.Append("<th>").Append(Inline(c)).Append("</th>");
					}
					_out.Append("</tr></thead><tbody>");
					i += 2;
					while (i < lines.Length && lines[i].IndexOf('|') >= 0 && !string.IsNullOrWhiteSpace(lines[i]))
					{
						_out.Append("<tr>");
						foreach (string c in SplitRow(lines[i]))
						{
							_out.Append("<td>").Append(Inline(c)).Append("</td>");
						}
						_out.Append("</tr>");
						i++;
					}
					_out.Append("</tbody></table>");
					continue;
				}
				// Blockquote run.
				if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
				{
					StringBuilder buf = new StringBuilder();
					while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
					{
						buf.Append(lines[i].TrimStart().Substring(1).TrimStart()).Append(' ');
						i++;
					}
					_out.Append("<blockquote><p>").Append(Inline(buf.ToString().Trim())).Append("</p></blockquote>");
					continue;
				}
				// List run (ordered or unordered, nested by indent).
				if (ListRe.IsMatch(line))
				{
					List<MdItem> items = new List<MdItem>();
					while (i < lines.Length)
					{
						Match m = ListRe.Match(lines[i]);
						if (!m.Success)
						{
							break;
						}
						MdItem item = new MdItem();
						item.Indent = m.Groups[1].Value.Length;
						item.Ordered = char.IsDigit(m.Groups[2].Value[0]);
						item.Text = m.Groups[3].Value;
						items.Add(item);
						i++;
					}
					_out.Append(RenderList(items, 0, items.Count > 0 ? items[0].Indent : 0));
					continue;
				}
				// Plain paragraph: consecutive lines joined with a space (soft wrap); a trailing double space is a <br/>.
				StringBuilder para = new StringBuilder();
				int start = i;
				while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) &&
					!lines[i].TrimStart().StartsWith("#", StringComparison.Ordinal) &&
					!lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal) &&
					!lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal) &&
					!ListMarkerOnlyRe.IsMatch(lines[i]) &&
					!IsTableStart(lines, i))
				{
					if (para.Length > 0)
					{
						para.Append(EndsWithDoubleSpace(para) ? "<br/>" : " ");
					}
					para.Append(lines[i].Trim());
					i++;
					// Setext underline ends this paragraph (handled next loop).
					if (i < lines.Length)
					{
						string nextTrim = lines[i].Trim();
						if (nextTrim.Length > 0 && AllOf(nextTrim, '=', '-'))
						{
							break;
						}
					}
				}
				if (i == start)
				{
					// A line like "#tag" matches no branch above: emit it as a paragraph so the loop always advances.
					para.Append(lines[i].Trim());
					i++;
				}
				_out.Append("<p>").Append(Inline(para.ToString())).Append("</p>");
			}
			FlushSection();
		}

		private static bool EndsWithDoubleSpace(StringBuilder sb)
		{
			return sb.Length >= 2 && sb[sb.Length - 1] == ' ' && sb[sb.Length - 2] == ' ';
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

		private static List<string> SplitRow(string line)
		{
			string t = line.Trim();
			if (t.StartsWith("|", StringComparison.Ordinal))
			{
				t = t.Substring(1);
			}
			if (t.EndsWith("|", StringComparison.Ordinal))
			{
				t = t.Substring(0, t.Length - 1);
			}
			List<string> cells = new List<string>();
			foreach (string c in t.Split('|'))
			{
				cells.Add(c.Trim());
			}
			return cells;
		}

		// Renders a flat item list into nested <ul>/<ol> by indent level.
		private string RenderList(List<MdItem> items, int from, int baseIndent)
		{
			const string tail = "</li>";
			StringBuilder sb = new StringBuilder();
			int idx = from;
			string openTag = null;
			while (idx < items.Count)
			{
				MdItem item = items[idx];
				if (item.Indent < baseIndent)
				{
					break;
				}
				if (item.Indent > baseIndent)
				{
					// Nested run belongs inside the previous <li>: reopen it.
					string nested = RenderList(items, idx, item.Indent);
					if (EndsWith(sb, tail))
					{
						sb.Length = sb.Length - tail.Length;
					}
					sb.Append(nested).Append(tail);
					int j = idx;
					while (j < items.Count && items[j].Indent >= item.Indent)
					{
						j++;
					}
					idx = j;
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
				sb.Append("<li>").Append(Inline(item.Text)).Append("</li>");
				idx++;
			}
			if (openTag != null)
			{
				sb.Append("</").Append(openTag).Append(">");
			}
			return sb.ToString();
		}

		// Inline formatting with code spans protected from later passes.
		private static string Inline(string text)
		{
			List<string> codes = new List<string>();
			string t = ConvertedBook.Escape(text);
			t = CodeSpanRe.Replace(t, (Match m) =>
			{
				codes.Add(m.Groups[1].Value);
				return "\u0000" + (codes.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0000";
			});
			// Images: keep only the alt text.
			t = ImageRe.Replace(t, (Match m) =>
			{
				string alt = m.Groups[1].Value;
				return string.IsNullOrWhiteSpace(alt) ? "" : "<em>" + alt + "</em>";
			});
			t = LinkRe.Replace(t, (Match m) =>
			{
				return "<a href=\"" + m.Groups[2].Value + "\">" + m.Groups[1].Value + "</a>";
			});
			t = BoldStarRe.Replace(t, "<strong>$1</strong>");
			t = BoldUnderRe.Replace(t, "<strong>$1</strong>");
			t = StrikeRe.Replace(t, "<del>$1</del>");
			t = ItalicStarRe.Replace(t, "<em>$1</em>");
			t = ItalicUnderRe.Replace(t, "<em>$1</em>");
			t = CodeRestoreRe.Replace(t, (Match m) =>
			{
				int idx = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				return "<code>" + codes[idx] + "</code>";
			});
			return t;
		}

		private static string StripInline(string text)
		{
			return TagRe.Replace(Inline(text), "")
				.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
				.Replace("&#39;", "'").Replace("&amp;", "&");
		}
	}
}
}

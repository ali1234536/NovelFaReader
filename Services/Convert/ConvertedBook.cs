using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EpubReader.Services
{

// بخش یک کتاب تبدیل‌شده: عنوان و فایل XHTML همون بخش
public sealed class ParsedChapter
{
	public string Title;
	public string File;
}

// نتیجه‌ی پارس یک فایل غیر EPUB: عنوان، بخش‌ها (فایل‌های ch0001.xhtml… توی یه پوشه) و جلد اختیاری
public sealed class ParsedBook
{
	public string Title = "";
	public List<ParsedChapter> Chapters = new List<ParsedChapter>();
	public string CoverFile;
}

// یک بخش خوانا: عنوان توی فهرست بخش‌ها و بدنه‌ی HTML
public sealed class ConvertedSection
{
	public string Title;
	public string BodyHtml;

	public ConvertedSection(string title, string bodyHtml)
	{
		Title = title;
		BodyHtml = bodyHtml;
	}
}

// شکل روی‌دیسکِ کتاب تبدیل‌شده (پورت ConvertedBook.kt اندروید): یه فایل XHTML برای هر بخش + فایل‌های جانبی (مثل media/)
public static class ConvertedBook
{
	public const int MaxSectionChars = 12000;

	public static ParsedBook Write(string outDir, string title, List<ConvertedSection> sections, string coverFile = null)
	{
		Directory.CreateDirectory(outDir);
		ParsedBook book = new ParsedBook();
		book.Title = title;
		book.CoverFile = coverFile;
		for (int i = 0; i < sections.Count; i++)
		{
			ConvertedSection s = sections[i];
			string file = Path.Combine(outDir, "ch" + (i + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".xhtml");
			File.WriteAllText(file, "<html><head><meta charset=\"utf-8\"><title>" + Escape(s.Title) + "</title></head><body>" + s.BodyHtml + "</body></html>", new UTF8Encoding(false));
			ParsedChapter ch = new ParsedChapter();
			ch.Title = s.Title;
			ch.File = file;
			book.Chapters.Add(ch);
		}
		return book;
	}

	private static readonly HashSet<string> VoidTags = new HashSet<string>(StringComparer.Ordinal)
	{
		"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"
	};

	private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.Ordinal)
	{
		"address", "article", "aside", "blockquote", "details", "div", "dl", "dt", "dd", "fieldset", "figcaption", "figure", "footer",
		"form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "li", "main", "nav", "ol", "pre", "section", "table", "ul"
	};

	private static readonly Regex ParaPattern = new Regex("<p(\\s[^>]*)?>([\\s\\S]*?)</p\\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	private static readonly Regex TokenPattern = new Regex("<[^>]*>|[^<]+", RegexOptions.CultureInvariant);
	private static readonly Regex TagNamePattern = new Regex("</?\\s*([A-Za-z][A-Za-z0-9-]*)", RegexOptions.CultureInvariant);
	private static readonly Regex TagStrip = new Regex("<[^>]*>", RegexOptions.CultureInvariant);

	private static int PlainTextLength(string html)
	{
		return TagStrip.Replace(html, "").Length;
	}

	public static string SplitLongParagraphs(string bodyHtml)
	{
		return ParaPattern.Replace(bodyHtml, (Match m) =>
		{
			string attrs = m.Groups[1].Value;
			string inner = m.Groups[2].Value;
			if (PlainTextLength(inner) <= MaxSectionChars)
			{
				return m.Value;
			}
			string split = SplitParagraph(attrs, inner);
			return split ?? m.Value;
		});
	}

	private static string SplitParagraph(string attrs, string inner)
	{
		List<string> tokens = new List<string>();
		foreach (Match t in TokenPattern.Matches(inner))
		{
			tokens.Add(t.Value);
		}
		foreach (string t in tokens)
		{
			if (!t.StartsWith("<", StringComparison.Ordinal))
			{
				continue;
			}
			Match nm = TagNamePattern.Match(t);
			if (nm.Success && BlockTags.Contains(nm.Groups[1].Value.ToLowerInvariant()))
			{
				return null;
			}
		}
		List<string> pieces = new List<string>();
		StringBuilder current = new StringBuilder();
		int currentText = 0;
		bool pieceHasContent = false;
		List<KeyValuePair<string, string>> stack = new List<KeyValuePair<string, string>>();

		Action closePiece = () =>
		{
			for (int k = stack.Count - 1; k >= 0; k--)
			{
				current.Append("</").Append(stack[k].Key).Append(">");
			}
			current.Append("</p>");
			pieces.Add(current.ToString());
			current.Clear();
			currentText = 0;
			pieceHasContent = false;
		};
		Action openPiece = () =>
		{
			current.Append("<p").Append(attrs).Append(">");
			foreach (KeyValuePair<string, string> e in stack)
			{
				current.Append(e.Value);
			}
		};

		openPiece();
		foreach (string t in tokens)
		{
			if (t.StartsWith("<", StringComparison.Ordinal))
			{
				Match nm = TagNamePattern.Match(t);
				if (!nm.Success)
				{
					current.Append(t);
					continue;
				}
				string name = nm.Groups[1].Value.ToLowerInvariant();
				bool isClose = t.StartsWith("</", StringComparison.Ordinal);
				bool isVoid = t.EndsWith("/>", StringComparison.Ordinal) || VoidTags.Contains(name);
				if (isClose)
				{
					while (stack.Count > 0)
					{
						string n = stack[stack.Count - 1].Key;
						stack.RemoveAt(stack.Count - 1);
						current.Append("</").Append(n).Append(">");
						if (n == name)
						{
							break;
						}
					}
				}
				else if (isVoid)
				{
					current.Append(t);
					pieceHasContent = true;
				}
				else
				{
					stack.Add(new KeyValuePair<string, string>(name, t));
					current.Append(t);
				}
				continue;
			}
			string rest = t;
			while (rest.Length > 0)
			{
				if (currentText >= MaxSectionChars)
				{
					closePiece();
					openPiece();
					continue;
				}
				int room = MaxSectionChars - currentText;
				if (rest.Length <= room)
				{
					current.Append(rest);
					currentText += rest.Length;
					pieceHasContent = true;
					rest = "";
					break;
				}
				int cut = rest.LastIndexOf(' ', Math.Min(room, rest.Length - 1));
				if (cut < 0)
				{
					cut = room;
				}
				cut = SafeCut(rest, cut);
				if (cut <= 0)
				{
					cut = Math.Min(room, rest.Length);
				}
				current.Append(rest.Substring(0, cut));
				closePiece();
				openPiece();
				rest = rest.Substring(cut);
			}
		}
		if (pieceHasContent)
		{
			for (int k = stack.Count - 1; k >= 0; k--)
			{
				current.Append("</").Append(stack[k].Key).Append(">");
			}
			current.Append("</p>");
			pieces.Add(current.ToString());
		}
		if (pieces.Count <= 1)
		{
			return null;
		}
		return string.Concat(pieces.ToArray());
	}

	private static int SafeCut(string text, int cut)
	{
		int c = Math.Max(0, Math.Min(text.Length, cut));
		if (c > 0 && c < text.Length && char.IsHighSurrogate(text[c - 1]) && char.IsLowSurrogate(text[c]))
		{
			c -= 1;
		}
		int amp = -1;
		int i = Math.Min(c - 1, text.Length - 1);
		while (i >= 0 && c - 1 - i < 10)
		{
			if (text[i] == ';')
			{
				break;
			}
			if (text[i] == '&')
			{
				amp = i;
				break;
			}
			i--;
		}
		if (amp >= 0)
		{
			c = amp;
		}
		return c;
	}

	public static List<ConvertedSection> ChunkSections(List<ConvertedSection> sections)
	{
		List<ConvertedSection> output = new List<ConvertedSection>();
		foreach (ConvertedSection section in sections)
		{
			if (section.BodyHtml.Length <= MaxSectionChars)
			{
				output.Add(section);
				continue;
			}
			string body = SplitLongParagraphs(section.BodyHtml);
			string[] paragraphs = Regex.Split(body, "(?=<p[ >])");
			StringBuilder current = new StringBuilder();
			int chunk = 1;
			Action flush = () =>
			{
				if (current.Length > 0)
				{
					string title = chunk == 1 ? section.Title : section.Title + " (" + chunk + ")";
					output.Add(new ConvertedSection(title, current.ToString()));
					current = new StringBuilder();
					chunk++;
				}
			};
			foreach (string p in paragraphs)
			{
				if (string.IsNullOrWhiteSpace(p))
				{
					continue;
				}
				if (current.Length > 0 && current.Length + p.Length > MaxSectionChars)
				{
					flush();
				}
				current.Append(p);
			}
			flush();
		}
		return output;
	}

	public static string Escape(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		StringBuilder sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			switch (c)
			{
				case '&': sb.Append("&amp;"); break;
				case '<': sb.Append("&lt;"); break;
				case '>': sb.Append("&gt;"); break;
				case '"': sb.Append("&quot;"); break;
				case '\'': sb.Append("&#39;"); break;
				default: sb.Append(c); break;
			}
		}
		return sb.ToString();
	}
}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace EpubReader.Services
{

// Text-layer PDF books, with no third-party dependency (port of PdfParser.kt).
// What works: classic and xref-stream cross-references, object streams, Flate-compressed streams, page trees, document outlines
// (-> chapters), document-info titles, simple fonts (WinAnsi / Differences) and CID fonts with ToUnicode maps. Encrypted PDFs using the
// Standard security handler (R2-R6; RC4 40/128, AESV2, AESV3) open with the empty user password or with a password from askPassword.
// Scanned/image-only PDFs have no text layer: they throw InvalidDataException("PDF_NO_TEXT"). Text is kept in stream order.
public static class PdfParser
{
	private const int MaxPages = 5000;
	private const int MaxInflatedBytes = 256 * 1024 * 1024;

	private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

	// ECMAScript mode: \s and \d are ASCII-only, like Java's. The Latin-1 view of a PDF has U+0085 / U+00A0 bytes that .NET's \s would treat as spaces.
	private const RegexOptions Rx = RegexOptions.ECMAScript;
	private static readonly Regex ObjHeader = new Regex("(\\d+)\\s+(\\d+)\\s+obj", Rx);
	private static readonly Regex ObjStart = new Regex("obj\\s", Rx);
	private static readonly Regex XrefEntry = new Regex("(\\d{10})\\s+(\\d{5})\\s+([nf])", Rx);
	private static readonly Regex DigitsRx = new Regex("\\d+", Rx);
	private static readonly Regex SplitWsRx = new Regex("\\s+", Rx);
	private static readonly Regex RefRx = new Regex("(\\d+)\\s+\\d+\\s+R", Rx);
	private static readonly Regex HexInAngle = new Regex("<([0-9A-Fa-f]+)>", Rx);
	private static readonly Regex LiteralInParen = new Regex("\\(([^)]*)\\)", Rx);
	private static readonly Regex FontRefRx = new Regex("/([^\\s\\[\\]<>(){}%/]+)\\s+(\\d+)\\s+\\d+\\s+R", Rx);
	private static readonly Regex DiffRx = new Regex("(\\d+)|/([^\\s\\[\\]/]+)", Rx);
	private static readonly Regex CodeSpaceBlock = new Regex("begincodespacerange([\\s\\S]*?)endcodespacerange", Rx);
	private static readonly Regex BfCharBlock = new Regex("beginbfchar([\\s\\S]*?)endbfchar", Rx);
	private static readonly Regex BfRangeBlock = new Regex("beginbfrange([\\s\\S]*?)endbfrange", Rx);
	private static readonly Regex HexPair = new Regex("<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]+)>", Rx);
	private static readonly Regex BfRangeEntry = new Regex("<([0-9A-Fa-f]+)>\\s*<([0-9A-Fa-f]+)>\\s*(<([0-9A-Fa-f]+)>|\\[([^\\]]*)\\])", Rx);
	private static readonly Regex NamedDestRx = new Regex("\\(([^)]*)\\)\\s*\\[(\\d+)\\s+\\d+\\s+R", Rx);
	private static readonly Regex FilterRx = new Regex("/Filter\\s*(\\[[^\\]]*\\]|/[^\\s/\\[\\]<>()%]+)", Rx);
	private static readonly Regex FilterNameRx = new Regex("/([A-Za-z0-9]+)", Rx);
	private static readonly Regex NegIntRx = new Regex("-?\\d+", Rx);
	private static readonly Regex EncryptMetaFalse = new Regex("/EncryptMetadata\\s+false", Rx);

	private sealed class OutlineEntry
	{
		public string Title;
		public int PageIndex;
		public int Order;
	}

	private sealed class Line
	{
		public string Text;
		public double Gap;

		public Line(string text, double gap)
		{
			Text = text;
			Gap = gap;
		}
	}

	private sealed class FontInfo
	{
		public Dictionary<int, string> ToUnicode;
		public int CodeBytes;
		public Dictionary<int, string> Differences;
		public string Encoding;
	}

	private sealed class CMap
	{
		public Dictionary<int, string> Map;
		public int CodeBytes;
	}

	private enum TokenKind
	{
		Str,
		Name,
		Number,
		Arr,
		Op
	}

	private sealed class Token
	{
		public TokenKind Kind;
		public byte[] Bytes;
		public string Value;
		public double Num;
		public List<Token> Items;
	}

	private enum CryptMethod
	{
		Identity,
		Rc4,
		Aes128,
		Aes256
	}

	// ------------------------------------------------------------------
	// Public entry points
	// ------------------------------------------------------------------

	// Parses a PDF file into chapters under outDir. askPassword(wrongBefore) returns a password, or null when the user cancels
	// (then OperationCanceledException is thrown). The empty password is always tried first without asking.
	// chapterFallback is the name used for untitled chapters: a format with {0} for the number, or a prefix ("Chapter" -> "Chapter 3").
	public static ParsedBook ParseFile(string pdfFile, string outDir, string chapterFallback, string titleFallback, Func<bool, string> askPassword)
	{
		Func<int, string> fallback = (int n) => FormatFallback(chapterFallback, n);
		return ParseFile(pdfFile, outDir, fallback, titleFallback, askPassword);
	}

	public static ParsedBook ParseFile(string pdfFile, string outDir, Func<int, string> chapterFallback, string titleFallback, Func<bool, string> askPassword)
	{
		byte[] bytes = File.ReadAllBytes(pdfFile);
		string fileName = !string.IsNullOrWhiteSpace(titleFallback) ? titleFallback : pdfFile;
		string password = null;
		while (true)
		{
			bool wrongBefore;
			try
			{
				return Parse(bytes, fileName, outDir, chapterFallback, password);
			}
			catch (PdfPasswordRequiredException)
			{
				wrongBefore = false;
			}
			catch (PdfPasswordWrongException)
			{
				wrongBefore = true;
			}
			password = askPassword != null ? askPassword(wrongBefore) : null;
			if (password == null)
			{
				throw new OperationCanceledException("PDF password was not entered");
			}
		}
	}

	private static string FormatFallback(string format, int n)
	{
		string num = n.ToString(CultureInfo.InvariantCulture);
		if (string.IsNullOrEmpty(format))
		{
			return num;
		}
		if (format.IndexOf("{0}", StringComparison.Ordinal) >= 0)
		{
			return format.Replace("{0}", num);
		}
		return format + " " + num;
	}

	// password: null means "try the empty password only". Throws PdfPasswordRequiredException / PdfPasswordWrongException /
	// PdfUnsupportedEncryptionException for encrypted files, InvalidDataException("PDF_NO_TEXT") when there is nothing to read.
	public static ParsedBook Parse(byte[] bytes, string fileName, string outDir, Func<int, string> chapterFallback, string password)
	{
		PdfDoc doc = new PdfDoc(bytes, password);
		List<int> pages = doc.PageObjectNumbers();
		if (pages.Count == 0)
		{
			throw new ArgumentException("PDF has no pages");
		}
		if (pages.Count > MaxPages)
		{
			throw new ArgumentException("PDF has too many pages");
		}
		List<List<string>> pageParagraphs = new List<List<string>>();
		foreach (int pageNum in pages)
		{
			pageParagraphs.Add(doc.PageParagraphs(pageNum));
		}
		List<OutlineEntry> outline = doc.OutlineEntries(pages);
		List<ConvertedSection> sections = new List<ConvertedSection>();
		if (outline.Count >= 2)
		{
			for (int idx = 0; idx < outline.Count; idx++)
			{
				OutlineEntry entry = outline[idx];
				int end = idx + 1 < outline.Count ? outline[idx + 1].PageIndex : pages.Count;
				StringBuilder body = new StringBuilder();
				for (int p = entry.PageIndex; p < end; p++)
				{
					foreach (string para in pageParagraphs[p])
					{
						body.Append("<p>").Append(ConvertedBook.Escape(para)).Append("</p>");
					}
				}
				string title = TrimWs(entry.Title);
				if (title.Length > 120)
				{
					int cut = char.IsHighSurrogate(title[119]) ? 119 : 120;
					title = title.Substring(0, cut);
				}
				if (IsBlank(title))
				{
					title = chapterFallback(idx + 1);
				}
				if (body.Length > 0)
				{
					sections.Add(new ConvertedSection(title, body.ToString()));
				}
			}
		}
		else
		{
			StringBuilder current = new StringBuilder();
			foreach (List<string> pageParas in pageParagraphs)
			{
				foreach (string para in pageParas)
				{
					string html = "<p>" + ConvertedBook.Escape(para) + "</p>";
					if (current.Length > 0 && current.Length + html.Length > ConvertedBook.MaxSectionChars)
					{
						sections.Add(new ConvertedSection(chapterFallback(sections.Count + 1), current.ToString()));
						current = new StringBuilder();
					}
					current.Append(html);
				}
			}
			if (current.Length > 0)
			{
				sections.Add(new ConvertedSection(chapterFallback(sections.Count + 1), current.ToString()));
			}
		}
		List<ConvertedSection> chunked = ConvertedBook.ChunkSections(sections);
		if (chunked.Count == 0)
		{
			throw new InvalidDataException("PDF_NO_TEXT");
		}
		string bookTitle = doc.InfoTitle();
		if (bookTitle == null || IsBlank(bookTitle))
		{
			bookTitle = TextBookParser.NameWithoutExtension(fileName);
		}
		return ConvertedBook.Write(outDir, bookTitle, chunked);
	}

	// ------------------------------------------------------------------
	// Small helpers: ordinal string search (the framework default is culture-sensitive), Java-like whitespace and number parsing.
	// ------------------------------------------------------------------

	private static int IdxOf(string s, string value, int start)
	{
		if (start < 0 || start > s.Length)
		{
			return -1;
		}
		return s.IndexOf(value, start, StringComparison.Ordinal);
	}

	private static int IdxOfChar(string s, char c, int start)
	{
		if (start < 0 || start >= s.Length)
		{
			return -1;
		}
		return s.IndexOf(c, start);
	}

	private static bool StartsAt(string s, string prefix, int index)
	{
		if (index < 0 || index + prefix.Length > s.Length)
		{
			return false;
		}
		return string.CompareOrdinal(s, index, prefix, 0, prefix.Length) == 0;
	}

	private static Match MatchFrom(Regex regex, string s, int start)
	{
		if (start < 0 || start > s.Length)
		{
			return Match.Empty;
		}
		return regex.Match(s, start);
	}

	// Java/Kotlin Char.isWhitespace: ASCII whitespace and separators, but not the no-break spaces or U+0085.
	private static bool IsWs(char c)
	{
		if (c == ' ' || c == '\t' || c == '\n' || c == '\u000B' || c == '\f' || c == '\r')
		{
			return true;
		}
		if (c < '\u001C')
		{
			return false;
		}
		if (c <= '\u001F')
		{
			return true;
		}
		if (c < '\u0100')
		{
			return false;
		}
		return char.IsWhiteSpace(c) && c != '\u2007' && c != '\u202F';
	}

	private static string TrimWs(string s)
	{
		int a = 0;
		int b = s.Length;
		while (a < b && IsWs(s[a]))
		{
			a++;
		}
		while (b > a && IsWs(s[b - 1]))
		{
			b--;
		}
		return s.Substring(a, b - a);
	}

	private static bool IsBlank(string s)
	{
		foreach (char c in s)
		{
			if (!IsWs(c))
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsAsciiDigit(char c)
	{
		return c >= '0' && c <= '9';
	}

	private static int? ParseIntOrNull(string s)
	{
		int v;
		if (s != null && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v))
		{
			return v;
		}
		return null;
	}

	private static double? ParseDoubleOrNull(string s)
	{
		double v;
		if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
		{
			return v;
		}
		return null;
	}

	private static int HexDigit(char c)
	{
		if (c >= '0' && c <= '9')
		{
			return c - '0';
		}
		if (c >= 'a' && c <= 'f')
		{
			return c - 'a' + 10;
		}
		if (c >= 'A' && c <= 'F')
		{
			return c - 'A' + 10;
		}
		return -1;
	}

	// Kotlin toIntOrNull(16): null for non-hex digits, empty input and Int overflow.
	private static int? ParseHex(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return null;
		}
		long v = 0;
		foreach (char c in s)
		{
			int d = HexDigit(c);
			if (d < 0)
			{
				return null;
			}
			v = v * 16 + d;
			if (v > int.MaxValue)
			{
				return null;
			}
		}
		return (int)v;
	}

	// Kotlin toIntOrNull(8).
	private static int? ParseOct(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return null;
		}
		long v = 0;
		foreach (char c in s)
		{
			if (c < '0' || c > '7')
			{
				return null;
			}
			v = v * 8 + (c - '0');
			if (v > int.MaxValue)
			{
				return null;
			}
		}
		return (int)v;
	}

	private static byte[] HexBytes(string hex)
	{
		StringBuilder clean = new StringBuilder();
		foreach (char c in hex)
		{
			if (char.IsLetterOrDigit(c))
			{
				clean.Append(c);
			}
		}
		string cs = clean.ToString();
		byte[] result = new byte[(cs.Length + 1) / 2];
		for (int i = 0; i < result.Length; i++)
		{
			int a = i * 2;
			int len = Math.Min(2, cs.Length - a);
			string pair = cs.Substring(a, len);
			if (len < 2)
			{
				pair = pair + "0";
			}
			int? v = ParseHex(pair);
			result[i] = (byte)(v ?? 0);
		}
		return result;
	}

	// Literal string at start ('(') -> its bytes and the end index; null when unbalanced.
	private static byte[] ParseLiteralBytes(string text, int start, out int end)
	{
		end = start;
		int i = start + 1;
		int depth = 1;
		MemoryStream result = new MemoryStream();
		while (i < text.Length && depth > 0)
		{
			char c = text[i];
			if (c == '\\' && i + 1 < text.Length)
			{
				char e = text[i + 1];
				switch (e)
				{
					case 'n':
						result.WriteByte((byte)'\n');
						i += 2;
						break;
					case 'r':
						result.WriteByte((byte)'\r');
						i += 2;
						break;
					case 't':
						result.WriteByte((byte)'\t');
						i += 2;
						break;
					case 'b':
						result.WriteByte((byte)8);
						i += 2;
						break;
					case 'f':
						result.WriteByte((byte)0x0C);
						i += 2;
						break;
					case '(':
					case ')':
					case '\\':
						result.WriteByte((byte)e);
						i += 2;
						break;
					case '\n':
						i += 2;
						break;
					case '\r':
						i += (i + 2 < text.Length && text[i + 2] == '\n') ? 3 : 2;
						break;
					default:
						if (IsAsciiDigit(e))
						{
							int j = i + 1;
							int n = 0;
							while (j < text.Length && j < i + 4 && IsAsciiDigit(text[j]))
							{
								n++;
								j++;
							}
							int? ov = ParseOct(text.Substring(i + 1, n));
							result.WriteByte((byte)(ov ?? (int)e));
							i = j;
						}
						else
						{
							result.WriteByte((byte)e);
							i += 2;
						}
						break;
				}
			}
			else if (c == '(')
			{
				depth++;
				result.WriteByte((byte)c);
				i++;
			}
			else if (c == ')')
			{
				depth--;
				if (depth > 0)
				{
					result.WriteByte((byte)c);
				}
				i++;
			}
			else
			{
				result.WriteByte((byte)(c & 0xFF));
				i++;
			}
		}
		if (depth != 0)
		{
			return null;
		}
		end = i;
		return result.ToArray();
	}

	// A PDF string: UTF-16 with a BOM (two separate bytes FE FF / FF FE), otherwise Latin-1.
	private static string DecodePdfString(byte[] data)
	{
		if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
		{
			return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
		}
		if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
		{
			return Encoding.Unicode.GetString(data, 2, data.Length - 2);
		}
		return Latin1.GetString(data);
	}

	private static string DecodeName(string name)
	{
		if (name.IndexOf('#') < 0)
		{
			return name;
		}
		StringBuilder sb = new StringBuilder();
		int i = 0;
		while (i < name.Length)
		{
			if (name[i] == '#' && i + 2 < name.Length)
			{
				int? v = ParseHex(name.Substring(i + 1, 2));
				if (v.HasValue)
				{
					sb.Append((char)v.Value);
					i += 3;
					continue;
				}
			}
			sb.Append(name[i]);
			i++;
		}
		return sb.ToString();
	}

	// ------------------------------------------------------------------
	// Dict helpers (regex-based, on the Latin-1 text of an object).
	// ------------------------------------------------------------------

	private static int? RefIn(string dict, string key)
	{
		Match m = Regex.Match(dict, "/" + key + "\\s+(\\d+)\\s+\\d+\\s+R", Rx);
		return m.Success ? ParseIntOrNull(m.Groups[1].Value) : null;
	}

	private static string NameIn(string dict, string key)
	{
		Match m = Regex.Match(dict, "/" + key + "\\s*/([^\\s\\[\\]<>(){}%/]+)", Rx);
		if (!m.Success)
		{
			return null;
		}
		return DecodeName(m.Groups[1].Value);
	}

	private static int? IntValue(string dict, string key)
	{
		Match m = Regex.Match(dict, "/" + key + "\\s+(-?\\d+)", Rx);
		return m.Success ? ParseIntOrNull(m.Groups[1].Value) : null;
	}

	private static int[] ArrayInts(string dict, string key)
	{
		Match seg = Regex.Match(dict, "/" + key + "\\s*\\[([^\\]]*)\\]", Rx);
		List<int> result = new List<int>();
		if (!seg.Success)
		{
			return result.ToArray();
		}
		foreach (Match m in NegIntRx.Matches(seg.Groups[1].Value))
		{
			int? v = ParseIntOrNull(m.Value);
			if (v.HasValue)
			{
				result.Add(v.Value);
			}
		}
		return result.ToArray();
	}

	private static List<int> RefsInText(string text)
	{
		List<int> result = new List<int>();
		foreach (Match m in RefRx.Matches(text))
		{
			int? v = ParseIntOrNull(m.Groups[1].Value);
			if (v.HasValue)
			{
				result.Add(v.Value);
			}
		}
		return result;
	}

	private static List<int> ArrayRefs(string dict, string key)
	{
		Match seg = Regex.Match(dict, "/" + key + "\\s*\\[([^\\]]*)\\]", Rx);
		if (!seg.Success)
		{
			return new List<int>();
		}
		return RefsInText(seg.Groups[1].Value);
	}

	// The dict starting at start, balanced over << >>.
	private static string BalancedDict(string src, int start)
	{
		if (start < 0 || start + 1 >= src.Length)
		{
			return null;
		}
		if (!StartsAt(src, "<<", start))
		{
			return null;
		}
		int depth = 0;
		int i = start;
		while (i + 1 < src.Length)
		{
			if (StartsAt(src, "<<", i))
			{
				depth++;
				i += 2;
			}
			else if (StartsAt(src, ">>", i))
			{
				depth--;
				i += 2;
				if (depth == 0)
				{
					return src.Substring(start, i - start);
				}
			}
			else if (src[i] == '<')
			{
				// Hex string: skip to its '>' so a closing '>' is never mistaken for half of '>>'.
				int close = IdxOfChar(src, '>', i);
				i = close < 0 ? src.Length : close + 1;
			}
			else if (src[i] == '(')
			{
				// Skip a literal string (nested, escaped).
				int d = 1;
				i++;
				while (i < src.Length && d > 0)
				{
					if (src[i] == '\\')
					{
						i += 2;
					}
					else if (src[i] == '(')
					{
						d++;
						i++;
					}
					else if (src[i] == ')')
					{
						d--;
						i++;
					}
					else
					{
						i++;
					}
				}
			}
			else
			{
				i++;
			}
		}
		return null;
	}

	private static string DictSegment(string dict, string key)
	{
		Match idx = Regex.Match(dict, "/" + key + "\\s*<<", Rx);
		if (!idx.Success)
		{
			return null;
		}
		int start = IdxOf(dict, "<<", idx.Index);
		return BalancedDict(dict, start);
	}

	private static string ArraySegment(string dict, string key)
	{
		Match idx = Regex.Match(dict, "/" + key + "\\s*\\[", Rx);
		if (!idx.Success)
		{
			return null;
		}
		int start = IdxOfChar(dict, '[', idx.Index);
		int depth = 0;
		int i = start;
		while (i >= 0 && i < dict.Length)
		{
			if (dict[i] == '[')
			{
				depth++;
			}
			else if (dict[i] == ']')
			{
				depth--;
				if (depth == 0)
				{
					return dict.Substring(start, i + 1 - start);
				}
			}
			i++;
		}
		return null;
	}

	// The dict with everything inside nested << >> removed, so a top-level key is never confused with a same-named key of a sub-dict
	// (e.g. /Length inside /CF in an Encrypt dict).
	private static string StripNested(string dict)
	{
		StringBuilder sb = new StringBuilder();
		int depth = 0;
		int i = 0;
		while (i < dict.Length)
		{
			if (StartsAt(dict, "<<", i))
			{
				depth++;
				if (depth == 1)
				{
					sb.Append("<<");
				}
				i += 2;
			}
			else if (StartsAt(dict, ">>", i))
			{
				if (depth == 1)
				{
					sb.Append(">>");
				}
				depth--;
				i += 2;
			}
			else if (dict[i] == '<')
			{
				int close = IdxOfChar(dict, '>', i);
				int stop = close < 0 ? dict.Length : close + 1;
				if (depth <= 1)
				{
					sb.Append(dict, i, stop - i);
				}
				i = stop;
			}
			else if (depth == 1 && dict[i] == '(')
			{
				int end;
				byte[] lit = ParseLiteralBytes(dict, i, out end);
				if (lit == null)
				{
					sb.Append(dict[i]);
					i++;
				}
				else
				{
					sb.Append(dict, i, end - i);
					i = end;
				}
			}
			else
			{
				if (depth <= 1)
				{
					sb.Append(dict[i]);
				}
				i++;
			}
		}
		return sb.ToString();
	}

	// Raw bytes of the string value after /Key (no text decoding). The key must be followed directly by a ( or < string.
	private static byte[] StringBytesAfterKey(string dict, string key)
	{
		Match m = Regex.Match(dict, "/" + key + "\\s*[(<]", Rx);
		if (!m.Success)
		{
			return null;
		}
		int i = m.Index + m.Length - 1;
		if (dict[i] == '(')
		{
			int end;
			return ParseLiteralBytes(dict, i, out end);
		}
		if (StartsAt(dict, "<<", i))
		{
			return null;
		}
		int close = IdxOfChar(dict, '>', i);
		return close < 0 ? null : HexBytes(dict.Substring(i + 1, close - i - 1));
	}

	// PNG predictors (10-15) as used with Flate xref/object streams.
	private static byte[] PngUnpredict(byte[] data, int columns, int colors, int bpc)
	{
		int rowBytes = (colors * bpc * columns + 7) / 8;
		int bpp = Math.Max(1, (colors * bpc + 7) / 8);
		if (rowBytes <= 0)
		{
			return data;
		}
		int rows = data.Length / (rowBytes + 1);
		byte[] result = new byte[rows * rowBytes];
		for (int r = 0; r < rows; r++)
		{
			int src = r * (rowBytes + 1);
			int dst = r * rowBytes;
			int ft = data[src];
			for (int x = 0; x < rowBytes; x++)
			{
				int raw = data[src + 1 + x];
				int left = x >= bpp ? result[dst + x - bpp] : 0;
				int up = r > 0 ? result[dst - rowBytes + x] : 0;
				int upLeft = (r > 0 && x >= bpp) ? result[dst - rowBytes + x - bpp] : 0;
				int v;
				switch (ft)
				{
					case 1:
						v = raw + left;
						break;
					case 2:
						v = raw + up;
						break;
					case 3:
						v = raw + ((left + up) / 2);
						break;
					case 4:
					{
						int p = left + up - upLeft;
						int pa = Math.Abs(p - left);
						int pb = Math.Abs(p - up);
						int pc = Math.Abs(p - upLeft);
						int pred = (pa <= pb && pa <= pc) ? left : (pb <= pc ? up : upLeft);
						v = raw + pred;
						break;
					}
					default:
						v = raw;
						break;
				}
				result[dst + x] = (byte)(v & 0xFF);
			}
		}
		return result;
	}

	// zlib streams: skip the 2-byte header (plus the 4-byte dictionary id when FDICT is set) and inflate the raw deflate data by hand;
	// when the header is not a valid zlib header the data is tried as raw deflate. Partial output of a damaged stream is kept.
	private static byte[] Inflate(byte[] data)
	{
		if (data.Length < 2)
		{
			return null;
		}
		bool zlibHeader = (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
		byte[] result;
		if (zlibHeader)
		{
			int start = (data[1] & 0x20) != 0 ? 6 : 2;
			result = InflateRaw(data, start);
			if (result != null)
			{
				return result;
			}
			return InflateRaw(data, 0);
		}
		result = InflateRaw(data, 0);
		if (result != null)
		{
			return result;
		}
		return InflateRaw(data, 2);
	}

	private static byte[] InflateRaw(byte[] data, int start)
	{
		if (start >= data.Length)
		{
			return null;
		}
		MemoryStream result = new MemoryStream();
		try
		{
			using (MemoryStream input = new MemoryStream(data, start, data.Length - start))
			using (DeflateStream ds = new DeflateStream(input, CompressionMode.Decompress))
			{
				byte[] buf = new byte[8192];
				int n;
				while ((n = ds.Read(buf, 0, buf.Length)) > 0)
				{
					result.Write(buf, 0, n);
					if (result.Length > MaxInflatedBytes)
					{
						break;
					}
				}
			}
		}
		catch (Exception)
		{
			// Damaged stream: keep what was decoded so far.
		}
		return result.Length > 0 ? result.ToArray() : null;
	}

	// ------------------------------------------------------------------
	// Document model: xref, objects, streams, pages, outlines, fonts and text.
	// ------------------------------------------------------------------

	private sealed class PdfDoc
	{
		private readonly byte[] bytes;
		private readonly string password;

		// Latin-1 view: char index == byte index, so offsets stay valid.
		private readonly string text;

		private readonly Dictionary<int, int> offsets = new Dictionary<int, int>();
		private readonly Dictionary<int, int> genMap = new Dictionary<int, int>();
		private readonly Dictionary<int, int[]> objStm = new Dictionary<int, int[]>();
		private readonly Dictionary<int, Dictionary<int, string>> objStmCache = new Dictionary<int, Dictionary<int, string>>();
		private readonly Dictionary<int, string> rawCache = new Dictionary<int, string>();
		private readonly Dictionary<int, byte[]> streamCache = new Dictionary<int, byte[]>();
		private readonly Dictionary<int, FontInfo> fontCache = new Dictionary<int, FontInfo>();
		private string trailerDict;

		// Null for unencrypted files; set once the /Encrypt dict is seen.
		private SecurityHandler security;

		public PdfDoc(byte[] bytes, string password)
		{
			this.bytes = bytes;
			this.password = password;
			text = Latin1.GetString(bytes);
			BuildXref();
		}

		// -- xref -------------------------------------------------------

		private int SkipWs(int pos)
		{
			int i = pos;
			while (i < text.Length && IsWs(text[i]))
			{
				i++;
			}
			return i;
		}

		private void BuildXref()
		{
			int? offset = LastStartxref();
			int guard = 0;
			while (offset.HasValue && guard++ < 25)
			{
				int at = SkipWs(offset.Value);
				if (StartsAt(text, "xref", at))
				{
					// Classic table, parsed line by line: a subsection header "first count" followed by count 20-byte entries,
					// then the next subsection or "trailer".
					int pos = at + 4;
					string trailer = null;
					while (pos < text.Length)
					{
						pos = SkipWs(pos);
						if (StartsAt(text, "trailer", pos))
						{
							trailer = BalancedDict(text, IdxOf(text, "<<", pos));
							break;
						}
						int lineEnd = IdxOfChar(text, '\n', pos);
						if (lineEnd < 0)
						{
							lineEnd = text.Length;
						}
						string[] header = SplitWsRx.Split(TrimWs(text.Substring(pos, lineEnd - pos)));
						int? start = header.Length > 0 ? ParseIntOrNull(header[0]) : null;
						int? count = header.Length > 1 ? ParseIntOrNull(header[1]) : null;
						if (!start.HasValue || !count.HasValue)
						{
							break;
						}
						pos = lineEnd + 1;
						for (int k = 0; k < count.Value; k++)
						{
							if (pos >= text.Length)
							{
								break;
							}
							int entryEnd = IdxOfChar(text, '\n', pos);
							if (entryEnd < 0)
							{
								entryEnd = text.Length;
							}
							Match entry = XrefEntry.Match(text.Substring(pos, entryEnd - pos));
							if (entry.Success && entry.Groups[3].Value == "n")
							{
								int num = start.Value + k;
								int? entryOffset = ParseIntOrNull(entry.Groups[1].Value);
								if (entryOffset.HasValue && !offsets.ContainsKey(num))
								{
									offsets[num] = entryOffset.Value;
									genMap[num] = ParseIntOrNull(entry.Groups[2].Value) ?? 0;
								}
							}
							pos = entryEnd + 1;
						}
					}
					if (trailerDict == null)
					{
						trailerDict = trailer;
					}
					if (trailer != null)
					{
						EnsureSecurity(trailer);
					}
					offset = trailer != null ? IntValue(trailer, "Prev") : null;
				}
				else
				{
					// An xref-stream object: its dict IS the trailer. The header must start exactly at `at`
					// (a later "N G obj" match inside stream bytes would be a different object entirely).
					Match header = MatchFrom(ObjHeader, text, at);
					if (!header.Success || header.Index != at)
					{
						break;
					}
					int? objNum = ParseIntOrNull(header.Groups[1].Value);
					if (!objNum.HasValue)
					{
						break;
					}
					// Register the stream object's own offset first, or DictOf/StreamOf below cannot locate it at all.
					if (!offsets.ContainsKey(objNum.Value))
					{
						offsets[objNum.Value] = header.Index;
					}
					string dict = DictOf(objNum.Value);
					if (dict == null)
					{
						break;
					}
					if (trailerDict == null)
					{
						trailerDict = dict;
					}
					// The dict (with /Encrypt and /ID) is plaintext, so security can be built BEFORE the stream bytes are read.
					EnsureSecurity(dict);
					ParseXrefStream(objNum.Value, dict);
					offset = IntValue(dict, "Prev");
				}
			}
			if (offsets.Count == 0 && objStm.Count == 0)
			{
				// Last resort: linear scan for "N G obj" occurrences.
				foreach (Match m in ObjHeader.Matches(text))
				{
					int? num = ParseIntOrNull(m.Groups[1].Value);
					if (num.HasValue && !offsets.ContainsKey(num.Value))
					{
						offsets[num.Value] = m.Index;
						genMap[num.Value] = ParseIntOrNull(m.Groups[2].Value) ?? 0;
					}
				}
			}
			// A trailer without /Encrypt (or a file whose trailers were all unreadable) simply stays unencrypted.
			if (trailerDict != null)
			{
				EnsureSecurity(trailerDict);
			}
		}

		// Builds the security handler from a trailer dict the first time an /Encrypt entry is seen. The encrypt dict is a regular object:
		// in classic files it is already in offsets; in xref-stream files the stream is not parsed yet, so it is found by a linear scan.
		private void EnsureSecurity(string trailer)
		{
			if (security != null)
			{
				return;
			}
			string encDict = DictSegment(trailer, "Encrypt");
			if (encDict == null)
			{
				int? encNum = RefIn(trailer, "Encrypt");
				if (!encNum.HasValue)
				{
					return;
				}
				encDict = DictOf(encNum.Value) ?? LinearScanDict(encNum.Value);
				if (encDict == null)
				{
					throw new PdfUnsupportedEncryptionException("Encrypt dict object " + encNum.Value.ToString(CultureInfo.InvariantCulture) + " not found");
				}
			}
			security = new SecurityHandler(this, encDict, IdFromTrailer(trailer), password);
		}

		private string LinearScanDict(int num)
		{
			Regex rx = new Regex("(?:^|[\\r\\n])" + num.ToString(CultureInfo.InvariantCulture) + "\\s+(\\d+)\\s+obj", Rx | RegexOptions.Multiline);
			Match header = rx.Match(text);
			if (!header.Success)
			{
				return null;
			}
			int headerStart = header.Index;
			if (header.Value.Length > 0 && (header.Value[0] == '\r' || header.Value[0] == '\n'))
			{
				headerStart++;
			}
			if (!offsets.ContainsKey(num))
			{
				offsets[num] = headerStart;
				genMap[num] = ParseIntOrNull(header.Groups[1].Value) ?? 0;
			}
			int rawStart = IdxOf(text, "<<", header.Index + header.Length);
			if (rawStart < 0)
			{
				return null;
			}
			return BalancedDict(text, rawStart);
		}

		private byte[] IdFromTrailer(string trailer)
		{
			string seg = ArraySegment(trailer, "ID");
			if (seg == null)
			{
				return null;
			}
			Match hex = HexInAngle.Match(seg);
			if (hex.Success)
			{
				return HexBytes(hex.Groups[1].Value);
			}
			Match lit = LiteralInParen.Match(seg);
			if (lit.Success)
			{
				return Latin1.GetBytes(lit.Groups[1].Value);
			}
			return null;
		}

		private int? LastStartxref()
		{
			string tail = text.Length > 4096 ? text.Substring(text.Length - 4096) : text;
			int idx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
			if (idx < 0)
			{
				return null;
			}
			// The number after startxref IS the absolute xref offset.
			Match m = MatchFrom(DigitsRx, tail, idx + 9);
			return m.Success ? ParseIntOrNull(m.Value) : null;
		}

		private static int ReadField(byte[] data, ref int pos, int size, int defaultValue)
		{
			if (size == 0)
			{
				return defaultValue;
			}
			int v = 0;
			for (int n = 0; n < size; n++)
			{
				if (pos < data.Length)
				{
					v = (v << 8) | (data[pos] & 0xFF);
					pos++;
				}
			}
			return v;
		}

		private void ParseXrefStream(int objNum, string dict)
		{
			int[] w = ArrayInts(dict, "W");
			if (w.Length < 3)
			{
				return;
			}
			int[] index = ArrayInts(dict, "Index");
			List<int[]> sections = new List<int[]>();
			if (index.Length >= 2)
			{
				for (int i = 0; i + 1 < index.Length; i += 2)
				{
					sections.Add(new int[] { index[i], index[i + 1] });
				}
			}
			else
			{
				sections.Add(new int[] { 0, IntValue(dict, "Size") ?? 0 });
			}
			byte[] data = StreamOf(objNum);
			if (data == null)
			{
				return;
			}
			int pos = 0;
			foreach (int[] section in sections)
			{
				int first = section[0];
				int count = section[1];
				for (int k = 0; k < count; k++)
				{
					if (pos >= data.Length)
					{
						break;
					}
					int type = ReadField(data, ref pos, w[0], 1);
					int f2 = ReadField(data, ref pos, w[1], 0);
					int f3 = ReadField(data, ref pos, w[2], 0);
					int num = first + k;
					if (type == 1)
					{
						if (!offsets.ContainsKey(num))
						{
							offsets[num] = f2;
							genMap[num] = f3;
						}
					}
					else if (type == 2)
					{
						if (!objStm.ContainsKey(num))
						{
							objStm[num] = new int[] { f2, f3 };
						}
					}
				}
			}
		}

		// -- raw objects and streams ------------------------------------

		public string RawObject(int num)
		{
			string cached;
			if (rawCache.TryGetValue(num, out cached))
			{
				return cached;
			}
			string result = null;
			int off;
			if (offsets.TryGetValue(num, out off))
			{
				Match header = MatchFrom(ObjHeader, text, off);
				if (header.Success && ParseIntOrNull(header.Groups[1].Value) == num && !genMap.ContainsKey(num))
				{
					genMap[num] = ParseIntOrNull(header.Groups[2].Value) ?? 0;
				}
				Match contentMatch = MatchFrom(ObjStart, text, off);
				if (contentMatch.Success)
				{
					int contentStart = contentMatch.Index + contentMatch.Length;
					int end = IdxOf(text, "endobj", contentStart);
					if (end >= 0)
					{
						result = text.Substring(contentStart, end - contentStart);
					}
				}
			}
			if (result == null)
			{
				int[] entry;
				if (objStm.TryGetValue(num, out entry) && entry[0] != num)
				{
					// The object-stream table is keyed by object number (the Kotlin source looked members up by their index in the stream).
					Dictionary<int, string> members = ObjStmObjects(entry[0]);
					string body;
					if (members.TryGetValue(num, out body))
					{
						result = body;
					}
				}
			}
			if (result != null)
			{
				rawCache[num] = result;
			}
			return result;
		}

		public Dictionary<int, string> ObjStmObjects(int stmNum)
		{
			Dictionary<int, string> cached;
			if (objStmCache.TryGetValue(stmNum, out cached))
			{
				return cached;
			}
			Dictionary<int, string> result = new Dictionary<int, string>();
			objStmCache[stmNum] = result;
			string dict = DictOf(stmNum);
			byte[] data = StreamOf(stmNum);
			if (dict != null && data != null)
			{
				int first = IntValue(dict, "First") ?? 0;
				int n = IntValue(dict, "N") ?? 0;
				int headLen = Math.Max(0, Math.Min(first, data.Length));
				string header = Latin1.GetString(data, 0, headLen);
				string body = Latin1.GetString(data, headLen, data.Length - headLen);
				List<int[]> pairs = new List<int[]>();
				foreach (Match m in Regex.Matches(header, "(\\d+)\\s+(\\d+)", Rx))
				{
					if (pairs.Count >= n)
					{
						break;
					}
					int? a = ParseIntOrNull(m.Groups[1].Value);
					int? b = ParseIntOrNull(m.Groups[2].Value);
					if (a.HasValue && b.HasValue)
					{
						pairs.Add(new int[] { a.Value, b.Value });
					}
				}
				for (int i = 0; i < pairs.Count; i++)
				{
					int off = pairs[i][1];
					int end = i + 1 < pairs.Count ? pairs[i + 1][1] : body.Length;
					if (off >= 0 && off <= body.Length && end >= off && end <= body.Length)
					{
						result[pairs[i][0]] = body.Substring(off, end - off);
					}
				}
			}
			return result;
		}

		public string DictOf(int num)
		{
			string raw = RawObject(num);
			if (raw == null)
			{
				return null;
			}
			int start = IdxOf(raw, "<<", 0);
			if (start < 0)
			{
				return null;
			}
			return BalancedDict(raw, start);
		}

		public byte[] StreamOf(int num)
		{
			byte[] cached;
			if (streamCache.TryGetValue(num, out cached))
			{
				return cached;
			}
			byte[] result = ComputeStream(num);
			streamCache[num] = result;
			return result;
		}

		private byte[] ComputeStream(int num)
		{
			string dict;
			byte[] stored;
			if (!RawStream(num, out dict, out stored))
			{
				return null;
			}
			// Encryption is applied AFTER filters on write, so it comes off FIRST on read; then the filters decode as usual.
			// Cross-reference streams are never encrypted, nor is the metadata stream when /EncryptMetadata is false.
			byte[] plain = stored;
			if (security != null)
			{
				string type = NameIn(dict, "Type");
				bool plainStream = type == "XRef" || (type == "Metadata" && !security.EncryptMetadata);
				if (!plainStream)
				{
					plain = security.DecryptStream(stored, num);
				}
			}
			return DecodeFilters(dict, plain);
		}

		// A stream's dict and its STORED bytes (still encrypted when the file is encrypted).
		private bool RawStream(int num, out string dictOut, out byte[] bytesOut)
		{
			dictOut = null;
			bytesOut = null;
			string raw = RawObject(num);
			if (raw == null)
			{
				return false;
			}
			int dictStart = IdxOf(raw, "<<", 0);
			string dict = BalancedDict(raw, dictStart);
			if (dict == null)
			{
				return false;
			}
			// The stream keyword AFTER the dict: a dict value could itself contain the word "stream".
			int streamIdx = IdxOf(raw, "stream", dictStart + dict.Length);
			if (streamIdx < 0)
			{
				return false;
			}
			int dataStart = streamIdx + 6;
			if (StartsAt(raw, "\r\n", dataStart))
			{
				dataStart += 2;
			}
			else if (dataStart < raw.Length && (raw[dataStart] == '\n' || raw[dataStart] == '\r'))
			{
				dataStart++;
			}
			// Byte offsets in the Latin-1 view equal raw-string indices, but the stream bytes live in the ORIGINAL byte array:
			// locate them via the object's file offset instead.
			int fileOff;
			if (!offsets.TryGetValue(num, out fileOff))
			{
				return false;
			}
			Match objText = MatchFrom(ObjStart, text, fileOff);
			if (!objText.Success)
			{
				return false;
			}
			int objTextStart = objText.Index + objText.Length;
			int streamFileIdx = objTextStart + dataStart;
			// endstream preceded by an end-of-line (a compressed stream can contain the bare bytes "endstream" by coincidence).
			int endMarker = -1;
			int searchFrom = streamFileIdx;
			while (true)
			{
				int found = IdxOf(text, "endstream", searchFrom);
				if (found < 0)
				{
					break;
				}
				if (found > streamFileIdx && (text[found - 1] == '\n' || text[found - 1] == '\r'))
				{
					endMarker = found;
					break;
				}
				searchFrom = found + 1;
			}
			if (endMarker < 0)
			{
				return false;
			}
			int dataEnd = endMarker;
			while (dataEnd > streamFileIdx && (text[dataEnd - 1] == '\n' || text[dataEnd - 1] == '\r'))
			{
				dataEnd--;
			}
			// /Length may itself be an indirect reference (checked first: IntValue would read the object number of "12 0 R").
			int? length;
			int? lengthRef = RefIn(dict, "Length");
			if (lengthRef.HasValue)
			{
				string lenRaw = RawObject(lengthRef.Value);
				length = lenRaw != null ? ParseIntOrNull(TrimWs(lenRaw)) : null;
			}
			else
			{
				length = IntValue(dict, "Length");
			}
			if (length.HasValue && length.Value > 0 && (long)streamFileIdx + length.Value <= bytes.Length)
			{
				dataEnd = streamFileIdx + length.Value;
			}
			if (dataEnd <= streamFileIdx)
			{
				return false;
			}
			int realEnd = Math.Min(dataEnd, bytes.Length);
			byte[] rawBytes = new byte[realEnd - streamFileIdx];
			Buffer.BlockCopy(bytes, streamFileIdx, rawBytes, 0, rawBytes.Length);
			dictOut = dict;
			bytesOut = rawBytes;
			return true;
		}

		private static byte[] DecodeFilters(string dict, byte[] data)
		{
			Match fm = FilterRx.Match(dict);
			if (!fm.Success)
			{
				return data;
			}
			List<string> filters = new List<string>();
			foreach (Match m in FilterNameRx.Matches(fm.Groups[1].Value))
			{
				filters.Add(m.Groups[1].Value);
			}
			byte[] current = data;
			foreach (string filter in filters)
			{
				if (filter == "FlateDecode" || filter == "Fl")
				{
					current = Inflate(current);
					if (current == null)
					{
						return null;
					}
				}
				else
				{
					// ASCII85 / LZW / DCT are image or exotic paths: a failed decode drops the stream rather than failing the whole book.
					return null;
				}
			}
			if (filters.Count == 1)
			{
				int predictor = IntValue(dict, "Predictor") ?? 1;
				if (predictor >= 10)
				{
					int columns = IntValue(dict, "Columns") ?? 1;
					int colors = IntValue(dict, "Colors") ?? 1;
					int bpc = IntValue(dict, "BitsPerComponent") ?? 8;
					if (columns > 0 && colors > 0 && bpc > 0)
					{
						current = PngUnpredict(current, columns, colors, bpc);
					}
				}
			}
			return current;
		}

		// -- strings and names --------------------------------------------

		// PDF string value after /Key: literal (...) or hex <...>. objNum decrypts the value when the file is encrypted
		// (the string lives in that object); object-stream members are already plaintext and must not be decrypted again.
		private string StringAfterKey(string raw, string key, int? objNum)
		{
			Match m = Regex.Match(raw, "/" + key + "\\s*[(<]", Rx);
			if (!m.Success)
			{
				return null;
			}
			int i = m.Index + m.Length - 1;
			byte[] data;
			if (raw[i] == '(')
			{
				int end;
				data = ParseLiteralBytes(raw, i, out end);
				if (data == null)
				{
					return null;
				}
			}
			else
			{
				if (StartsAt(raw, "<<", i))
				{
					return null;
				}
				int close = IdxOfChar(raw, '>', i);
				if (close < 0)
				{
					return null;
				}
				data = HexBytes(raw.Substring(i + 1, close - i - 1));
			}
			return DecodePdfString(DecryptStringOf(data, objNum));
		}

		private byte[] DecryptStringOf(byte[] data, int? objNum)
		{
			if (security != null && objNum.HasValue && !objStm.ContainsKey(objNum.Value))
			{
				return security.DecryptString(data, objNum.Value);
			}
			return data;
		}

		// -- pages, outlines, info ---------------------------------------

		public List<int> PageObjectNumbers()
		{
			List<int> result = new List<int>();
			if (trailerDict == null)
			{
				return result;
			}
			int? rootNum = RefIn(trailerDict, "Root");
			if (!rootNum.HasValue)
			{
				return result;
			}
			string catalog = DictOf(rootNum.Value);
			if (catalog == null)
			{
				return result;
			}
			int? pagesNum = RefIn(catalog, "Pages");
			if (!pagesNum.HasValue)
			{
				return result;
			}
			HashSet<int> visited = new HashSet<int>();
			WalkPages(pagesNum.Value, result, visited);
			return result;
		}

		private void WalkPages(int num, List<int> result, HashSet<int> visited)
		{
			if (visited.Contains(num) || result.Count > MaxPages)
			{
				return;
			}
			visited.Add(num);
			string dict = DictOf(num);
			if (dict == null)
			{
				return;
			}
			string type = NameIn(dict, "Type");
			if (type == "Page" || RefIn(dict, "Contents").HasValue)
			{
				result.Add(num);
				return;
			}
			foreach (int kid in ArrayRefs(dict, "Kids"))
			{
				WalkPages(kid, result, visited);
			}
		}

		public string InfoTitle()
		{
			if (trailerDict == null)
			{
				return null;
			}
			int? infoNum = RefIn(trailerDict, "Info");
			if (!infoNum.HasValue)
			{
				return null;
			}
			string dict = DictOf(infoNum.Value);
			if (dict == null)
			{
				return null;
			}
			string raw = RawObject(infoNum.Value);
			if (raw == null)
			{
				return null;
			}
			return StringAfterKey(raw, "Title", infoNum.Value);
		}

		public List<OutlineEntry> OutlineEntries(List<int> pages)
		{
			List<OutlineEntry> empty = new List<OutlineEntry>();
			if (trailerDict == null)
			{
				return empty;
			}
			int? rootNum = RefIn(trailerDict, "Root");
			if (!rootNum.HasValue)
			{
				return empty;
			}
			string catalog = DictOf(rootNum.Value);
			if (catalog == null)
			{
				return empty;
			}
			int? outlinesNum = RefIn(catalog, "Outlines");
			if (!outlinesNum.HasValue)
			{
				return empty;
			}
			string outlines = DictOf(outlinesNum.Value);
			if (outlines == null)
			{
				return empty;
			}
			int? firstRef = RefIn(outlines, "First");
			if (!firstRef.HasValue)
			{
				return empty;
			}
			int current = firstRef.Value;
			Dictionary<int, int> pageIndexByNum = new Dictionary<int, int>();
			for (int i = 0; i < pages.Count; i++)
			{
				pageIndexByNum[pages[i]] = i;
			}
			Dictionary<string, int> destByName = NamedDestinations(catalog);
			List<OutlineEntry> found = new List<OutlineEntry>();
			int guard = 0;
			while (guard++ < 10000)
			{
				string dict = DictOf(current);
				if (dict == null)
				{
					break;
				}
				string raw = RawObject(current);
				if (raw == null)
				{
					break;
				}
				string title = StringAfterKey(raw, "Title", current);
				int? pageNum = null;
				string destSeg = ArraySegment(dict, "Dest");
				if (destSeg != null)
				{
					Match rm = RefRx.Match(destSeg);
					if (rm.Success)
					{
						pageNum = ParseIntOrNull(rm.Groups[1].Value);
					}
				}
				if (!pageNum.HasValue)
				{
					// /A << /D [page ...] >> (action destination).
					string action = DictSegment(dict, "A");
					if (action != null)
					{
						string dSeg = ArraySegment(action, "D");
						if (dSeg != null)
						{
							Match rm = RefRx.Match(dSeg);
							if (rm.Success)
							{
								pageNum = ParseIntOrNull(rm.Groups[1].Value);
							}
						}
					}
				}
				if (!pageNum.HasValue)
				{
					// Named destination: /Dest (name), decrypted with this object's key when the file is encrypted.
					Match nm = Regex.Match(dict, "/Dest\\s*\\(([^)]*)\\)", Rx);
					if (nm.Success)
					{
						string nameRaw = nm.Groups[1].Value;
						int litEnd;
						byte[] nameBytes = ParseLiteralBytes("(" + nameRaw + ")", 0, out litEnd) ?? Latin1.GetBytes(nameRaw);
						byte[] plain = DecryptStringOf(nameBytes, current);
						int destPage;
						if (destByName.TryGetValue(DecodePdfString(plain), out destPage))
						{
							pageNum = destPage;
						}
					}
				}
				if (title != null && pageNum.HasValue)
				{
					int idx;
					if (pageIndexByNum.TryGetValue(pageNum.Value, out idx))
					{
						OutlineEntry entry = new OutlineEntry();
						entry.Title = title;
						entry.PageIndex = idx;
						entry.Order = found.Count;
						found.Add(entry);
					}
				}
				int? next = RefIn(dict, "Next");
				if (!next.HasValue)
				{
					break;
				}
				current = next.Value;
			}
			// Sorted by page (stable), one entry per page (first title wins on duplicates).
			found.Sort((OutlineEntry a, OutlineEntry b) =>
			{
				int c = a.PageIndex.CompareTo(b.PageIndex);
				return c != 0 ? c : a.Order.CompareTo(b.Order);
			});
			List<OutlineEntry> result = new List<OutlineEntry>();
			foreach (OutlineEntry entry in found)
			{
				if (result.Count == 0 || result[result.Count - 1].PageIndex != entry.PageIndex)
				{
					result.Add(entry);
				}
			}
			return result;
		}

		private Dictionary<string, int> NamedDestinations(string catalog)
		{
			Dictionary<string, int> result = new Dictionary<string, int>();
			int? namesNum = RefIn(catalog, "Names");
			if (!namesNum.HasValue)
			{
				return result;
			}
			string names = DictOf(namesNum.Value);
			if (names == null)
			{
				return result;
			}
			int? destsNum = RefIn(names, "Dests");
			if (!destsNum.HasValue)
			{
				return result;
			}
			string dests = DictOf(destsNum.Value);
			if (dests == null)
			{
				return result;
			}
			string array = ArraySegment(dests, "Names");
			if (array == null)
			{
				return result;
			}
			foreach (Match m in NamedDestRx.Matches(array))
			{
				// Names live in the dests object: decrypt them when the file is encrypted (unless the object is compressed).
				int litEnd;
				byte[] nameBytes = ParseLiteralBytes("(" + m.Groups[1].Value + ")", 0, out litEnd) ?? Latin1.GetBytes(m.Groups[1].Value);
				byte[] plain = DecryptStringOf(nameBytes, destsNum.Value);
				int? pageNum = ParseIntOrNull(m.Groups[2].Value);
				if (pageNum.HasValue)
				{
					result[DecodePdfString(plain)] = pageNum.Value;
				}
			}
			return result;
		}

		// -- fonts and text ----------------------------------------------

		public List<string> PageParagraphs(int pageNum)
		{
			List<string> paragraphs = new List<string>();
			string dict = DictOf(pageNum);
			if (dict == null)
			{
				return paragraphs;
			}
			List<int> contents = ArrayRefs(dict, "Contents");
			if (contents.Count == 0)
			{
				int? single = RefIn(dict, "Contents");
				if (single.HasValue)
				{
					contents.Add(single.Value);
				}
			}
			string resources = ResourcesOf(dict);
			List<Line> lines = new List<Line>();
			foreach (int contentNum in contents)
			{
				byte[] stream = StreamOf(contentNum);
				if (stream == null)
				{
					// /Contents may point at an indirect array of streams.
					string arr = RawObject(contentNum);
					if (arr != null && TrimWs(arr).StartsWith("[", StringComparison.Ordinal))
					{
						foreach (int sub in RefsInText(arr))
						{
							byte[] subStream = StreamOf(sub);
							if (subStream != null)
							{
								lines.AddRange(ExtractLines(Latin1.GetString(subStream), resources));
							}
						}
					}
					continue;
				}
				lines.AddRange(ExtractLines(Latin1.GetString(stream), resources));
			}
			if (lines.Count == 0)
			{
				return paragraphs;
			}
			{
				List<string> raw = new List<string>(lines.Count);
				foreach (Line line in lines)
				{
					raw.Add(line.Text);
				}
				List<string> fixedLines = PdfTextFix.FixLines(raw);
				for (int li = 0; li < lines.Count; li++)
				{
					lines[li].Text = fixedLines[li];
				}
			}
			List<double> gaps = new List<double>();
			foreach (Line line in lines)
			{
				if (line.Gap > 0)
				{
					gaps.Add(line.Gap);
				}
			}
			gaps.Sort();
			double median = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0.0;
			double threshold = Math.Max(median * 1.6, 4.0);
			StringBuilder current = new StringBuilder();
			foreach (Line line in lines)
			{
				if (current.Length > 0)
				{
					current.Append(' ');
				}
				current.Append(line.Text);
				if (line.Gap > threshold)
				{
					if (current.Length > 0)
					{
						paragraphs.Add(current.ToString());
						current.Clear();
					}
				}
			}
			if (current.Length > 0)
			{
				paragraphs.Add(current.ToString());
			}
			List<string> filtered = new List<string>();
			foreach (string p in paragraphs)
			{
				if (!IsBlank(p))
				{
					filtered.Add(p);
				}
			}
			return filtered;
		}

		private string ResourcesOf(string pageDict)
		{
			string direct = DictSegment(pageDict, "Resources");
			if (direct != null)
			{
				return direct;
			}
			int? reference = RefIn(pageDict, "Resources");
			if (!reference.HasValue)
			{
				return null;
			}
			return DictOf(reference.Value);
		}

		private static Token LastOfKind(List<Token> stack, TokenKind kind)
		{
			for (int i = stack.Count - 1; i >= 0; i--)
			{
				if (stack[i].Kind == kind)
				{
					return stack[i];
				}
			}
			return null;
		}

		private static List<Token> AllOfKind(List<Token> stack, TokenKind kind)
		{
			List<Token> result = new List<Token>();
			foreach (Token t in stack)
			{
				if (t.Kind == kind)
				{
					result.Add(t);
				}
			}
			return result;
		}

		private List<Line> ExtractLines(string content, string resources)
		{
			List<Line> lines = new List<Line>();
			StringBuilder cur = new StringBuilder();
			FontInfo font = null;
			double fontSize = 12.0;
			double? lastY = null;
			double? lastX = null;
			bool needSpace = false;
			Action<double> flush = (double gap) =>
			{
				string t = TrimWs(cur.ToString());
				if (t.Length > 0)
				{
					lines.Add(new Line(t, gap));
				}
				cur.Clear();
				needSpace = false;
			};
			Action<string> appendShow = (string show) =>
			{
				if (show.Length == 0)
				{
					return;
				}
				if (needSpace && cur.Length > 0 && char.IsLetterOrDigit(cur[cur.Length - 1]) && char.IsLetterOrDigit(show[0]))
				{
					cur.Append(' ');
				}
				cur.Append(show);
				needSpace = false;
			};
			List<Token> tokens = Tokenize(content);
			List<Token> stack = new List<Token>();
			foreach (Token tok in tokens)
			{
				if (tok.Kind != TokenKind.Op)
				{
					stack.Add(tok);
					continue;
				}
				switch (tok.Value)
				{
					case "BT":
					{
						flush(0.0);
						lastY = null;
						lastX = null;
						break;
					}
					case "ET":
					{
						flush(0.0);
						break;
					}
					case "Tf":
					{
						Token nameTok = LastOfKind(stack, TokenKind.Name);
						Token sizeTok = LastOfKind(stack, TokenKind.Number);
						if (nameTok != null)
						{
							font = FontInfoFor(resources, nameTok.Value);
							if (sizeTok != null)
							{
								fontSize = sizeTok.Num;
							}
						}
						break;
					}
					case "Td":
					case "TD":
					{
						List<Token> nums = AllOfKind(stack, TokenKind.Number);
						if (nums.Count >= 2)
						{
							double tx = nums[nums.Count - 2].Num;
							double ty = nums[nums.Count - 1].Num;
							if (ty != 0.0)
							{
								flush(Math.Abs(ty));
								lastY = null;
								lastX = null;
							}
							else if (tx != 0.0)
							{
								needSpace = true;
							}
						}
						break;
					}
					case "Tm":
					{
						List<Token> nums = AllOfKind(stack, TokenKind.Number);
						if (nums.Count >= 6)
						{
							double e = nums[nums.Count - 2].Num;
							double f = nums[nums.Count - 1].Num;
							if (lastY.HasValue && f != lastY.Value)
							{
								flush(Math.Abs(f - lastY.Value));
							}
							else if (lastX.HasValue && e != lastX.Value)
							{
								needSpace = true;
							}
							lastY = f;
							lastX = e;
						}
						break;
					}
					case "T*":
					{
						flush(fontSize);
						break;
					}
					case "Tj":
					{
						Token s = LastOfKind(stack, TokenKind.Str);
						if (s != null)
						{
							appendShow(DecodeShow(font, s.Bytes));
						}
						break;
					}
					case "TJ":
					{
						Token arr = LastOfKind(stack, TokenKind.Arr);
						if (arr != null)
						{
							foreach (Token el in arr.Items)
							{
								if (el.Kind == TokenKind.Str)
								{
									appendShow(DecodeShow(font, el.Bytes));
								}
								else if (el.Kind == TokenKind.Number && el.Num < -150)
								{
									needSpace = true;
								}
							}
						}
						break;
					}
					case "'":
					case "\"":
					{
						// Both are T* + show: next line, then text.
						flush(fontSize);
						Token s = LastOfKind(stack, TokenKind.Str);
						if (s != null)
						{
							appendShow(DecodeShow(font, s.Bytes));
						}
						break;
					}
					default:
						break;
				}
				stack.Clear();
			}
			flush(0.0);
			return lines;
		}

		private FontInfo FontInfoFor(string resources, string name)
		{
			if (resources == null)
			{
				return null;
			}
			string fontDictSeg = DictSegment(resources, "Font");
			if (fontDictSeg == null)
			{
				int? fontRef = RefIn(resources, "Font");
				if (fontRef.HasValue)
				{
					fontDictSeg = DictOf(fontRef.Value);
				}
			}
			if (fontDictSeg == null)
			{
				return null;
			}
			int? reference = null;
			foreach (Match m in FontRefRx.Matches(fontDictSeg))
			{
				if (DecodeName(m.Groups[1].Value) == name)
				{
					reference = ParseIntOrNull(m.Groups[2].Value);
					break;
				}
			}
			if (!reference.HasValue)
			{
				return null;
			}
			FontInfo cached;
			if (fontCache.TryGetValue(reference.Value, out cached))
			{
				return cached;
			}
			FontInfo result = BuildFontInfo(reference.Value);
			fontCache[reference.Value] = result;
			return result;
		}

		private FontInfo BuildFontInfo(int reference)
		{
			string dict = DictOf(reference);
			if (dict == null)
			{
				return null;
			}
			string subtype = NameIn(dict, "Subtype");
			if (subtype == "Type0")
			{
				List<int> descRefs = ArrayRefs(dict, "DescendantFonts");
				if (descRefs.Count == 0)
				{
					return null;
				}
				string desc = DictOf(descRefs[0]);
				if (desc == null)
				{
					return null;
				}
				// ToUnicode normally sits on the Type0 font itself; the descendant is checked as a fallback.
				CMap cmap = ToUnicodeMap(RefIn(dict, "ToUnicode") ?? RefIn(desc, "ToUnicode"));
				string enc0 = NameIn(dict, "Encoding");
				int codeBytes0 = 2;
				if (cmap != null && !(enc0 != null && enc0.StartsWith("Identity", StringComparison.Ordinal)))
				{
					codeBytes0 = cmap.CodeBytes;
				}
				FontInfo type0 = new FontInfo();
				type0.ToUnicode = cmap != null ? cmap.Map : new Dictionary<int, string>();
				type0.CodeBytes = codeBytes0;
				type0.Differences = new Dictionary<int, string>();
				type0.Encoding = null;
				return type0;
			}
			CMap toUnicode = ToUnicodeMap(RefIn(dict, "ToUnicode"));
			string encoding = NameIn(dict, "Encoding");
			Dictionary<int, string> differences = new Dictionary<int, string>();
			string encSeg = DictSegment(dict, "Encoding");
			if (encSeg != null)
			{
				string diffSeg = ArraySegment(encSeg, "Differences");
				if (diffSeg != null)
				{
					int code = 0;
					foreach (Match m in DiffRx.Matches(diffSeg))
					{
						if (m.Groups[1].Value.Length > 0)
						{
							code = ParseIntOrNull(m.Groups[1].Value) ?? 0;
						}
						else
						{
							differences[code] = m.Groups[2].Value;
							code++;
						}
					}
				}
			}
			FontInfo info = new FontInfo();
			info.ToUnicode = toUnicode != null ? toUnicode.Map : new Dictionary<int, string>();
			info.CodeBytes = toUnicode != null ? toUnicode.CodeBytes : 1;
			info.Differences = differences;
			info.Encoding = encoding;
			return info;
		}

		private CMap ToUnicodeMap(int? reference)
		{
			if (!reference.HasValue)
			{
				return null;
			}
			byte[] stream = StreamOf(reference.Value);
			if (stream == null)
			{
				return null;
			}
			string cmap = Latin1.GetString(stream);
			int maxHi = 0;
			// A CMap holds many bfchar/bfrange blocks (100 entries each): every block is read.
			foreach (Match block in CodeSpaceBlock.Matches(cmap))
			{
				foreach (Match m in HexPair.Matches(block.Groups[1].Value))
				{
					maxHi = Math.Max(maxHi, ParseHex(m.Groups[2].Value) ?? 0);
				}
			}
			Dictionary<int, string> map = new Dictionary<int, string>();
			foreach (Match block in BfCharBlock.Matches(cmap))
			{
				foreach (Match m in HexPair.Matches(block.Groups[1].Value))
				{
					int? src = ParseHex(m.Groups[1].Value);
					if (!src.HasValue)
					{
						continue;
					}
					map[src.Value] = DecodeCMapDst(m.Groups[2].Value);
				}
			}
			foreach (Match block in BfRangeBlock.Matches(cmap))
			{
				foreach (Match m in BfRangeEntry.Matches(block.Groups[1].Value))
				{
					int? lo = ParseHex(m.Groups[1].Value);
					int? hi = ParseHex(m.Groups[2].Value);
					if (!lo.HasValue || !hi.HasValue)
					{
						continue;
					}
					if (m.Groups[4].Value.Length > 0)
					{
						string baseStr = DecodeCMapDst(m.Groups[4].Value);
						int baseCode = baseStr.Length > 0 ? baseStr[0] : 0;
						for (int code = lo.Value; code <= hi.Value; code++)
						{
							if (code - lo.Value > 10000)
							{
								break;
							}
							map[code] = ((char)(baseCode + (code - lo.Value))).ToString();
						}
					}
					else if (m.Groups[5].Value.Length > 0)
					{
						List<string> dsts = new List<string>();
						foreach (Match d in HexInAngle.Matches(m.Groups[5].Value))
						{
							dsts.Add(DecodeCMapDst(d.Groups[1].Value));
						}
						for (int k = 0; k < dsts.Count && lo.Value + k <= hi.Value; k++)
						{
							map[lo.Value + k] = dsts[k];
						}
					}
				}
			}
			if (map.Count == 0)
			{
				return null;
			}
			CMap result = new CMap();
			result.Map = map;
			result.CodeBytes = maxHi > 0xFF ? 2 : 1;
			return result;
		}

		private static string DecodeCMapDst(string hex)
		{
			byte[] data = HexBytes(hex);
			if (data.Length == 0)
			{
				return "";
			}
			if (data.Length == 1)
			{
				return ((char)(data[0] & 0xFF)).ToString();
			}
			return Encoding.BigEndianUnicode.GetString(data);
		}

		private static string DecodeShow(FontInfo font, byte[] data)
		{
			StringBuilder sb = new StringBuilder();
			if (font == null)
			{
				// No font context: Latin-1 passthrough for plain ASCII text.
				foreach (byte b in data)
				{
					sb.Append((char)(b & 0xFF));
				}
				return sb.ToString();
			}
			int i = 0;
			while (i < data.Length)
			{
				int code;
				if (font.CodeBytes == 2)
				{
					if (i + 1 >= data.Length)
					{
						break;
					}
					code = ((data[i] & 0xFF) << 8) | (data[i + 1] & 0xFF);
					i += 2;
				}
				else
				{
					code = data[i] & 0xFF;
					i += 1;
				}
				string mapped;
				if (font.ToUnicode.TryGetValue(code, out mapped))
				{
					sb.Append(mapped);
					continue;
				}
				string diffName;
				if (font.Differences.TryGetValue(code, out diffName))
				{
					string glyph = GlyphToUnicode(diffName);
					if (glyph != null)
					{
						sb.Append(glyph);
					}
					continue;
				}
				if (code >= 32 && code <= 126)
				{
					sb.Append((char)code);
				}
				else if (font.Encoding == "WinAnsiEncoding")
				{
					char w = WinAnsi(code);
					if (w != '\0')
					{
						sb.Append(w);
					}
				}
				else if (code >= 160 && code <= 255)
				{
					sb.Append((char)code);
				}
				// Otherwise: unmapped glyph, dropped, not replaced.
			}
			return sb.ToString();
		}

		private static string GlyphToUnicode(string name)
		{
			if (name.Length == 1)
			{
				return name;
			}
			if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length > 3)
			{
				string hex = name.Substring(3);
				bool allHex = true;
				foreach (char c in hex)
				{
					if (HexDigit(c) < 0)
					{
						allHex = false;
						break;
					}
				}
				if (allHex)
				{
					StringBuilder sb = new StringBuilder();
					for (int i = 0; i < hex.Length; i += 4)
					{
						int? v = ParseHex(hex.Substring(i, Math.Min(4, hex.Length - i)));
						if (v.HasValue)
						{
							sb.Append((char)v.Value);
						}
					}
					return sb.ToString();
				}
			}
			switch (name)
			{
				case "space": return " ";
				case "hyphen":
				case "minus": return "-";
				case "endash": return "\u2013";
				case "emdash": return "\u2014";
				case "quoteleft": return "\u2018";
				case "quoteright": return "\u2019";
				case "quotedblleft": return "\u201C";
				case "quotedblright": return "\u201D";
				case "quotesingle": return "'";
				case "quotedbl": return "\"";
				case "bullet": return "\u2022";
				case "ellipsis": return "\u2026";
				case "Euro": return "\u20AC";
				case "fi": return "fi";
				case "fl": return "fl";
				case "ff": return "ff";
				case "ffi": return "ffi";
				case "ffl": return "ffl";
				default: return null;
			}
		}

		// '\0' means "no mapping".
		private static char WinAnsi(int code)
		{
			switch (code)
			{
				case 0x80: return '\u20AC';
				case 0x82: return '\u201A';
				case 0x83: return '\u0192';
				case 0x84: return '\u201E';
				case 0x85: return '\u2026';
				case 0x86: return '\u2020';
				case 0x87: return '\u2021';
				case 0x88: return '\u02C6';
				case 0x89: return '\u2030';
				case 0x8A: return '\u0160';
				case 0x8B: return '\u2039';
				case 0x8C: return '\u0152';
				case 0x8E: return '\u017D';
				case 0x91: return '\u2018';
				case 0x92: return '\u2019';
				case 0x93: return '\u201C';
				case 0x94: return '\u201D';
				case 0x95: return '\u2022';
				case 0x96: return '\u2013';
				case 0x97: return '\u2014';
				case 0x98: return '\u02DC';
				case 0x99: return '\u2122';
				case 0x9A: return '\u0161';
				case 0x9B: return '\u203A';
				case 0x9C: return '\u0153';
				case 0x9E: return '\u017E';
				case 0x9F: return '\u0178';
				default: return '\0';
			}
		}

		// -- content-stream tokenizer ------------------------------------

		private static bool IsDelim(char c)
		{
			return IsWs(c) || "()<>[]{}/%".IndexOf(c) >= 0;
		}

		private static Token MakeStr(byte[] data)
		{
			Token t = new Token();
			t.Kind = TokenKind.Str;
			t.Bytes = data;
			return t;
		}

		// Literal string at start ('(') in a content stream -> its bytes; next is the index after the closing paren.
		private static byte[] ReadLiteral(string content, int start, out int next)
		{
			MemoryStream result = new MemoryStream();
			int depth = 1;
			int i = start + 1;
			while (i < content.Length && depth > 0)
			{
				char ch = content[i];
				if (ch == '\\' && i + 1 < content.Length)
				{
					char e = content[i + 1];
					switch (e)
					{
						case 'n':
							result.WriteByte((byte)'\n');
							i += 2;
							break;
						case 'r':
							result.WriteByte((byte)'\r');
							i += 2;
							break;
						case 't':
							result.WriteByte((byte)'\t');
							i += 2;
							break;
						case 'b':
							result.WriteByte((byte)8);
							i += 2;
							break;
						case 'f':
							result.WriteByte((byte)0x0C);
							i += 2;
							break;
						case '(':
						case ')':
						case '\\':
							result.WriteByte((byte)e);
							i += 2;
							break;
						case '\n':
							i += 2;
							break;
						case '\r':
							i += (i + 2 < content.Length && content[i + 2] == '\n') ? 3 : 2;
							break;
						default:
							if (IsAsciiDigit(e))
							{
								int j = i + 1;
								while (j < content.Length && j < i + 4 && IsAsciiDigit(content[j]))
								{
									j++;
								}
								int? ov = ParseOct(content.Substring(i + 1, j - i - 1));
								result.WriteByte((byte)(ov ?? 0));
								i = j;
							}
							else
							{
								result.WriteByte((byte)e);
								i += 2;
							}
							break;
					}
				}
				else if (ch == '(')
				{
					depth++;
					result.WriteByte((byte)ch);
					i++;
				}
				else if (ch == ')')
				{
					depth--;
					if (depth > 0)
					{
						result.WriteByte((byte)ch);
					}
					i++;
				}
				else
				{
					result.WriteByte((byte)(ch & 0xFF));
					i++;
				}
			}
			next = i;
			return result.ToArray();
		}

		private static List<Token> Tokenize(string content)
		{
			List<Token> result = new List<Token>();
			int i = 0;
			while (i < content.Length)
			{
				char c = content[i];
				if (IsWs(c))
				{
					i++;
				}
				else if (c == '%')
				{
					while (i < content.Length && content[i] != '\n')
					{
						i++;
					}
				}
				else if (c == '(')
				{
					int next;
					byte[] data = ReadLiteral(content, i, out next);
					result.Add(MakeStr(data));
					i = next;
				}
				else if (c == '<' && i + 1 < content.Length && content[i + 1] == '<')
				{
					// Inline dict (gs arguments etc.): skip balanced.
					int depth = 0;
					while (i + 1 < content.Length)
					{
						if (StartsAt(content, "<<", i))
						{
							depth++;
							i += 2;
						}
						else if (StartsAt(content, ">>", i))
						{
							depth--;
							i += 2;
							if (depth == 0)
							{
								break;
							}
						}
						else
						{
							i++;
						}
					}
				}
				else if (c == '<')
				{
					int end = IdxOfChar(content, '>', i);
					if (end < 0)
					{
						break;
					}
					result.Add(MakeStr(HexBytes(content.Substring(i + 1, end - i - 1))));
					i = end + 1;
				}
				else if (c == '[')
				{
					List<Token> items = new List<Token>();
					i++;
					while (i < content.Length)
					{
						while (i < content.Length && IsWs(content[i]))
						{
							i++;
						}
						if (i >= content.Length)
						{
							break;
						}
						if (content[i] == ']')
						{
							i++;
							break;
						}
						if (content[i] == '(')
						{
							int next;
							byte[] data = ReadLiteral(content, i, out next);
							items.Add(MakeStr(data));
							i = next;
						}
						else if (content[i] == '<')
						{
							int end = IdxOfChar(content, '>', i);
							if (end < 0)
							{
								break;
							}
							items.Add(MakeStr(HexBytes(content.Substring(i + 1, end - i - 1))));
							i = end + 1;
						}
						else
						{
							int j = i;
							while (j < content.Length && !IsDelim(content[j]))
							{
								j++;
							}
							double? num = ParseDoubleOrNull(content.Substring(i, j - i));
							if (num.HasValue)
							{
								Token nt = new Token();
								nt.Kind = TokenKind.Number;
								nt.Num = num.Value;
								items.Add(nt);
							}
							// A delimiter that starts no token (e.g. '/', ')') is skipped, so the scan always advances.
							i = j > i ? j : i + 1;
						}
					}
					Token arr = new Token();
					arr.Kind = TokenKind.Arr;
					arr.Items = items;
					result.Add(arr);
				}
				else if (c == '/')
				{
					int j = i + 1;
					while (j < content.Length && !IsDelim(content[j]))
					{
						j++;
					}
					Token nt = new Token();
					nt.Kind = TokenKind.Name;
					nt.Value = content.Substring(i + 1, j - i - 1);
					result.Add(nt);
					i = j;
				}
				else if (IsAsciiDigit(c) || c == '-' || c == '+' || c == '.')
				{
					int j = i;
					while (j < content.Length && (IsAsciiDigit(content[j]) || "-+.eE".IndexOf(content[j]) >= 0))
					{
						j++;
					}
					double? num = ParseDoubleOrNull(content.Substring(i, j - i));
					if (num.HasValue)
					{
						Token nt = new Token();
						nt.Kind = TokenKind.Number;
						nt.Num = num.Value;
						result.Add(nt);
					}
					i = j;
				}
				else if (char.IsLetter(c) || c == '*' || c == '\'' || c == '"')
				{
					int j = i;
					while (j < content.Length && (char.IsLetter(content[j]) || "*'\"".IndexOf(content[j]) >= 0))
					{
						j++;
					}
					Token ot = new Token();
					ot.Kind = TokenKind.Op;
					ot.Value = content.Substring(i, j - i);
					result.Add(ot);
					i = j;
				}
				else
				{
					i++;
				}
			}
			return result;
		}

		// ------------------------------------------------------------------
		// Standard security handler, built from the /Encrypt dict. The constructor validates the password: it tries the given password
		// first, then the empty password (permission-only files open with no prompt at all). Both failing throws
		// PdfPasswordWrongException when a password was given, PdfPasswordRequiredException otherwise. Malformed dicts and
		// non-Standard handlers throw PdfUnsupportedEncryptionException.
		// ------------------------------------------------------------------

		private sealed class SecurityHandler
		{
			private readonly PdfDoc doc;
			private readonly int revision;
			private readonly int version;
			private readonly int keyLen;
			private readonly byte[] oBytes;
			private readonly byte[] uBytes;
			private readonly byte[] oeBytes;
			private readonly byte[] ueBytes;
			private readonly byte[] permsBytes;
			private readonly byte[] pBytes;
			private readonly byte[] id0;
			private readonly CryptMethod stringMethod;
			private readonly CryptMethod streamMethod;

			// False when the metadata stream is stored unencrypted.
			public readonly bool EncryptMetadata;

			// The file key: derived once, used for every object.
			private readonly byte[] fileKey;

			public SecurityHandler(PdfDoc doc, string encDictFull, byte[] id0, string password)
			{
				this.doc = doc;
				this.id0 = id0 ?? new byte[0];
				// Top-level keys only: /Length inside /CF must not shadow the main /Length.
				string encDict = StripNested(encDictFull);
				int? rev = IntValue(encDict, "R");
				if (!rev.HasValue)
				{
					throw new PdfUnsupportedEncryptionException("Encrypt dict has no /R");
				}
				revision = rev.Value;
				version = IntValue(encDict, "V") ?? 0;
				if (revision >= 5)
				{
					keyLen = 32;
				}
				else if (version >= 4)
				{
					keyLen = 16;
				}
				else
				{
					keyLen = (IntValue(encDict, "Length") ?? 40) / 8;
				}
				oBytes = StringBytesAfterKey(encDict, "O");
				if (oBytes == null)
				{
					throw new PdfUnsupportedEncryptionException("Encrypt dict has no /O");
				}
				uBytes = StringBytesAfterKey(encDict, "U");
				if (uBytes == null)
				{
					throw new PdfUnsupportedEncryptionException("Encrypt dict has no /U");
				}
				oeBytes = StringBytesAfterKey(encDict, "OE");
				ueBytes = StringBytesAfterKey(encDict, "UE");
				permsBytes = StringBytesAfterKey(encDict, "Perms");
				int pValue = IntValue(encDict, "P") ?? 0;
				pBytes = new byte[]
				{
					unchecked((byte)pValue), unchecked((byte)(pValue >> 8)),
					unchecked((byte)(pValue >> 16)), unchecked((byte)(pValue >> 24))
				};
				string cfDict = DictSegment(encDictFull, "CF");

				string filter = NameIn(encDict, "Filter");
				if (filter != null && filter != "Standard")
				{
					throw new PdfUnsupportedEncryptionException("Security handler is not Standard: " + filter);
				}
				if (revision < 2 || revision > 6)
				{
					throw new PdfUnsupportedEncryptionException("Unsupported security revision: " + revision.ToString(CultureInfo.InvariantCulture));
				}
				if (revision < 5)
				{
					if (oBytes.Length != 32 || uBytes.Length != 32)
					{
						throw new PdfUnsupportedEncryptionException("Malformed /O or /U");
					}
				}
				else
				{
					if (uBytes.Length != 48 || oBytes.Length != 48 || ueBytes == null || ueBytes.Length != 32 || oeBytes == null || oeBytes.Length != 32)
					{
						throw new PdfUnsupportedEncryptionException("Malformed R5/R6 entries");
					}
				}
				stringMethod = MethodFor(NameIn(encDict, "StrF"), cfDict);
				streamMethod = MethodFor(NameIn(encDict, "StmF"), cfDict);
				if (revision >= 5 &&
					(stringMethod == CryptMethod.Rc4 || stringMethod == CryptMethod.Aes128 ||
					streamMethod == CryptMethod.Rc4 || streamMethod == CryptMethod.Aes128))
				{
					throw new PdfUnsupportedEncryptionException("R5/R6 requires AESV3 crypt filters");
				}
				if (revision >= 5)
				{
					EncryptMetadata = !(permsBytes != null && permsBytes.Length > 8 && permsBytes[8] == 0x46);
				}
				else
				{
					EncryptMetadata = !EncryptMetaFalse.IsMatch(encDict);
				}
				byte[] pwBytes = password != null ? Encoding.UTF8.GetBytes(password) : null;
				List<byte[]> candidates = new List<byte[]>();
				if (pwBytes != null)
				{
					candidates.Add(pwBytes);
				}
				if (pwBytes == null || pwBytes.Length != 0)
				{
					candidates.Add(new byte[0]);
				}
				byte[] found = null;
				foreach (byte[] pw in candidates)
				{
					found = TryPassword(pw);
					if (found != null)
					{
						break;
					}
				}
				if (found == null)
				{
					if (password != null)
					{
						throw new PdfPasswordWrongException();
					}
					throw new PdfPasswordRequiredException();
				}
				fileKey = found;
			}

			private CryptMethod MethodFor(string which, string cfDict)
			{
				if (version < 4)
				{
					return CryptMethod.Rc4;
				}
				if (which == null || which == "Identity")
				{
					return CryptMethod.Identity;
				}
				string seg = cfDict != null ? DictSegment(cfDict, which) : null;
				if (seg == null)
				{
					throw new PdfUnsupportedEncryptionException("Unknown crypt filter: " + which);
				}
				string cfm = NameIn(seg, "CFM");
				switch (cfm)
				{
					case "None":
						return CryptMethod.Identity;
					case "V1":
					case "V2":
						return CryptMethod.Rc4;
					case "AESV2":
						return CryptMethod.Aes128;
					case "AESV3":
						return CryptMethod.Aes256;
					default:
						throw new PdfUnsupportedEncryptionException("Unsupported crypt filter method in " + which);
				}
			}

			// File key for pw, or null when it does not open the file.
			private byte[] TryPassword(byte[] pw)
			{
				if (revision == 2)
				{
					byte[] padded = PdfCrypto.PadPassword32(pw);
					byte[] key = PdfCrypto.CopyOf(PdfCrypto.Md5(padded, oBytes, pBytes, id0), 5);
					byte[] computed = PdfCrypto.Rc4(key, PdfCrypto.Padding);
					return PdfCrypto.SameBytes(computed, uBytes) ? key : null;
				}
				if (revision == 3 || revision == 4)
				{
					byte[] padded = PdfCrypto.PadPassword32(pw);
					byte[] meta = (revision >= 4 && !EncryptMetadata) ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF } : new byte[0];
					byte[] digest = PdfCrypto.Md5(padded, oBytes, pBytes, id0, meta);
					// Algorithm 2 step (h): 50 more MD5 rounds over the first keyLen bytes.
					int n = Math.Max(1, Math.Min(keyLen, 16));
					for (int i = 0; i < 50; i++)
					{
						digest = PdfCrypto.Md5(PdfCrypto.CopyOf(digest, n));
					}
					byte[] key = PdfCrypto.CopyOf(digest, n);
					return ValidatesU(key) ? key : null;
				}
				if (revision == 5 || revision == 6)
				{
					byte[] pw127 = PdfCrypto.TruncatePassword127(pw);
					// User password first, then owner password (an owner password also opens the document).
					byte[] userKey = UserFileKey(pw127, revision == 6);
					if (userKey != null)
					{
						return userKey;
					}
					return OwnerFileKey(pw127, revision == 6);
				}
				return null;
			}

			// R3/R4 U validation (Algorithm 5): MD5(padding + ID) run through RC4 20 times, keyed by the file key XORed with the pass
			// number; the first 16 bytes must equal U.
			private bool ValidatesU(byte[] key)
			{
				if (uBytes.Length < 16)
				{
					return false;
				}
				byte[] data = PdfCrypto.Md5(PdfCrypto.Padding, id0);
				for (int i = 0; i < 20; i++)
				{
					byte[] k = new byte[key.Length];
					for (int j = 0; j < k.Length; j++)
					{
						k[j] = (byte)(key[j] ^ i);
					}
					data = PdfCrypto.Rc4(k, data);
				}
				return PdfCrypto.SameBytes(PdfCrypto.CopyOfRange(data, 0, 16), PdfCrypto.CopyOfRange(uBytes, 0, 16));
			}

			// UE/OE hold the file key encrypted with AES-256 CBC, zero IV, no padding.
			private static byte[] UnwrapKey(byte[] keyHash, byte[] wrapped)
			{
				return PdfCrypto.AesCbcDecrypt(keyHash, new byte[16], wrapped);
			}

			private byte[] UserFileKey(byte[] pw127, bool isR6)
			{
				if (uBytes.Length < 48 || ueBytes == null)
				{
					return null;
				}
				byte[] valSalt = PdfCrypto.CopyOfRange(uBytes, 32, 40);
				byte[] hash = isR6 ? PdfCrypto.R6Hash(pw127, valSalt, new byte[0]) : PdfCrypto.Sha256(pw127, valSalt);
				if (!PdfCrypto.SameBytes(hash, PdfCrypto.CopyOfRange(uBytes, 0, 32)))
				{
					return null;
				}
				byte[] salt = PdfCrypto.CopyOfRange(uBytes, 40, 48);
				byte[] keyHash = isR6 ? PdfCrypto.R6Hash(pw127, salt, new byte[0]) : PdfCrypto.Sha256(pw127, salt);
				byte[] key = UnwrapKey(keyHash, ueBytes);
				if (key == null)
				{
					return null;
				}
				return ValidPerms(key) ? key : null;
			}

			private byte[] OwnerFileKey(byte[] pw127, bool isR6)
			{
				if (oBytes.Length < 48 || oeBytes == null || uBytes.Length < 48)
				{
					return null;
				}
				byte[] u48 = PdfCrypto.CopyOfRange(uBytes, 0, 48);
				byte[] valSalt = PdfCrypto.CopyOfRange(oBytes, 32, 40);
				byte[] hash = isR6 ? PdfCrypto.R6Hash(pw127, valSalt, u48) : PdfCrypto.Sha256(pw127, valSalt, u48);
				if (!PdfCrypto.SameBytes(hash, PdfCrypto.CopyOfRange(oBytes, 0, 32)))
				{
					return null;
				}
				byte[] salt = PdfCrypto.CopyOfRange(oBytes, 40, 48);
				byte[] keyHash = isR6 ? PdfCrypto.R6Hash(pw127, salt, u48) : PdfCrypto.Sha256(pw127, salt, u48);
				byte[] key = UnwrapKey(keyHash, oeBytes);
				if (key == null)
				{
					return null;
				}
				return ValidPerms(key) ? key : null;
			}

			// Perms integrity: P, all-ones, and the 'adb' marker.
			private bool ValidPerms(byte[] key)
			{
				if (permsBytes == null || permsBytes.Length != 16)
				{
					return true;
				}
				byte[] dec = PdfCrypto.AesEcbDecrypt(key, permsBytes);
				if (dec == null)
				{
					return false;
				}
				if (!PdfCrypto.SameBytes(PdfCrypto.CopyOfRange(dec, 0, 4), pBytes))
				{
					return false;
				}
				for (int i = 4; i < 8; i++)
				{
					if ((dec[i] & 0xFF) != 0xFF)
					{
						return false;
					}
				}
				return dec[9] == 0x61 && dec[10] == 0x64 && dec[11] == 0x62;
			}

			// Per-object key (R<5): file key + object/generation + salt.
			private byte[] ObjectKey(int objNum, bool aesSalt)
			{
				int gen;
				if (!doc.genMap.TryGetValue(objNum, out gen))
				{
					gen = 0;
				}
				byte[] objPart = new byte[]
				{
					(byte)(objNum & 0xFF), (byte)((objNum >> 8) & 0xFF), (byte)((objNum >> 16) & 0xFF),
					(byte)(gen & 0xFF), (byte)((gen >> 8) & 0xFF)
				};
				byte[] salt = aesSalt ? new byte[] { 0x73, 0x41, 0x6C, 0x54 } : new byte[0];
				byte[] digest = PdfCrypto.Md5(PdfCrypto.Concat(fileKey, objPart, salt));
				int len = Math.Min(keyLen + 5, 16);
				return PdfCrypto.CopyOf(digest, len);
			}

			private byte[] Decrypt(CryptMethod method, byte[] data, int objNum)
			{
				switch (method)
				{
					case CryptMethod.Rc4:
						return PdfCrypto.Rc4(ObjectKey(objNum, false), data);
					case CryptMethod.Aes128:
						return PdfCrypto.AesCbcDecryptWithIvPrefix(ObjectKey(objNum, true), data) ?? new byte[0];
					case CryptMethod.Aes256:
						return PdfCrypto.AesCbcDecryptWithIvPrefix(fileKey, data) ?? new byte[0];
					default:
						return data;
				}
			}

			// Decrypts one string value belonging to objNum. Strings inside object streams are plaintext (the whole stream was decrypted).
			public byte[] DecryptString(byte[] data, int objNum)
			{
				if (doc.objStm.ContainsKey(objNum))
				{
					return data;
				}
				return Decrypt(stringMethod, data, objNum);
			}

			// Decrypts one stream belonging to objNum.
			public byte[] DecryptStream(byte[] data, int objNum)
			{
				return Decrypt(streamMethod, data, objNum);
			}
		}
	}
}
}

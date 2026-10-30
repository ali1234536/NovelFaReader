using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EpubReader.Services
{

// یک آبجکت JSON که ترتیب کلیدها رو نگه می‌داره (برای اینکه نوشتن دوباره‌ی مانیفست بایت‌به‌بایت ثابت بمونه).
public sealed class JObject
{
	private readonly List<string> order = new List<string>();
	private readonly Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);

	public int Count { get { return order.Count; } }

	public IList<string> Keys { get { return order; } }

	public bool Has(string key)
	{
		return map.ContainsKey(key);
	}

	public object Get(string key)
	{
		object v;
		return map.TryGetValue(key, out v) ? v : null;
	}

	public bool TryGet(string key, out object value)
	{
		return map.TryGetValue(key, out value);
	}

	// کلید تکراری ممنوعه (ابهام ایجاد می‌کنه)
	public void Add(string key, object value)
	{
		if (map.ContainsKey(key))
		{
			throw new FormatException("duplicate key: " + key);
		}
		map[key] = value;
		order.Add(key);
	}

	public JObject Put(string key, object value)
	{
		Add(key, value);
		return this;
	}
}

// پارسر و رایتر کوچک JSON فقط برای مانیفست بکاپ.
// پارسر: آبجکت، آرایه، رشته (با همه‌ی escapeها)، عدد صحیح، true/false/null. هر چیز دیگه‌ای = FormatException.
// خروجی پارسر: JObject / List&lt;object&gt; / string / long / bool / null.
// رایتر: ترتیب کلیدها و قالب خروجی ثابته (آبجکت‌های تک‌سطحیِ حداکثر ۳ کلیدی و آرایه‌های اسکالر یک‌خطی، بقیه چندخطی با دو فاصله).
public static class MiniJson
{
	private const int MaxDepth = 32;

	// ---------- پارس ----------
	public static object Parse(string text)
	{
		if (text == null)
		{
			throw new FormatException("empty");
		}
		Reader r = new Reader(text);
		r.SkipWs();
		object v = r.ReadValue(0);
		r.SkipWs();
		if (!r.AtEnd)
		{
			throw new FormatException("trailing data");
		}
		return v;
	}

	private sealed class Reader
	{
		private readonly string s;
		private int i;

		public Reader(string text)
		{
			s = text;
		}

		public bool AtEnd { get { return i >= s.Length; } }

		public void SkipWs()
		{
			while (i < s.Length)
			{
				char c = s[i];
				if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
				{
					i++;
				}
				else
				{
					break;
				}
			}
		}

		private char Peek()
		{
			if (i >= s.Length)
			{
				throw new FormatException("unexpected end");
			}
			return s[i];
		}

		private void Expect(char c)
		{
			if (Peek() != c)
			{
				throw new FormatException("expected '" + c + "' at " + i);
			}
			i++;
		}

		public object ReadValue(int depth)
		{
			if (depth > MaxDepth)
			{
				throw new FormatException("too deep");
			}
			char c = Peek();
			if (c == '{')
			{
				return ReadObject(depth);
			}
			if (c == '[')
			{
				return ReadArray(depth);
			}
			if (c == '"')
			{
				return ReadString();
			}
			if (c == '-' || (c >= '0' && c <= '9'))
			{
				return ReadNumber();
			}
			if (Literal("true"))
			{
				return true;
			}
			if (Literal("false"))
			{
				return false;
			}
			if (Literal("null"))
			{
				return null;
			}
			throw new FormatException("bad value at " + i);
		}

		private bool Literal(string word)
		{
			if (string.CompareOrdinal(s, i, word, 0, word.Length) == 0)
			{
				i += word.Length;
				return true;
			}
			return false;
		}

		private JObject ReadObject(int depth)
		{
			Expect('{');
			JObject obj = new JObject();
			SkipWs();
			if (Peek() == '}')
			{
				i++;
				return obj;
			}
			while (true)
			{
				SkipWs();
				if (Peek() != '"')
				{
					throw new FormatException("key expected at " + i);
				}
				string key = ReadString();
				SkipWs();
				Expect(':');
				SkipWs();
				object value = ReadValue(depth + 1);
				obj.Add(key, value);
				SkipWs();
				char c = Peek();
				i++;
				if (c == ',')
				{
					continue;
				}
				if (c == '}')
				{
					return obj;
				}
				throw new FormatException("bad object at " + (i - 1));
			}
		}

		private List<object> ReadArray(int depth)
		{
			Expect('[');
			List<object> list = new List<object>();
			SkipWs();
			if (Peek() == ']')
			{
				i++;
				return list;
			}
			while (true)
			{
				SkipWs();
				list.Add(ReadValue(depth + 1));
				SkipWs();
				char c = Peek();
				i++;
				if (c == ',')
				{
					continue;
				}
				if (c == ']')
				{
					return list;
				}
				throw new FormatException("bad array at " + (i - 1));
			}
		}

		private string ReadString()
		{
			Expect('"');
			StringBuilder sb = new StringBuilder();
			while (true)
			{
				if (i >= s.Length)
				{
					throw new FormatException("unterminated string");
				}
				char c = s[i++];
				if (c == '"')
				{
					return sb.ToString();
				}
				if (c < 0x20)
				{
					throw new FormatException("control character in string");
				}
				if (c != '\\')
				{
					sb.Append(c);
					continue;
				}
				if (i >= s.Length)
				{
					throw new FormatException("bad escape");
				}
				char e = s[i++];
				switch (e)
				{
					case '"': sb.Append('"'); break;
					case '\\': sb.Append('\\'); break;
					case '/': sb.Append('/'); break;
					case 'b': sb.Append('\b'); break;
					case 'f': sb.Append('\f'); break;
					case 'n': sb.Append('\n'); break;
					case 'r': sb.Append('\r'); break;
					case 't': sb.Append('\t'); break;
					case 'u':
						if (i + 4 > s.Length)
						{
							throw new FormatException("bad unicode escape");
						}
						int code = 0;
						for (int k = 0; k < 4; k++)
						{
							int d = HexVal(s[i + k]);
							if (d < 0)
							{
								throw new FormatException("bad unicode escape");
							}
							code = (code << 4) | d;
						}
						i += 4;
						sb.Append((char)code);
						break;
					default:
						throw new FormatException("bad escape \\" + e);
				}
			}
		}

		private static int HexVal(char c)
		{
			if (c >= '0' && c <= '9') return c - '0';
			if (c >= 'a' && c <= 'f') return c - 'a' + 10;
			if (c >= 'A' && c <= 'F') return c - 'A' + 10;
			return -1;
		}

		private object ReadNumber()
		{
			int start = i;
			if (s[i] == '-')
			{
				i++;
			}
			if (i >= s.Length || s[i] < '0' || s[i] > '9')
			{
				throw new FormatException("bad number");
			}
			if (s[i] == '0')
			{
				i++;
			}
			else
			{
				while (i < s.Length && s[i] >= '0' && s[i] <= '9')
				{
					i++;
				}
			}
			if (i < s.Length && (s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
			{
				// عدد اعشاری (مثلاً درصد پیشرفت توی records.json اندروید)
				if (s[i] == '.')
				{
					i++;
					while (i < s.Length && s[i] >= '0' && s[i] <= '9')
					{
						i++;
					}
				}
				if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
				{
					i++;
					if (i < s.Length && (s[i] == '+' || s[i] == '-'))
					{
						i++;
					}
					while (i < s.Length && s[i] >= '0' && s[i] <= '9')
					{
						i++;
					}
				}
				double dv;
				if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out dv) || double.IsInfinity(dv) || double.IsNaN(dv))
				{
					throw new FormatException("bad number");
				}
				return dv;
			}
			long value;
			if (!long.TryParse(s.Substring(start, i - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
			{
				throw new FormatException("number out of range");
			}
			return value;
		}
	}

	// ---------- نوشتن ----------
	public static string Write(object value)
	{
		StringBuilder sb = new StringBuilder();
		WriteValue(sb, value, 0);
		return sb.ToString();
	}

	private static bool IsScalar(object v)
	{
		return !(v is JObject) && !(v is List<object>);
	}

	private static void Indent(StringBuilder sb, int level)
	{
		sb.Append(' ', level * 2);
	}

	private static void WriteValue(StringBuilder sb, object v, int level)
	{
		if (v == null)
		{
			sb.Append("null");
		}
		else if (v is string)
		{
			WriteString(sb, (string)v);
		}
		else if (v is bool)
		{
			sb.Append((bool)v ? "true" : "false");
		}
		else if (v is long)
		{
			sb.Append(((long)v).ToString(CultureInfo.InvariantCulture));
		}
		else if (v is double)
		{
			sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture));
		}
		else if (v is int)
		{
			sb.Append(((int)v).ToString(CultureInfo.InvariantCulture));
		}
		else if (v is JObject)
		{
			WriteObject(sb, (JObject)v, level);
		}
		else if (v is List<object>)
		{
			WriteArray(sb, (List<object>)v, level);
		}
		else
		{
			throw new ArgumentException("unsupported type " + v.GetType().Name);
		}
	}

	private static void WriteArray(StringBuilder sb, List<object> list, int level)
	{
		if (list.Count == 0)
		{
			sb.Append("[]");
			return;
		}
		bool inline = true;
		foreach (object item in list)
		{
			if (!IsScalar(item))
			{
				inline = false;
				break;
			}
		}
		if (inline)
		{
			sb.Append('[');
			for (int k = 0; k < list.Count; k++)
			{
				if (k > 0)
				{
					sb.Append(", ");
				}
				WriteValue(sb, list[k], level);
			}
			sb.Append(']');
			return;
		}
		sb.Append("[\n");
		for (int k = 0; k < list.Count; k++)
		{
			Indent(sb, level + 1);
			WriteValue(sb, list[k], level + 1);
			sb.Append(k < list.Count - 1 ? ",\n" : "\n");
		}
		Indent(sb, level);
		sb.Append(']');
	}

	private static void WriteObject(StringBuilder sb, JObject obj, int level)
	{
		if (obj.Count == 0)
		{
			sb.Append("{}");
			return;
		}
		bool inline = obj.Count <= 3;
		if (inline)
		{
			foreach (string key in obj.Keys)
			{
				if (!IsScalar(obj.Get(key)))
				{
					inline = false;
					break;
				}
			}
		}
		if (inline)
		{
			sb.Append("{ ");
			for (int k = 0; k < obj.Keys.Count; k++)
			{
				if (k > 0)
				{
					sb.Append(", ");
				}
				WriteString(sb, obj.Keys[k]);
				sb.Append(": ");
				WriteValue(sb, obj.Get(obj.Keys[k]), level);
			}
			sb.Append(" }");
			return;
		}
		sb.Append("{\n");
		for (int k = 0; k < obj.Keys.Count; k++)
		{
			Indent(sb, level + 1);
			WriteString(sb, obj.Keys[k]);
			sb.Append(": ");
			WriteValue(sb, obj.Get(obj.Keys[k]), level + 1);
			sb.Append(k < obj.Keys.Count - 1 ? ",\n" : "\n");
		}
		Indent(sb, level);
		sb.Append('}');
	}

	private static void WriteString(StringBuilder sb, string s)
	{
		sb.Append('"');
		foreach (char c in s)
		{
			switch (c)
			{
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\b': sb.Append("\\b"); break;
				case '\f': sb.Append("\\f"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (c < 0x20)
					{
						sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
					}
					else
					{
						sb.Append(c);
					}
					break;
			}
		}
		sb.Append('"');
	}
}
}

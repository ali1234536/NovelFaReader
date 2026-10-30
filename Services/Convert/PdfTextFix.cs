using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EpubReader.Services
{
	// اصلاح متن فارسی/عربی استخراج‌شده از PDF:
	// خیلی از PDFها (مخصوصاً خروجی Word) حروف رو به ترتیب «ظاهری» (چپ‌به‌راست) ذخیره می‌کنن،
	// و ToUnicode هم گاهی شکل‌های نمایشی (ﺑ ﺍ ...) برمی‌گردونه. این کلاس
	// ۱) شکل‌های نمایشی رو به حروف عادی برمی‌گردونه
	// ۲) با شمردن کلمه‌های پرتکرار فارسی/عربی تشخیص می‌ده متن برعکسه یا نه و درستش می‌کنه
	internal static class PdfTextFix
	{
		private static readonly HashSet<string> Common = new HashSet<string>(StringComparer.Ordinal)
		{
			"و", "در", "از", "را", "که", "به", "با", "این", "او", "ها",
			"آن", "بر", "تا", "هم", "بود", "می", "شد", "است", "یک", "من",
			"هر", "نیز", "باشد", "شود", "برای", "همه", "ولی", "اما",
			"فی", "من", "على", "إلى", "هذا", "هذه", "الذي", "أن", "لا", "ما"
		};

		private enum Mode { None, WholeLine, PerWord }

		public static bool IsArabicScript(char c)
		{
			return (c >= '؀' && c <= 'ۿ') || (c >= 'ݐ' && c <= 'ݿ') || (c >= 'ﭐ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿');
		}

		public static string Normalize(string s)
		{
			bool need = false;
			foreach (char c in s)
			{
				if ((c >= 'ﭐ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿'))
				{
					need = true;
					break;
				}
			}
			if (!need)
			{
				return s;
			}
			try
			{
				return s.Normalize(NormalizationForm.FormKC);
			}
			catch (ArgumentException)
			{
				return s;
			}
		}

		// خطوط یک صفحه رو درست می‌کنه. ورودی و خروجی: لیست متن خط‌ها (هم‌اندازه)
		public static List<string> FixLines(List<string> lines)
		{
			List<string> norm = new List<string>(lines.Count);
			int arabic = 0;
			int letters = 0;
			foreach (string l in lines)
			{
				string n = Normalize(l);
				norm.Add(n);
				foreach (char c in n)
				{
					if (char.IsLetter(c))
					{
						letters++;
						if (IsArabicScript(c))
						{
							arabic++;
						}
					}
				}
			}
			if (letters == 0 || arabic * 100 < letters * 30)
			{
				return norm;
			}
			int none = Score(norm, Mode.None);
			int whole = Score(norm, Mode.WholeLine);
			int per = Score(norm, Mode.PerWord);
			Mode best = Mode.None;
			int bestScore = none;
			if (whole > bestScore * 1.3 + 1)
			{
				best = Mode.WholeLine;
				bestScore = whole;
			}
			if (per > bestScore * 1.3 + 1)
			{
				best = Mode.PerWord;
			}
			if (best == Mode.None)
			{
				return norm;
			}
			List<string> result = new List<string>(norm.Count);
			foreach (string n in norm)
			{
				result.Add(Apply(n, best));
			}
			return result;
		}

		private static int Score(List<string> lines, Mode mode)
		{
			int score = 0;
			foreach (string l in lines)
			{
				string t = Apply(l, mode);
				foreach (string w in t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
				{
					string word = StripMarks(w);
					if (Common.Contains(word))
					{
						score++;
					}
				}
			}
			return score;
		}

		private static string StripMarks(string w)
		{
			StringBuilder sb = new StringBuilder(w.Length);
			foreach (char c in w)
			{
				if (c >= 'ً' && c <= 'ٟ')
				{
					continue;
				}
				if (c == '‌' || c == '‍' || char.IsPunctuation(c))
				{
					continue;
				}
				sb.Append(c);
			}
			return sb.ToString();
		}

		private static string Apply(string line, Mode mode)
		{
			if (mode == Mode.None)
			{
				return line;
			}
			if (mode == Mode.PerWord)
			{
				string[] parts = line.Split(' ');
				for (int i = 0; i < parts.Length; i++)
				{
					parts[i] = ReverseRtlWord(parts[i]);
				}
				return string.Join(" ", parts);
			}
			// کل خط برعکس؛ بعد رشته‌های LTR (عدد/لاتین) دوباره برعکس می‌شن تا درست بمونن
			char[] chars = line.ToCharArray();
			Array.Reverse(chars);
			return FixLtrRuns(new string(chars));
		}

		private static string ReverseRtlWord(string w)
		{
			bool hasArabic = false;
			foreach (char c in w)
			{
				if (IsArabicScript(c))
				{
					hasArabic = true;
					break;
				}
			}
			if (!hasArabic)
			{
				return w;
			}
			char[] chars = w.ToCharArray();
			Array.Reverse(chars);
			return FixLtrRuns(new string(chars));
		}

		private static bool IsLtr(char c)
		{
			return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
		}

		private static string FixLtrRuns(string s)
		{
			StringBuilder sb = new StringBuilder(s.Length);
			int i = 0;
			while (i < s.Length)
			{
				if (IsLtr(s[i]))
				{
					int j = i;
					while (j < s.Length && (IsLtr(s[j]) || ((s[j] == '.' || s[j] == ',' || s[j] == '-' || s[j] == '/' || s[j] == ':') && j + 1 < s.Length && IsLtr(s[j + 1]) && j > i)))
					{
						j++;
					}
					char[] run = s.Substring(i, j - i).ToCharArray();
					Array.Reverse(run);
					sb.Append(run);
					i = j;
				}
				else
				{
					sb.Append(s[i]);
					i++;
				}
			}
			return sb.ToString();
		}
	}
}

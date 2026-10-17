using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EpubReader.Services
{

// یک سطر توی بخش «مجموعه‌ها» (اسم + تعداد کتاب)
public sealed class CollectionRow
{
	public string Name { get; set; }

	public int Count { get; set; }

	public string CountText
	{
		get { return Count == 0 ? Loc.T("خالی") : Loc.FN("{0} کتاب", Count, Count); }
	}

	// متن دکمه‌های هر ردیف (توی DataTemplate ترجمه‌ی خودکار نمی‌شن)
	public string AddText { get { return Loc.T("افزودن EPUB"); } }

	public string OpenText { get { return Loc.T("باز کردن"); } }

	public string RenameText { get { return Loc.T("تغییر نام"); } }

	public string DeleteText { get { return Loc.T("حذف"); } }
}

// فهرست مجموعه‌هایی که کاربر دستی ساخته. مجموعه‌ی خالی هیچ کتابی نداره که اسمش رو نگه داره،
// پس اسم‌ها اینجا ذخیره می‌شن (فایل collections.txt کنار library.json).
// مجموعه‌هایی که فقط از روی کتاب‌ها ساخته شدن هم توی لیست دیده می‌شن؛ اینجا لازم نیستن.
public static class CollectionRegistry
{
	private static string FilePath
	{
		get
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
			if (!Directory.Exists(folder))
			{
				Directory.CreateDirectory(folder);
			}
			return Path.Combine(folder, "collections.txt");
		}
	}

	public static List<string> Load()
	{
		List<string> names = new List<string>();
		try
		{
			if (File.Exists(FilePath))
			{
				foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
				{
					string t = line.Trim();
					if (t.Length > 0 && !Contains(names, t))
					{
						names.Add(t);
					}
				}
			}
		}
		catch
		{
		}
		return names;
	}

	private static bool Contains(List<string> list, string name)
	{
		foreach (string n in list)
		{
			if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static void Save(List<string> names)
	{
		try
		{
			File.WriteAllLines(FilePath, names.ToArray(), Encoding.UTF8);
		}
		catch
		{
		}
	}

	public static void Add(string name)
	{
		string t = (name ?? "").Trim();
		if (t.Length == 0)
		{
			return;
		}
		List<string> names = Load();
		if (!Contains(names, t))
		{
			names.Add(t);
			Save(names);
		}
	}

	public static void Remove(string name)
	{
		List<string> names = Load();
		int removed = names.RemoveAll((string n) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		if (removed > 0)
		{
			Save(names);
		}
	}

	public static void Rename(string oldName, string newName)
	{
		List<string> names = Load();
		names.RemoveAll((string n) => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase));
		string t = (newName ?? "").Trim();
		if (t.Length > 0 && !Contains(names, t))
		{
			names.Add(t);
		}
		Save(names);
	}
}
}

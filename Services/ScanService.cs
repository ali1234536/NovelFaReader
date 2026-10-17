using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace EpubReader.Services
{

// یک فایل EPUB که اسکن پیداش کرده (هنوز توی کتابخونه نیست)
public sealed class ScannedFile
{
	public string Path { get; set; }

	public long Size { get; set; }

	public string Name
	{
		get { return System.IO.Path.GetFileNameWithoutExtension(Path ?? ""); }
	}

	public string Folder
	{
		get
		{
			try
			{
				return System.IO.Path.GetDirectoryName(Path ?? "") ?? "";
			}
			catch
			{
				return "";
			}
		}
	}

	// متن دکمه‌های هر ردیف (توی DataTemplate ترجمه‌ی خودکار نمی‌شن)
	public string AddText { get { return Loc.T("افزودن"); } }

	public string IgnoreText { get { return Loc.T("نادیده"); } }

	public string SizeText
	{
		get
		{
			if (Size >= 1024L * 1024L)
			{
				return (Size / (1024.0 * 1024.0)).ToString("0.0") + " MB";
			}
			return Math.Max(1L, Size / 1024L) + " KB";
		}
	}
}

// گزارش پیشرفت اسکن؛ NewFiles فقط فایل‌های تازه از آخرین گزارش به بعده
public sealed class ScanProgress
{
	public int Folders;

	public int Found;

	public string CurrentFolder;

	public List<ScannedFile> NewFiles = new List<ScannedFile>();
}

// اسکن همه‌ی درایوهای کامپیوتر برای پیدا کردن فایل‌های EPUB.
// روی نخ پس‌زمینه با اولویت پایین اجرا می‌شه، پوشه‌های سیستمی و میان‌برها (junction) رو رد می‌کنه
// و روی سیستم ضعیف بین پوشه‌ها مکث کوتاه می‌کنه تا کامپیوتر کند نشه.
public static class ScanService
{
	// پوشه‌هایی که توشون کتاب شخصی پیدا نمی‌شه و اسکنشون فقط وقت تلف می‌کنه
	private static readonly HashSet<string> skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"windows", "program files", "program files (x86)", "programdata", "$recycle.bin", "system volume information",
		"recovery", "msocache", "node_modules", ".git", "temp", "tmp", "cache", "epubreaderapp", "windowsapps", "$windows.~bt", "$windows.~ws"
	};

	private const long MinSize = 2048;

	private static string DataFolder
	{
		get
		{
			string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
			if (!Directory.Exists(folder))
			{
				Directory.CreateDirectory(folder);
			}
			return folder;
		}
	}

	private static string ResultFile { get { return System.IO.Path.Combine(DataFolder, "scanned.txt"); } }

	private static string IgnoredFile { get { return System.IO.Path.Combine(DataFolder, "scan_ignored.txt"); } }

	// اسکن خودکار موقع باز شدن برنامه (پیش‌فرض خاموش چون اسکن کل دیسک سنگینه)
	public static bool AutoOnStart
	{
		get { return AppSettings.Get("ScanOnStart", "0") == "1"; }
		set { AppSettings.Set("ScanOnStart", value ? "1" : "0"); }
	}

	// تاریخ آخرین اسکن کامل (yyyy-MM-dd HH:mm) یا خالی
	public static string LastScan
	{
		get { return AppSettings.Get("ScanLast", ""); }
		set { AppSettings.Set("ScanLast", value ?? ""); }
	}

	// ---------- ذخیره و خوندن نتیجه ----------
	public static List<ScannedFile> LoadResults()
	{
		List<ScannedFile> list = new List<ScannedFile>();
		try
		{
			if (!File.Exists(ResultFile))
			{
				return list;
			}
			foreach (string line in File.ReadAllLines(ResultFile, Encoding.UTF8))
			{
				int tab = line.IndexOf('\t');
				if (tab <= 0)
				{
					continue;
				}
				long size;
				if (!long.TryParse(line.Substring(0, tab), out size))
				{
					continue;
				}
				string path = line.Substring(tab + 1);
				if (path.Length > 0)
				{
					list.Add(new ScannedFile { Path = path, Size = size });
				}
			}
		}
		catch
		{
		}
		return list;
	}

	public static void SaveResults(IEnumerable<ScannedFile> files)
	{
		try
		{
			List<string> lines = new List<string>();
			foreach (ScannedFile f in files)
			{
				lines.Add(f.Size + "\t" + f.Path);
			}
			File.WriteAllLines(ResultFile, lines.ToArray(), Encoding.UTF8);
		}
		catch
		{
		}
	}

	public static HashSet<string> LoadIgnored()
	{
		HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (File.Exists(IgnoredFile))
			{
				foreach (string line in File.ReadAllLines(IgnoredFile, Encoding.UTF8))
				{
					if (line.Length > 0)
					{
						set.Add(line);
					}
				}
			}
		}
		catch
		{
		}
		return set;
	}

	public static void Ignore(string path)
	{
		try
		{
			File.AppendAllText(IgnoredFile, path + Environment.NewLine, Encoding.UTF8);
		}
		catch
		{
		}
	}

	// ---------- اسکن ----------
	private static bool ShouldSkip(DirectoryInfo dir)
	{
		if (skipNames.Contains(dir.Name))
		{
			return true;
		}
		try
		{
			FileAttributes a = dir.Attributes;
			// میان‌بر/junction (برای جلوگیری از حلقه) و پوشه‌های سیستمی
			if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.System) != 0)
			{
				return true;
			}
		}
		catch
		{
			return true;
		}
		return false;
	}

	// همه‌ی درایوهای ثابت و قابل‌حمل رو اسکن می‌کنه. report حداکثر هر ۳۰۰ میلی‌ثانیه صدا زده می‌شه (روی نخ پس‌زمینه)
	// نتیجه: همه‌ی فایل‌های پیداشده. اگه لغو بشه، همونایی که تا اون لحظه پیدا شده برمی‌گرده.
	public static List<ScannedFile> Run(CancellationToken token, Action<ScanProgress> report)
	{
		List<ScannedFile> all = new List<ScannedFile>();
		ThreadPriority oldPriority = Thread.CurrentThread.Priority;
		try
		{
			Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
		}
		catch
		{
		}

		bool lowEnd = PerfHint.LowEnd;
		Stopwatch clock = Stopwatch.StartNew();
		long lastReport = 0;
		int folders = 0;
		List<ScannedFile> fresh = new List<ScannedFile>();
		string current = "";

		try
		{
			List<string> roots = new List<string>();
			foreach (DriveInfo d in DriveInfo.GetDrives())
			{
				try
				{
					if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
					{
						roots.Add(d.RootDirectory.FullName);
					}
				}
				catch
				{
				}
			}

			Stack<string> pending = new Stack<string>();
			for (int i = roots.Count - 1; i >= 0; i--)
			{
				pending.Push(roots[i]);
			}

			while (pending.Count > 0 && !token.IsCancellationRequested)
			{
				string path = pending.Pop();
				current = path;
				DirectoryInfo dir;
				try
				{
					dir = new DirectoryInfo(path);
				}
				catch
				{
					continue;
				}
				folders++;

				try
				{
					foreach (FileInfo f in dir.EnumerateFiles("*.epub"))
					{
						try
						{
							if (f.Length >= MinSize && string.Equals(f.Extension, ".epub", StringComparison.OrdinalIgnoreCase))
							{
								ScannedFile sf = new ScannedFile { Path = f.FullName, Size = f.Length };
								all.Add(sf);
								fresh.Add(sf);
							}
						}
						catch
						{
						}
					}
				}
				catch
				{
					// بدون دسترسی یا پوشه‌ی خراب: رد می‌شیم
				}

				try
				{
					foreach (DirectoryInfo sub in dir.EnumerateDirectories())
					{
						if (!ShouldSkip(sub))
						{
							pending.Push(sub.FullName);
						}
					}
				}
				catch
				{
				}

				if (lowEnd && (folders & 63) == 0)
				{
					// سیستم ضعیف: مکث کوتاه تا کامپیوتر برای کارهای دیگه آزاد بمونه
					Thread.Sleep(2);
				}

				long now = clock.ElapsedMilliseconds;
				if (report != null && now - lastReport >= 300)
				{
					lastReport = now;
					Flush(report, folders, all.Count, current, fresh);
				}
			}
		}
		catch
		{
		}
		finally
		{
			try
			{
				Thread.CurrentThread.Priority = oldPriority;
			}
			catch
			{
			}
		}

		if (report != null)
		{
			Flush(report, folders, all.Count, current, fresh);
		}
		return all;
	}

	private static void Flush(Action<ScanProgress> report, int folders, int found, string current, List<ScannedFile> fresh)
	{
		ScanProgress p = new ScanProgress();
		p.Folders = folders;
		p.Found = found;
		p.CurrentFolder = current;
		p.NewFiles.AddRange(fresh);
		fresh.Clear();
		report(p);
	}
}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EpubReader.Services
{

// نگهبان XP: یه فایل جدا (xpguard.dat) کنار appsettings.txt با بیشترین XP و بالاترین عنوانی که تا حالا رسیدی و یه کپی از دفترچه.
// XP توی برنامه هیچ‌وقت کم نمی‌شه، پس اگه appsettings.txt خراب/برگردونده‌شده/نیمه‌کاره باشه، XP و تم‌های بازشده از اینجا برمی‌گردن.
// همه‌ی تغییرها و افت‌های تشخیص‌داده‌شده توی xpguard.log ثبت می‌شن تا اگه چیزی عجیب شد قابل ردگیری باشه
public static class XpGuard
{
	private static readonly object gate = new object();
	private static bool loaded;
	private static int peak;
	private static int maxTitle = -1;
	private static string ledgerBackup = "";

	private static string Dir
	{
		get
		{
			string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp");
			try
			{
				Directory.CreateDirectory(d);
			}
			catch
			{
			}
			return d;
		}
	}

	private static string DataPath { get { return Path.Combine(Dir, "xpguard.dat"); } }

	private static string LogPath { get { return Path.Combine(Dir, "xpguard.log"); } }

	private static void EnsureLoaded()
	{
		lock (gate)
		{
			if (loaded)
			{
				return;
			}
			loaded = true;
			foreach (string p in new string[] { DataPath, DataPath + ".bak" })
			{
				try
				{
					if (!File.Exists(p))
					{
						continue;
					}
					foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
					{
						int i = line.IndexOf('=');
						if (i <= 0)
						{
							continue;
						}
						string k = line.Substring(0, i);
						string v = line.Substring(i + 1);
						int n;
						if (k == "peak" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > peak)
						{
							peak = n;
						}
						else if (k == "title" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > maxTitle)
						{
							maxTitle = n;
						}
						else if (k == "ledger" && v.Length > ledgerBackup.Length)
						{
							ledgerBackup = v;
						}
					}
				}
				catch
				{
				}
			}
		}
	}

	public static int Peak
	{
		get
		{
			EnsureLoaded();
			return peak;
		}
	}

	public static int MaxTitle
	{
		get
		{
			EnsureLoaded();
			return maxTitle;
		}
	}

	public static string LedgerBackup
	{
		get
		{
			EnsureLoaded();
			return ledgerBackup;
		}
	}

	private static void Persist()
	{
		try
		{
			string tmp = DataPath + ".tmp";
			string text = "peak=" + peak.ToString(CultureInfo.InvariantCulture) + "\r\n"
				+ "title=" + maxTitle.ToString(CultureInfo.InvariantCulture) + "\r\n"
				+ "ledger=" + ledgerBackup + "\r\n";
			File.WriteAllText(tmp, text, new UTF8Encoding(false));
			if (File.Exists(DataPath))
			{
				File.Replace(tmp, DataPath, DataPath + ".bak", true);
			}
			else
			{
				File.Move(tmp, DataPath);
			}
		}
		catch
		{
		}
	}

	// XP کل جدید ثبت شد (فقط بالا می‌ره)
	public static void NoteTotal(int total)
	{
		EnsureLoaded();
		lock (gate)
		{
			int title = XpRank.LevelFor(total).TitleIndex;
			bool changed = false;
			if (total > peak)
			{
				peak = total;
				changed = true;
			}
			if (title > maxTitle)
			{
				maxTitle = title;
				changed = true;
			}
			if (changed)
			{
				Persist();
			}
		}
	}

	public static void NoteLedger(string serialized)
	{
		EnsureLoaded();
		lock (gate)
		{
			if (serialized != null && serialized != ledgerBackup && serialized.Length >= ledgerBackup.Length / 2)
			{
				ledgerBackup = serialized;
				Persist();
			}
		}
	}

	public static void Log(string text)
	{
		lock (gate)
		{
			try
			{
				FileInfo fi = new FileInfo(LogPath);
				if (fi.Exists && fi.Length > 200000)
				{
					File.Delete(LogPath);
				}
				File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n", new UTF8Encoding(false));
			}
			catch
			{
			}
		}
	}
}
}

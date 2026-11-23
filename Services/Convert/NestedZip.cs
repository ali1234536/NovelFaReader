using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EpubReader.Services
{

// ZIP تو در تو (مثل اندروید): اگه آرشیو بیرونی خودش عکس نداشته باشه و داخلش ZIP/CBZ باشه (مثلاً هر فصل یه ZIP)،
// عکس‌ها به ترتیب طبیعی توی یه ZIP تخت (در کش موقت) جمع می‌شن و نمایشگر کمیک همون رو می‌خونه. فایل اصلی دست نمی‌خوره
public static class NestedZip
{
	private const int MaxDepth = 3;
	private const long MaxTotalBytes = 6L * 1024 * 1024 * 1024;

	private static bool IsArchive(string name)
	{
		if (string.IsNullOrEmpty(name) || name.EndsWith("/", StringComparison.Ordinal) || name.StartsWith("__MACOSX/", StringComparison.Ordinal))
		{
			return false;
		}
		string n = name.Substring(name.LastIndexOf('/') + 1);
		if (n.Length == 0 || n[0] == '.')
		{
			return false;
		}
		string ext = Path.GetExtension(n).ToLowerInvariant();
		return ext == ".zip" || ext == ".cbz";
	}

	// مسیر ZIPی که باید خونده بشه: خود فایل، یا نسخه‌ی تخت‌شده‌ی کش‌شده
	public static string Resolve(string path)
	{
		try
		{
			bool nested = false;
			using (ZipArchive zip = ZipFile.OpenRead(path))
			{
				bool hasImage = false;
				bool hasArchive = false;
				foreach (ZipArchiveEntry e in zip.Entries)
				{
					string n = e.FullName.Replace('\\', '/');
					if (CbzParser.IsImageEntry(n))
					{
						hasImage = true;
						break;
					}
					if (IsArchive(n))
					{
						hasArchive = true;
					}
				}
				nested = !hasImage && hasArchive;
			}
			if (!nested)
			{
				return path;
			}
			FileInfo fi = new FileInfo(path);
			string key;
			using (SHA256 sha = SHA256.Create())
			{
				byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(fi.FullName.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks));
				key = BitConverter.ToString(h, 0, 10).Replace("-", "");
			}
			string dir = Path.Combine(Path.GetTempPath(), "EpubFlatComics");
			Directory.CreateDirectory(dir);
			string flat = Path.Combine(dir, key + ".cbz");
			if (File.Exists(flat) && new FileInfo(flat).Length > 0)
			{
				return flat;
			}
			string tmp = flat + "." + Guid.NewGuid().ToString("N") + ".part";
			try
			{
				long budget = MaxTotalBytes;
				using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
				using (ZipArchive outZip = new ZipArchive(fs, ZipArchiveMode.Create))
				{
					using (ZipArchive src = ZipFile.OpenRead(path))
					{
						int count = Append(src, "", outZip, 0, ref budget);
						if (count == 0)
						{
							throw new InvalidDataException("no images");
						}
					}
				}
				if (File.Exists(flat))
				{
					File.Delete(tmp);
				}
				else
				{
					File.Move(tmp, flat);
				}
				return flat;
			}
			catch
			{
				try
				{
					if (File.Exists(tmp))
					{
						File.Delete(tmp);
					}
				}
				catch
				{
				}
				return path;
			}
		}
		catch
		{
			return path;
		}
	}

	// عکس‌های این آرشیو و آرشیوهای داخلش (به ترتیب طبیعی) با پیشوند مسیر توی خروجی می‌رن
	private static int Append(ZipArchive src, string prefix, ZipArchive dst, int depth, ref long budget)
	{
		int added = 0;
		List<string> names = new List<string>();
		Dictionary<string, ZipArchiveEntry> byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
		foreach (ZipArchiveEntry e in src.Entries)
		{
			string n = e.FullName.Replace('\\', '/');
			if (!byName.ContainsKey(n))
			{
				byName[n] = e;
				if (CbzParser.IsImageEntry(n) || (depth < MaxDepth && IsArchive(n)))
				{
					names.Add(n);
				}
			}
		}
		names.Sort(CbzParser.CompareNatural);
		int idx = 0;
		foreach (string n in names)
		{
			ZipArchiveEntry en = byName[n];
			if (CbzParser.IsImageEntry(n))
			{
				budget -= en.Length;
				if (budget < 0)
				{
					throw new InvalidDataException("too big");
				}
				ZipArchiveEntry o = dst.CreateEntry(prefix + n, CompressionLevel.NoCompression);
				using (Stream s = en.Open())
				using (Stream d = o.Open())
				{
					s.CopyTo(d);
				}
				added++;
			}
			else
			{
				idx++;
				string tmp = Path.Combine(Path.GetTempPath(), "EpubNest_" + Guid.NewGuid().ToString("N") + ".zip");
				try
				{
					using (Stream s = en.Open())
					using (FileStream f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
					{
						s.CopyTo(f);
					}
					string baseName = Path.GetFileNameWithoutExtension(n);
					string sub = prefix + idx.ToString("D4") + "_" + baseName + "/";
					using (ZipArchive inner = ZipFile.OpenRead(tmp))
					{
						added += Append(inner, sub, dst, depth + 1, ref budget);
					}
				}
				catch (InvalidDataException ex)
				{
					if (ex.Message == "too big")
					{
						throw;
					}
				}
				finally
				{
					try
					{
						File.Delete(tmp);
					}
					catch
					{
					}
				}
			}
		}
		return added;
	}
}
}

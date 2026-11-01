using System.Collections.Generic;

namespace EpubReader.Models
{

public class EpubBook
{
	public string Title { get; set; }

	public string ExtractPath { get; set; }

	public string CoverImagePath { get; set; }

	// فرمت‌های غیر EPUB (بدون تبدیل): نوع، مسیر فایل اصلی و (برای کمیک) صفحه‌ها
	public string NativeKind { get; set; }

	public string NativePath { get; set; }

	public List<string> NativePages { get; set; }

	public List<KeyValuePair<string, int>> NativeStarts { get; set; }

	public List<ChapterItem> Chapters { get; set; } = new List<ChapterItem>();
}
}

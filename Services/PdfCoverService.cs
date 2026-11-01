using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace EpubReader.Services
{

// جلد PDF بدون اینکه کاربر کتاب رو باز کنه: یه پنجره‌ی نامرئیِ کوچیک با همون نمایشگر PDF صفحه‌ی اول رو می‌کشه و ذخیره می‌کنه
public static class PdfCoverService
{
	public static string CoverPathFor(string pdfPath)
	{
		string covers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpubReaderApp", "Covers");
		Directory.CreateDirectory(covers);
		string key;
		using (MD5 md5 = MD5.Create())
		{
			key = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes((pdfPath ?? "").ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
		}
		return Path.Combine(covers, "cover_" + key + ".jpg");
	}

	// مسیر جلد ذخیره‌شده، یا null اگه نشد (رمزدار، خراب، زمان تموم شد)
	public static async Task<string> MakeCoverAsync(string pdfPath)
	{
		TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
		Window w = null;
		Microsoft.Web.WebView2.Wpf.WebView2 wv = null;
		try
		{
			string dest = CoverPathFor(pdfPath);
			w = new Window();
			w.Width = 320;
			w.Height = 420;
			w.WindowStyle = WindowStyle.None;
			w.ShowInTaskbar = false;
			w.ShowActivated = false;
			w.ResizeMode = ResizeMode.NoResize;
			w.WindowStartupLocation = WindowStartupLocation.Manual;
			w.Left = -32000;
			w.Top = -32000;
			wv = new Microsoft.Web.WebView2.Wpf.WebView2();
			w.Content = wv;
			w.Show();
			CoreWebView2Environment env = await MainWindow.GetWebViewEnvironmentAsync();
			await wv.EnsureCoreWebView2Async(env);
			CoreWebView2 core = wv.CoreWebView2;
			core.WebMessageReceived += (object s, CoreWebView2WebMessageReceivedEventArgs e) =>
			{
				string m;
				try
				{
					m = e.TryGetWebMessageAsString();
				}
				catch
				{
					return;
				}
				if (m == null)
				{
					return;
				}
				if (m.StartsWith("cv|", StringComparison.Ordinal))
				{
					try
					{
						byte[] jpg = Convert.FromBase64String(m.Substring(3));
						if (jpg.Length >= 200)
						{
							File.WriteAllBytes(dest, jpg);
							tcs.TrySetResult(dest);
							return;
						}
					}
					catch
					{
					}
					tcs.TrySetResult(null);
				}
				else if (m == "cverr")
				{
					tcs.TrySetResult(null);
				}
			};
			PdfViewerHost.Attach(core, pdfPath);
			core.Navigate(PdfViewerHost.BuildUrl(pdfPath, "#1c1c1e", "#e8e8e8", true, 1, 0, true) + "&coveronly=1");
			await Task.WhenAny(tcs.Task, Task.Delay(25000));
			return tcs.Task.IsCompleted ? tcs.Task.Result : null;
		}
		catch (Exception ex)
		{
			LibraryService.LogOpen("[Cover] failed " + pdfPath + " : " + ex.Message);
			return null;
		}
		finally
		{
			try
			{
				if (wv != null)
				{
					wv.Dispose();
				}
			}
			catch
			{
			}
			try
			{
				if (w != null)
				{
					w.Close();
				}
			}
			catch
			{
			}
		}
	}
}
}

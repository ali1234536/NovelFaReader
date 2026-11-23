using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EpubReader.Services;

namespace EpubReader
{

// راهنمای جامع برنامه: آشنایی با کتابخانه، بخش پیشرفته و هوشمند، اسکرول خودکار، کرونومتر و پومودورو
public sealed class GuideWindow : Window
{
	private static readonly string[][] Pages = new string[][]
	{
		new string[]
		{
			"به ناول‌فا خوش اومدی",
			"کتاب‌خوان هوشمند فارسی با پشتیبانی از EPUB، PDF، کمیک (CBZ/ZIP) و اسناد (DOCX/TXT). همه‌ی کتاب‌ها، پیشرفت مطالعه، تم‌ها و ابزارهای هوشمندت اینجاست."
		},
		new string[]
		{
			"کتابخانه و سوئیچر هوشمند رابط کاربری",
			"فایل کتاب‌ها را بکش و در کتابخانه رها کن یا با دکمه‌ی افزودن اضافه کن.\n\nدر نوار بالای کتابخانه و صفحه‌ی مطالعه، با سوئیچر هوشمند می‌تونی فوراً بین حالت‌های «استاندارد»، «فشرده / تمرکز (ذن)» و «هوشمند / پیشرفته» جابه‌جا بشی."
		},
		new string[]
		{
			"بخش پیشرفته و بهینه‌سازی سرعت",
			"از منوی کناری وارد «✦ بخش پیشرفته» شو تا به ۴ بخش کامل دسترسی داشته باشی:\n• داشبورد هوشمند: پیشنهاد هوشمند کتاب بعدی و نمودار هدف روزانه\n• عملکرد و روان‌سازی: انتخاب حالت «توربو» برای نهایت روانی و دکمه‌ی پاکسازی کش و حافظه."
		},
		new string[]
		{
			"اسکرول خودکار برای تمام فرمت‌ها",
			"در صفحه‌ی مطالعه (برای EPUB، PDF، کمیک و اسناد) روی «▶ اسکرول خودکار» یا چرخ‌دنده‌ی کنارش بزن:\n• حالت زمان‌دار: مثلاً تنظیم کن هر ۲ ثانیه صفحه خودکار اسکرول بخوره (با دکمه‌های سریع ۱ تا ۱۰ ثانیه).\n• حالت پیوسته: حرکت نرم و مداوم با سرعت دلخواه.\n• عبور خودکار به بخش/صفحه‌ی بعدی و توقف موقت هنگام لمس یا اسکرول دستی."
		},
		new string[]
		{
			"کرونومتر مطالعه، پومودورو و محافظ چشم",
			"در ابزارهای هوشمند صفحه‌ی مطالعه:\n• کرونومتر مطالعه: زمان دقیق مطالعه‌ات را به‌صورت زنده اندازه‌گیری کن (شروع، مکث و صفر کردن).\n• تایمر پومودورو: جلسات تمرکز (پیش‌فرض ۲۵ دقیقه) با اعلان استراحت و یادآور قانون ۲۰-۲۰-۲۰.\n• فیلتر نور آبی (محافظ چشم)، تنظیم روشنایی و خط‌کش تمرکز هوشمند برای خواندن طولانی بدون خستگی چشم."
		},
		new string[]
		{
			"رنک XP، پشتیبان‌گیری و تم‌های خاص",
			"با خواندن هر بخش XP می‌گیری و رتبه‌ات بالا می‌رود. از بخش پشتیبان‌گیری نسخه ذخیره کن تا کتاب‌ها و پیشرفتت همیشه امن بماند، و از تم‌های خاص با رنگ دلخواه لذت ببر."
		}
	};

	private int index;
	private readonly TextBlock title = new TextBlock();
	private readonly TextBlock body = new TextBlock();
	private readonly TextBlock counter = new TextBlock();
	private readonly Button prev = new Button();
	private readonly Button next = new Button();
	private readonly Button skip = new Button();

	private GuideWindow()
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		SizeToContent = SizeToContent.Height;
		Width = 490;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		FlowDirection = Loc.Flow;
		Title = Loc.T("راهنمای برنامه");

		Border frame = new Border();
		frame.Margin = new Thickness(16.0);
		frame.CornerRadius = new CornerRadius(18.0);
		frame.BorderThickness = new Thickness(1.0);
		frame.SetResourceReference(Border.BackgroundProperty, "BgDark");
		frame.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
		DropShadowEffect shadow = new DropShadowEffect();
		shadow.BlurRadius = 24.0;
		shadow.ShadowDepth = 4.0;
		shadow.Opacity = 0.55;
		try
		{
			shadow.Color = (Color)Application.Current.FindResource("ShadowColor");
		}
		catch
		{
		}
		frame.Effect = shadow;
		frame.MouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			try
			{
				DragMove();
			}
			catch
			{
			}
		};

		StackPanel panel = new StackPanel();
		panel.Margin = new Thickness(28.0, 26.0, 28.0, 22.0);

		title.FontSize = 18.5;
		title.FontWeight = FontWeights.Bold;
		title.TextWrapping = TextWrapping.Wrap;
		title.TextAlignment = TextAlignment.Center;
		title.Margin = new Thickness(0.0, 0.0, 0.0, 14.0);
		title.SetResourceReference(TextBlock.ForegroundProperty, "AccentColor");
		panel.Children.Add(title);

		body.FontSize = 13.0;
		body.TextWrapping = TextWrapping.Wrap;
		body.TextAlignment = TextAlignment.Right;
		body.LineHeight = 23.0;
		body.MinHeight = 128.0;
		body.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
		panel.Children.Add(body);

		counter.FontSize = 11.5;
		counter.HorizontalAlignment = HorizontalAlignment.Center;
		counter.Margin = new Thickness(0.0, 14.0, 0.0, 14.0);
		counter.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
		panel.Children.Add(counter);

		StackPanel buttons = new StackPanel();
		buttons.Orientation = Orientation.Horizontal;
		buttons.HorizontalAlignment = HorizontalAlignment.Center;

		skip.Content = Loc.T("بستن");
		skip.Width = 90.0;
		skip.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		skip.Padding = new Thickness(10.0, 9.0, 10.0, 9.0);
		skip.Style = (Style)Application.Current.FindResource("SoftButton");
		skip.Click += (object s, RoutedEventArgs e) => { Close(); };
		buttons.Children.Add(skip);

		prev.Content = Loc.T("قبلی");
		prev.Width = 90.0;
		prev.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		prev.Padding = new Thickness(10.0, 9.0, 10.0, 9.0);
		prev.Style = (Style)Application.Current.FindResource("SoftButton");
		prev.Click += (object s, RoutedEventArgs e) => { Go(index - 1); };
		buttons.Children.Add(prev);

		next.Width = 115.0;
		next.Style = (Style)Application.Current.FindResource("ModernButton");
		next.IsDefault = true;
		next.Click += (object s, RoutedEventArgs e) =>
		{
			if (index >= Pages.Length - 1)
			{
				Close();
			}
			else
			{
				Go(index + 1);
			}
		};
		buttons.Children.Add(next);

		panel.Children.Add(buttons);
		frame.Child = panel;
		Content = frame;
		Go(0);
	}

	private void Go(int i)
	{
		index = Math.Max(0, Math.Min(Pages.Length - 1, i));
		title.Text = Loc.T(Pages[index][0]);
		body.Text = Loc.T(Pages[index][1]);
		counter.Text = (index + 1) + " / " + Pages.Length;
		prev.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
		skip.Visibility = index < Pages.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
		next.Content = Loc.T(index >= Pages.Length - 1 ? "شروع" : "بعدی");
	}

	public static void ShowFor(Window owner)
	{
		GuideWindow w = new GuideWindow();
		if (owner != null && owner.IsVisible)
		{
			w.Owner = owner;
		}
		else
		{
			w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		}
		w.ShowDialog();
		AppSettings.Set("GuideSeen", "1");
	}

	public static bool Seen
	{
		get { return AppSettings.Get("GuideSeen", "0") == "1"; }
	}
}
}

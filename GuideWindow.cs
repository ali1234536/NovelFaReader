using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EpubReader.Services;

namespace EpubReader
{

// راهنمای ۵ صفحه‌ای اول‌اجرا (هم‌ارز اندروید): خوش‌آمد، افزودن کتاب، حالت‌های مطالعه، رنک XP، بکاپ و تم
public sealed class GuideWindow : Window
{
	private static readonly string[][] Pages = new string[][]
	{
		new string[] { "به ناول‌فا خوش اومدی", "کتاب‌خوان EPUB فارسی؛ کتاب‌هات، پیشرفتت و تم‌های شخصیتت همه‌ی اینجاست." },
		new string[] { "کتاب اضافه کن", "فایل‌های EPUB رو بکش و توی کتابخونه رها کن، یا از دکمه‌ی افزودن انتخابشون کن. می‌تونی مجموعه‌ی دلخواه هم بسازی." },
		new string[] { "حالت‌های مطالعه", "حالت پیوسته برای اسکرول و حالت تک‌صفحه‌ای برای ورق زدن. فونت، اندازه و فاصله‌ی خطوط برای هر کتاب جدا ذخیره می‌شه." },
		new string[] { "بخون و رنک بگیر", "با خوندن هر فصل XP می‌گیری؛ ۱۲ عنوان و ۵ ستاره که با پیشرفتت پر می‌شن. روی نشان رتبه کلیک کن تا همه‌ی رنک‌ها رو ببینی." },
		new string[] { "پشتیبان و تم", "از پشتیبان بگیر و برگردون تا رنک و پیشرفتت از بین نره، و یه تم شخصیت انتخاب کن؛ ذرات متحرک فقط با تم‌ها نمایش داده می‌شن." }
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
		Width = 420;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		FlowDirection = Loc.Flow;
		Title = Loc.T("راهنمای برنامه");

		Border frame = new Border();
		frame.Margin = new Thickness(16.0);
		frame.CornerRadius = new CornerRadius(16.0);
		frame.BorderThickness = new Thickness(1.0);
		frame.SetResourceReference(Border.BackgroundProperty, "BgDark");
		frame.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
		DropShadowEffect shadow = new DropShadowEffect();
		shadow.BlurRadius = 24.0;
		shadow.ShadowDepth = 4.0;
		shadow.Opacity = 0.6;
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

		title.FontSize = 19.0;
		title.FontWeight = FontWeights.Bold;
		title.TextWrapping = TextWrapping.Wrap;
		title.TextAlignment = TextAlignment.Center;
		title.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
		title.SetResourceReference(TextBlock.ForegroundProperty, "AccentColor");
		panel.Children.Add(title);

		body.FontSize = 13.0;
		body.TextWrapping = TextWrapping.Wrap;
		body.TextAlignment = TextAlignment.Center;
		body.LineHeight = 22.0;
		body.MinHeight = 90.0;
		body.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
		panel.Children.Add(body);

		counter.FontSize = 11.0;
		counter.HorizontalAlignment = HorizontalAlignment.Center;
		counter.Margin = new Thickness(0.0, 10.0, 0.0, 14.0);
		counter.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
		panel.Children.Add(counter);

		StackPanel buttons = new StackPanel();
		buttons.Orientation = Orientation.Horizontal;
		buttons.HorizontalAlignment = HorizontalAlignment.Center;

		skip.Content = Loc.T("رد شدن");
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

		next.Width = 110.0;
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

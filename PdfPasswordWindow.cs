using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EpubReader.Services;

namespace EpubReader
{

// پنجره‌ی گرفتن رمز یک فایل PDF رمزدار. با ظاهر بقیه‌ی پنجره‌های برنامه
public sealed class PdfPasswordWindow : Window
{
	private readonly PasswordBox box = new PasswordBox();
	private string result;

	private PdfPasswordWindow(string fileName, bool wrong)
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		SizeToContent = SizeToContent.Height;
		Width = 400;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		FlowDirection = Loc.Flow;
		Title = Loc.T("رمز PDF");

		Border frame = new Border();
		frame.Margin = new Thickness(16.0);
		frame.CornerRadius = new CornerRadius(14.0);
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
		panel.Margin = new Thickness(22.0);

		TextBlock heading = new TextBlock();
		heading.Text = Loc.T("این PDF رمز داره");
		heading.FontSize = 15.0;
		heading.FontWeight = FontWeights.Bold;
		heading.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		heading.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
		panel.Children.Add(heading);

		TextBlock name = new TextBlock();
		name.Text = fileName;
		name.FontSize = 11.5;
		name.TextTrimming = TextTrimming.CharacterEllipsis;
		name.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
		name.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
		panel.Children.Add(name);

		box.FontSize = 13.0;
		box.Padding = new Thickness(10.0, 8.0, 10.0, 8.0);
		box.BorderThickness = new Thickness(1.0);
		box.SetResourceReference(Control.BackgroundProperty, "BgCard");
		box.SetResourceReference(Control.BorderBrushProperty, "BorderColor");
		box.SetResourceReference(Control.ForegroundProperty, "TextColor");
		panel.Children.Add(box);

		TextBlock error = new TextBlock();
		error.FontSize = 11.5;
		error.MinHeight = 22.0;
		error.Margin = new Thickness(0.0, 6.0, 0.0, 10.0);
		error.Text = wrong ? Loc.T("رمز اشتباهه. دوباره امتحان کن.") : "";
		error.SetResourceReference(TextBlock.ForegroundProperty, "DangerColor");
		panel.Children.Add(error);

		StackPanel buttons = new StackPanel();
		buttons.Orientation = Orientation.Horizontal;
		buttons.HorizontalAlignment = HorizontalAlignment.Center;

		Button ok = new Button();
		ok.Content = Loc.T("تایید");
		ok.Width = 110.0;
		ok.Margin = new Thickness(0.0, 0.0, 10.0, 0.0);
		ok.IsDefault = true;
		ok.Style = (Style)Application.Current.FindResource("ModernButton");
		ok.Click += (object s, RoutedEventArgs e) =>
		{
			result = box.Password;
			DialogResult = true;
		};
		buttons.Children.Add(ok);

		Button cancel = new Button();
		cancel.Content = Loc.T("انصراف");
		cancel.Width = 110.0;
		cancel.Padding = new Thickness(16.0, 10.0, 16.0, 10.0);
		cancel.IsCancel = true;
		cancel.Style = (Style)Application.Current.FindResource("SoftButton");
		cancel.Click += (object s, RoutedEventArgs e) => { DialogResult = false; };
		buttons.Children.Add(cancel);

		panel.Children.Add(buttons);
		frame.Child = panel;
		Content = frame;
		Loaded += (object s, RoutedEventArgs e) => { box.Focus(); };
	}

	// رمز واردشده، یا null اگه کاربر انصراف داد. از هر نخی صدا زده بشه به نخ رابط کاربری می‌ره
	public static string Ask(string fileName, bool wrong)
	{
		Application app = Application.Current;
		if (app == null)
		{
			return null;
		}
		return (string)app.Dispatcher.Invoke(new Func<string>(() =>
		{
			PdfPasswordWindow w = new PdfPasswordWindow(fileName, wrong);
			Window owner = null;
			foreach (Window x in app.Windows)
			{
				if (x.IsActive && x.IsVisible && x != w)
				{
					owner = x;
					break;
				}
			}
			if (owner != null)
			{
				w.Owner = owner;
			}
			else
			{
				w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
			}
			return w.ShowDialog() == true ? w.result : null;
		}));
	}
}
}

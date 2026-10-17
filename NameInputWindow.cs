using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EpubReader.Services;

namespace EpubReader
{

// پنجره‌ی کوچک گرفتن یک اسم (برای ساخت یا تغییر نام مجموعه). با ظاهر بقیه‌ی پنجره‌های برنامه.
// validator پیام خطا برمی‌گردونه یا null اگه اسم قبوله؛ تا اسم درست نشه پنجره بسته نمی‌شه.
public sealed class NameInputWindow : Window
{
	private readonly TextBox box = new TextBox();
	private readonly TextBlock error = new TextBlock();
	private readonly Func<string, string> validator;
	private string result;

	private NameInputWindow(string title, string prompt, string initial, Func<string, string> validator)
	{
		this.validator = validator;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		SizeToContent = SizeToContent.Height;
		Width = 400;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		FlowDirection = Loc.Flow;
		Title = Loc.T(title);

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
		heading.Text = Loc.T(title);
		heading.FontSize = 15.0;
		heading.FontWeight = FontWeights.Bold;
		heading.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
		heading.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
		panel.Children.Add(heading);

		TextBlock label = new TextBlock();
		label.Text = Loc.T(prompt);
		label.FontSize = 11.5;
		label.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		label.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
		panel.Children.Add(label);

		box.Text = initial ?? "";
		box.FontSize = 13.0;
		box.Padding = new Thickness(10.0, 8.0, 10.0, 8.0);
		box.BorderThickness = new Thickness(1.0);
		box.MaxLength = 60;
		box.SetResourceReference(Control.BackgroundProperty, "BgCard");
		box.SetResourceReference(Control.BorderBrushProperty, "BorderColor");
		box.SetResourceReference(Control.ForegroundProperty, "TextColor");
		box.SetResourceReference(TextBox.CaretBrushProperty, "TextColor");
		box.TextChanged += (object s, TextChangedEventArgs e) => { error.Text = ""; };
		panel.Children.Add(box);

		error.FontSize = 11.5;
		error.MinHeight = 22.0;
		error.Margin = new Thickness(0.0, 6.0, 0.0, 10.0);
		error.TextWrapping = TextWrapping.Wrap;
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
		ok.Click += (object s, RoutedEventArgs e) => { Accept(); };
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

		Loaded += (object s, RoutedEventArgs e) =>
		{
			box.Focus();
			box.SelectAll();
		};
	}

	private void Accept()
	{
		string name = (box.Text ?? "").Trim();
		string problem = (validator != null) ? validator(name) : null;
		if (problem != null)
		{
			error.Text = problem;
			box.Focus();
			return;
		}
		result = name;
		DialogResult = true;
	}

	// اسم واردشده، یا null اگه کاربر انصراف داد
	public static string Ask(Window owner, string title, string prompt, string initial, Func<string, string> validator)
	{
		NameInputWindow w = new NameInputWindow(title, prompt, initial, validator);
		if (owner != null && owner.IsVisible)
		{
			w.Owner = owner;
		}
		else
		{
			w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		}
		return (w.ShowDialog() == true) ? w.result : null;
	}
}
}

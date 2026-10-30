using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EpubReader.Services;

namespace EpubReader
{

// «مشاهده‌ی همه‌ی رنک‌ها»: ۱۲ عنوان XP با حداقل XP، وضعیت (رسیده / فعلی / قفل)، تم‌هایی که هر عنوان باز می‌کنه و نشانه‌های ابدیت
public sealed class RanksWindow : Window
{
	private RanksWindow()
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		Width = 460;
		Height = 620;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		FlowDirection = Loc.Flow;
		Title = Loc.T("رنک‌ها");

		int xp = XpStore.TotalXp;
		XpLevel now = XpRank.LevelFor(xp);

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

		DockPanel root = new DockPanel();
		root.Margin = new Thickness(20.0);

		StackPanel header = new StackPanel();
		DockPanel.SetDock(header, Dock.Top);
		header.Children.Add(MakeText(Loc.T("رنک‌ها"), 16.0, FontWeights.Bold, "TextColor", new Thickness(0.0, 0.0, 0.0, 6.0)));
		header.Children.Add(MakeText(now.DisplayName, 13.0, FontWeights.SemiBold, "AccentColor", new Thickness(0.0, 0.0, 0.0, 2.0)));
		string sub = Loc.F("{0} XP", xp);
		if (now.XpToNext > 0)
		{
			sub += " · " + Loc.F("{0} XP تا مرحله‌ی بعد", now.XpToNext);
		}
		header.Children.Add(MakeText(sub, 11.5, FontWeights.Normal, "TextMuted", new Thickness(0.0, 0.0, 0.0, 8.0)));
		ProgressBar bar = new ProgressBar();
		bar.Height = 6.0;
		bar.Minimum = 0.0;
		bar.Maximum = 1.0;
		bar.Value = now.Progress;
		bar.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
		bar.SetResourceReference(Control.ForegroundProperty, "AccentColor");
		bar.SetResourceReference(Control.BackgroundProperty, "BgCard");
		bar.BorderThickness = new Thickness(0.0);
		header.Children.Add(bar);
		root.Children.Add(header);

		Button close = new Button();
		close.Content = Loc.T("بستن");
		close.Width = 120.0;
		close.Margin = new Thickness(0.0, 12.0, 0.0, 0.0);
		close.HorizontalAlignment = HorizontalAlignment.Center;
		close.IsCancel = true;
		close.IsDefault = true;
		close.Style = (Style)Application.Current.FindResource("ModernButton");
		close.Click += (object s, RoutedEventArgs e) => { Close(); };
		DockPanel.SetDock(close, Dock.Bottom);
		root.Children.Add(close);

		StackPanel list = new StackPanel();
		foreach (XpTitle t in XpRank.Titles)
		{
			list.Children.Add(MakeRow(t, now));
		}
		list.Children.Add(MakeText(Loc.T("نشانه‌های ابدیت"), 13.0, FontWeights.Bold, "TextColor", new Thickness(0.0, 14.0, 0.0, 4.0)));
		list.Children.Add(MakeText(Loc.F("بعد از {0} XP، به ازای هر {1} XP یک نشانه‌ی ابدیت می‌گیری.", XpRank.CapXp, XpRank.MarkStepXp), 11.5, FontWeights.Normal, "TextMuted", new Thickness(0.0, 0.0, 0.0, 6.0)));
		for (int i = 1; i <= XpRank.EternityMarkNames.Length; i++)
		{
			string line = "✦" + i + "  " + XpRank.EternityMarkName(i);
			bool reached = now.EternityMarks >= i;
			list.Children.Add(MakeText(line, 12.0, reached ? FontWeights.SemiBold : FontWeights.Normal, reached ? "AccentColor" : "TextMuted", new Thickness(4.0, 1.0, 0.0, 1.0)));
		}

		ScrollViewer sv = new ScrollViewer();
		sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		sv.Content = list;
		root.Children.Add(sv);

		frame.Child = root;
		Content = frame;
	}

	private static TextBlock MakeText(string text, double size, FontWeight weight, string brushKey, Thickness margin)
	{
		TextBlock tb = new TextBlock();
		tb.Text = text;
		tb.FontSize = size;
		tb.FontWeight = weight;
		tb.Margin = margin;
		tb.TextWrapping = TextWrapping.Wrap;
		tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
		return tb;
	}

	private static UIElement MakeRow(XpTitle t, XpLevel now)
	{
		bool reached = now.TitleIndex >= t.Index;
		bool current = now.TitleIndex == t.Index;
		Border row = new Border();
		row.CornerRadius = new CornerRadius(10.0);
		row.Padding = new Thickness(12.0, 8.0, 12.0, 8.0);
		row.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		row.BorderThickness = new Thickness(current ? 2.0 : 1.0);
		row.SetResourceReference(Border.BackgroundProperty, "BgCard");
		row.SetResourceReference(Border.BorderBrushProperty, current ? "AccentColor" : "BorderColor");

		StackPanel sp = new StackPanel();
		DockPanel top = new DockPanel();
		TextBlock xpText = MakeText(Loc.F("{0} XP", t.MinXp), 11.5, FontWeights.Normal, "TextMuted", new Thickness(8.0, 0.0, 0.0, 0.0));
		DockPanel.SetDock(xpText, Dock.Right);
		top.Children.Add(xpText);
		string mark = reached ? (current ? "▶ " : "✓ ") : "🔒 ";
		top.Children.Add(MakeText(mark + t.Name, 13.0, current ? FontWeights.Bold : FontWeights.SemiBold, reached ? "TextColor" : "TextMuted", new Thickness(0.0)));
		sp.Children.Add(top);

		List<string> themes = new List<string>();
		foreach (CharacterTheme th in CharacterThemes.All)
		{
			if (XpRank.XpTitleFor(th) == t.Index && t.Index > 0)
			{
				themes.Add(Loc.Q(th.Name));
			}
		}
		if (themes.Count > 0)
		{
			sp.Children.Add(MakeText(Loc.F("تم {0} رو باز می‌کنه", string.Join(Loc.ListSeparator, themes.ToArray())), 11.0, FontWeights.Normal, "AccentColor", new Thickness(0.0, 3.0, 0.0, 0.0)));
		}
		row.Child = sp;
		return row;
	}

	public static void ShowFor(Window owner)
	{
		RanksWindow w = new RanksWindow();
		if (owner != null && owner.IsVisible)
		{
			w.Owner = owner;
		}
		else
		{
			w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		}
		w.ShowDialog();
	}
}
}

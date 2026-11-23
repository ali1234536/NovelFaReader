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

// «نقل‌قول‌ها»: لیست متن‌هایی که از کتاب ذخیره کردی (کپی، ترجمه، حذف)
public sealed class QuotesWindow : Window
{
	private readonly string bookId;
	private readonly StackPanel list = new StackPanel();
	private List<QuoteItem> items;

	private QuotesWindow(string bookId, string bookTitle)
	{
		this.bookId = bookId;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		Width = 500;
		Height = 600;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		FlowDirection = Loc.Flow;
		Title = Loc.T("نقل‌قول‌ها");

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
		header.Children.Add(Text(Loc.T("نقل‌قول‌ها"), 16.0, FontWeights.Bold, "TextColor", new Thickness(0.0, 0.0, 0.0, 4.0)));
		header.Children.Add(Text(bookTitle ?? "", 12.0, FontWeights.Normal, "TextMuted", new Thickness(0.0, 0.0, 0.0, 10.0)));
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

		ScrollViewer sv = new ScrollViewer();
		sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		sv.Content = list;
		root.Children.Add(sv);

		frame.Child = root;
		Content = frame;
		items = QuoteStore.Load(bookId);
		Rebuild();
	}

	private void Rebuild()
	{
		list.Children.Clear();
		if (items.Count == 0)
		{
			list.Children.Add(Text(Loc.T("هنوز نقل‌قولی ذخیره نکردی. متن رو انتخاب کن و «نقل‌قول» رو بزن."), 12.5, FontWeights.Normal, "TextMuted", new Thickness(0.0, 20.0, 0.0, 0.0)));
			return;
		}
		for (int i = items.Count - 1; i >= 0; i--)
		{
			list.Children.Add(MakeCard(items[i]));
		}
	}

	private UIElement MakeCard(QuoteItem q)
	{
		Border card = new Border();
		card.CornerRadius = new CornerRadius(10.0);
		card.Padding = new Thickness(12.0, 10.0, 12.0, 8.0);
		card.Margin = new Thickness(0.0, 0.0, 0.0, 8.0);
		card.BorderThickness = new Thickness(1.0);
		card.SetResourceReference(Border.BackgroundProperty, "BgCard");
		card.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
		StackPanel sp = new StackPanel();
		TextBlock body = Text(q.Text, 13.0, FontWeights.Normal, "TextColor", new Thickness(0.0, 0.0, 0.0, 6.0));
		body.TextWrapping = TextWrapping.Wrap;
		sp.Children.Add(body);
		string meta = (string.IsNullOrEmpty(q.Chapter) ? "" : q.Chapter + " · ") + q.Date;
		sp.Children.Add(Text(meta, 11.0, FontWeights.Normal, "AccentColor", new Thickness(0.0, 0.0, 0.0, 4.0)));
		StackPanel row = new StackPanel();
		row.Orientation = Orientation.Horizontal;
		row.Children.Add(Link(Loc.T("کپی"), () => { try { Clipboard.SetText(q.Text); } catch { } }));
		row.Children.Add(Link(Loc.T("ترجمه"), () => { ReaderSelect.Translate(q.Text); }));
		row.Children.Add(Link(Loc.T("حذف"), () =>
		{
			items.Remove(q);
			QuoteStore.Save(bookId, items);
			Rebuild();
		}));
		sp.Children.Add(row);
		card.Child = sp;
		return card;
	}

	private static TextBlock Link(string label, Action act)
	{
		TextBlock tb = Text(label, 11.5, FontWeights.SemiBold, "TextMuted", new Thickness(0.0, 0.0, 14.0, 0.0));
		tb.Cursor = Cursors.Hand;
		tb.MouseEnter += (object s, MouseEventArgs e) => { tb.SetResourceReference(TextBlock.ForegroundProperty, "AccentColor"); };
		tb.MouseLeave += (object s, MouseEventArgs e) => { tb.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"); };
		tb.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) => { act(); };
		return tb;
	}

	private static TextBlock Text(string text, double size, FontWeight weight, string brushKey, Thickness margin)
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

	public static void ShowFor(Window owner, string bookId, string bookTitle)
	{
		QuotesWindow w = new QuotesWindow(bookId, bookTitle);
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

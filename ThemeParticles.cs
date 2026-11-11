using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using EpubReader.Services;

namespace EpubReader
{

// لایه‌ی ذرات متحرک تم شخصیت: جرقه‌های سوبارو، گلبرگ رام، درخشش رم، پروانه‌ی بیاتریس، برف امیلیا.
// نامرئی برای ماوس، سبک (حدود ۲۰ تا شکل کوچک با ۳۰ فریم) و با ThemeManager.Applied خودش به‌روز می‌شه.
public sealed class ThemeParticleLayer : Canvas
{
	private sealed class Particle
	{
		public FrameworkElement Element;
		public TranslateTransform Fall;
		public TranslateTransform Sway;
		public RotateTransform Rotate;
		public ScaleTransform Scale;
	}

	private static readonly Random rng = new Random();

	private readonly List<Particle> particles = new List<Particle>();
	private string builtKey = "";
	private DispatcherTimer resizeTimer;
	private Window host;

	// کاربر می‌تونه از پنجره‌ی تم‌ها خاموشش کنه
	public static bool Enabled
	{
		get { return AppSettings.Get("ThemeParticles", "0") == "1"; }
		set { AppSettings.Set("ThemeParticles", value ? "1" : "0"); }
	}

	public ThemeParticleLayer()
	{
		IsHitTestVisible = false;
		ClipToBounds = true;
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		SizeChanged += OnSizeChanged;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		ThemeManager.Applied -= OnThemeApplied;
		ThemeManager.Applied += OnThemeApplied;
		DetachHost();
		host = Window.GetWindow(this);
		if (host != null)
		{
			// وقتی پنجره فعال نیست یا کوچک شده، انیمیشن‌ها کاملاً متوقف می‌شن
			host.Activated += OnHostStateChanged;
			host.Deactivated += OnHostStateChanged;
			host.StateChanged += OnHostStateChanged;
		}
		Refresh(true);
	}

	private void DetachHost()
	{
		if (host != null)
		{
			host.Activated -= OnHostStateChanged;
			host.Deactivated -= OnHostStateChanged;
			host.StateChanged -= OnHostStateChanged;
			host = null;
		}
	}

	private void OnHostStateChanged(object sender, EventArgs e)
	{
		Refresh(false);
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		DetachHost();
		ThemeManager.Applied -= OnThemeApplied;
		Stop();
		builtKey = "";
	}

	private void OnThemeApplied(object sender, EventArgs e)
	{
		Refresh(false);
	}

	private void OnSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (resizeTimer == null)
		{
			resizeTimer = new DispatcherTimer();
			resizeTimer.Interval = TimeSpan.FromMilliseconds(400);
			resizeTimer.Tick += (object s, EventArgs a) =>
			{
				resizeTimer.Stop();
				Refresh(true);
			};
		}
		resizeTimer.Stop();
		resizeTimer.Start();
	}

	public void Refresh(bool force)
	{
		CharacterTheme ch = ThemeManager.AppliedCharacter;
		bool hostActive = (host == null) || (host.IsActive && host.WindowState != WindowState.Minimized);
		bool on = Enabled && ch != null && IsLoaded && hostActive && ActualWidth > 80.0 && ActualHeight > 80.0;
		string key = on ? (ch.Id + (ThemeManager.AppliedLight ? ".l" : ".d")) : "";
		if (!force && key == builtKey)
		{
			return;
		}
		Stop();
		builtKey = key;
		if (on)
		{
			Build(ch, ThemeManager.AppliedLight);
		}
	}

	private void Stop()
	{
		foreach (Particle p in particles)
		{
			if (p.Fall != null) { p.Fall.BeginAnimation(TranslateTransform.YProperty, null); }
			if (p.Sway != null) { p.Sway.BeginAnimation(TranslateTransform.XProperty, null); }
			if (p.Rotate != null) { p.Rotate.BeginAnimation(RotateTransform.AngleProperty, null); }
			if (p.Scale != null)
			{
				p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			}
			p.Element.BeginAnimation(UIElement.OpacityProperty, null);
		}
		particles.Clear();
		Children.Clear();
	}

	private static SolidColorBrush MakeBrush(string hex)
	{
		Color c;
		if (!ThemeManager.TryParseColor(hex, out c))
		{
			c = Colors.White;
		}
		SolidColorBrush b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}

	private static double Rand(double min, double max)
	{
		return min + rng.NextDouble() * (max - min);
	}

	private static DoubleAnimation Anim(double from, double to, double seconds)
	{
		DoubleAnimation a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds));
		Timeline.SetDesiredFrameRate(a, PerfHint.LowEnd ? 20 : 30);
		return a;
	}

	private void Build(CharacterTheme ch, bool light)
	{
		ThemePalette pal = ch.Palette(light);
		SolidColorBrush b1 = MakeBrush(pal.Accent);
		SolidColorBrush b2 = MakeBrush(pal.Aura2);
		SolidColorBrush b3 = MakeBrush(light ? pal.Accent : pal.Text);

		int count = 14;
		double sizeMin = 6.0;
		double sizeMax = 12.0;
		double durMin = 10.0;
		double durMax = 20.0;
		double peak = 0.5;
		bool rising = false;
		double swayAmp = 24.0;
		string kind = ch.Particle;

		switch (kind)
		{
			case "embers":
				count = 12; sizeMin = 3.0; sizeMax = 7.0; durMin = 8.0; durMax = 16.0; peak = 0.7; rising = true; swayAmp = 18.0;
				break;
			case "petals":
				count = 10; sizeMin = 11.0; sizeMax = 19.0; durMin = 13.0; durMax = 24.0; peak = 0.75; swayAmp = 46.0;
				break;
			case "sparkles":
				count = 10; sizeMin = 7.0; sizeMax = 15.0; durMin = 11.0; durMax = 22.0; peak = 0.8; rising = true; swayAmp = 14.0;
				break;
			case "butterflies":
				count = 5; sizeMin = 17.0; sizeMax = 27.0; durMin = 16.0; durMax = 28.0; peak = 0.75; rising = true; swayAmp = 60.0;
				break;
			case "leaves":
				count = 16; sizeMin = 10.0; sizeMax = 20.0; durMin = 10.0; durMax = 22.0; peak = 0.9; swayAmp = 48.0;
				break;
			case "ash":
				count = 22; sizeMin = 3.0; sizeMax = 9.0; durMin = 14.0; durMax = 28.0; peak = 0.6; swayAmp = 22.0;
				break;
			case "snow":
				count = 18; sizeMin = 5.0; sizeMax = 11.0; durMin = 12.0; durMax = 24.0; peak = 0.85; swayAmp = 28.0;
				break;
		}
		// «سوبارو · غرور»: جهنم کامل، ذره‌های درخشان‌تر و بیشتر + شعله‌های کف صفحه
		bool inferno = ch.Id == "reif";
		if (inferno)
		{
			count = 22; sizeMin = 4.0; sizeMax = 10.0; durMin = 5.0; durMax = 12.0; peak = 0.9; swayAmp = 26.0;
		}
		if (light)
		{
			peak = peak * 0.8;
		}
		if (PerfHint.LowEnd)
		{
			// سیستم ضعیف: نصف ذرات
			count = Math.Max(4, count / 2);
		}

		double w = ActualWidth;
		double h = ActualHeight;

		for (int i = 0; i < count; i++)
		{
			double size = Rand(sizeMin, sizeMax);
			double dur = Rand(durMin, durMax);
			double delay = -Rand(0.0, dur);

			FrameworkElement el;
			SolidColorBrush fill = (i % 3 == 0) ? b2 : ((i % 3 == 1) ? b1 : b3);
			if (kind == "embers")
			{
				Ellipse dot = new Ellipse();
				dot.Width = size;
				dot.Height = size;
				dot.Fill = inferno ? EmberGlow(pal, i % 5 == 0) : ((i % 4 == 0) ? b2 : b1);
				el = dot;
			}
			else
			{
				string glyph = "spark";
				Geometry data;
				if (kind == "petals") { glyph = "petal"; }
				else if (kind == "snow") { glyph = "snow"; }
				else if (kind == "leaves") { glyph = "leaf"; }
				else if (kind == "ash") { glyph = "shard"; }
				if (kind == "butterflies")
				{
					data = ch.Emblem;
					fill = (i % 2 == 0) ? b1 : b2;
				}
				else
				{
					data = CharacterThemes.Glyph(glyph);
					if (kind == "petals" || kind == "leaves") { fill = (i % 2 == 0) ? b1 : b2; }
					else if (kind == "ash") { fill = (i % 9 == 0) ? b2 : ((i % 2 == 0) ? b1 : b3); }
				}
				Path shape = new Path();
				shape.Data = data;
				shape.Stretch = Stretch.Uniform;
				shape.Width = size;
				shape.Height = size;
				shape.Fill = fill;
				el = shape;
			}

			Particle p = new Particle();
			p.Element = el;
			p.Fall = new TranslateTransform();
			p.Sway = new TranslateTransform();
			p.Rotate = new RotateTransform();
			p.Scale = new ScaleTransform(1.0, 1.0);

			TransformGroup group = new TransformGroup();
			group.Children.Add(p.Scale);
			group.Children.Add(p.Rotate);
			group.Children.Add(p.Sway);
			group.Children.Add(p.Fall);
			el.RenderTransformOrigin = new Point(0.5, 0.5);
			el.RenderTransform = group;
			el.Opacity = 0.0;
			if (!PerfHint.LowEnd)
			{
				el.CacheMode = new BitmapCache();
			}
			el.IsHitTestVisible = false;

			SetLeft(el, Rand(0.0, w));
			SetTop(el, 0.0);
			Children.Add(el);
			particles.Add(p);

			// بالا/پایین رفتن
			DoubleAnimation fall = rising ? Anim(h + 30.0, -40.0, dur) : Anim(-40.0, h + 30.0, dur);
			fall.RepeatBehavior = RepeatBehavior.Forever;
			fall.BeginTime = TimeSpan.FromSeconds(delay);
			p.Fall.BeginAnimation(TranslateTransform.YProperty, fall);

			// پیدا و محو شدن
			DoubleAnimationUsingKeyFrames fade = new DoubleAnimationUsingKeyFrames();
			fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
			fade.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.12)));
			fade.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.88)));
			fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));
			fade.Duration = TimeSpan.FromSeconds(dur);
			fade.RepeatBehavior = RepeatBehavior.Forever;
			fade.BeginTime = TimeSpan.FromSeconds(delay);
			Timeline.SetDesiredFrameRate(fade, PerfHint.LowEnd ? 20 : 30);
			el.BeginAnimation(UIElement.OpacityProperty, fade);

			// تاب خوردن به چپ و راست
			double amp = Rand(swayAmp * 0.4, swayAmp);
			DoubleAnimation sway = Anim(-amp, amp, Rand(3.0, 6.5));
			sway.AutoReverse = true;
			sway.RepeatBehavior = RepeatBehavior.Forever;
			sway.BeginTime = TimeSpan.FromSeconds(-Rand(0.0, 5.0));
			sway.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
			p.Sway.BeginAnimation(TranslateTransform.XProperty, sway);

			if (kind == "petals" || kind == "snow" || kind == "leaves" || kind == "ash")
			{
				// چرخیدن
				double dir = (rng.Next(2) == 0) ? 360.0 : -360.0;
				DoubleAnimation spin = Anim(0.0, dir, Rand(7.0, 15.0));
				spin.RepeatBehavior = RepeatBehavior.Forever;
				p.Rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
			}
			else if (kind == "butterflies")
			{
				// بال‌زدن
				DoubleAnimation flap = Anim(1.0, 0.3, Rand(0.28, 0.45));
				flap.AutoReverse = true;
				flap.RepeatBehavior = RepeatBehavior.Forever;
				p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, flap);
			}
			else if (inferno)
			{
				// سوسو زدن جرقه‌ها
				double flicker = Rand(0.35, 0.9);
				DoubleAnimation fx = Anim(0.4, 1.25, flicker);
				fx.AutoReverse = true;
				fx.RepeatBehavior = RepeatBehavior.Forever;
				p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, fx);
				p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, fx);
			}
			else if (kind == "sparkles")
			{
				// چشمک‌زدن
				double twinkleSeconds = Rand(1.0, 2.2);
				DoubleAnimation twX = Anim(0.45, 1.1, twinkleSeconds);
				twX.AutoReverse = true;
				twX.RepeatBehavior = RepeatBehavior.Forever;
				p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, twX);
				DoubleAnimation twY = Anim(0.45, 1.1, twinkleSeconds);
				twY.AutoReverse = true;
				twY.RepeatBehavior = RepeatBehavior.Forever;
				p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, twY);
			}
		}
		if (inferno)
		{
			AddInferno(pal, light, w, h);
		}
	}

	// ذره‌ی درخشان: مرکز داغ، لبه‌ی محو
	private static Brush EmberGlow(ThemePalette pal, bool hot)
	{
		Color core, mid, edge;
		ThemeManager.TryParseColor(hot ? "#FFF1C2" : pal.Aura2, out core);
		ThemeManager.TryParseColor(pal.Accent, out mid);
		edge = Color.FromArgb(0, mid.R, mid.G, mid.B);
		RadialGradientBrush g = new RadialGradientBrush();
		g.GradientStops.Add(new GradientStop(core, 0.0));
		g.GradientStops.Add(new GradientStop(mid, 0.45));
		g.GradientStops.Add(new GradientStop(edge, 1.0));
		g.Freeze();
		return g;
	}

	// شعله‌های کف صفحه + هاله‌ی گرمای پایین پنجره
	private void AddInferno(ThemePalette pal, bool light, double w, double h)
	{
		Color accent, deep, hotc;
		ThemeManager.TryParseColor(pal.Accent, out accent);
		ThemeManager.TryParseColor(pal.AccentDark, out deep);
		ThemeManager.TryParseColor(pal.Aura2, out hotc);
		double k = light ? 0.55 : 1.0;

		// هاله‌ی گرما: از شفاف تا نارنجیِ کم‌رنگ، آرام می‌تپه
		Rectangle glow = new Rectangle();
		glow.Width = w;
		glow.Height = Math.Max(120.0, h * 0.34);
		LinearGradientBrush gb = new LinearGradientBrush();
		gb.StartPoint = new Point(0.5, 0.0);
		gb.EndPoint = new Point(0.5, 1.0);
		gb.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.0));
		gb.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(70 * k), accent.R, accent.G, accent.B), 0.65));
		gb.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(130 * k), hotc.R, hotc.G, hotc.B), 1.0));
		gb.Freeze();
		glow.Fill = gb;
		glow.IsHitTestVisible = false;
		if (!PerfHint.LowEnd)
		{
			// هاله یک بار به شکل تصویر ذخیره می‌شه و فقط شفافیتش تغییر می‌کنه (خیلی سبک‌تر از رندر دوباره‌ی گرادیان)
			glow.CacheMode = new BitmapCache();
		}
		SetLeft(glow, 0.0);
		SetTop(glow, h - glow.Height);
		Children.Add(glow);
		DoubleAnimation pulse = Anim(0.55, 1.0, 3.2);
		Timeline.SetDesiredFrameRate(pulse, 15);
		pulse.AutoReverse = true;
		pulse.RepeatBehavior = RepeatBehavior.Forever;
		pulse.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
		glow.BeginAnimation(UIElement.OpacityProperty, pulse);
		Particle gp = new Particle();
		gp.Element = glow;
		particles.Add(gp);

	}
}
}

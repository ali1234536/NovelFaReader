using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EpubReader.Services;

namespace EpubReader
{

// تشخیص سیستم ضعیف: رندر نرم‌افزاری (بدون شتاب‌دهنده‌ی گرافیکی) یا پردازنده‌ی دو هسته‌ای و کمتر.
// افکت‌های تم (ذرات، رد موس، خاکستر صفحه‌ی خواندن) روی این سیستم‌ها خودکار سبک‌تر یا خاموش می‌شن.
public static class PerfHint
{
	public static readonly bool LowEnd = Detect();

	private static bool Detect()
	{
		try
		{
			if ((RenderCapability.Tier >> 16) < 2)
			{
				return true;
			}
		}
		catch
		{
		}
		return Environment.ProcessorCount <= 2;
	}
}

// حالت موس: نشانگر وکتوری هر تم شخصیت (فلش با رنگ تم و نشان کوچک شخصیت) + رد دنباله‌دار ذره‌ها پشت موس.
// حالت‌ها: 0 خاموش، 1 فقط نشانگر، 2 نشانگر + رد دنباله‌دار. با ThemeManager.Applied خودش به‌روز می‌شه.
// نشانگر روی همه‌ی پنجره‌ها اعمال می‌شه؛ صفحه‌ی خواندن (WebView2) از طریق CSS (ReaderCursorCss) نشانگر رو می‌گیره
// و رد دنباله‌دار روی صفحه‌ی خواندن کشیده نمی‌شه تا خواندن رو اذیت نکنه.
public static class MouseFx
{
	public const int ModeOff = 0;
	public const int ModeCursor = 1;
	public const int ModeCursorTrail = 2;

	private const int CursorSize = 32;

	private static bool initialized;
	private static bool coerceInstalled;
	private static string builtKey = "";
	private static Cursor themedCursor;
	private static Cursor previousCursor;
	private static string readerCss = "";
	private static string cursorCss = "";
	private static readonly ConditionalWeakTable<Window, MouseTrailAdorner> trails = new ConditionalWeakTable<Window, MouseTrailAdorner>();

	// وقتی نشانگر یا تم عوض شد (صفحه‌ی خواندن از این برای به‌روز کردن CSS استفاده می‌کنه)
	public static event EventHandler Changed;

	public static int Mode
	{
		get
		{
			int v;
			if (int.TryParse(AppSettings.Get("MouseFx", "1"), out v) && v >= ModeOff && v <= ModeCursorTrail)
			{
				return v;
			}
			return ModeCursor;
		}
		set
		{
			int v = Math.Max(ModeOff, Math.Min(ModeCursorTrail, value));
			AppSettings.Set("MouseFx", v.ToString());
		}
	}

	// استایل CSS صفحه‌ی خواندن (نشانگر تم + خاکستر لبه‌ی صفحه برای تم شکم‌پرستی). وقتی چیزی فعال نیست خالیه
	public static string ReaderFxCss
	{
		get { return readerCss; }
	}

	// یک بار موقع شروع برنامه (بعد از AppSettings.Load و قبل از ساخته شدن پنجره‌ها) صدا زده بشه
	public static void Init()
	{
		if (initialized)
		{
			return;
		}
		initialized = true;
		try
		{
			// نشانگر پیش‌فرض (null یا فلش) هر پنجره با نشانگر تم عوض می‌شه؛ نشانگرهای صریح (دست، I-beam، Wait و ...) دست نمی‌خورن
			FrameworkElement.CursorProperty.OverrideMetadata(typeof(Window),
				new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.None, null, new CoerceValueCallback(CoerceCursor)));
			coerceInstalled = true;
		}
		catch
		{
			coerceInstalled = false;
		}
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
		ThemeManager.Applied += (object s, EventArgs e) => Refresh();
	}

	private static object CoerceCursor(DependencyObject d, object value)
	{
		Cursor c = value as Cursor;
		if (themedCursor != null && (c == null || object.ReferenceEquals(c, Cursors.Arrow)))
		{
			return themedCursor;
		}
		return value;
	}

	private static void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		Window w = sender as Window;
		if (w == null)
		{
			return;
		}
		ApplyToWindow(w);
	}

	// ساخت دوباره‌ی نشانگر (اگه تم عوض شده) و اعمال روی همه‌ی پنجره‌های باز
	public static void Refresh()
	{
		CharacterTheme ch = ThemeManager.AppliedCharacter;
		bool light = ThemeManager.AppliedLight;
		int mode = Mode;
		bool cursorOn = mode >= ModeCursor && ch != null;
		bool ashOn = ch != null && ch.Id == "tsugi" && ThemeParticleLayer.Enabled && !PerfHint.LowEnd;
		string key = (ch == null ? "" : ch.Id + (light ? ".l" : ".d")) + (cursorOn ? "c" : "") + (ashOn ? "a" : "");
		if (key != builtKey)
		{
			builtKey = key;
			previousCursor = themedCursor;
			themedCursor = null;
			cursorCss = "";
			string ashCss = "";
			if (cursorOn)
			{
				try
				{
					RenderTargetBitmap bmp = RenderCursorBitmap(ch, light);
					themedCursor = MakeCursor(bmp, 1, 1);
					cursorCss = "html{cursor:url(\"data:image/png;base64," + ToPngBase64(bmp) + "\") 1 1,default;}";
				}
				catch
				{
					themedCursor = null;
					cursorCss = "";
				}
			}
			if (ashOn)
			{
				ashCss = BuildAshCss(ch, light);
			}
			readerCss = cursorCss + ashCss;
		}

		if (Application.Current != null)
		{
			foreach (Window w in Application.Current.Windows)
			{
				ApplyToWindow(w);
			}
		}

		EventHandler handler = Changed;
		if (handler != null)
		{
			handler(null, EventArgs.Empty);
		}
	}

	private static void ApplyToWindow(Window w)
	{
		try
		{
			if (coerceInstalled)
			{
				w.CoerceValue(FrameworkElement.CursorProperty);
			}
			else if (themedCursor != null && (w.Cursor == null || object.ReferenceEquals(w.Cursor, Cursors.Arrow)))
			{
				w.Cursor = themedCursor;
			}
			else if (themedCursor == null && previousCursor != null && object.ReferenceEquals(w.Cursor, previousCursor))
			{
				// نشانگرِ ساخته‌ی خودمون بود و حالا خاموش شده
				w.ClearValue(FrameworkElement.CursorProperty);
			}
		}
		catch
		{
		}
		SyncTrail(w);
	}

	// ---------- رد دنباله‌دار ----------
	private static void SyncTrail(Window w)
	{
		if (!w.IsLoaded)
		{
			return;
		}
		CharacterTheme ch = ThemeManager.AppliedCharacter;
		bool want = Mode == ModeCursorTrail && ch != null;
		MouseTrailAdorner adorner;
		bool has = trails.TryGetValue(w, out adorner);
		if (!want)
		{
			if (has)
			{
				adorner.Detach();
				trails.Remove(w);
			}
			return;
		}
		if (!has)
		{
			UIElement root = w.Content as UIElement;
			if (root == null)
			{
				return;
			}
			AdornerLayer layer = AdornerLayer.GetAdornerLayer(root);
			if (layer == null)
			{
				return;
			}
			adorner = new MouseTrailAdorner(root, w);
			layer.Add(adorner);
			trails.Add(w, adorner);
		}
		adorner.SetTheme(ch, ThemeManager.AppliedLight);
	}

	// ---------- ساخت نشانگر ----------
	private static Color Col(string hex)
	{
		Color c;
		if (!ThemeManager.TryParseColor(hex, out c))
		{
			c = Colors.White;
		}
		return c;
	}

	private static RenderTargetBitmap RenderCursorBitmap(CharacterTheme ch, bool light)
	{
		ThemePalette p = ch.Palette(light);
		Color grad1 = Col(p.Grad1);
		Color grad2 = Col(p.Grad2);
		SolidColorBrush accent = new SolidColorBrush(Col(p.Accent));
		SolidColorBrush outline = new SolidColorBrush(light ? Color.FromRgb(0x1B, 0x16, 0x24) : Colors.White);

		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			// فلش
			Geometry arrow = Geometry.Parse("M1.5,1.5 L1.5,23 L7,18 L10.8,26.5 L14.6,24.8 L10.9,16.6 L18.2,16.6 Z");
			if (ch.Id == "tsugi")
			{
				// نشانگر شکم‌پرستی: دو گاز از لبه‌ی فلش برداشته شده
				arrow = Geometry.Combine(arrow, new EllipseGeometry(new Point(18.4, 16.9), 3.7, 3.7), GeometryCombineMode.Exclude, null);
				arrow = Geometry.Combine(arrow, new EllipseGeometry(new Point(13.2, 25.4), 2.8, 2.8), GeometryCombineMode.Exclude, null);
			}
			Pen pen = new Pen(outline, 1.6);
			pen.LineJoin = PenLineJoin.Round;
			dc.DrawGeometry(new LinearGradientBrush(grad1, grad2, 45.0), pen, arrow);

			// نشان کوچک شخصیت پایین‌راست
			Point center = new Point(24.5, 24.5);
			dc.DrawEllipse(new SolidColorBrush(Col(p.Hero1)), new Pen(accent, 1.3), center, 7.2, 7.2);
			dc.PushTransform(new TranslateTransform(center.X - 5.0, center.Y - 5.0));
			dc.PushTransform(new ScaleTransform(0.1, 0.1));
			dc.DrawGeometry(new LinearGradientBrush(grad1, grad2, 45.0), null, ch.Emblem);
			dc.Pop();
			dc.Pop();
		}
		RenderTargetBitmap bmp = new RenderTargetBitmap(CursorSize, CursorSize, 96.0, 96.0, PixelFormats.Pbgra32);
		bmp.Render(dv);
		return bmp;
	}

	// فایل .cur ۳۲بیتی (با شفافیت) رو توی حافظه می‌سازه؛ بدون API ویندوز و روی ویندوز ۷ هم کار می‌کنه
	private static Cursor MakeCursor(RenderTargetBitmap bmp, int hotX, int hotY)
	{
		int size = CursorSize;
		byte[] px = new byte[size * size * 4];
		bmp.CopyPixels(px, size * 4, 0);
		// Pbgra → bgra معمولی
		for (int i = 0; i < px.Length; i += 4)
		{
			int a = px[i + 3];
			if (a > 0 && a < 255)
			{
				px[i] = (byte)Math.Min(255, px[i] * 255 / a);
				px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
				px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
			}
		}
		int maskStride = ((size + 31) / 32) * 4;
		int imageSize = 40 + size * size * 4 + maskStride * size;

		MemoryStream ms = new MemoryStream();
		BinaryWriter bw = new BinaryWriter(ms);
		bw.Write((short)0);
		bw.Write((short)2);
		bw.Write((short)1);
		bw.Write((byte)size);
		bw.Write((byte)size);
		bw.Write((byte)0);
		bw.Write((byte)0);
		bw.Write((short)hotX);
		bw.Write((short)hotY);
		bw.Write(imageSize);
		bw.Write(22);
		bw.Write(40);
		bw.Write(size);
		bw.Write(size * 2);
		bw.Write((short)1);
		bw.Write((short)32);
		bw.Write(0);
		bw.Write(size * size * 4);
		bw.Write(0);
		bw.Write(0);
		bw.Write(0);
		bw.Write(0);
		for (int y = size - 1; y >= 0; y--)
		{
			bw.Write(px, y * size * 4, size * 4);
		}
		bw.Write(new byte[maskStride * size]);
		bw.Flush();
		ms.Position = 0;
		return new Cursor(ms);
	}

	private static string Rgb(string hex)
	{
		Color c = Col(hex);
		return "rgb(" + c.R + "," + c.G + "," + c.B + ")";
	}

	// خاکستر آرومِ لبه‌ی صفحه‌ی خواندن: فقط دو نوار باریک (۳۶ پیکسل) کنار صفحه، با انیمیشن transform که روی کارت گرافیک اجرا می‌شه
	// و CPU رو درگیر نمی‌کنه. روی متن نمی‌افته و نمی‌شه روش کلیک کرد. توی کاهش حرکت سیستم‌عامل هم خاموشه.
	private static string BuildAshCss(CharacterTheme ch, bool light)
	{
		ThemePalette p = ch.Palette(light);
		string a = Rgb(p.Accent);
		string m = Rgb(p.Muted);
		string t = Rgb(p.Aura1);
		string dots = "radial-gradient(circle at 9px 22px," + a + " 0,transparent 2.2px),"
			+ "radial-gradient(circle at 25px 96px," + m + " 0,transparent 1.8px),"
			+ "radial-gradient(circle at 14px 171px," + t + " 0,transparent 2.4px),"
			+ "radial-gradient(circle at 28px 232px," + m + " 0,transparent 1.5px)";
		string common = "content:'';position:fixed;top:-300px;height:calc(100vh + 300px);width:36px;pointer-events:none;z-index:2147483000;"
			+ "background-image:" + dots + ";background-size:36px 300px;background-repeat:repeat-y;opacity:" + (light ? "0.45" : "0.6") + ";will-change:transform;";
		return "html::before{" + common + "left:0;animation:rdrAshA 24s linear infinite;}"
			+ "html::after{" + common + "right:0;animation:rdrAshB 31s linear infinite;}"
			+ "@keyframes rdrAshA{from{transform:translate3d(0,0,0);}to{transform:translate3d(0,300px,0);}}"
			+ "@keyframes rdrAshB{from{transform:translate3d(0,0,0);}to{transform:translate3d(0,300px,0);}}"
			+ "@media (prefers-reduced-motion:reduce){html::before,html::after{animation:none;}}";
	}

	private static string ToPngBase64(RenderTargetBitmap bmp)
	{
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using (MemoryStream ms = new MemoryStream())
		{
			enc.Save(ms);
			return Convert.ToBase64String(ms.ToArray());
		}
	}
}

// لایه‌ی رد دنباله‌ی موس روی محتوای پنجره. نامرئی برای موس و فقط وقتی ذره‌ای زنده‌ست فریم می‌کشه
public sealed class MouseTrailAdorner : Adorner
{
	private sealed class Dot
	{
		public double X;
		public double Y;
		public double Vx;
		public double Vy;
		public double Rot;
		public double Spin;
		public double Size;
		public double Life;
		public double Born;
		public double Gravity;
		public int Color;
	}

	private static readonly Random rng = new Random();

	private readonly Window window;
	private readonly List<Dot> dots = new List<Dot>();
	private readonly Stopwatch clock = Stopwatch.StartNew();
	private SolidColorBrush[] brushes = new SolidColorBrush[0];
	private Geometry shape;
	private double shapeBox = 20.0;
	private bool crumbs;
	private string kind = "";
	private bool light;
	private bool rendering;
	private bool attached;
	private double lastEmit = -1000.0;
	private double lastFrame;
	private Point lastPos;
	private bool hasLast;

	public MouseTrailAdorner(UIElement adorned, Window host) : base(adorned)
	{
		window = host;
		IsHitTestVisible = false;
		window.PreviewMouseMove += OnMouseMove;
		window.MouseLeave += OnMouseLeave;
		attached = true;
	}

	public void Detach()
	{
		if (attached)
		{
			attached = false;
			window.PreviewMouseMove -= OnMouseMove;
			window.MouseLeave -= OnMouseLeave;
		}
		StopRendering();
		dots.Clear();
		try
		{
			AdornerLayer layer = AdornerLayer.GetAdornerLayer(AdornedElement);
			if (layer != null)
			{
				layer.Remove(this);
			}
		}
		catch
		{
		}
	}

	public void SetTheme(CharacterTheme ch, bool isLight)
	{
		string newKind = ch.Particle + (isLight ? ".l" : ".d") + ch.Id;
		if (newKind == kind)
		{
			return;
		}
		kind = newKind;
		light = isLight;
		// شکم‌پرستی: خرده‌هایی که از دهان می‌ریزن؛ شتابِ گرانش می‌گیرن و می‌افتن
		crumbs = ch.Id == "tsugi";
		ThemePalette p = ch.Palette(isLight);
		brushes = new SolidColorBrush[]
		{
			MakeBrush(p.Accent),
			MakeBrush(p.Grad1),
			MakeBrush(p.Aura2),
			MakeBrush(isLight ? p.Accent : p.Text)
		};
		switch (ch.Particle)
		{
			case "petals": shape = CharacterThemes.Glyph("petal"); shapeBox = 20.0; break;
			case "snow": shape = CharacterThemes.Glyph("snow"); shapeBox = 20.0; break;
			case "leaves": shape = CharacterThemes.Glyph("leaf"); shapeBox = 20.0; break;
			case "ash": shape = CharacterThemes.Glyph("shard"); shapeBox = 20.0; break;
			case "butterflies": shape = ch.Emblem; shapeBox = 100.0; break;
			case "embers": shape = null; shapeBox = 20.0; break;
			default: shape = CharacterThemes.Glyph("spark"); shapeBox = 20.0; break;
		}
		dots.Clear();
		InvalidateVisual();
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

	private void OnMouseLeave(object sender, MouseEventArgs e)
	{
		hasLast = false;
	}

	private void OnMouseMove(object sender, MouseEventArgs e)
	{
		if (brushes.Length == 0 || !window.IsActive)
		{
			return;
		}
		Point pos = e.GetPosition(this);
		double now = clock.Elapsed.TotalMilliseconds;
		if (!hasLast)
		{
			lastPos = pos;
			hasLast = true;
			return;
		}
		double dx = pos.X - lastPos.X;
		double dy = pos.Y - lastPos.Y;
		if (dx * dx + dy * dy < (PerfHint.LowEnd ? 196.0 : 100.0) || now - lastEmit < (PerfHint.LowEnd ? 45.0 : 22.0))
		{
			return;
		}
		lastPos = pos;
		lastEmit = now;
		Emit(pos, now);
		StartRendering();
	}

	private void Emit(Point pos, double now)
	{
		bool rising = kind.StartsWith("embers") || kind.StartsWith("sparkles") || kind.StartsWith("butterflies");
		Dot d = new Dot();
		d.X = pos.X + Rand(-4.0, 4.0);
		d.Y = pos.Y + Rand(-4.0, 4.0);
		d.Vx = Rand(-16.0, 16.0);
		d.Vy = (rising ? -1.0 : 1.0) * Rand(18.0, 46.0);
		if (crumbs)
		{
			d.Vx = Rand(-34.0, 34.0);
			d.Vy = Rand(-30.0, 8.0);
			d.Gravity = Rand(230.0, 330.0);
			d.Spin = Rand(-260.0, 260.0);
		}
		d.Rot = Rand(0.0, 360.0);
		d.Spin = Rand(-140.0, 140.0);
		if (crumbs)
		{
			d.Spin = Rand(-260.0, 260.0);
		}
		d.Size = kind.StartsWith("butterflies") ? Rand(12.0, 17.0) : (kind.StartsWith("embers") ? Rand(3.0, 6.0) : Rand(8.0, 14.0));
		d.Life = Rand(650.0, 1100.0);
		d.Born = now;
		d.Color = rng.Next(brushes.Length);
		dots.Add(d);
		if (dots.Count > (PerfHint.LowEnd ? 12 : 30))
		{
			dots.RemoveAt(0);
		}
	}

	private void StartRendering()
	{
		if (!rendering)
		{
			rendering = true;
			lastFrame = clock.Elapsed.TotalMilliseconds;
			CompositionTarget.Rendering += OnFrame;
		}
	}

	private void StopRendering()
	{
		if (rendering)
		{
			rendering = false;
			CompositionTarget.Rendering -= OnFrame;
		}
	}

	private void OnFrame(object sender, EventArgs e)
	{
		double now = clock.Elapsed.TotalMilliseconds;
		// بیشتر از ۳۰ فریم در ثانیه لازم نیست؛ فریم‌های اضافی رد می‌شن تا سیستم ضعیف درگیر نشه
		if (now - lastFrame < 32.0)
		{
			return;
		}
		double dt = Math.Min(0.05, (now - lastFrame) / 1000.0);
		lastFrame = now;
		for (int i = dots.Count - 1; i >= 0; i--)
		{
			Dot d = dots[i];
			if (now - d.Born >= d.Life)
			{
				dots.RemoveAt(i);
				continue;
			}
			d.Vy += d.Gravity * dt;
			d.X += d.Vx * dt;
			d.Y += d.Vy * dt;
			d.Rot += d.Spin * dt;
		}
		InvalidateVisual();
		if (dots.Count == 0)
		{
			StopRendering();
		}
	}

	protected override void OnRender(DrawingContext dc)
	{
		if (dots.Count == 0 || brushes.Length == 0)
		{
			return;
		}
		double now = clock.Elapsed.TotalMilliseconds;
		double peak = light ? 0.75 : 0.9;
		foreach (Dot d in dots)
		{
			double t = (now - d.Born) / d.Life;
			if (t < 0.0 || t >= 1.0)
			{
				continue;
			}
			double fade = 1.0 - t;
			dc.PushOpacity(peak * fade * fade);
			double size = d.Size * (1.0 - 0.35 * t);
			SolidColorBrush fill = brushes[d.Color];
			if (shape == null)
			{
				dc.DrawEllipse(fill, null, new Point(d.X, d.Y), size / 2.0, size / 2.0);
			}
			else
			{
				double sc = size / shapeBox;
				dc.PushTransform(new TranslateTransform(d.X, d.Y));
				dc.PushTransform(new RotateTransform(d.Rot));
				dc.PushTransform(new ScaleTransform(sc, sc));
				dc.PushTransform(new TranslateTransform(-shapeBox / 2.0, -shapeBox / 2.0));
				dc.DrawGeometry(fill, null, shape);
				dc.Pop();
				dc.Pop();
				dc.Pop();
				dc.Pop();
			}
			dc.Pop();
		}
	}
}
}

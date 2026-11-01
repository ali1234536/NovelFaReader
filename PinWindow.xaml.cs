using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using EpubReader.Services;

namespace EpubReader
{

public partial class PinWindow : Window
{
	private readonly bool isSetupMode;

	private readonly bool isChangeMode;

	public PinWindow()
		: this(forceSetupMode: false)
	{
	}

	public PinWindow(bool forceSetupMode)
	{
		InitializeComponent();
		Loc.Prepare(this);
		bool hasPin = SettingsService.HasPin();
		isChangeMode = forceSetupMode & hasPin;
		isSetupMode = forceSetupMode || !hasPin;
		if (isSetupMode)
		{
			txtTitle.Text = (isChangeMode ? Loc.T("پین جدید رو وارد کنید") : Loc.T("یک پین بسازید"));
			txtSubtitle.Text = (isChangeMode ? Loc.T("پین جدید جایگزین پین قبلی میشه") : Loc.T("برای قفل کردن کتاب\u200cهای خصوصی"));
			lblPin.Text = (isChangeMode ? Loc.T("پین جدید") : Loc.T("پین"));
			lblConfirm.Visibility = Visibility.Visible;
			pinBox2.Visibility = Visibility.Visible;
		}
		else
		{
			txtTitle.Text = Loc.T("پین رو وارد کنید");
			txtSubtitle.Text = Loc.T("بخش خصوصی قفله");
			lblConfirm.Visibility = Visibility.Collapsed;
			pinBox2.Visibility = Visibility.Collapsed;
			btnForgot.Visibility = Visibility.Visible;
		}
		Window owner = Application.Current.Windows.OfType<Window>().FirstOrDefault((Window w) => w.IsActive && w != this);
		if (owner != null)
		{
			Owner = owner;
		}
		else
		{
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		pinBox1.Focus();
	}

	private void ShowError(string message)
	{
		txtError.Text = Loc.T(message);
		txtError.Visibility = Visibility.Visible;
	}

	private void btnOk_Click(object sender, RoutedEventArgs e)
	{
		string pin = pinBox1.Password;
		if (string.IsNullOrEmpty(pin))
		{
			ShowError("پین نمی\u200cتونه خالی باشه");
		}
		else if (isSetupMode)
		{
			if (pin.Length < 4)
			{
				ShowError("پین باید حداقل ۴ کاراکتر باشه");
			}
			else if (pin != pinBox2.Password)
			{
				ShowError("پین\u200cها یکسان نیستن");
				pinBox2.Clear();
				pinBox2.Focus();
			}
			else
			{
				SettingsService.SetPin(pin);
				DialogResult = true;
			}
		}
		else if (LockoutSecondsLeft() > 0)
		{
			txtError.Text = Loc.F("تلاش‌های اشتباه زیاد بود. {0} ثانیه دیگه دوباره امتحان کن.", LockoutSecondsLeft());
			txtError.Visibility = Visibility.Visible;
			pinBox1.Clear();
		}
		else if (SettingsService.VerifyPin(pin))
		{
			SaveThrottle(0, 0L);
			DialogResult = true;
		}
		else
		{
			int failures = ReadThrottleFailures() + 1;
			if (failures >= MaxFailures)
			{
				SaveThrottle(0, DateTime.UtcNow.AddSeconds(LockoutSeconds).Ticks);
				txtError.Text = Loc.F("تلاش‌های اشتباه زیاد بود. {0} ثانیه دیگه دوباره امتحان کن.", LockoutSeconds);
				txtError.Visibility = Visibility.Visible;
			}
			else
			{
				SaveThrottle(failures, 0L);
				ShowError("پین اشتباهه");
			}
			pinBox1.Clear();
			pinBox1.Focus();
		}
	}

	// ضد حدس زدن: ۵ پین اشتباه پشت‌سرهم = ۳۰ ثانیه قفل. وضعیتش توی تنظیمات می‌مونه، پس با بستن و باز کردن برنامه صفر نمی‌شه
	private const int MaxFailures = 5;

	private const int LockoutSeconds = 30;

	private static int ReadThrottleFailures()
	{
		int v;
		return int.TryParse(AppSettings.Get("PinFailures", "0"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0 ? v : 0;
	}

	private static int LockoutSecondsLeft()
	{
		long ticks;
		if (!long.TryParse(AppSettings.Get("PinLockedUntil", "0"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ticks) || ticks <= 0)
		{
			return 0;
		}
		double left = (new DateTime(Math.Min(ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds;
		if (left <= 0.0)
		{
			return 0;
		}
		// ساعت سیستم رو به عقب بردن هم قفل رو طولانی‌تر از سقفش نمی‌کنه
		return (int)Math.Min(LockoutSeconds, Math.Ceiling(left));
	}

	private static void SaveThrottle(int failures, long lockedUntilTicks)
	{
		AppSettings.Set("PinFailures", failures.ToString(System.Globalization.CultureInfo.InvariantCulture));
		AppSettings.Set("PinLockedUntil", lockedUntilTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	// بعد از «فراموشی پین» true می‌شه تا کتابخونه فهرستش رو دوباره بخونه
	public static bool ForgotPinPerformed;

	// فراموشی پین: تنها راه برگشت، حذف همه‌ی کتاب‌های خصوصی از کتابخونه و پاک کردن پینه (بدون پین، قفل دور زده نمی‌شه)
	private void btnForgot_Click(object sender, RoutedEventArgs e)
	{
		MessageBoxResult ask = DarkMessageBox.Show(Loc.T("این کار همه‌ی کتاب‌های خصوصی رو برای همیشه از کتابخونه حذف می‌کنه و برگشت‌پذیر نیست. این یه محافظت امنیتیه: بدون پین نمی‌شه کتاب‌های خصوصی رو باز کرد و برای اینکه قفل دور زده نشه، تنها راه برگشت حذف اوناست (خود فایل‌های کتاب پاک نمی‌شن). ادامه بدیم؟"), Loc.T("فراموشی پین"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
		if (ask != MessageBoxResult.Yes)
		{
			return;
		}
		int removed = 0;
		foreach (EpubReader.Models.Book b in LibraryService.LoadBooks().Where((EpubReader.Models.Book x) => x.IsPrivate).ToList())
		{
			LibraryService.RemoveBook(b.FilePath);
			removed++;
		}
		SettingsService.ClearPin();
		ForgotPinPerformed = true;
		DarkMessageBox.Show(Loc.T("همه‌ی کتاب‌های خصوصی حذف شدند و پین پاک شد."), Loc.T("فراموشی پین"), MessageBoxButton.OK, MessageBoxImage.Information);
		DialogResult = true;
	}

	private void btnCancel_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
	}

	private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		try
		{
			DragMove();
		}
		catch
		{
		}
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			if (isSetupMode && pinBox1.IsKeyboardFocused)
			{
				pinBox2.Focus();
			}
			else
			{
				btnOk_Click(sender, e);
			}
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			DialogResult = false;
			e.Handled = true;
		}
	}

	public static bool Authenticate()
	{
		PinWindow win = new PinWindow();
		return win.ShowDialog() == true;
	}

	public static bool ChangePin()
	{
		if (SettingsService.HasPin() && !Authenticate())
		{
			return false;
		}
		PinWindow win = new PinWindow(forceSetupMode: true);
		return win.ShowDialog() == true;
	}
}
}

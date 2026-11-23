using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EpubReader.Services;

namespace EpubReader
{
    public partial class MainWindow
    {
        private bool smartUiInitializing = false;
        private bool smartSuppressSync = false;
        private bool autoScrollActive = false;
        private bool autoScrollPendingResume = false;
        private DispatcherTimer autoScrollTimer;
        private DateTime autoScrollNextTickUtc;
        private DispatcherTimer pomodoroTimer;
        private int pomodoroRemainingSec = 0;
        private bool pomodoroRunning = false;
        private DispatcherTimer stopwatchTimer;
        private int stopwatchElapsedSec = 0;
        private bool stopwatchRunning = false;
        private int eyeRestSeconds = 0;

        private void FireScript(string js)
        {
            var _ = RunScriptAsync(js);
        }

        private void InitSmartReader()
        {
            smartUiInitializing = true;
            try
            {
                sldAsInterval.Value = SmartSettings.AutoScrollIntervalSec;
                sldAsStep.Value = SmartSettings.AutoScrollStepPercent;
                sldAsSpeed.Value = SmartSettings.AutoScrollSmoothSpeed;
                chkAsAutoNext.IsChecked = SmartSettings.AutoScrollAutoNext;
                chkAsPauseInteract.IsChecked = SmartSettings.AutoScrollPauseOnInteract;

                sldEyeCare.Value = SmartSettings.EyeCareWarmth;
                sldBrightness.Value = SmartSettings.ReaderBrightness;
                chkReadingRuler.IsChecked = SmartSettings.ReadingRulerEnabled;
                sldRulerHeight.Value = SmartSettings.ReadingRulerHeight;
            }
            catch { }
            smartUiInitializing = false;

            UpdateAutoScrollUi();
            UpdateSmartToolsUi();
            ApplyReaderUiMode(SmartSettings.ReaderUiMode, false);
            ApplyReaderPerformanceVisuals();

            SmartSettings.Changed += OnSmartSettingsChanged;
        }

        private void DisposeSmartReader()
        {
            try { SmartSettings.Changed -= OnSmartSettingsChanged; } catch { }
            StopAutoScroll(false);
            StopPomodoroTimer();
            StopStopwatchTimer();
        }

        private void OnSmartSettingsChanged(object sender, EventArgs e)
        {
            if (windowClosed || smartSuppressSync) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (smartSuppressSync) return;
                ApplyReaderPerformanceVisuals();
                UpdateAutoScrollUi();
                UpdateSmartToolsUi();
                ApplyEyeCareLive();
                ApplyRulerLive();
            }));
        }

        private void ApplyReaderPerformanceVisuals()
        {
            try
            {
                if (ReaderCardShadow != null)
                {
                    ReaderCardShadow.Opacity = SmartSettings.IsTurboMode ? 0.0 : 0.45;
                }
            }
            catch { }
        }

        // ---- تزریق اسکریپت سراسری پس از بارگذاری هر فرمت (EPUB, PDF, Comic, Doc) ----
        private async void OnSmartNavigationCompleted()
        {
            try
            {
                await RunScriptAsync(SmartWebBridge.UniversalScript);
                ApplyEyeCareLive();
                ApplyRulerLive();
                UpdateSmartEtaAsync();

                if (autoScrollActive && autoScrollPendingResume)
                {
                    autoScrollPendingResume = false;
                    StartAutoScrollEngine();
                }
            }
            catch { }
        }

        // ---- پردازش پیام‌های دریافتی از موتور هوشمند WebView2 ----
        private bool HandleSmartWebMessage(string msg)
        {
            if (msg == "as_end")
            {
                OnAutoScrollReachedEnd();
                return true;
            }
            if (msg == "as_interact")
            {
                if (autoScrollActive && SmartSettings.AutoScrollPauseOnInteract)
                {
                    autoScrollNextTickUtc = DateTime.UtcNow.AddSeconds(Math.Max(2.5, SmartSettings.AutoScrollIntervalSec));
                }
                return true;
            }
            return false;
        }

        // ---- سوئیچر هوشمند رابط کاربری صفحه مطالعه ----
        private void ReaderUiMode_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            if (btn == null || btn.Tag == null) return;
            ApplyReaderUiMode(btn.Tag.ToString(), true);
        }

        private void ApplyReaderUiMode(string mode, bool save)
        {
            if (string.IsNullOrEmpty(mode)) mode = "standard";
            if (save)
            {
                smartSuppressSync = true;
                SmartSettings.ReaderUiMode = mode;
                smartSuppressSync = false;
            }

            bool focus = mode == "focus";
            bool smart = mode == "smart";

            if (!isFullScreen)
            {
                SidebarColumn.Width = focus ? new GridLength(0.0) : new GridLength(280.0);
                SidebarBorder.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
                TopBar.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
                BottomBar.Margin = focus ? new Thickness(0, 10, 0, 0) : new Thickness(0, 16, 0, 0);
                ContentGrid.Margin = focus ? new Thickness(20, 10, 20, 12) : new Thickness(28, 20, 28, 18);
            }

            SmartHudBar.Visibility = (smart || autoScrollActive || pomodoroRunning || stopwatchRunning) ? Visibility.Visible : Visibility.Collapsed;

            UpdateModeButtonStyle(btnReaderModeStandard, mode == "standard");
            UpdateModeButtonStyle(btnReaderModeFocus, focus);
            UpdateModeButtonStyle(btnReaderModeSmart, smart);
        }

        private void UpdateModeButtonStyle(Button btn, bool active)
        {
            if (btn == null) return;
            try
            {
                btn.Background = active ? (Brush)FindResource("AccentColorDark") : Brushes.Transparent;
                btn.Foreground = active ? Brushes.White : (Brush)FindResource("TextMuted");
                btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
            catch { }
        }

        private void ToggleFocusModeShortcut()
        {
            string next = SmartSettings.ReaderUiMode == "focus" ? "standard" : "focus";
            ApplyReaderUiMode(next, true);
        }

        private void ToggleSmartHudShortcut()
        {
            string next = SmartSettings.ReaderUiMode == "smart" ? "standard" : "smart";
            ApplyReaderUiMode(next, true);
        }

        // ---- موتور اسکرول خودکار سراسری (برای EPUB, PDF, CBZ/Comic, DOCX/TXT) ----
        private void btnAutoScroll_Click(object sender, RoutedEventArgs e)
        {
            ToggleAutoScroll();
        }

        private void btnAutoScrollCfg_Click(object sender, RoutedEventArgs e)
        {
            popupAutoScroll.IsOpen = !popupAutoScroll.IsOpen;
        }

        private void ToggleAutoScroll()
        {
            if (autoScrollActive)
            {
                StopAutoScroll(true);
            }
            else
            {
                StartAutoScroll();
            }
        }

        private void StartAutoScroll()
        {
            if (currentBook == null && pdfSource == null) return;
            autoScrollActive = true;
            autoScrollPendingResume = false;
            SmartHudBar.Visibility = Visibility.Visible;
            StartAutoScrollEngine();
            UpdateAutoScrollUi();
        }

        private void StartAutoScrollEngine()
        {
            StopAutoScrollTimersOnly();
            string mode = SmartSettings.AutoScrollMode;
            bool pauseOnInteract = SmartSettings.AutoScrollPauseOnInteract;
            bool autoNext = SmartSettings.AutoScrollAutoNext;

            if (mode == "smooth")
            {
                int spd = SmartSettings.AutoScrollSmoothSpeed;
                FireScript("window.__smartAutoScroll&&window.__smartAutoScroll.startContinuous(" + spd + "," + (autoNext ? "true" : "false") + "," + (pauseOnInteract ? "true" : "false") + ");");
                txtHudCountdown.Text = "در حال حرکت نرم";
            }
            else
            {
                FireScript("window.__smartAutoScroll&&(window.__smartAutoScroll.stopContinuous(),window.__smartAutoScroll.setInteractPause(" + (pauseOnInteract ? "true" : "false") + "));");
                autoScrollNextTickUtc = DateTime.UtcNow.AddSeconds(SmartSettings.AutoScrollIntervalSec);
                if (autoScrollTimer == null)
                {
                    autoScrollTimer = new DispatcherTimer();
                    autoScrollTimer.Interval = TimeSpan.FromMilliseconds(150);
                    autoScrollTimer.Tick += AutoScrollTimer_Tick;
                }
                autoScrollTimer.Start();
            }
        }

        private void StopAutoScrollTimersOnly()
        {
            if (autoScrollTimer != null)
            {
                autoScrollTimer.Stop();
            }
        }

        private void StopAutoScroll(bool updateUi)
        {
            autoScrollActive = false;
            autoScrollPendingResume = false;
            StopAutoScrollTimersOnly();
            try
            {
                FireScript("window.__smartAutoScroll&&window.__smartAutoScroll.stopContinuous();");
            }
            catch { }
            if (updateUi)
            {
                UpdateAutoScrollUi();
                if (SmartSettings.ReaderUiMode != "smart" && !pomodoroRunning && !stopwatchRunning)
                {
                    SmartHudBar.Visibility = Visibility.Collapsed;
                }
            }
        }

        private async void AutoScrollTimer_Tick(object sender, EventArgs e)
        {
            if (!autoScrollActive || windowClosed)
            {
                StopAutoScrollTimersOnly();
                return;
            }
            if (SmartSettings.AutoScrollMode != "step")
            {
                StopAutoScrollTimersOnly();
                return;
            }

            double rem = (autoScrollNextTickUtc - DateTime.UtcNow).TotalSeconds;
            if (rem > 0.05)
            {
                txtHudCountdown.Text = "گام بعدی: " + rem.ToString("0.0", CultureInfo.InvariantCulture) + " ث";
                return;
            }

            autoScrollNextTickUtc = DateTime.UtcNow.AddSeconds(SmartSettings.AutoScrollIntervalSec);
            double frac = SmartSettings.AutoScrollStepPercent / 100.0;
            bool smooth = SmartSettings.SmoothScroll;
            bool autoNext = SmartSettings.AutoScrollAutoNext;
            string js = "window.__smartAutoScroll?window.__smartAutoScroll.step("
                + frac.ToString("0.##", CultureInfo.InvariantCulture) + ","
                + (smooth ? "true" : "false") + ","
                + (autoNext ? "true" : "false") + "):'none'";
            await RunScriptAsync(js);
        }

        private void OnAutoScrollReachedEnd()
        {
            if (!autoScrollActive || !SmartSettings.AutoScrollAutoNext) return;

            if (pdfSource != null)
            {
                PdfStep(1);
                return;
            }

            if (currentBook != null && listBoxChapters.SelectedIndex >= 0 && listBoxChapters.SelectedIndex < listBoxChapters.Items.Count - 1)
            {
                autoScrollPendingResume = true;
                StopAutoScrollTimersOnly();
                listBoxChapters.SelectedIndex++;
            }
            else
            {
                StopAutoScroll(true);
                txtHudCountdown.Text = "پایان کتاب";
            }
        }

        private void btnAsModeStep_Click(object sender, RoutedEventArgs e)
        {
            smartSuppressSync = true;
            SmartSettings.AutoScrollMode = "step";
            smartSuppressSync = false;
            UpdateAutoScrollUi();
            if (autoScrollActive) StartAutoScrollEngine();
        }

        private void btnAsModeSmooth_Click(object sender, RoutedEventArgs e)
        {
            smartSuppressSync = true;
            SmartSettings.AutoScrollMode = "smooth";
            smartSuppressSync = false;
            UpdateAutoScrollUi();
            if (autoScrollActive) StartAutoScrollEngine();
        }

        private void AsPresetSec_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            double sec;
            if (btn != null && btn.Tag != null && double.TryParse(btn.Tag.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
            {
                smartSuppressSync = true;
                SmartSettings.AutoScrollMode = "step";
                SmartSettings.AutoScrollIntervalSec = sec;
                smartSuppressSync = false;
                smartUiInitializing = true;
                sldAsInterval.Value = sec;
                smartUiInitializing = false;
                UpdateAutoScrollUi();
                if (autoScrollActive) StartAutoScrollEngine();
            }
        }

        private void AsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (smartUiInitializing || !IsLoaded) return;
            smartSuppressSync = true;
            try
            {
                if (sender == sldAsInterval)
                {
                    double val = Math.Round(sldAsInterval.Value * 2.0) / 2.0;
                    SmartSettings.AutoScrollIntervalSec = val;
                    txtAsIntervalVal.Text = val.ToString("0.#", CultureInfo.InvariantCulture) + " ثانیه";
                    if (SmartSettings.AutoScrollMode == "step")
                    {
                        txtHudSpeedBadge.Text = "هر " + val.ToString("0.#", CultureInfo.InvariantCulture) + " ثانیه";
                    }
                    if (autoScrollActive && SmartSettings.AutoScrollMode == "step")
                    {
                        autoScrollNextTickUtc = DateTime.UtcNow.AddSeconds(val);
                    }
                }
                else if (sender == sldAsStep)
                {
                    int pct = (int)Math.Round(sldAsStep.Value);
                    SmartSettings.AutoScrollStepPercent = pct;
                    txtAsStepVal.Text = pct >= 95 ? "یک صفحه کامل" : (pct + "٪ صفحه");
                }
                else if (sender == sldAsSpeed)
                {
                    int spd = (int)Math.Round(sldAsSpeed.Value);
                    SmartSettings.AutoScrollSmoothSpeed = spd;
                    txtAsSpeedVal.Text = spd + " پیکسل/ثانیه";
                    if (SmartSettings.AutoScrollMode == "smooth")
                    {
                        txtHudSpeedBadge.Text = spd + " px/s";
                    }
                    if (autoScrollActive && SmartSettings.AutoScrollMode == "smooth")
                    {
                        FireScript("window.__smartAutoScroll&&window.__smartAutoScroll.updateSpeed(" + spd + ");");
                    }
                }
            }
            finally
            {
                smartSuppressSync = false;
            }
        }

        private void AsOption_Changed(object sender, RoutedEventArgs e)
        {
            if (smartUiInitializing || !IsLoaded) return;
            smartSuppressSync = true;
            SmartSettings.AutoScrollAutoNext = chkAsAutoNext.IsChecked == true;
            SmartSettings.AutoScrollPauseOnInteract = chkAsPauseInteract.IsChecked == true;
            smartSuppressSync = false;
            if (autoScrollActive) StartAutoScrollEngine();
        }

        private void btnHudFaster_Click(object sender, RoutedEventArgs e)
        {
            smartSuppressSync = true;
            if (SmartSettings.AutoScrollMode == "step")
            {
                double next = Math.Max(0.5, SmartSettings.AutoScrollIntervalSec - 0.5);
                SmartSettings.AutoScrollIntervalSec = next;
                smartUiInitializing = true;
                sldAsInterval.Value = next;
                smartUiInitializing = false;
            }
            else
            {
                int next = Math.Min(220, SmartSettings.AutoScrollSmoothSpeed + 10);
                SmartSettings.AutoScrollSmoothSpeed = next;
                smartUiInitializing = true;
                sldAsSpeed.Value = next;
                smartUiInitializing = false;
                if (autoScrollActive) FireScript("window.__smartAutoScroll&&window.__smartAutoScroll.updateSpeed(" + next + ");");
            }
            smartSuppressSync = false;
            UpdateAutoScrollUi();
        }

        private void btnHudSlower_Click(object sender, RoutedEventArgs e)
        {
            smartSuppressSync = true;
            if (SmartSettings.AutoScrollMode == "step")
            {
                double next = Math.Min(30.0, SmartSettings.AutoScrollIntervalSec + 0.5);
                SmartSettings.AutoScrollIntervalSec = next;
                smartUiInitializing = true;
                sldAsInterval.Value = next;
                smartUiInitializing = false;
            }
            else
            {
                int next = Math.Max(15, SmartSettings.AutoScrollSmoothSpeed - 10);
                SmartSettings.AutoScrollSmoothSpeed = next;
                smartUiInitializing = true;
                sldAsSpeed.Value = next;
                smartUiInitializing = false;
                if (autoScrollActive) FireScript("window.__smartAutoScroll&&window.__smartAutoScroll.updateSpeed(" + next + ");");
            }
            smartSuppressSync = false;
            UpdateAutoScrollUi();
        }

        private void UpdateAutoScrollUi()
        {
            try
            {
                bool isStep = SmartSettings.AutoScrollMode == "step";
                pnlAsStepOptions.Visibility = isStep ? Visibility.Visible : Visibility.Collapsed;
                pnlAsSmoothOptions.Visibility = isStep ? Visibility.Collapsed : Visibility.Visible;

                UpdateModeButtonStyle(btnAsModeStep, isStep);
                UpdateModeButtonStyle(btnAsModeSmooth, !isStep);

                double sec = SmartSettings.AutoScrollIntervalSec;
                int pct = SmartSettings.AutoScrollStepPercent;
                int spd = SmartSettings.AutoScrollSmoothSpeed;

                txtAsIntervalVal.Text = sec.ToString("0.#", CultureInfo.InvariantCulture) + " ثانیه";
                txtAsStepVal.Text = pct >= 95 ? "یک صفحه کامل" : (pct + "٪ صفحه");
                txtAsSpeedVal.Text = spd + " پیکسل/ثانیه";

                txtHudSpeedBadge.Text = isStep ? ("هر " + sec.ToString("0.#", CultureInfo.InvariantCulture) + " ثانیه") : (spd + " px/s");

                txtAutoScrollIcon.Text = autoScrollActive ? "⏸" : "▶";
                txtAutoScrollBtn.Text = autoScrollActive ? (isStep ? (sec.ToString("0.#", CultureInfo.InvariantCulture) + "ث") : "در حال اسکرول") : "اسکرول خودکار";
                txtHudAutoScroll.Text = autoScrollActive ? "⏸ توقف اسکرول" : "▶ شروع اسکرول";
                txtAsPopupStatus.Text = autoScrollActive ? "فعال" : "خاموش";
                btnAsPopupToggle.Content = autoScrollActive ? "توقف اسکرول خودکار" : "شروع اسکرول خودکار";

                btnAutoScroll.Background = (Brush)FindResource(autoScrollActive ? "AccentColorDark" : "HoverColor");
                btnAutoScroll.Foreground = autoScrollActive ? Brushes.White : (Brush)FindResource("TextColor");

                if (!autoScrollActive)
                {
                    txtHudCountdown.Text = "";
                }
            }
            catch { }
        }

        // ---- ابزارهای هوشمند: محافظ چشم، خط‌کش تمرکز، کرونومتر و پومودورو ----
        private void btnSmartTools_Click(object sender, RoutedEventArgs e)
        {
            popupSmartTools.IsOpen = !popupSmartTools.IsOpen;
        }

        private void SmartVisual_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (smartUiInitializing || !IsLoaded) return;
            smartSuppressSync = true;
            try
            {
                if (sender == sldEyeCare)
                {
                    int v = (int)Math.Round(sldEyeCare.Value);
                    SmartSettings.EyeCareWarmth = v;
                    txtEyeCareVal.Text = v + "٪";
                }
                else if (sender == sldBrightness)
                {
                    int v = (int)Math.Round(sldBrightness.Value);
                    SmartSettings.ReaderBrightness = v;
                    txtBrightnessVal.Text = v + "٪";
                }
                UpdateSmartToolsUi();
                ApplyEyeCareLive();
            }
            finally
            {
                smartSuppressSync = false;
            }
        }

        private void SmartRuler_Changed(object sender, RoutedEventArgs e)
        {
            if (smartUiInitializing || !IsLoaded) return;
            smartSuppressSync = true;
            SmartSettings.ReadingRulerEnabled = chkReadingRuler.IsChecked == true;
            smartSuppressSync = false;
            UpdateSmartToolsUi();
            ApplyRulerLive();
        }

        private void SmartRuler_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (smartUiInitializing || !IsLoaded) return;
            smartSuppressSync = true;
            try
            {
                int h = (int)Math.Round(sldRulerHeight.Value);
                SmartSettings.ReadingRulerHeight = h;
                txtRulerHeightVal.Text = h + " px";
                ApplyRulerLive();
            }
            finally
            {
                smartSuppressSync = false;
            }
        }

        private void btnHudRuler_Click(object sender, RoutedEventArgs e)
        {
            bool next = !SmartSettings.ReadingRulerEnabled;
            smartSuppressSync = true;
            SmartSettings.ReadingRulerEnabled = next;
            smartSuppressSync = false;
            smartUiInitializing = true;
            chkReadingRuler.IsChecked = next;
            smartUiInitializing = false;
            UpdateSmartToolsUi();
            ApplyRulerLive();
        }

        private void btnHudEyeCare_Click(object sender, RoutedEventArgs e)
        {
            int next = SmartSettings.EyeCareWarmth > 0 ? 0 : 30;
            smartSuppressSync = true;
            SmartSettings.EyeCareWarmth = next;
            smartSuppressSync = false;
            smartUiInitializing = true;
            sldEyeCare.Value = next;
            smartUiInitializing = false;
            UpdateSmartToolsUi();
            ApplyEyeCareLive();
        }

        private void ApplyEyeCareLive()
        {
            try
            {
                FireScript("window.__smartEyeCare&&window.__smartEyeCare(" + SmartSettings.EyeCareWarmth + "," + SmartSettings.ReaderBrightness + ");");
            }
            catch { }
        }

        private void ApplyRulerLive()
        {
            try
            {
                FireScript("window.__smartRuler&&window.__smartRuler(" + (SmartSettings.ReadingRulerEnabled ? "true" : "false") + "," + SmartSettings.ReadingRulerHeight + ");");
            }
            catch { }
        }

        private void UpdateSmartToolsUi()
        {
            try
            {
                txtEyeCareVal.Text = SmartSettings.EyeCareWarmth + "٪";
                txtBrightnessVal.Text = SmartSettings.ReaderBrightness + "٪";
                txtRulerHeightVal.Text = SmartSettings.ReadingRulerHeight + " px";

                btnHudRuler.Background = (Brush)FindResource(SmartSettings.ReadingRulerEnabled ? "AccentColorDark" : "HoverColor");
                btnHudRuler.Foreground = SmartSettings.ReadingRulerEnabled ? Brushes.White : (Brush)FindResource("TextColor");

                bool eyeOn = SmartSettings.EyeCareWarmth > 0;
                btnHudEyeCare.Background = (Brush)FindResource(eyeOn ? "AccentColorDark" : "HoverColor");
                btnHudEyeCare.Foreground = eyeOn ? Brushes.White : (Brush)FindResource("TextColor");
            }
            catch { }
        }

        // ---- کرونومتر مطالعه (Stopwatch) ----
        private void btnStopwatchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (stopwatchRunning)
            {
                stopwatchRunning = false;
                StopStopwatchTimer();
                btnStopwatchToggle.Content = "▶ ادامه‌ی کرونومتر (" + FormatDuration(stopwatchElapsedSec) + ")";
            }
            else
            {
                stopwatchRunning = true;
                SmartHudBar.Visibility = Visibility.Visible;
                if (stopwatchTimer == null)
                {
                    stopwatchTimer = new DispatcherTimer();
                    stopwatchTimer.Interval = TimeSpan.FromSeconds(1);
                    stopwatchTimer.Tick += StopwatchTimer_Tick;
                }
                stopwatchTimer.Start();
                UpdateStopwatchUi();
            }
        }

        private void btnStopwatchReset_Click(object sender, RoutedEventArgs e)
        {
            stopwatchRunning = false;
            StopStopwatchTimer();
            stopwatchElapsedSec = 0;
            btnStopwatchToggle.Content = "⏱ شروع کرونومتر (00:00)";
            txtHudStopwatch.Text = "";
            if (SmartSettings.ReaderUiMode != "smart" && !autoScrollActive && !pomodoroRunning)
            {
                SmartHudBar.Visibility = Visibility.Collapsed;
            }
        }

        private void StopStopwatchTimer()
        {
            if (stopwatchTimer != null)
            {
                stopwatchTimer.Stop();
            }
        }

        private void StopwatchTimer_Tick(object sender, EventArgs e)
        {
            if (!stopwatchRunning) return;
            stopwatchElapsedSec++;
            UpdateStopwatchUi();
        }

        private void UpdateStopwatchUi()
        {
            string formatted = FormatDuration(stopwatchElapsedSec);
            txtHudStopwatch.Text = "⏱ " + formatted;
            btnStopwatchToggle.Content = "⏸ مکث کرونومتر (" + formatted + ")";
        }

        private static string FormatDuration(int totalSeconds)
        {
            int h = totalSeconds / 3600;
            int m = (totalSeconds % 3600) / 60;
            int s = totalSeconds % 60;
            if (h > 0)
            {
                return h.ToString("00") + ":" + m.ToString("00") + ":" + s.ToString("00");
            }
            return m.ToString("00") + ":" + s.ToString("00");
        }

        // ---- تایمر تمرکز پومودورو ----
        private void btnPomodoroToggle_Click(object sender, RoutedEventArgs e)
        {
            if (pomodoroRunning)
            {
                pomodoroRunning = false;
                StopPomodoroTimer();
                btnPomodoroToggle.Content = "🍅 ادامه‌ی پومودورو";
            }
            else
            {
                if (pomodoroRemainingSec <= 0)
                {
                    pomodoroRemainingSec = SmartSettings.PomodoroMinutes * 60;
                }
                pomodoroRunning = true;
                SmartHudBar.Visibility = Visibility.Visible;
                if (pomodoroTimer == null)
                {
                    pomodoroTimer = new DispatcherTimer();
                    pomodoroTimer.Interval = TimeSpan.FromSeconds(1);
                    pomodoroTimer.Tick += PomodoroTimer_Tick;
                }
                pomodoroTimer.Start();
                btnPomodoroToggle.Content = "⏸ توقف موقت پومودورو";
                UpdatePomodoroHud();
            }
        }

        private void btnPomodoroReset_Click(object sender, RoutedEventArgs e)
        {
            pomodoroRunning = false;
            StopPomodoroTimer();
            pomodoroRemainingSec = SmartSettings.PomodoroMinutes * 60;
            btnPomodoroToggle.Content = "🍅 شروع پومودورو (" + SmartSettings.PomodoroMinutes + " دقیقه)";
            txtHudPomodoro.Text = "";
        }

        private void StopPomodoroTimer()
        {
            if (pomodoroTimer != null)
            {
                pomodoroTimer.Stop();
            }
        }

        private void PomodoroTimer_Tick(object sender, EventArgs e)
        {
            if (!pomodoroRunning) return;
            pomodoroRemainingSec--;
            if (pomodoroRemainingSec <= 0)
            {
                pomodoroRunning = false;
                StopPomodoroTimer();
                txtHudPomodoro.Text = "🍅 زمان استراحت!";
                btnPomodoroToggle.Content = "🍅 شروع مجدد پومودورو";
                DarkMessageBox.Show("زمان پومودورو به پایان رسید! ۵ دقیقه به چشمانت استراحت بده.", "تایمر هوشمند مطالعه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            UpdatePomodoroHud();
        }

        private void UpdatePomodoroHud()
        {
            int m = pomodoroRemainingSec / 60;
            int s = pomodoroRemainingSec % 60;
            txtHudPomodoro.Text = "🍅 " + m.ToString("00") + ":" + s.ToString("00");
        }

        // ---- آمار زنده مطالعه، نوار پیشرفت و یادآور استراحت چشم ----
        private void UpdateSmartProgressBar(double fraction)
        {
            try
            {
                if (SmartProgressFill == null || ReaderCard == null) return;
                double maxW = Math.Max(0, ReaderCard.ActualWidth - 36);
                double f = Math.Max(0.0, Math.Min(1.0, fraction));
                SmartProgressFill.Width = maxW * f;
            }
            catch { }
        }

        private async void UpdateSmartEtaAsync()
        {
            try
            {
                string raw = JsonToString(await RunScriptAsync("window.__smartStats?window.__smartStats():''"));
                if (string.IsNullOrEmpty(raw)) return;
                string[] parts = raw.Split('|');
                int words;
                double pct;
                if (parts.Length == 2 && int.TryParse(parts[0], out words) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                {
                    UpdateSmartProgressBar(pct);
                    if (words > 20)
                    {
                        double remWords = words * (1.0 - pct);
                        int mins = Math.Max(1, (int)Math.Ceiling(remWords / Math.Max(80, SmartSettings.SmartWpm)));
                        int pctInt = (int)Math.Round(pct * 100);
                        txtHudEta.Text = "پیشرفت بخش: " + pctInt + "٪ · باقی‌مانده: ~" + mins + " دقیقه";
                    }
                    else
                    {
                        int pctInt = (int)Math.Round(pct * 100);
                        txtHudEta.Text = "پیشرفت صفحه: " + pctInt + "٪";
                    }
                }
            }
            catch { }
        }

        private void TrackSmartReadingTick(int deltaSeconds)
        {
            SmartSettings.AddTodayReadSeconds(deltaSeconds);
            if (SmartSettings.EyeRestReminder)
            {
                eyeRestSeconds += deltaSeconds;
                if (eyeRestSeconds >= 1200)
                {
                    eyeRestSeconds = 0;
                    txtHudCountdown.Text = "👁 ۲۰ ثانیه به دوردست نگاه کن";
                }
            }
        }
    }
}

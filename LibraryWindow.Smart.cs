using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EpubReader.Models;
using EpubReader.Services;

namespace EpubReader
{
    public partial class LibraryWindow
    {
        private bool advInitializing = false;
        private bool advSuppressSync = false;
        private string currentAdvSubTab = "smart";
        private DispatcherTimer searchDebounceTimer;
        private Book smartRecommendedBook;

        private void InitSmartLibrary()
        {
            searchDebounceTimer = new DispatcherTimer();
            searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
            searchDebounceTimer.Tick += (object s, EventArgs e) =>
            {
                searchDebounceTimer.Stop();
                ApplyFilter();
            };

            LoadAdvancedControlsFromSettings();
            ApplyLibraryUiMode(SmartSettings.LibraryUiMode, false);
            SwitchAdvancedSubTab("smart");

            SmartSettings.Changed += OnLibrarySmartSettingsChanged;
            Closed += (object s, EventArgs e) =>
            {
                try { SmartSettings.Changed -= OnLibrarySmartSettingsChanged; } catch { }
                try { if (searchDebounceTimer != null) searchDebounceTimer.Stop(); } catch { }
            };
        }

        private void OnLibrarySmartSettingsChanged(object sender, EventArgs e)
        {
            if (advSuppressSync) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (advSuppressSync) return;
                LoadAdvancedControlsFromSettings();
                if (currentTab == "advanced")
                {
                    RefreshSmartDashboard();
                }
            }));
        }

        private void DebouncedSearchChanged()
        {
            if (searchDebounceTimer == null)
            {
                ApplyFilter();
                return;
            }
            searchDebounceTimer.Stop();
            searchDebounceTimer.Start();
        }

        // ---- سوئیچر هوشمند رابط کاربری کتابخانه (استاندارد / فشرده / پیشرفته) ----
        private void LibUiMode_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            if (btn == null || btn.Tag == null) return;
            ApplyLibraryUiMode(btn.Tag.ToString(), true);
        }

        private void CycleLibraryUiMode()
        {
            string cur = SmartSettings.LibraryUiMode;
            string next = cur == "standard" ? "compact" : (cur == "compact" ? "smart" : "standard");
            ApplyLibraryUiMode(next, true);
        }

        private void ApplyLibraryUiMode(string mode, bool save)
        {
            if (string.IsNullOrEmpty(mode)) mode = "standard";
            if (save)
            {
                advSuppressSync = true;
                SmartSettings.LibraryUiMode = mode;
                advSuppressSync = false;
            }

            bool isCompact = mode == "compact";
            bool isSmart = mode == "smart";

            try
            {
                if (statsStrip != null)
                {
                    statsStrip.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
                }
                if (heroCard != null && isCompact)
                {
                    heroCard.Visibility = Visibility.Collapsed;
                }
                if (isSmart && currentTab != "advanced")
                {
                    currentTab = "advanced";
                    UpdateTabStyles();
                    ApplyFilter();
                }
                else if (!isSmart && currentTab == "advanced" && save)
                {
                    currentTab = "all";
                    UpdateTabStyles();
                    ApplyFilter();
                }
                else if (!isCompact && currentTab != "advanced" && currentTab != "collections" && save)
                {
                    ApplyFilter();
                }

                UpdateLibModeBtn(btnLibModeStandard, mode == "standard");
                UpdateLibModeBtn(btnLibModeCompact, isCompact);
                UpdateLibModeBtn(btnLibModeSmart, isSmart);
            }
            catch { }
        }

        private void UpdateLibModeBtn(Button btn, bool active)
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

        // ---- سوئیچ بین زیربخش‌های «بخش پیشرفته» ----
        private void AdvSubTab_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            if (btn == null || btn.Tag == null) return;
            SwitchAdvancedSubTab(btn.Tag.ToString());
        }

        private void SwitchAdvancedSubTab(string tab)
        {
            currentAdvSubTab = tab ?? "smart";
            try
            {
                pnlAdvSmart.Visibility = currentAdvSubTab == "smart" ? Visibility.Visible : Visibility.Collapsed;
                pnlAdvAutoScroll.Visibility = currentAdvSubTab == "autoscroll" ? Visibility.Visible : Visibility.Collapsed;
                pnlAdvPerf.Visibility = currentAdvSubTab == "perf" ? Visibility.Visible : Visibility.Collapsed;
                pnlAdvEyeCare.Visibility = currentAdvSubTab == "eyecare" ? Visibility.Visible : Visibility.Collapsed;

                UpdateLibModeBtn(btnAdvTabSmart, currentAdvSubTab == "smart");
                UpdateLibModeBtn(btnAdvTabAutoScroll, currentAdvSubTab == "autoscroll");
                UpdateLibModeBtn(btnAdvTabPerf, currentAdvSubTab == "perf");
                UpdateLibModeBtn(btnAdvTabEyeCare, currentAdvSubTab == "eyecare");

                if (currentAdvSubTab == "smart")
                {
                    RefreshSmartDashboard();
                }
            }
            catch { }
        }

        // ---- داشبورد هوشمند کتابخانه ----
        private void RefreshSmartDashboard()
        {
            try
            {
                int goalMin = SmartSettings.DailyGoalMinutes;
                int todayMin = SmartSettings.TodayReadSeconds / 60;
                double pct = Math.Min(1.0, (double)SmartSettings.TodayReadSeconds / Math.Max(60, goalMin * 60));

                txtSmartGoalSummary.Text = todayMin + " از " + goalMin + " دقیقه (" + (int)Math.Round(pct * 100) + "٪)";
                if (barSmartGoalFill != null)
                {
                    barSmartGoalFill.Width = Math.Max(8, 280 * pct);
                }

                Book pick = null;
                if (books != null && books.Count > 0)
                {
                    var visible = books.Where(b => !b.IsPrivate).ToList();
                    pick = visible.Where(b => b.IsInProgress)
                        .OrderByDescending(b => b.LastOpenedDate ?? "")
                        .FirstOrDefault();
                    if (pick == null)
                    {
                        pick = visible.Where(b => !b.IsCompleted)
                            .OrderByDescending(b => b.DateAdded ?? "")
                            .FirstOrDefault();
                    }
                    if (pick == null)
                    {
                        pick = visible.OrderByDescending(b => b.LastOpenedDate ?? "").FirstOrDefault();
                    }
                }

                smartRecommendedBook = pick;
                if (pick != null)
                {
                    txtSmartRecTitle.Text = string.IsNullOrEmpty(pick.DisplayTitle) ? pick.Title : pick.DisplayTitle;
                    txtSmartRecReason.Text = pick.IsInProgress
                        ? ("در حال مطالعه · پیشرفت: " + pick.PercentDisplay + "٪ — آماده‌ی ادامه از آخرین صفحه")
                        : "کتاب پیشنهادی آماده برای شروع مطالعه";
                    btnSmartRecOpen.Visibility = Visibility.Visible;
                }
                else
                {
                    txtSmartRecTitle.Text = "هنوز کتابی در کتابخانه نیست";
                    txtSmartRecReason.Text = "اولین کتاب EPUB، PDF، کمیک یا DOCX خود را اضافه کن تا تحلیل هوشمند فعال شود.";
                    btnSmartRecOpen.Visibility = Visibility.Collapsed;
                }

                string profLabel = SmartSettings.PerfProfile == "turbo" ? "توربو (روان‌ترین)" : (SmartSettings.PerfProfile == "rich" ? "حداکثر جلوه" : "هوشمند (خودکار)");
                string asLabel = SmartSettings.AutoScrollMode == "step"
                    ? ("زمان‌دار · هر " + SmartSettings.AutoScrollIntervalSec.ToString("0.#", CultureInfo.InvariantCulture) + " ثانیه")
                    : ("پیوسته · " + SmartSettings.AutoScrollSmoothSpeed + " px/s");
                txtSmartStatusSummary.Text = "پروفایل عملکرد: " + profLabel + "  |  اسکرول خودکار: " + asLabel + "  |  سرعت مطالعه: " + SmartSettings.SmartWpm + " کلمه/دقیقه";
            }
            catch { }
        }

        private void btnSmartRecOpen_Click(object sender, RoutedEventArgs e)
        {
            if (smartRecommendedBook != null)
            {
                OpenSingleBook(smartRecommendedBook);
            }
        }

        // ---- بارگذاری و ذخیره کنترل‌های بخش پیشرفته ----
        private void LoadAdvancedControlsFromSettings()
        {
            advInitializing = true;
            try
            {
                bool isStep = SmartSettings.AutoScrollMode == "step";
                UpdateLibModeBtn(btnAdvAsStep, isStep);
                UpdateLibModeBtn(btnAdvAsSmooth, !isStep);
                sldAdvAsInterval.Value = SmartSettings.AutoScrollIntervalSec;
                sldAdvAsStep.Value = SmartSettings.AutoScrollStepPercent;
                sldAdvAsSpeed.Value = SmartSettings.AutoScrollSmoothSpeed;
                chkAdvAsAutoNext.IsChecked = SmartSettings.AutoScrollAutoNext;
                chkAdvAsPause.IsChecked = SmartSettings.AutoScrollPauseOnInteract;
                txtAdvAsIntervalVal.Text = SmartSettings.AutoScrollIntervalSec.ToString("0.#", CultureInfo.InvariantCulture) + " ثانیه";
                txtAdvAsStepVal.Text = SmartSettings.AutoScrollStepPercent >= 95 ? "یک صفحه کامل" : (SmartSettings.AutoScrollStepPercent + "٪ صفحه");
                txtAdvAsSpeedVal.Text = SmartSettings.AutoScrollSmoothSpeed + " پیکسل/ثانیه";

                string prof = SmartSettings.PerfProfile;
                UpdateLibModeBtn(btnAdvPerfAuto, prof == "auto");
                UpdateLibModeBtn(btnAdvPerfTurbo, prof == "turbo");
                UpdateLibModeBtn(btnAdvPerfRich, prof == "rich");
                chkAdvSmoothScroll.IsChecked = SmartSettings.SmoothScroll;
                chkAdvCardAnim.IsChecked = SmartSettings.CardAnimations;
                chkAdvLazyImages.IsChecked = SmartSettings.LazyImages;
                chkAdvParticles.IsChecked = ThemeParticleLayer.Enabled;

                chkAdvAutoTheme.IsChecked = SmartSettings.AutoThemeByTime;
                sldAdvEyeCare.Value = SmartSettings.EyeCareWarmth;
                sldAdvBrightness.Value = SmartSettings.ReaderBrightness;
                chkAdvRuler.IsChecked = SmartSettings.ReadingRulerEnabled;
                chkAdvEyeRest.IsChecked = SmartSettings.EyeRestReminder;
                sldAdvDailyGoal.Value = SmartSettings.DailyGoalMinutes;
                sldAdvPomodoro.Value = SmartSettings.PomodoroMinutes;
                sldAdvWpm.Value = SmartSettings.SmartWpm;

                txtAdvEyeCareVal.Text = SmartSettings.EyeCareWarmth + "٪";
                txtAdvBrightnessVal.Text = SmartSettings.ReaderBrightness + "٪";
                txtAdvDailyGoalVal.Text = SmartSettings.DailyGoalMinutes + " دقیقه";
                txtAdvPomodoroVal.Text = SmartSettings.PomodoroMinutes + " دقیقه";
                txtAdvWpmVal.Text = SmartSettings.SmartWpm + " کلمه/دقیقه";
            }
            catch { }
            advInitializing = false;
        }

        private void AdvAsMode_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            if (btn == null || btn.Tag == null) return;
            advSuppressSync = true;
            SmartSettings.AutoScrollMode = btn.Tag.ToString();
            advSuppressSync = false;
            bool isStep = SmartSettings.AutoScrollMode == "step";
            UpdateLibModeBtn(btnAdvAsStep, isStep);
            UpdateLibModeBtn(btnAdvAsSmooth, !isStep);
        }

        private void AdvAsPreset_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            double sec;
            if (btn != null && btn.Tag != null && double.TryParse(btn.Tag.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
            {
                advSuppressSync = true;
                SmartSettings.AutoScrollMode = "step";
                SmartSettings.AutoScrollIntervalSec = sec;
                advSuppressSync = false;
                LoadAdvancedControlsFromSettings();
            }
        }

        // به‌روزرسانی مستقیم و روان هنگام کشیدن اسلایدر بدون بازنویسی مقدار اسلایدر
        private void AdvSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (advInitializing || !IsLoaded) return;
            advSuppressSync = true;
            try
            {
                if (sender == sldAdvAsInterval)
                {
                    double v = Math.Round(sldAdvAsInterval.Value * 2.0) / 2.0;
                    SmartSettings.AutoScrollIntervalSec = v;
                    txtAdvAsIntervalVal.Text = v.ToString("0.#", CultureInfo.InvariantCulture) + " ثانیه";
                }
                else if (sender == sldAdvAsStep)
                {
                    int v = (int)Math.Round(sldAdvAsStep.Value);
                    SmartSettings.AutoScrollStepPercent = v;
                    txtAdvAsStepVal.Text = v >= 95 ? "یک صفحه کامل" : (v + "٪ صفحه");
                }
                else if (sender == sldAdvAsSpeed)
                {
                    int v = (int)Math.Round(sldAdvAsSpeed.Value);
                    SmartSettings.AutoScrollSmoothSpeed = v;
                    txtAdvAsSpeedVal.Text = v + " پیکسل/ثانیه";
                }
                else if (sender == sldAdvEyeCare)
                {
                    int v = (int)Math.Round(sldAdvEyeCare.Value);
                    SmartSettings.EyeCareWarmth = v;
                    txtAdvEyeCareVal.Text = v + "٪";
                }
                else if (sender == sldAdvBrightness)
                {
                    int v = (int)Math.Round(sldAdvBrightness.Value);
                    SmartSettings.ReaderBrightness = v;
                    txtAdvBrightnessVal.Text = v + "٪";
                }
                else if (sender == sldAdvDailyGoal)
                {
                    int v = (int)Math.Round(sldAdvDailyGoal.Value);
                    SmartSettings.DailyGoalMinutes = v;
                    txtAdvDailyGoalVal.Text = v + " دقیقه";
                }
                else if (sender == sldAdvPomodoro)
                {
                    int v = (int)Math.Round(sldAdvPomodoro.Value);
                    SmartSettings.PomodoroMinutes = v;
                    txtAdvPomodoroVal.Text = v + " دقیقه";
                }
                else if (sender == sldAdvWpm)
                {
                    int v = (int)Math.Round(sldAdvWpm.Value);
                    SmartSettings.SmartWpm = v;
                    txtAdvWpmVal.Text = v + " کلمه/دقیقه";
                }
            }
            finally
            {
                advSuppressSync = false;
            }
        }

        private void AdvCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (advInitializing || !IsLoaded) return;
            advSuppressSync = true;
            try
            {
                if (sender == chkAdvAsAutoNext) SmartSettings.AutoScrollAutoNext = chkAdvAsAutoNext.IsChecked == true;
                else if (sender == chkAdvAsPause) SmartSettings.AutoScrollPauseOnInteract = chkAdvAsPause.IsChecked == true;
                else if (sender == chkAdvSmoothScroll) SmartSettings.SmoothScroll = chkAdvSmoothScroll.IsChecked == true;
                else if (sender == chkAdvCardAnim) SmartSettings.CardAnimations = chkAdvCardAnim.IsChecked == true;
                else if (sender == chkAdvLazyImages) SmartSettings.LazyImages = chkAdvLazyImages.IsChecked == true;
                else if (sender == chkAdvParticles) ThemeParticleLayer.Enabled = chkAdvParticles.IsChecked == true;
                else if (sender == chkAdvAutoTheme)
                {
                    SmartSettings.AutoThemeByTime = chkAdvAutoTheme.IsChecked == true;
                    if (SmartPerformanceController.ApplyCircadianThemeIfEnabled())
                    {
                        RefreshAppThemeUi();
                    }
                }
                else if (sender == chkAdvRuler) SmartSettings.ReadingRulerEnabled = chkAdvRuler.IsChecked == true;
                else if (sender == chkAdvEyeRest) SmartSettings.EyeRestReminder = chkAdvEyeRest.IsChecked == true;
            }
            finally
            {
                advSuppressSync = false;
            }
        }

        private void AdvPerfProfile_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            if (btn == null || btn.Tag == null) return;
            advSuppressSync = true;
            SmartSettings.PerfProfile = btn.Tag.ToString();
            SmartPerformanceController.ApplyPerformanceProfile(true);
            advSuppressSync = false;
            LoadAdvancedControlsFromSettings();
        }

        private void btnAdvClearCache_Click(object sender, RoutedEventArgs e)
        {
            int removed = SmartPerformanceController.ClearTempCaches();
            txtAdvCacheStatus.Text = "حافظه و کش بهینه‌سازی شد (" + removed + " پوشه‌ی موقت پاک شد).";
        }
    }
}

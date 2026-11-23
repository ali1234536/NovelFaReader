using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EpubReader.Services
{
    /// <summary>
    /// موتور هوشمندسازی، بهینه‌سازی عملکرد و پل ارتباطی اسکرول خودکار برای همه فرمت‌ها (EPUB, PDF, CBZ/Comic, DOCX/TXT).
    /// </summary>
    public static class SmartSettings
    {
        public static event EventHandler Changed;

        private static void NotifyChanged()
        {
            try
            {
                EventHandler h = Changed;
                if (h != null) h(null, EventArgs.Empty);
            }
            catch { }
        }

        private static bool GetBool(string key, bool def)
        {
            string v = AppSettings.Get(key, def ? "1" : "0");
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static void SetBool(string key, bool val)
        {
            AppSettings.Set(key, val ? "1" : "0");
            NotifyChanged();
        }

        private static int GetInt(string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(AppSettings.Get(key, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                v = def;
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        private static void SetInt(string key, int val, int min, int max)
        {
            if (val < min) val = min;
            if (val > max) val = max;
            AppSettings.Set(key, val.ToString(CultureInfo.InvariantCulture));
            NotifyChanged();
        }

        private static double GetDouble(string key, double def, double min, double max)
        {
            double v;
            if (!double.TryParse(AppSettings.Get(key, def.ToString("0.##", CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                v = def;
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        private static void SetDouble(string key, double val, double min, double max)
        {
            if (val < min) val = min;
            if (val > max) val = max;
            AppSettings.Set(key, val.ToString("0.##", CultureInfo.InvariantCulture));
            NotifyChanged();
        }

        // ---- پروفایل عملکرد و روان‌سازی ----
        // "auto" (هوشمند)، "turbo" (حداکثر روانی)، "rich" (حداکثر جلوه)
        public static string PerfProfile
        {
            get { return AppSettings.Get("Smart.PerfProfile", "auto"); }
            set { AppSettings.Set("Smart.PerfProfile", value ?? "auto"); NotifyChanged(); }
        }

        public static bool SmoothScroll
        {
            get { return GetBool("Smart.SmoothScroll", true); }
            set { SetBool("Smart.SmoothScroll", value); }
        }

        public static bool CardAnimations
        {
            get { return GetBool("Smart.CardAnimations", !PerfHint.LowEnd); }
            set { SetBool("Smart.CardAnimations", value); }
        }

        public static bool LazyImages
        {
            get { return GetBool("Smart.LazyImages", true); }
            set { SetBool("Smart.LazyImages", value); }
        }

        // ---- سوئیچر رابط کاربری هوشمند ----
        // کتابخانه: "standard" | "compact" | "smart"
        public static string LibraryUiMode
        {
            get { return AppSettings.Get("Smart.LibraryUiMode", "standard"); }
            set { AppSettings.Set("Smart.LibraryUiMode", value ?? "standard"); NotifyChanged(); }
        }

        // صفحه مطالعه: "standard" | "focus" | "smart"
        public static string ReaderUiMode
        {
            get { return AppSettings.Get("Smart.ReaderUiMode", "standard"); }
            set { AppSettings.Set("Smart.ReaderUiMode", value ?? "standard"); NotifyChanged(); }
        }

        // ---- اسکرول خودکار سراسری برای همه فرمت‌ها ----
        // "step" (زمان‌دار مثلاً هر ۲ ثانیه) | "smooth" (اسکرول نرم پیوسته)
        public static string AutoScrollMode
        {
            get { return AppSettings.Get("Smart.AutoScrollMode", "step"); }
            set { AppSettings.Set("Smart.AutoScrollMode", value ?? "step"); NotifyChanged(); }
        }

        public static double AutoScrollIntervalSec
        {
            get { return GetDouble("Smart.AutoScrollIntervalSec", 2.0, 0.5, 30.0); }
            set { SetDouble("Smart.AutoScrollIntervalSec", value, 0.5, 30.0); }
        }

        public static int AutoScrollStepPercent
        {
            get { return GetInt("Smart.AutoScrollStepPercent", 45, 10, 100); }
            set { SetInt("Smart.AutoScrollStepPercent", value, 10, 100); }
        }

        public static int AutoScrollSmoothSpeed
        {
            get { return GetInt("Smart.AutoScrollSmoothSpeed", 45, 10, 240); }
            set { SetInt("Smart.AutoScrollSmoothSpeed", value, 10, 240); }
        }

        public static bool AutoScrollAutoNext
        {
            get { return GetBool("Smart.AutoScrollAutoNext", true); }
            set { SetBool("Smart.AutoScrollAutoNext", value); }
        }

        public static bool AutoScrollPauseOnInteract
        {
            get { return GetBool("Smart.AutoScrollPauseOnInteract", true); }
            set { SetBool("Smart.AutoScrollPauseOnInteract", value); }
        }

        // ---- هوشمندسازی و محافظ چشم ----
        public static bool AutoThemeByTime
        {
            get { return GetBool("Smart.AutoThemeByTime", false); }
            set { SetBool("Smart.AutoThemeByTime", value); }
        }

        public static int EyeCareWarmth
        {
            get { return GetInt("Smart.EyeCareWarmth", 0, 0, 60); }
            set { SetInt("Smart.EyeCareWarmth", value, 0, 60); }
        }

        public static int ReaderBrightness
        {
            get { return GetInt("Smart.ReaderBrightness", 100, 45, 100); }
            set { SetInt("Smart.ReaderBrightness", value, 45, 100); }
        }

        public static bool ReadingRulerEnabled
        {
            get { return GetBool("Smart.ReadingRuler", false); }
            set { SetBool("Smart.ReadingRuler", value); }
        }

        public static int ReadingRulerHeight
        {
            get { return GetInt("Smart.ReadingRulerHeight", 82, 44, 160); }
            set { SetInt("Smart.ReadingRulerHeight", value, 44, 160); }
        }

        public static int SmartWpm
        {
            get { return GetInt("Smart.Wpm", 210, 80, 600); }
            set { SetInt("Smart.Wpm", value, 80, 600); }
        }

        public static int DailyGoalMinutes
        {
            get { return GetInt("Smart.DailyGoalMin", 30, 5, 300); }
            set { SetInt("Smart.DailyGoalMin", value, 5, 300); }
        }

        public static int PomodoroMinutes
        {
            get { return GetInt("Smart.PomodoroMin", 25, 5, 90); }
            set { SetInt("Smart.PomodoroMin", value, 5, 90); }
        }

        public static bool EyeRestReminder
        {
            get { return GetBool("Smart.EyeRestReminder", false); }
            set { SetBool("Smart.EyeRestReminder", value); }
        }
        public static int TodayReadSeconds
        {
            get
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (AppSettings.Get("Smart.TodayDate", "") != today)
                {
                    return 0;
                }
                return GetInt("Smart.TodaySeconds", 0, 0, 86400);
            }
        }

        public static void AddTodayReadSeconds(int deltaSec)
        {
            if (deltaSec <= 0) return;
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int cur = 0;
            if (AppSettings.Get("Smart.TodayDate", "") == today)
            {
                cur = GetInt("Smart.TodaySeconds", 0, 0, 86400);
            }
            AppSettings.Set("Smart.TodayDate", today);
            AppSettings.Set("Smart.TodaySeconds", Math.Min(86400, cur + deltaSec).ToString(CultureInfo.InvariantCulture));
        }

        public static bool IsTurboMode
        {
            get { return string.Equals(PerfProfile, "turbo", StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// مدیریت هوشمند عملکرد، تم بر اساس ساعت شبانه‌روز و پاکسازی کش.
    /// </summary>
    public static class SmartPerformanceController
    {
        private static DispatcherTimer circadianTimer;

        public static void Initialize()
        {
            ApplyPerformanceProfile(false);
            ApplyCircadianThemeIfEnabled();

            if (circadianTimer == null)
            {
                circadianTimer = new DispatcherTimer();
                circadianTimer.Interval = TimeSpan.FromMinutes(15);
                circadianTimer.Tick += (object s, EventArgs e) => ApplyCircadianThemeIfEnabled();
                circadianTimer.Start();
            }
        }

        public static void ApplyPerformanceProfile(bool userTriggered)
        {
            try
            {
                string prof = SmartSettings.PerfProfile;
                if (prof == "turbo")
                {
                    Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 30 });
                }
            }
            catch
            {
                // OverrideMetadata فقط یک‌بار در طول عمر برنامه قابل فراخوانی است؛ در تغییر بعدی نادیده گرفته می‌شود
            }

            try
            {
                string prof = SmartSettings.PerfProfile;
                if (prof == "turbo")
                {
                    ThemeParticleLayer.Enabled = false;
                    if (userTriggered)
                    {
                        MouseFx.Mode = 0;
                        SmartSettings.CardAnimations = false;
                    }
                }
                else if (prof == "rich")
                {
                    ThemeParticleLayer.Enabled = true;
                    if (userTriggered)
                    {
                        SmartSettings.CardAnimations = true;
                    }
                }
                else
                {
                    // auto: هوشمند بر اساس توان سیستم
                    bool low = PerfHint.LowEnd;
                    ThemeParticleLayer.Enabled = !low;
                    if (userTriggered)
                    {
                        SmartSettings.CardAnimations = !low;
                    }
                }
            }
            catch { }
        }

        public static bool ApplyCircadianThemeIfEnabled()
        {
            if (!SmartSettings.AutoThemeByTime) return false;
            try
            {
                int hour = DateTime.Now.Hour;
                string desired = (hour >= 7 && hour < 19) ? "light" : "dark";
                if (!string.Equals(AppSettings.Theme, desired, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeManager.Apply(desired);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static int ClearTempCaches()
        {
            int removed = 0;
            try
            {
                string tempRoot = Path.Combine(Path.GetTempPath(), "EpubReaderPages");
                if (Directory.Exists(tempRoot))
                {
                    foreach (string dir in Directory.GetDirectories(tempRoot))
                    {
                        try
                        {
                            if ((DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir)).TotalHours > 1)
                            {
                                Directory.Delete(dir, true);
                                removed++;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                GC.WaitForPendingFinalizers();
            }
            catch { }

            return removed;
        }
    }

    /// <summary>
    /// اسکریپت یکپارچه برای همه فرمت‌ها در WebView2 (EPUB, PDF, CBZ/Comic, DOCX/TXT):
    /// اسکرول خودکار زمان‌دار و پیوسته، فیلتر محافظ چشم، خط‌کش تمرکز هوشمند و آمار لحظه‌ای.
    /// </summary>
    public static class SmartWebBridge
    {
        public static readonly string UniversalScript = @"(function(){
if(window.__smartReady){return;}
window.__smartReady=1;
function post(m){try{window.chrome.webview.postMessage(m);}catch(x){}}
function getScrollTop(){return Math.max(document.documentElement.scrollTop||0,document.body.scrollTop||0,window.pageYOffset||0);}
function setScrollTop(v){if(v<0)v=0;document.documentElement.scrollTop=v;document.body.scrollTop=v;}
function getScrollHeight(){return Math.max(document.documentElement.scrollHeight||0,document.body.scrollHeight||0);}
function getClientHeight(){return document.documentElement.clientHeight||window.innerHeight||document.body.clientHeight||600;}

// 1. موتور اسکرول نرم و زمان‌دار سراسری برای تمام فرمت‌ها
var animId=0, contId=0, contSpeed=0, contAutoNext=true, contCarry=0, contLastTs=0;
var pauseUntil=0, pauseOnInteract=true, endNotified=false;

function markUserInteract(){
  if(!pauseOnInteract)return;
  var now=Date.now();
  pauseUntil=now+2500;
  if(animId){cancelAnimationFrame(animId);animId=0;}
  post('as_interact');
}
window.addEventListener('wheel',markUserInteract,{passive:true});
window.addEventListener('touchstart',markUserInteract,{passive:true});
window.addEventListener('mousedown',function(e){if(e.button===0)markUserInteract();},{passive:true});

function smoothScrollBy(delta,duration){
  if(animId){cancelAnimationFrame(animId);animId=0;}
  var startY=getScrollTop();
  var maxY=Math.max(0,getScrollHeight()-getClientHeight());
  var targetY=Math.min(maxY,Math.max(0,startY+delta));
  if(Math.abs(targetY-startY)<1){return false;}
  var startTs=0;
  function frame(ts){
    if(!startTs)startTs=ts;
    var p=Math.min(1,(ts-startTs)/duration);
    var ease=1-Math.pow(1-p,3);
    setScrollTop(startY+(targetY-startY)*ease);
    if(p<1){animId=requestAnimationFrame(frame);}else{animId=0;}
  }
  animId=requestAnimationFrame(frame);
  return true;
}

window.__smartAutoScroll={
  setInteractPause:function(on){pauseOnInteract=!!on;},
  step:function(stepFraction,smooth,autoNext){
    if(pauseOnInteract && Date.now()<pauseUntil){return 'paused';}
    // اگر در حالت تک‌صفحه یا دوصفحه‌ی PDF/Comic هستیم، مستقیم صفحه بعد برو
    if(typeof window.__smartIsPagedMode==='function' && window.__smartIsPagedMode()){
      if(typeof window.__pdfStep==='function'){window.__pdfStep(1);return 'paged';}
    }
    var st=getScrollTop(), sh=getScrollHeight(), ch=getClientHeight();
    var atBottom=(st+ch)>=(sh-10);
    if(atBottom){
      if(autoNext){post('as_end');return 'end';}
      return 'bottom';
    }
    var frac=Math.max(0.1,Math.min(1.0,stepFraction||0.45));
    var delta=Math.round(ch*frac);
    if(delta<40)delta=40;
    if(smooth){
      smoothScrollBy(delta,420);
    }else{
      setScrollTop(Math.min(sh-ch,st+delta));
    }
    return 'ok';
  },
  startContinuous:function(pxPerSec,autoNext,interactPause){
    this.stopContinuous();
    contSpeed=Math.max(10,Math.min(300,pxPerSec||45));
    contAutoNext=!!autoNext;
    pauseOnInteract=!!interactPause;
    contCarry=0; contLastTs=0; endNotified=false;
    function tick(ts){
      if(!contSpeed){contId=0;return;}
      if(!contLastTs){contLastTs=ts;contId=requestAnimationFrame(tick);return;}
      var dt=Math.min(0.1,(ts-contLastTs)/1000);
      contLastTs=ts;
      if(!(pauseOnInteract && Date.now()<pauseUntil)){
        if(typeof window.__smartIsPagedMode==='function' && window.__smartIsPagedMode()){
          contCarry+=dt;
          if(contCarry>=Math.max(1.0,60/contSpeed)){
            contCarry=0;
            if(typeof window.__pdfStep==='function'){window.__pdfStep(1);}
          }
        }else{
          var st=getScrollTop(), sh=getScrollHeight(), ch=getClientHeight();
          if((st+ch)>=(sh-6)){
            if(contAutoNext && !endNotified){endNotified=true;post('as_end');}
          }else{
            endNotified=false;
            contCarry+=contSpeed*dt;
            var move=Math.floor(contCarry);
            if(move>=1){
              contCarry-=move;
              setScrollTop(st+move);
            }
          }
        }
      }
      contId=requestAnimationFrame(tick);
    }
    contId=requestAnimationFrame(tick);
  },
  updateSpeed:function(pxPerSec){contSpeed=Math.max(10,Math.min(300,pxPerSec||45));},
  stopContinuous:function(){
    contSpeed=0;
    if(contId){cancelAnimationFrame(contId);contId=0;}
    if(animId){cancelAnimationFrame(animId);animId=0;}
  }
};

// 2. فیلتر محافظ چشم (نور آبی) و روشنایی برای همه فرمت‌ها
window.__smartEyeCare=function(warmth,brightness){
  warmth=Math.max(0,Math.min(60,warmth||0));
  brightness=Math.max(45,Math.min(100,brightness||100));
  var el=document.getElementById('__smartEyeOverlay');
  if(warmth===0 && brightness>=100){if(el)el.remove();return;}
  if(!el){
    el=document.createElement('div');
    el.id='__smartEyeOverlay';
    el.style.cssText='position:fixed;inset:0;pointer-events:none;z-index:2147483000;transition:background-color .25s ease;';
    document.documentElement.appendChild(el);
  }
  var dim=(100-brightness)/100*0.65;
  var warmAlpha=(warmth/60)*0.26;
  if(warmth>0 && dim>0){
    el.style.background='linear-gradient(rgba(255,165,55,'+warmAlpha.toFixed(3)+'),rgba(255,165,55,'+warmAlpha.toFixed(3)+')),rgba(0,0,0,'+dim.toFixed(3)+')';
  }else if(warmth>0){
    el.style.background='rgba(255,165,55,'+warmAlpha.toFixed(3)+')';
  }else{
    el.style.background='rgba(0,0,0,'+dim.toFixed(3)+')';
  }
};

// 3. خط‌کش تمرکز هوشمند (Reading Focus Spotlight)
var rulerOn=false, rulerH=82, rulerY=260, rulerRaf=0;
function updateRulerPos(){
  rulerRaf=0;
  var topEl=document.getElementById('__smartRulerTop');
  var botEl=document.getElementById('__smartRulerBot');
  if(!topEl||!botEl)return;
  var ch=getClientHeight();
  var topH=Math.max(0,Math.round(rulerY-rulerH/2));
  var botTop=Math.min(ch,topH+rulerH);
  topEl.style.height=topH+'px';
  botEl.style.top=botTop+'px';
}
window.addEventListener('mousemove',function(e){
  if(!rulerOn)return;
  rulerY=e.clientY;
  if(!rulerRaf)rulerRaf=requestAnimationFrame(updateRulerPos);
},{passive:true});

window.__smartRuler=function(enabled,heightPx){
  rulerOn=!!enabled;
  if(heightPx)rulerH=Math.max(44,Math.min(180,heightPx));
  var t=document.getElementById('__smartRulerTop');
  var b=document.getElementById('__smartRulerBot');
  if(!rulerOn){if(t)t.remove();if(b)b.remove();return;}
  if(!t){
    t=document.createElement('div'); t.id='__smartRulerTop';
    t.style.cssText='position:fixed;left:0;right:0;top:0;pointer-events:none;z-index:2147482900;background:rgba(0,0,0,0.44);border-bottom:1.5px solid rgba(167,139,250,0.55);';
    document.documentElement.appendChild(t);
  }
  if(!b){
    b=document.createElement('div'); b.id='__smartRulerBot';
    b.style.cssText='position:fixed;left:0;right:0;bottom:0;pointer-events:none;z-index:2147482900;background:rgba(0,0,0,0.44);border-top:1.5px solid rgba(167,139,250,0.55);';
    document.documentElement.appendChild(b);
  }
  rulerY=Math.round(getClientHeight()*0.38);
  updateRulerPos();
};

// 5. محاسبه آمار هوشمند صفحه (تعداد کلمات و پیشرفت)
window.__smartStats=function(){
  try{
    var txt=(document.body&&document.body.innerText||'').trim();
    var words=txt?txt.split(/\s+/).length:0;
    var st=getScrollTop(), sh=getScrollHeight(), ch=getClientHeight();
    var maxS=Math.max(1,sh-ch);
    var pct=Math.max(0,Math.min(1,st/maxS));
    return words+'|'+pct.toFixed(4);
  }catch(x){return '0|0';}
};
})();";
    }
}

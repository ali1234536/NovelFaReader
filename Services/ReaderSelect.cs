using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace EpubReader.Services
{
    // منوی انتخاب متن (مثل ReadEra / اندروید): کپی، نقل‌قول (ذخیره در لیست کتاب) و ترجمه (باز کردن مترجم گوگل)
    public static class ReaderSelect
    {
        public const string PageScript = @"(function(){
if(window.__selUI){return;}window.__selUI=1;
function post(m){try{window.chrome.webview.postMessage(m);}catch(x){}}
var L=window.__selL||{q:'Quote',t:'Translate',c:'Copy'};
var bar=null,tm=null;
function hide(){if(bar&&bar.parentNode){bar.parentNode.removeChild(bar);}bar=null;}
function txt(){var s=window.getSelection();return s?String(s.toString()).replace(/\s+/g,' ').trim():'';}
function mk(label,fn){
  var b=document.createElement('button');b.type='button';b.textContent=label;
  b.style.cssText='background:transparent;border:0;color:#f2f2f2;font:600 13px Segoe UI,Tahoma,sans-serif;padding:7px 12px;border-radius:8px;cursor:pointer;';
  b.onmouseenter=function(){b.style.background='rgba(255,255,255,.16)';};b.onmouseleave=function(){b.style.background='transparent';};
  b.onmousedown=function(e){e.preventDefault();e.stopPropagation();};
  b.onclick=function(e){e.preventDefault();e.stopPropagation();fn();};
  return b;
}
function show(){
  var s=window.getSelection();if(!s||s.rangeCount===0||s.isCollapsed){hide();return;}
  var t=txt();if(t.length<2){hide();return;}
  var r=s.getRangeAt(0).getBoundingClientRect();if(!r||(!r.width&&!r.height)){hide();return;}
  hide();
  bar=document.createElement('div');bar.id='selbar';bar.setAttribute('dir','ltr');
  bar.style.cssText='position:fixed;z-index:2147482500;display:flex;gap:2px;padding:4px;border-radius:12px;background:rgba(30,30,32,.96);border:1px solid rgba(255,255,255,.14);box-shadow:0 8px 28px rgba(0,0,0,.5);visibility:hidden;left:0;top:0;';
  bar.appendChild(mk(L.c,function(){try{navigator.clipboard.writeText(t);}catch(e){document.execCommand('copy');}hide();}));
  bar.appendChild(mk(L.q,function(){post('qt|'+encodeURIComponent(t.slice(0,2000)));hide();try{window.getSelection().removeAllRanges();}catch(e){}}));
  bar.appendChild(mk(L.t,function(){post('tr|'+encodeURIComponent(t.slice(0,1500)));hide();}));
  document.body.appendChild(bar);
  var w=bar.offsetWidth,h=bar.offsetHeight,x=r.left+r.width/2-w/2,y=r.top-h-8;
  if(y<6){y=r.bottom+8;}
  x=Math.max(6,Math.min(innerWidth-w-6,x));y=Math.max(6,Math.min(innerHeight-h-6,y));
  bar.style.left=x+'px';bar.style.top=y+'px';bar.style.visibility='visible';
}
document.addEventListener('mouseup',function(e){if(bar&&bar.contains(e.target)){return;}clearTimeout(tm);tm=setTimeout(show,60);},true);
document.addEventListener('keyup',function(e){if(e.shiftKey){clearTimeout(tm);tm=setTimeout(show,120);}},true);
document.addEventListener('mousedown',function(e){if(bar&&!bar.contains(e.target)){hide();}},true);
window.addEventListener('scroll',function(){hide();},{passive:true,capture:true});
window.__selSetL=function(l){L=l;};
})();";

        public static string LabelsScript()
        {
            return "window.__selL={c:" + ReaderFind.JsStr(Loc.T("کپی")) + ",q:" + ReaderFind.JsStr(Loc.T("نقل‌قول")) + ",t:" + ReaderFind.JsStr(Loc.T("ترجمه")) + "};";
        }

        public static void Translate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            try
            {
                string tl = Loc.IsEnglish ? "en" : "fa";
                string url = "https://translate.google.com/?sl=auto&tl=" + tl + "&op=translate&text=" + Uri.EscapeDataString(text);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
            }
        }
    }

    public sealed class QuoteItem
    {
        public string Date;
        public string Chapter;
        public string Text;
    }

    // نقل‌قول‌های هر کتاب، در AppSettings (هر کتاب یک کلید؛ هر مورد: تاریخ|base64(بخش)|base64(متن))
    public static class QuoteStore
    {
        private const int MaxPerBook = 300;

        private static string Key(string bookId)
        {
            return "Quotes." + bookId;
        }

        private static string B64(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static string FromB64(string s)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch
            {
                return "";
            }
        }

        public static List<QuoteItem> Load(string bookId)
        {
            List<QuoteItem> list = new List<QuoteItem>();
            if (string.IsNullOrEmpty(bookId))
            {
                return list;
            }
            string raw = AppSettings.Get(Key(bookId), "");
            foreach (string part in raw.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = part.Split('|');
                if (f.Length != 3)
                {
                    continue;
                }
                QuoteItem q = new QuoteItem();
                q.Date = f[0];
                q.Chapter = FromB64(f[1]);
                q.Text = FromB64(f[2]);
                if (q.Text.Length > 0)
                {
                    list.Add(q);
                }
            }
            return list;
        }

        public static void Save(string bookId, List<QuoteItem> list)
        {
            if (string.IsNullOrEmpty(bookId))
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            int start = Math.Max(0, list.Count - MaxPerBook);
            for (int i = start; i < list.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append(';');
                }
                sb.Append(list[i].Date).Append('|').Append(B64(list[i].Chapter)).Append('|').Append(B64(list[i].Text));
            }
            AppSettings.Set(Key(bookId), sb.ToString());
            AppSettings.Save();
        }

        // false = این متن از قبل ذخیره بوده
        public static bool Add(string bookId, string chapter, string text)
        {
            if (string.IsNullOrEmpty(bookId) || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            List<QuoteItem> list = Load(bookId);
            foreach (QuoteItem q in list)
            {
                if (q.Text == text)
                {
                    return false;
                }
            }
            QuoteItem n = new QuoteItem();
            n.Date = DateTime.Now.ToString("yyyy-MM-dd");
            n.Chapter = chapter ?? "";
            n.Text = text;
            list.Add(n);
            Save(bookId, list);
            return true;
        }
    }
}

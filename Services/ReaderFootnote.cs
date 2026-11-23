using System;
using System.Text.RegularExpressions;

namespace EpubReader.Services
{
    // پنجره‌ی پاورقی: روی شماره‌ی پاورقی بزنی، متنش همون‌جا کنار شماره باز می‌شه (مثل Moon+ Reader)
    public static class ReaderFootnote
    {
        public const string SettingKey = "FootnotePopup";

        public static bool Enabled
        {
            get { return AppSettings.Get(SettingKey, "1") != "0"; }
            set { AppSettings.Set(SettingKey, value ? "1" : "0"); }
        }

        // اسکریپت داخل صفحه‌ی EPUB: تشخیص لینک پاورقی و نمایش حباب
        public const string PageScript = @"(function(){
if(window.__fnUI){return;}window.__fnUI=1;
function post(m){try{window.chrome.webview.postMessage(m);}catch(x){}}
var pop=null;
var FRAG=/(?:^|[-_.])(?:fn|footnote|note|endnote)[-_]?[0-9]+[a-z]?$/i;
function fragOf(h){var i=h.indexOf('#');var f=i>=0?h.substring(i+1):'';try{f=decodeURIComponent(f);}catch(e){}return f;}
function isNote(a,h){
  try{
    var t=(a.getAttribute('epub:type')||'')+' '+(a.getAttribute('role')||'');
    if(/noteref/i.test(t)){return true;}
    if(/(^|\s)(footnote|noteref|fn|note)(\s|$)/i.test(a.className||'')){return true;}
    var f=fragOf(h);if(f&&FRAG.test(f)){return true;}
  }catch(e){}
  return false;
}
function closePop(){if(pop&&pop.parentNode){pop.parentNode.removeChild(pop);}pop=null;}
function clean(html){
  var d;
  try{d=new DOMParser().parseFromString('<div>'+html+'</div>','text/html');}catch(e){return '';}
  var bad=d.querySelectorAll('script,style,iframe,object,embed,link,meta,form');
  for(var i=0;i<bad.length;i++){bad[i].parentNode.removeChild(bad[i]);}
  var all=d.body.querySelectorAll('*');
  for(var j=0;j<all.length;j++){
    var el=all[j],at=el.attributes;
    for(var k=at.length-1;k>=0;k--){
      var n=at[k].name.toLowerCase(),v=String(at[k].value||'');
      if(n.indexOf('on')===0||((n==='href'||n==='src')&&/^\s*javascript:/i.test(v))){el.removeAttribute(at[k].name);}
    }
  }
  return d.body.firstChild?d.body.firstChild.innerHTML:'';
}
function show(html,x,y){
  closePop();
  var cs=getComputedStyle(document.body);
  var bg=cs.backgroundColor;if(!bg||bg==='transparent'||bg==='rgba(0, 0, 0, 0)'){bg='#222';}
  pop=document.createElement('div');pop.id='fnpop';
  pop.setAttribute('dir','auto');
  pop.style.cssText='position:fixed;z-index:2147482000;box-sizing:border-box;max-width:min(460px,calc(100vw - 24px));min-width:180px;max-height:42vh;overflow:auto;padding:12px 16px;border-radius:14px;border:1px solid rgba(128,128,128,.45);box-shadow:0 10px 36px rgba(0,0,0,.4);font-size:.92em;line-height:1.95;text-align:start;left:0;top:0;visibility:hidden;';
  pop.style.background=bg;pop.style.color=cs.color;pop.style.fontFamily=cs.fontFamily;
  pop.innerHTML=clean(html);
  var as=pop.querySelectorAll('a');
  for(var i=0;i<as.length;i++){
    var a=as[i],tx=(a.textContent||'').trim(),t=(a.getAttribute('epub:type')||'')+' '+(a.className||'');
    if(tx.indexOf('↩')>=0||/backlink|footnote-back|fnback|back/i.test(t)){a.parentNode.removeChild(a);}
  }
  document.documentElement.appendChild(pop);
  var vw=window.innerWidth,vh=window.innerHeight,w=pop.offsetWidth,h=pop.offsetHeight;
  var left=Math.max(12,Math.min(vw-w-12,x-w/2));
  var top=(y+18+h<vh)?y+18:Math.max(12,y-h-14);
  pop.style.left=left+'px';pop.style.top=top+'px';pop.style.visibility='visible';
}
function findInDom(scope,id){
  var l=scope.querySelectorAll('[id],[name]');
  for(var i=0;i<l.length;i++){if(l[i].id===id||l[i].getAttribute('name')===id){return l[i];}}
  return null;
}
function noteHtml(el){
  var n=el,t=n.tagName;
  if(t==='A'||t==='SPAN'||t==='SUP'){
    var p=n.closest?n.closest('li,p,aside,div,section,dd'):null;if(p){n=p;}
  }
  var h=n.innerHTML;return h?h.trim():'';
}
window.__rdrNote=function(b64,x,y){
  try{show(decodeURIComponent(escape(atob(b64))),x,y);}catch(e){}
};
document.addEventListener('click',function(e){
  var a=e.target;
  if(pop&&!pop.contains(a)){closePop();}
  if(!window.__rdrFn){return;}
  while(a&&a.tagName!=='A'){a=a.parentNode;}
  if(!a||!a.getAttribute||(pop&&pop.contains(a))){return;}
  var h=a.getAttribute('href');if(!h||/^(https?|mailto|tel|javascript):/i.test(h)){return;}
  if(!isNote(a,h)){return;}
  var x=e.clientX||0,y=e.clientY||0;
  if(h.charAt(0)==='#'){
    var cc=a.closest?a.closest('.rdr-chap'):null;
    var el=findInDom(cc||document,fragOf(h));
    if(el){var html=noteHtml(el);if(html){e.preventDefault();e.stopImmediatePropagation();show(html,x,y);return;}}
    return;
  }
  e.preventDefault();e.stopImmediatePropagation();
  post('fp|'+encodeURIComponent(a.href)+'|'+Math.round(x)+'|'+Math.round(y));
},true);
document.addEventListener('keydown',function(e){
  if(e.keyCode===27&&pop){e.preventDefault();e.stopImmediatePropagation();closePop();}
},true);
window.addEventListener('scroll',function(e){if(pop&&!(e.target&&pop.contains&&pop.contains(e.target))){closePop();}},true);
})();
";

        private static readonly Regex StartTag = new Regex("^[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);

        // متن داخل المنتی که id (یا name) اون fragId هست؛ اگه روی یه <a> خالی باشه، پاراگراف دورش رو برمی‌داره
        public static string ExtractNoteHtml(string html, string fragId)
        {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(fragId)) { return null; }
            Match idAt = Regex.Match(html, "\\b(?:id|name)\\s*=\\s*[\"']" + Regex.Escape(fragId) + "[\"']", RegexOptions.IgnoreCase);
            if (!idAt.Success) { return null; }
            int tagStart = html.LastIndexOf('<', idAt.Index);
            if (tagStart < 0 || tagStart + 1 >= html.Length || html[tagStart + 1] == '/') { return null; }
            Match nm = StartTag.Match(html.Substring(tagStart + 1, Math.Min(32, html.Length - tagStart - 1)));
            if (!nm.Success) { return null; }
            string tag = nm.Value;
            int contentStart = html.IndexOf('>', idAt.Index + idAt.Length);
            if (contentStart < 0) { return null; }
            string lower = tag.ToLowerInvariant();
            if (lower == "a" || lower == "span" || lower == "sup")
            {
                // مرجع خالی داخل پاراگراف پاورقی: خودِ پاراگراف رو بگیر
                int best = -1;
                string bestName = null;
                foreach (Match m in Regex.Matches(html.Substring(0, tagStart), "<(p|li|aside|div|section|dd)\\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    best = m.Index + m.Length;
                    bestName = m.Groups[1].Value;
                }
                if (best < 0) { return null; }
                tag = bestName;
                contentStart = best - 1;
            }
            Match close = Regex.Match(html.Substring(contentStart + 1), "</" + Regex.Escape(tag) + "\\s*>", RegexOptions.IgnoreCase);
            if (!close.Success) { return null; }
            string inner = html.Substring(contentStart + 1, close.Index).Trim();
            if (inner.Length == 0 || inner.Length > 40000) { return null; }
            return inner;
        }
    }
}

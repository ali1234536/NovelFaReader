using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace EpubReader.Services
{
    // جستجوی داخل کتاب: هم‌ارز کردن حروف فارسی/عربی (ی/ي، ک/ك، اعراب، نیم‌فاصله، ارقام) و پیدا کردن فصل‌های دارای نتیجه
    public static class ReaderFind
    {
        // موتور هایلایت + نوار جستجوی داخل صفحه (برای صفحه‌ی EPUB)
        public const string PageScript = @"(function(){
if(window.__fnd){return;}
var BLK={P:1,DIV:1,LI:1,H1:1,H2:1,H3:1,H4:1,H5:1,H6:1,TD:1,TH:1,TR:1,BLOCKQUOTE:1,PRE:1,SECTION:1,ARTICLE:1,BR:1,UL:1,OL:1,TABLE:1,BODY:1,FIGURE:1,FIGCAPTION:1};
var MAP={'\u064A':'\u06CC','\u0649':'\u06CC','\u06D2':'\u06CC','\u0643':'\u06A9','\u0623':'\u0627','\u0625':'\u0627','\u0622':'\u0627','\u0671':'\u0627','\u0624':'\u0648','\u0629':'\u0647','\u06C0':'\u0647','\u06C1':'\u0647','\u06BE':'\u0647'};
function nc(ch){
  var c=ch.charCodeAt(0);
  if((c>=0x064B&&c<=0x065F)||c===0x0670||c===0x0640||c===0x200C||c===0x200D||c===0x00AD||c===0x200E||c===0x200F||c===0x061C){return '';}
  if(c>=0x06F0&&c<=0x06F9){return String.fromCharCode(c-0x06F0+48);}
  if(c>=0x0660&&c<=0x0669){return String.fromCharCode(c-0x0660+48);}
  if(c===0x00A0||c===0x2009||c===0x202F){return ' ';}
  if(MAP[ch]){return MAP[ch];}
  return ch.toLowerCase();
}
function norm(s){var o='';for(var i=0;i<s.length;i++){o+=nc(s.charAt(i));}return o.replace(/\s+/g,' ').trim();}
var st={q:'',marks:[],cur:-1,root:null};
function blockOf(n){var p=n.parentNode;while(p&&p.nodeType===1&&!BLK[p.tagName]){p=p.parentNode;}return p;}
function clear(){
  var ms=document.querySelectorAll('mark.fnd');
  var ps=[];
  for(var i=0;i<ms.length;i++){var m=ms[i],p=m.parentNode;if(!p){continue;}while(m.firstChild){p.insertBefore(m.firstChild,m);}p.removeChild(m);if(ps.indexOf(p)<0){ps.push(p);}}
  for(var j=0;j<ps.length;j++){try{ps[j].normalize();}catch(e){}}
  st.marks=[];st.cur=-1;
}
function collect(root){
  var w=document.createTreeWalker(root,NodeFilter.SHOW_TEXT,{acceptNode:function(n){var p=n.parentNode;if(!p){return NodeFilter.FILTER_REJECT;}if(p.closest&&p.closest('#fndbar')){return NodeFilter.FILTER_REJECT;}var t=p.tagName;if(t==='SCRIPT'||t==='STYLE'||t==='NOSCRIPT'){return NodeFilter.FILTER_REJECT;}return NodeFilter.FILTER_ACCEPT;}});
  var nodes=[],n;while((n=w.nextNode())){nodes.push(n);}return nodes;
}
function build(root){
  var nodes=collect(root),text='',map=[],lastB=null,k;
  for(var i=0;i<nodes.length;i++){
    var nd=nodes[i],b=blockOf(nd);
    if(b!==lastB&&text.length){text+='\u0001';map.push(null);}
    lastB=b;
    var s=nd.nodeValue;
    for(k=0;k<s.length;k++){
      var c=nc(s.charAt(k));
      for(var z=0;z<c.length;z++){text+=c.charAt(z);map.push(z===0?[i,k]:[i,k]);}
    }
  }
  return {nodes:nodes,text:text,map:map};
}
function run(q,root){
  clear();
  st.q=q;
  var nq=norm(q||'');
  if(!nq){return '0|0';}
  root=root||document.getElementById('doc')||document.body;
  st.root=root;
  var d=build(root),hits=[],from=0,idx;
  while((idx=d.text.indexOf(nq,from))>=0){hits.push(idx);from=idx+Math.max(1,nq.length);if(hits.length>4000){break;}}
  /* per-node segments for each hit, then wrap from the end backwards */
  var segs=[];
  for(var h=0;h<hits.length;h++){
    var a=hits[h],b=hits[h]+nq.length-1,per={},order=[];
    for(var p=a;p<=b;p++){var m=d.map[p];if(!m){continue;}if(!per[m[0]]){per[m[0]]={n:m[0],s:m[1],e:m[1]};order.push(m[0]);}else{if(m[1]>per[m[0]].e){per[m[0]].e=m[1];}if(m[1]<per[m[0]].s){per[m[0]].s=m[1];}}}
    var list=[];for(var o=0;o<order.length;o++){list.push(per[order[o]]);}
    segs.push(list);
  }
  var created=new Array(segs.length);
  for(var hi=segs.length-1;hi>=0;hi--){
    var L=segs[hi],mk=[];
    for(var li=L.length-1;li>=0;li--){
      var sg=L[li],node=d.nodes[sg.n];
      if(!node||!node.parentNode){continue;}
      try{
        var r=document.createRange();r.setStart(node,sg.s);r.setEnd(node,Math.min(node.nodeValue.length,sg.e+1));
        var el=document.createElement('mark');el.className='fnd';r.surroundContents(el);mk.unshift(el);
      }catch(e){}
    }
    created[hi]=mk;
  }
  /* flatten: one entry per hit (first mark), keep all marks for current highlight */
  st.marks=[];
  for(var i2=0;i2<created.length;i2++){if(created[i2]&&created[i2].length){st.marks.push(created[i2]);}}
  if(!st.marks.length){return '0|0';}
  /* start from the first hit at/below the viewport top */
  var top=0,start=0;
  for(var s2=0;s2<st.marks.length;s2++){if(st.marks[s2][0].getBoundingClientRect().top>=top-2){start=s2;break;}}
  setCur(start,true);
  return (st.cur+1)+'|'+st.marks.length;
}
function setCur(i,scroll){
  if(st.cur>=0&&st.marks[st.cur]){for(var a=0;a<st.marks[st.cur].length;a++){st.marks[st.cur][a].className='fnd';}}
  st.cur=i;
  var mk=st.marks[i];if(!mk){return;}
  for(var b=0;b<mk.length;b++){mk[b].className='fnd fnd-cur';}
  if(scroll){try{mk[0].scrollIntoView({block:'center',behavior:'auto'});}catch(e){}}
}
function step(dir){
  var n=st.marks.length;if(!n){return '0|0|';}
  var nx=st.cur+dir,edge='';
  if(nx>=n){nx=0;edge='e';}else if(nx<0){nx=n-1;edge='s';}
  setCur(nx,true);
  return (st.cur+1)+'|'+n+'|'+edge;
}
function info(){return (st.cur+1)+'|'+st.marks.length;}
var css=document.createElement('style');
css.textContent='mark.fnd{background:rgba(227,184,95,.38)!important;color:inherit!important;border-radius:3px;padding:0;}mark.fnd.fnd-cur{background:#e3b85f!important;color:#1a1608!important;box-shadow:0 0 0 2px rgba(227,184,95,.55);}';
(document.head||document.documentElement).appendChild(css);
function gotoIdx(i){if(i<0||i>=st.marks.length){return info();}setCur(i,true);return info();}
function chapIdx(i){var m=st.marks[i];if(!m||!m[0]){return -1;}var c=m[0].closest?m[0].closest('.rdr-chap'):null;return c?parseInt(c.getAttribute('data-idx'),10):-1;}
function gotoChapter(c,k){var n=0;for(var i=0;i<st.marks.length;i++){if(chapIdx(i)===c){if(n===k){return gotoIdx(i);}n++;}}return info();}
window.__fnd={run:run,step:step,clear:clear,info:info,norm:norm,gotoIdx:gotoIdx,gotoChapter:gotoChapter};
})();

(function(){
if(window.__fndUI){return;}window.__fndUI=1;
function post(m){try{window.chrome.webview.postMessage(m);}catch(x){}}
var L=window.__fndL||{};
var bar=null,inp=null,cnt=null,lastQ='',lastN=-1,book='',lastR='0|0',sT=null,hbox=null,lbox=null,hist=(window.__fndHist||[]).slice(0),items=[];
function chaps(){return document.querySelectorAll('.rdr-chap').length;}
function show(r){
  lastR=r;var p=String(r).split('|');
  if(!inp||!inp.value){cnt.textContent='';return;}
  if(!p[1]||p[1]==='0'){cnt.textContent=book?((L.book||'Book')+' '+book):(L.none||'Not found');return;}
  cnt.textContent=p[0]+' / '+p[1]+(book?'  \u00B7  '+book:'');
}
function mk(){
  if(bar){return;}
  bar=document.createElement('div');bar.id='fndbar';bar.setAttribute('dir','ltr');
  bar.style.cssText='position:fixed;top:12px;left:50%;transform:translateX(-50%);z-index:2147483000;display:none;align-items:center;gap:6px;background:rgba(30,30,32,.96);color:#eee;border:1px solid rgba(255,255,255,.14);border-radius:12px;padding:6px 8px;font:13px Tahoma,sans-serif;box-shadow:0 6px 24px rgba(0,0,0,.45);animation:none;line-height:normal;';
  inp=document.createElement('input');inp.type='text';inp.spellcheck=false;inp.placeholder=L.ph||'Search';
  inp.style.cssText='background:rgba(255,255,255,.1);border:1px solid transparent;color:#eee;border-radius:8px;height:28px;width:220px;padding:0 8px;font-size:13px;direction:auto;outline:none;font-family:inherit;margin:0;';
  cnt=document.createElement('span');cnt.style.cssText='min-width:74px;text-align:center;opacity:.85;font-size:12px;white-space:nowrap;';
  function btn(t,f){var b=document.createElement('button');b.type='button';b.textContent=t;b.style.cssText='background:transparent;border:0;color:#eee;width:30px;height:30px;border-radius:50%;cursor:pointer;font-size:15px;padding:0;margin:0;';b.onmouseenter=function(){b.style.background='rgba(255,255,255,.14)';};b.onmouseleave=function(){b.style.background='transparent';};b.onclick=f;return b;}
  bar.appendChild(inp);bar.appendChild(cnt);
  bar.appendChild(btn('\u2630',toggleList));bar.appendChild(btn('\u2191',function(){go(-1);}));bar.appendChild(btn('\u2193',function(){go(1);}));bar.appendChild(btn('\u2715',close));
  document.documentElement.appendChild(bar);
  inp.addEventListener('input',function(){clearTimeout(sT);sT=setTimeout(search,220);showHist();});
  inp.addEventListener('focus',showHist);
  hbox=document.createElement('div');hbox.style.cssText='position:fixed;z-index:2147483000;display:none;min-width:240px;max-width:360px;max-height:260px;overflow:auto;background:rgba(30,30,32,.97);color:#eee;border:1px solid rgba(255,255,255,.14);border-radius:12px;padding:4px;font:13px Tahoma,sans-serif;box-shadow:0 6px 24px rgba(0,0,0,.45);';
  hbox.setAttribute('id','fndhist');
  lbox=document.createElement('div');lbox.setAttribute('id','fndlist');lbox.setAttribute('dir','auto');
  lbox.style.cssText='position:fixed;z-index:2147482900;display:none;width:360px;max-width:calc(100vw - 24px);max-height:60vh;overflow:auto;background:rgba(30,30,32,.97);color:#eee;border:1px solid rgba(255,255,255,.14);border-radius:12px;padding:6px;font:13px Tahoma,sans-serif;box-shadow:0 6px 24px rgba(0,0,0,.45);line-height:1.8;';
  document.documentElement.appendChild(hbox);document.documentElement.appendChild(lbox);
}
function place(box){var r=bar.getBoundingClientRect();box.style.top=(r.bottom+6)+'px';var l=Math.max(12,Math.min(window.innerWidth-box.offsetWidth-12,r.left));box.style.left=l+'px';}
function rowStyle(d){d.style.cssText='padding:7px 10px;border-radius:8px;cursor:pointer;';d.onmouseenter=function(){d.style.background='rgba(255,255,255,.1)';};d.onmouseleave=function(){d.style.background='transparent';};}
function showHist(){
  if(!hbox){return;}
  if(inp.value||!hist.length){hbox.style.display='none';return;}
  hbox.innerHTML='';
  for(var i=0;i<hist.length;i++){(function(q){var d=document.createElement('div');rowStyle(d);d.textContent='\u21BB  '+q;d.onmousedown=function(e){e.preventDefault();inp.value=q;hbox.style.display='none';search();post('fh|'+encodeURIComponent(q));};hbox.appendChild(d);})(hist[i]);}
  hbox.style.display='block';place(hbox);
}
function remember(q){
  if(!q||q.length<2){return;}
  var i=hist.indexOf(q);if(i>=0){hist.splice(i,1);}hist.unshift(q);if(hist.length>12){hist.length=12;}
  post('fh|'+encodeURIComponent(q));
}
function toggleList(){
  if(!lbox){return;}
  if(lbox.style.display==='block'){lbox.style.display='none';return;}
  drawList();lbox.style.display='block';place(lbox);
}
function drawList(){
  lbox.innerHTML='';
  if(!items.length){var e=document.createElement('div');e.style.cssText='padding:10px;opacity:.7;';e.textContent=(inp&&inp.value)?(L.none||'Not found'):(L.hint||'');lbox.appendChild(e);return;}
  var lastC=-1;
  for(var i=0;i<items.length;i++){(function(it){
    if(it.c!==lastC){lastC=it.c;var h=document.createElement('div');h.style.cssText='padding:6px 10px 2px;font-size:11px;opacity:.65;';h.textContent=it.t||'';lbox.appendChild(h);}
    var d=document.createElement('div');rowStyle(d);
    var a=document.createElement('span');a.textContent=it.a;var m=document.createElement('b');m.textContent=it.m;m.style.cssText='background:rgba(227,184,95,.45);border-radius:3px;padding:0 2px;color:#fff;font-weight:700;';var b=document.createElement('span');b.textContent=it.b;
    d.appendChild(a);d.appendChild(m);d.appendChild(b);
    d.onclick=function(){remember(inp.value);post('fg|'+it.c+'|'+it.k+'|'+encodeURIComponent(inp.value));};
    lbox.appendChild(d);})(items[i]);}
}
window.__fndList=function(arr){items=arr||[];if(lbox&&lbox.style.display==='block'){drawList();}};
window.__fndGoto=function(q,c,k){
  mk();bar.style.display='flex';inp.value=q;lastQ=q;lastN=chaps();
  window.__fnd.run(q,document.body);
  show(window.__fnd.gotoChapter(c,k));
};
function open(){mk();bar.style.display='flex';inp.focus();inp.select();}
function close(){if(!bar){return;}bar.style.display='none';if(hbox){hbox.style.display='none';}if(lbox){lbox.style.display='none';}lastQ='';book='';try{window.__fnd.clear();}catch(e){}post('fx|0');}
function search(){
  var q=inp.value;book='';
  if(!q){try{window.__fnd.clear();}catch(e){}lastQ='';cnt.textContent='';return;}
  lastQ=q;lastN=chaps();
  show(window.__fnd.run(q,document.body));
  post('fq|'+encodeURIComponent(q));
}
function go(dir){
  var q=inp.value;if(!q){return;}
  if(q!==lastQ||chaps()!==lastN){search();return;}
  remember(q);
  var r=window.__fnd.step(dir),p=String(r).split('|');
  show(r);
  if(p[1]==='0'||p[2]){post('fn|'+dir+'|'+encodeURIComponent(q));}
}
window.__fndOpen=open;
window.__fndBook=function(total){book=total>0?String(total):'';show(lastR);};
window.__fndReopen=function(q,dir){
  mk();bar.style.display='flex';inp.value=q;lastQ=q;lastN=chaps();
  var r=window.__fnd.run(q,document.body);
  if(dir<0){r=window.__fnd.step(-1);}
  show(r);
};
document.addEventListener('keydown',function(e){
  var k=e.keyCode;
  if(e.ctrlKey&&!e.altKey&&k===70){e.preventDefault();e.stopImmediatePropagation();open();return;}
  if(bar&&e.target===inp){
    e.stopImmediatePropagation();
    if(k===13){e.preventDefault();clearTimeout(sT);go(e.shiftKey?-1:1);}
    else if(k===27){e.preventDefault();close();}
    return;
  }
  if(k===27&&bar&&bar.style.display==='flex'){e.preventDefault();e.stopImmediatePropagation();close();}
},true);
})();
";

        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) { return ""; }
            StringBuilder sb = new StringBuilder(s.Length);
            bool lastSpace = false;
            foreach (char ch in s)
            {
                int c = ch;
                if ((c >= 0x064B && c <= 0x065F) || c == 0x0670 || c == 0x0640 || c == 0x200C || c == 0x200D || c == 0x00AD || c == 0x200E || c == 0x200F || c == 0x061C) { continue; }
                char o = ch;
                if (c >= 0x06F0 && c <= 0x06F9) { o = (char)(c - 0x06F0 + 48); }
                else if (c >= 0x0660 && c <= 0x0669) { o = (char)(c - 0x0660 + 48); }
                else
                {
                    switch (c)
                    {
                        case 0x064A: case 0x0649: case 0x06D2: o = (char)0x06CC; break;
                        case 0x0643: o = (char)0x06A9; break;
                        case 0x0623: case 0x0625: case 0x0622: case 0x0671: o = (char)0x0627; break;
                        case 0x0624: o = (char)0x0648; break;
                        case 0x0629: case 0x06C0: case 0x06C1: case 0x06BE: o = (char)0x0647; break;
                        case 0x00A0: case 0x2009: case 0x202F: o = ' '; break;
                        default: o = char.ToLowerInvariant(ch); break;
                    }
                }
                if (char.IsWhiteSpace(o))
                {
                    if (lastSpace) { continue; }
                    lastSpace = true;
                    sb.Append(' ');
                }
                else
                {
                    lastSpace = false;
                    sb.Append(o);
                }
            }
            return sb.ToString().Trim();
        }

        private static readonly Regex DropBlocks = new Regex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BlockTags = new Regex(@"</?(p|div|br|li|ul|ol|h[1-6]|tr|td|th|table|blockquote|pre|section|article|figure|figcaption)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnyTag = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        // متن اصلی فصل (بدون تگ‌ها) برای نشون دادن جمله‌ی اطراف نتیجه
        public static string PlainText(string html)
        {
            if (string.IsNullOrEmpty(html)) { return ""; }
            string t = DropBlocks.Replace(html, " ");
            t = BlockTags.Replace(t, "\u0001");
            t = AnyTag.Replace(t, "");
            return System.Net.WebUtility.HtmlDecode(t);
        }

        // مثل Norm ولی برای هر نویسه‌ی نتیجه، جای نویسه‌ی اصلی رو هم نگه می‌داره (برای ساختن جمله‌ی نتیجه)
        public static string NormWithMap(string s, out int[] map)
        {
            List<int> m = new List<int>(s.Length);
            StringBuilder sb = new StringBuilder(s.Length);
            bool lastSpace = true;
            for (int i = 0; i < s.Length; i++)
            {
                char one = NormChar(s[i]);
                if (one == '\0')
                {
                    // نویسه‌ی حذف‌شده (اعراب، نیم‌فاصله…) یا فاصله: فاصله‌ی پشت‌سرهم یکی می‌شه
                    if (char.IsWhiteSpace(s[i]) && !lastSpace)
                    {
                        sb.Append(' ');
                        m.Add(i);
                        lastSpace = true;
                    }
                    continue;
                }
                if (one == ' ')
                {
                    if (lastSpace) { continue; }
                    lastSpace = true;
                }
                else
                {
                    lastSpace = false;
                }
                sb.Append(one);
                m.Add(i);
            }
            map = m.ToArray();
            return sb.ToString();
        }

        // نویسه‌ی هم‌ارز شده؛ '\0' یعنی این نویسه نادیده گرفته می‌شه (اعراب، نیم‌فاصله…)
        private static char NormChar(char ch)
        {
            int c = ch;
            if ((c >= 0x064B && c <= 0x065F) || c == 0x0670 || c == 0x0640 || c == 0x200C || c == 0x200D || c == 0x00AD || c == 0x200E || c == 0x200F || c == 0x061C) { return '\0'; }
            if (c >= 0x06F0 && c <= 0x06F9) { return (char)(c - 0x06F0 + 48); }
            if (c >= 0x0660 && c <= 0x0669) { return (char)(c - 0x0660 + 48); }
            switch (c)
            {
                case 0x064A: case 0x0649: case 0x06D2: return (char)0x06CC;
                case 0x0643: return (char)0x06A9;
                case 0x0623: case 0x0625: case 0x0622: case 0x0671: return (char)0x0627;
                case 0x0624: return (char)0x0648;
                case 0x0629: case 0x06C0: case 0x06C1: case 0x06BE: return (char)0x0647;
                case 0x00A0: case 0x2009: case 0x202F: return ' ';
            }
            if (char.IsWhiteSpace(ch)) { return ' '; }
            return char.ToLowerInvariant(ch);
        }

        private static string Flat(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            bool sp = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || c == '\u0001')
                {
                    if (!sp) { sb.Append(' '); }
                    sp = true;
                }
                else
                {
                    sb.Append(c);
                    sp = false;
                }
            }
            return sb.ToString();
        }

        // جمله‌ی اطراف یک نتیجه: قبل، خودِ نتیجه، بعد
        public static void Snippet(string orig, int[] map, int normIdx, int normLen, out string before, out string match, out string after)
        {
            int a = map[normIdx];
            int e = map[Math.Min(map.Length - 1, normIdx + Math.Max(1, normLen) - 1)];
            int b0 = Math.Max(0, a - 32);
            int a1 = Math.Min(orig.Length, e + 1);
            int a2 = Math.Min(orig.Length, a1 + 48);
            before = (b0 > 0 ? "\u2026" : "") + Flat(orig.Substring(b0, a - b0)).TrimStart();
            match = Flat(orig.Substring(a, a1 - a));
            after = Flat(orig.Substring(a1, a2 - a1)).TrimEnd() + (a2 < orig.Length ? "\u2026" : "");
        }

        // رشته‌ی امن برای قرار دادن توی اسکریپت (داخل کوتیشن دوتایی)
        public static string JsStr(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in (s ?? ""))
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '<': sb.Append("\\u003c"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\u2028': sb.Append("\\u2028"); break;
                    case '\u2029': sb.Append("\\u2029"); break;
                    default:
                        if (c < ' ') { sb.Append(' '); } else { sb.Append(c); }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // متن ساده‌ی یک فصل، برای شمارش نتیجه‌ها
        public static string PlainNorm(string html)
        {
            if (string.IsNullOrEmpty(html)) { return ""; }
            string t = DropBlocks.Replace(html, " ");
            t = BlockTags.Replace(t, "\u0001");
            t = AnyTag.Replace(t, "");
            t = System.Net.WebUtility.HtmlDecode(t);
            return Norm(t);
        }

        public static int Count(string hay, string needle)
        {
            if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) { return 0; }
            int n = 0;
            int from = 0;
            while (true)
            {
                int i = hay.IndexOf(needle, from, StringComparison.Ordinal);
                if (i < 0) { break; }
                n++;
                from = i + Math.Max(1, needle.Length);
                if (n >= 5000) { break; }
            }
            return n;
        }
    }
}

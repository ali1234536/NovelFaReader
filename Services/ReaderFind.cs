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
var MAP={'ي':'ی','ى':'ی','ے':'ی','ك':'ک','أ':'ا','إ':'ا','آ':'ا','ٱ':'ا','ؤ':'و','ة':'ه','ۀ':'ه','ہ':'ه','ھ':'ه'};
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
window.__fnd={run:run,step:step,clear:clear,info:info,norm:norm};
})();

(function(){
if(window.__fndUI){return;}window.__fndUI=1;
function post(m){try{window.chrome.webview.postMessage(m);}catch(x){}}
var L=window.__fndL||{};
var bar=null,inp=null,cnt=null,lastQ='',lastN=-1,book='',lastR='0|0',sT=null;
function chaps(){return document.querySelectorAll('.rdr-chap').length;}
function show(r){
  lastR=r;var p=String(r).split('|');
  if(!inp||!inp.value){cnt.textContent='';return;}
  if(!p[1]||p[1]==='0'){cnt.textContent=book?((L.book||'Book')+' '+book):(L.none||'Not found');return;}
  cnt.textContent=p[0]+' / '+p[1]+(book?'  ·  '+book:'');
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
  bar.appendChild(btn('↑',function(){go(-1);}));bar.appendChild(btn('↓',function(){go(1);}));bar.appendChild(btn('✕',close));
  document.documentElement.appendChild(bar);
  inp.addEventListener('input',function(){clearTimeout(sT);sT=setTimeout(search,220);});
}
function open(){mk();bar.style.display='flex';inp.focus();inp.select();}
function close(){if(!bar){return;}bar.style.display='none';lastQ='';book='';try{window.__fnd.clear();}catch(e){}post('fx|0');}
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

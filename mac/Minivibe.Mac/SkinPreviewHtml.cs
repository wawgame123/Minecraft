namespace Minivibe.Mac;

internal static class SkinPreviewHtml
{
    public static string Empty()
    {
        return """
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{height:100%;margin:0;background:#0b0d14;color:#dce7f5;font-family:-apple-system,BlinkMacSystemFont,sans-serif}
body{display:grid;place-items:center}.hint{opacity:.72;font-size:15px;text-align:center;padding:20px}
</style></head><body><div class="hint">Выберите PNG/JPG скин для 3D-превью</div></body></html>
""";
    }

    public static string Build(string skinDataUrl)
    {
        var escapedSkin = skinDataUrl
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $$$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
html,body{height:100%;margin:0;overflow:hidden;background:radial-gradient(circle at 30% 20%,#20283c,#0b0d14 58%,#05060a);font-family:-apple-system,BlinkMacSystemFont,sans-serif}
.stage{height:100%;display:grid;place-items:center;perspective:900px;cursor:grab;touch-action:none}
.stage:active{cursor:grabbing}.model{position:relative;width:0;height:0;transform-style:preserve-3d;animation:spin 9s linear infinite}
.stage.dragging .model{animation:none}.part{position:absolute;transform-style:preserve-3d}.face{position:absolute;background-size:100% 100%;background-repeat:no-repeat;image-rendering:pixelated;backface-visibility:hidden;box-shadow:inset 0 0 0 1px rgba(255,255,255,.08)}
.overlay .face{box-shadow:inset 0 0 0 1px rgba(255,255,255,.16),0 0 8px rgba(255,255,255,.08)}
html.no-overlays .overlay{display:none}
.head{transform:translate3d(-24px,-126px,0)}.body{transform:translate3d(-24px,-78px,0)}.armL{transform:translate3d(-48px,-78px,0)}.armR{transform:translate3d(24px,-78px,0)}.legL{transform:translate3d(-24px,-6px,0)}.legR{transform:translate3d(0,-6px,0)}
@keyframes spin{from{transform:rotateX(-9deg) rotateY(0deg)}to{transform:rotateX(-9deg) rotateY(360deg)}}
</style>
</head>
<body>
<div class="stage" id="stage"><div class="model" id="model"></div></div>
<script>
const S=6, skin='{{{escapedSkin}}}', model=document.getElementById('model'), stage=document.getElementById('stage');
const skinImg=new Image(), skinCanvas=document.createElement('canvas'), skinCtx=skinCanvas.getContext('2d',{willReadFrequently:true});
const skinReady=new Promise(resolve=>{skinImg.onload=()=>{skinCanvas.width=64;skinCanvas.height=64;skinCtx.imageSmoothingEnabled=false;skinCtx.clearRect(0,0,64,64);skinCtx.drawImage(skinImg,0,0,64,skinImg.naturalHeight===32?32:64);resolve(true)};skinImg.onerror=()=>resolve(false);skinImg.src=skin});
function crop(u,w,h){const c=document.createElement('canvas'),x=c.getContext('2d');c.width=w;c.height=h;x.imageSmoothingEnabled=false;x.drawImage(skinCanvas,u[0],u[1],w,h,0,0,w,h);return c.toDataURL('image/png')}
function setTexture(f,u,w,h){skinReady.then(ok=>{if(ok)f.style.backgroundImage=`url(${crop(u,w,h)})`})}
function detectOverlayAlpha(){return skinReady.then(ok=>{if(!ok)return true;try{const regions=[[32,0,32,16],[16,32,48,16],[0,48,64,16]];let transparent=false,paint=false;for(const r of regions){const d=skinCtx.getImageData(...r).data;for(let i=3;i<d.length;i+=4){if(d[i]<250)transparent=true;else paint=true}}return transparent&&paint}catch{return true}})}
function part(cls,w,h,d,uv,overlay=false){const p=document.createElement('div');p.className='part '+cls+(overlay?' overlay':'');model.appendChild(p);const o=overlay?1.7:0,g=overlay?2:0;
 const faces=[['front',w,h,`translateZ(${d/2*S+o}px)`,uv.f],['back',w,h,`rotateY(180deg) translateZ(${d/2*S+o}px)`,uv.b],['left',d,h,`rotateY(-90deg) translateZ(${w/2*S+o}px)`,uv.l],['right',d,h,`rotateY(90deg) translateZ(${w/2*S+o}px)`,uv.r],['top',w,d,`rotateX(90deg) translateZ(${d/2*S+o}px)`,uv.t],['bottom',w,d,`rotateX(-90deg) translateZ(${h*S-d/2*S+o}px)`,uv.o]];
 for(const [n,fw,fh,tr,u] of faces){const f=document.createElement('div');f.className='face '+n;f.style.width=fw*S+g*2+'px';f.style.height=fh*S+g*2+'px';f.style.left=-g+'px';f.style.top=-g+'px';f.style.transform=tr;setTexture(f,u,fw,fh);p.appendChild(f)}}
part('head',8,8,8,{f:[8,8],b:[24,8],l:[16,8],r:[0,8],t:[8,0],o:[16,0]});
part('body',8,12,4,{f:[20,20],b:[32,20],l:[28,20],r:[16,20],t:[20,16],o:[28,16]});
part('armL',4,12,4,{f:[36,52],b:[44,52],l:[40,52],r:[32,52],t:[36,48],o:[40,48]});
part('armR',4,12,4,{f:[44,20],b:[52,20],l:[48,20],r:[40,20],t:[44,16],o:[48,16]});
part('legL',4,12,4,{f:[20,52],b:[28,52],l:[24,52],r:[16,52],t:[20,48],o:[24,48]});
part('legR',4,12,4,{f:[4,20],b:[12,20],l:[8,20],r:[0,20],t:[4,16],o:[8,16]});
part('head',8,8,8,{f:[40,8],b:[56,8],l:[48,8],r:[32,8],t:[40,0],o:[48,0]},true);
part('body',8,12,4,{f:[20,36],b:[32,36],l:[28,36],r:[16,36],t:[20,32],o:[28,32]},true);
part('armL',4,12,4,{f:[52,52],b:[60,52],l:[56,52],r:[48,52],t:[52,48],o:[56,48]},true);
part('armR',4,12,4,{f:[44,36],b:[52,36],l:[48,36],r:[40,36],t:[44,32],o:[48,32]},true);
part('legL',4,12,4,{f:[4,52],b:[12,52],l:[8,52],r:[0,52],t:[4,48],o:[8,48]},true);
part('legR',4,12,4,{f:[4,36],b:[12,36],l:[8,36],r:[0,36],t:[4,32],o:[8,32]},true);
detectOverlayAlpha().then(show=>{if(!show)document.documentElement.classList.add('no-overlays')});
let down=false,lastX=0,lastY=0,ry=25,rx=-9;function apply(){model.style.transform=`rotateX(${rx}deg) rotateY(${ry}deg)`}
stage.addEventListener('pointerdown',e=>{down=true;stage.classList.add('dragging');lastX=e.clientX;lastY=e.clientY;stage.setPointerCapture(e.pointerId);apply()});
stage.addEventListener('pointermove',e=>{if(!down)return;ry+=e.clientX-lastX;rx=Math.max(-40,Math.min(30,rx-(e.clientY-lastY)*.4));lastX=e.clientX;lastY=e.clientY;apply()});
stage.addEventListener('pointerup',()=>{down=false;stage.classList.remove('dragging')});
</script>
</body>
</html>
""";
    }
}

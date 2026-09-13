const {chromium}=require('C:/Sean Project/RMT-1022-astra-implementation/product/client/node_modules/playwright');
const fs=require('fs');
(async()=>{
const b=await chromium.launch();const p=await b.newPage({viewport:{width:1280,height:1000}});
await p.goto('http://127.0.0.1:5231/tests/fixtures/change-network.html?case=dense&long=1');
await p.locator('.dtCanvasNode:not(.is-offscreen)').first().click({position:{x:8,y:8}});
await p.locator('.dtnPanelTools').getByRole('button',{name:'Bottom',exact:true}).click();
await p.waitForTimeout(1000);
await p.evaluate(()=>{const values=[...document.querySelectorAll('.dtnPanel *, .dtCanvasNode *')].map(el=>[el,parseFloat(getComputedStyle(el).fontSize)]);for(const [el,size]of values)el.style.setProperty('font-size',size*1.25+'px','important')});
await p.waitForTimeout(1000);
const state=()=>p.evaluate(()=>{const s=document.querySelector('.dtCanvasOffscreen');const i=document.querySelector('.dtnPanelIdentityCol');return{strip:{width:s.clientWidth,scrollWidth:s.scrollWidth,scrollLeft:s.scrollLeft,pe:getComputedStyle(s).pointerEvents,rect:s.getBoundingClientRect().toJSON()},identity:{height:i.clientHeight,scrollHeight:i.scrollHeight,scrollTop:i.scrollTop,overflow:getComputedStyle(i).overflow},camera:getComputedStyle(document.querySelector('.dtCanvasScene')).transform}});
const before=await state();const strip=p.locator('.dtCanvasOffscreen');const r=await strip.boundingBox();
await p.mouse.move(r.x+40,r.y+10);await p.mouse.wheel(400,0);await p.waitForTimeout(400);const wheelOnButton=await state();
const blank=await p.evaluate(()=>{const s=document.querySelector('.dtCanvasOffscreen'),r=s.getBoundingClientRect();for(let y=r.top;y<r.bottom;y+=2)for(let x=r.left;x<r.right;x+=2){const el=document.elementFromPoint(x,y);if(!el?.closest('.dtCanvasOffscreen'))return{x,y,hit:el?.className}}return null});
if(blank){await p.mouse.move(blank.x,blank.y);await p.mouse.wheel(400,0);await p.waitForTimeout(400)}const wheelOnGap=await state();
const identity=p.locator('.dtnPanelIdentityCol');const ir=await identity.boundingBox();await p.mouse.move(ir.x+80,ir.y+60);await p.mouse.wheel(0,600);await p.waitForTimeout(300);const identityScrolled=await state();
await p.screenshot({path:'C:/Users/seanm/AppData/Local/Temp/astra-1022-implementation-20260912/paint-80fdb568-scroll.png'});
const cdp=await p.context().newCDPSession(p);await cdp.send('Performance.enable');const metric=async()=>Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.filter(m=>['LayoutCount','RecalcStyleCount','LayoutDuration','RecalcStyleDuration'].includes(m.name)).map(m=>[m.name,m.value]));
const m0=await metric();await p.mouse.move(1260,160);await p.mouse.down();await p.mouse.move(1160,160,{steps:10});await p.mouse.up();const m1=await metric();
const out={before,wheelOnButton,blank,wheelOnGap,identityScrolled,panMetrics:{before:m0,after:m1,delta:Object.fromEntries(Object.keys(m0).map(k=>[k,m1[k]-m0[k]]))}};
fs.writeFileSync('C:/Users/seanm/AppData/Local/Temp/astra-1022-implementation-20260912/paint-80fdb568.json',JSON.stringify(out,null,2));console.log(JSON.stringify(out));await b.close();
})().catch(e=>{console.error(e);process.exit(1)});


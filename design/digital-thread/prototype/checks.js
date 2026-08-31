/* Headless smoke test for the Digital Thread artboard logic.
   Not shipped — it proves build/render/scrub/zoom/trace do not throw and hold their invariants. */
const fs = require('fs');

function makeEl(tag) {
  const el = {
    tag, style: {}, dataset: {}, children: [], _attrs: {}, _html: '',
    classList: {
      _s: new Set(),
      add() { for (const c of arguments) this._s.add(c); },
      remove() { for (const c of arguments) this._s.delete(c); },
      toggle(c, f) { if (f === undefined) { this._s.has(c) ? this._s.delete(c) : this._s.add(c); } else if (f) this._s.add(c); else this._s.delete(c); },
      contains(c) { return this._s.has(c); }
    },
    appendChild(c) { this.children.push(c); return c; },
    addEventListener() {}, removeEventListener() {},
    setAttribute(k, v) { this._attrs[k] = v; }, getAttribute(k) { return this._attrs[k]; },
    getBoundingClientRect() { return { width: 1280, height: 692, left: 0, top: 0 }; },
    closest() { return null; },
    querySelector(sel) { this._q = this._q || {}; return this._q[sel] || (this._q[sel] = makeEl(sel)); },
    querySelectorAll() { return []; },
    focus() {}
  };
  Object.defineProperty(el, 'className', { get() { return [...el.classList._s].join(' '); }, set(v) { el.classList._s = new Set(String(v).split(/\s+/).filter(Boolean)); } });
  Object.defineProperty(el, 'innerHTML', { get() { return el._html; }, set(v) { el._html = String(v); if (v === '') el.children = []; } });
  Object.defineProperty(el, 'textContent', { get() { return el._t || ''; }, set(v) { el._t = String(v); } });
  return el;
}
const root = makeEl('div');
global.document = { getElementById: id => (id === 'dtRoot' ? root : makeEl('div')), createElement: t => makeEl(t), createElementNS: (ns, t) => makeEl(t) };
let frames = 0;
global.requestAnimationFrame = fn => { if (frames++ < 600) fn(); return frames; };
global.cancelAnimationFrame = () => {};
global.setTimeout = fn => { fn(); return 0; };
global.window = { addEventListener() {}, removeEventListener() {} };
class DCLogic { constructor() { this.props = {}; } }
global.DCLogic = DCLogic;

const src = fs.readFileSync(__dirname + '/Main.dc.html', 'utf8');
const body = src.match(/<script data-dc-script[^>]*>([\s\S]*?)<\/script>/)[1];
const Component = eval('(function(){' + body + '; return Component;})()');
const c = new Component();
c.componentDidMount();

const problems = [];
const ok = (cond, msg) => { if (!cond) problems.push(msg); };
const finite = (v, msg) => { if (!isFinite(v)) problems.push(msg + ' not finite (' + v + ')'); };
function markupCheck(label) {
  const ids = Object.keys(c.cardEls);
  ok(ids.length > 0, label + ': no cards');
  ids.forEach(id => {
    const h = c.cardEls[id].innerHTML;
    ['undefined', 'NaN', '[object Object]'].forEach(bad => { if (h.indexOf(bad) >= 0) problems.push(label + '/' + id + ': markup has "' + bad + '"'); });
  });
  Object.values(c.pos).forEach(p => { finite(p.x, label + ' pos.x'); finite(p.y, label + ' pos.y'); });
  return ids.length;
}

// ---- 1. network renders, bottom dock is the default ----------------------
ok(c.dockPref === 'bottom', 'bottom dock is not the default (' + c.dockPref + ')');
let n = markupCheck('network');
console.log('network  : ' + n + ' cards, ' + c.edges.length + ' edges, band ' + Math.round(c.bandH) + ', k=' + c.k.toFixed(2));

// ---- 2. no wasted vertical space: the band fills the window --------------
['network', 'thread'].forEach(m => {
  c.setMode(m);
  const win = c.windowScene(), maxH = c.maxLaneHAt(c.lod);
  const want = Math.min(win, maxH);
  ok(Math.abs(c.bandH - want) < 1, m + ': band ' + Math.round(c.bandH) + ' does not fill the window (' + Math.round(want) + ')');
  console.log(m.padEnd(9) + ': window ' + Math.round(win) + ', tallest lane ' + Math.round(maxH) + ', band ' + Math.round(c.bandH) +
    (maxH > win ? ' (rolling)' : ' (all shown)'));
});

// ---- 3. zoom floor: pulling back stops once everything is shown ----------
c.setMode('network');
const floor = c.minK();
for (let i = 0; i < 30; i++) c.zoomBy(0.81);
ok(Math.abs(c.k - floor) < 0.01, 'zoom did not stop at the floor: k=' + c.k.toFixed(3) + ' floor=' + floor.toFixed(3));
ok(c.outBtn.disabled === true, 'zoom-out button not disabled at the floor');
const winAtFloor = c.windowScene(), tallest = c.maxLaneHAt(c.lod);
ok(tallest <= winAtFloor + 2 || floor <= 0.585, 'at the floor the tallest lane (' + Math.round(tallest) + ') still exceeds the window (' + Math.round(winAtFloor) + ')');
ok(c.sceneW * c.k <= c.free().w, 'at the floor the board is still wider than the viewport');
console.log('zoom     : floor ' + Math.round(floor * 100) + '%, every lane visible, zoom-out disabled there');
[[1.5, 2], [0.75, 1]].forEach(([k, want]) => {
  c.k = k; c.apply();
  ok(c.lod === want, 'lod at k=' + k + ' is ' + c.lod);
  console.log('density  : ' + Math.round(k * 100) + '% → pitch ' + c.g.RP + ', ~' + Math.floor(c.bandH / c.g.RP) + ' records per lane');
});
c.fit(false);

// ---- 4. transitive trace goes all the way down --------------------------
c.setMode('network');
const deep = c.web('LLRTPCR-000009.00');
const maxHop = Math.max.apply(null, Object.values(deep.hop));
ok(maxHop >= 3, 'trace from LLRTPCR-000009.00 only reaches ' + maxHop + ' hops — not the full web');
ok(deep.up.has('SRCR-00039.00'), 'trace up from LLRTPCR-000009.00 misses SRCR-00039.00');
console.log('trace up : LLRTPCR-000009.00 → ' + deep.up.size + ' upstream records, deepest ' + maxHop + ' hops');
const w39 = c.web('SRCR-00039.00');
ok(w39.down.has('LLRTPCR-000009.00'), 'trace down from SRCR-00039.00 misses LLRTPCR-000009.00');
ok(!w39.down.has('SRCR-00040.00'), 'trace down from SRCR-00039.00 leaked sideways into SRCR-00040.00');
console.log('trace dn : SRCR-00039.00 → ' + w39.down.size + ' downstream records, no sideways bleed');
c.highlight('SRCR-00039.00');
const faded = Object.keys(c.cardEls).filter(k => c.cardEls[k].classList.contains('fade')).length;
ok(faded === c.nodes.length - w39.nodes.size, 'fade count ' + faded + ' does not match the untraced set');
console.log('focus    : ' + w39.nodes.size + ' traced, ' + faded + ' pushed back');
c.highlight(null);
ok(Object.keys(c.cardEls).filter(k => c.cardEls[k].classList.contains('fade')).length === 0, 'fade not cleared');

// ---- 5. lane scrubbing + cross-lane sync --------------------------------
c.setMode('network');
const before = c.laneOff.slice();
c.laneOff[1] = Math.max(c.laneMin[1], -240);
const anchor = c.nearestInLane(1);
ok(!!anchor, 'no anchor in lane 1');
c.syncFrom(anchor.id, 1);
c.laneOff.forEach((o, i) => { finite(o, 'laneOff[' + i + ']'); ok(o <= 0.5 && o >= c.laneMin[i] - 0.5, 'lane ' + i + ' offset out of clamp'); });
const moved = c.laneOff.filter((o, i) => Math.abs(o - (before[i] || 0)) > 1).length;
ok(moved >= 2, 'sync moved only ' + moved + ' lanes');
console.log('scrub    : anchor ' + anchor.id + ' → ' + moved + ' lanes followed');

// ---- 6. inside a change: full change-request register in lane 0 ---------
c.setMode('inside', 'SRCR-00039.00');
const allCRs = Object.keys(c.D.CR).filter(i => c.D.CR[i].lane > 0).length;
let lane0 = c.nodes.filter(x => x.lane === 0).length;
ok(lane0 === allCRs, 'inside lane 0 holds ' + lane0 + ' change requests, expected ' + allCRs);
ok(c.laneMin[0] < -1, 'inside lane 0 is not rollable');
ok(c.nodes.some(x => x.lane === 0 && x.cls.indexOf('focusCR') >= 0), 'focused change request not marked in lane 0');
console.log('inside   : lane 0 lists all ' + lane0 + ' change requests, rollable, focus marked');
[['sys', 1], ['hlr', 2], ['llr', 3], ['ver', 4]].forEach(([t, lane]) => {
  c.crType = t; c.setMode('inside', 'SRCR-00039.00');
  const got = c.nodes.filter(x => x.lane === 0).length;
  const want = Object.keys(c.D.CR).filter(i => c.D.CR[i].lane === lane).length + (lane === 1 ? 0 : 1);
  ok(got === want, 'type ' + t + ': lane 0 has ' + got + ', expected ' + want);
});
c.crType = 'all';
console.log('type sel : all / sys / hlr / llr / ver each filter lane 0 correctly');

// clicking another change request in lane 0 switches the view
c.setMode('inside', 'SRCR-00039.00');
c.select('SRCR-00041.00');
ok(c.crFocus === 'SRCR-00041.00', 'selecting another change request in lane 0 did not switch focus');
console.log('switch   : clicking a lane-0 change request re-enters it');

// ---- 7. every change request opens --------------------------------------
Object.keys(c.D.CR).forEach(id => {
  if (c.D.CR[id].lane === 0) return;
  try {
    c.setMode('inside', id);
    markupCheck('inside:' + id);
    ok(c.edges.filter(e => !c.pos[e.a] || !c.pos[e.b]).length === 0, 'inside:' + id + ': dangling edges');
  } catch (err) { problems.push('inside:' + id + ' threw ' + err.message); }
});
console.log('open all : ' + allCRs + ' change requests all opened');

// ---- 8. docking never covers a directly linked record -------------------
c.setMode('network');
['bottom', 'right', 'auto'].forEach(pref => {
  c.dockPref = pref;
  ['SRCR-00039.00', 'PR-00003.00', 'LLRTPCR-000009.00', 'HLRCR-00132.00'].forEach(id => {
    c.select(id);
    const f = c.free();
    const direct = c.edges.filter(e => e.a === id || e.b === id).map(e => (e.a === id ? e.b : e.a));
    let covered = 0;
    direct.concat([id]).forEach(nid => {
      const nd = c.nodes.find(z => z.id === nid); if (!nd) return;
      const p = c.nodeXY(nd);
      const sx = p.x * c.k + c.tx, sx2 = (p.x + c.g.LW) * c.k + c.tx;
      const sy = p.y * c.k + c.ty, sy2 = (p.y + c.g.CH) * c.k + c.ty;
      if (sx2 > f.x + f.w + 1 || sx < f.x - 1) covered++;
      else if (sy2 > f.y + f.h + 1 || sy < f.y - 1) covered++;
    });
    ok(covered === 0, 'dock ' + pref + '/' + id + ': ' + covered + ' of ' + (direct.length + 1) + ' linked records under the panel (dock=' + c.dock + ')');
  });
  console.log('dock ' + pref.padEnd(7) + ': 4 selections, nothing linked ends up under the panel');
});
c.dockPref = 'bottom';

// ---- 9. panel content ----------------------------------------------------
['network', 'thread'].forEach(m => {
  c.setMode(m);
  c.nodes.forEach(nd => {
    try { c.select(nd.id); } catch (err) { problems.push('select ' + m + '/' + nd.id + ' threw ' + err.message); }
    const h = c.insp.innerHTML;
    ok(h.indexOf(nd.id) >= 0, 'panel missing ' + nd.id + ' in ' + m);
    ok(h.indexOf('undefined') < 0, 'panel undefined for ' + nd.id + ' in ' + m);
    finite(c.k, 'k'); finite(c.tx, 'tx'); finite(c.ty, 'ty');
    ok(c.k >= c.minK() - 0.01, 'k fell below the floor after selecting ' + nd.id);
  });
});
console.log('panel    : every node in network and thread selects cleanly, all hops listed');

console.log('\n' + (problems.length ? 'PROBLEMS:\n - ' + problems.join('\n - ') : 'ALL CHECKS PASSED'));
process.exit(problems.length ? 1 : 0);

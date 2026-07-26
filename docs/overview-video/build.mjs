/**
 * Builds the overview video from slides.js.
 *
 *   node build.mjs                 render changed slides, then encode the MP4 and the shareable page
 *   node build.mjs --frames        render changed slides only — fast, for checking a wording change
 *   node build.mjs --all           ignore the cache and re-render every slide
 *   node build.mjs --only 3,7      re-render just these slides (0-based), then encode
 *
 * Only slides whose data actually changed are re-rendered, so a one-word edit costs a couple of seconds
 * rather than a full pass. Encoding is all-or-nothing because the slides cross-fade into each other, but it
 * is the cheap half; use --frames while iterating and drop the flag when you want the file.
 */
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { readFile, writeFile, mkdir, stat, readdir } from 'node:fs/promises';
import { existsSync, statSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join, extname } from 'node:path';
import { spawn } from 'node:child_process';

const HERE = dirname(fileURLToPath(import.meta.url));
const OUT = join(HERE, 'build');
const CACHE = join(OUT, '.cache.json');
const PORT = 8899;

const CHROME = [
  '/opt/pw-browsers/chromium-1228/chrome-linux64/chrome',
  '/opt/pw-browsers/chromium-1194/chrome-linux/chrome',
  '/opt/pw-browsers/chromium/chrome-linux/chrome',
].find(existsSync);

const args = process.argv.slice(2);
const has = f => args.includes(f);
const only = (() => {
  const i = args.indexOf('--only');
  return i >= 0 && args[i + 1] ? args[i + 1].split(',').map(Number) : null;
})();

const say = (...m) => console.log(...m);

/* ── a static server, so the page can load slides.js as a module and the shots as images ────────────── */
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.png': 'image/png', '.jpg': 'image/jpeg' };
function serve() {
  const server = createServer(async (req, res) => {
    const path = join(HERE, decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '') || 'template.html');
    try {
      const body = await readFile(path);
      res.writeHead(200, { 'content-type': TYPES[extname(path)] || 'application/octet-stream' });
      res.end(body);
    } catch {
      res.writeHead(404).end('not found');
    }
  });
  return new Promise(done => server.listen(PORT, '127.0.0.1', () => done(server)));
}

/* ── what each slide depends on, so we know when it must be redrawn ─────────────────────────────────── */
async function fingerprints() {
  const { slides, branding } = await import(`./slides.js?v=${Date.now()}`);
  const template = await readFile(join(HERE, 'template.html'), 'utf8');
  const chrome = createHash('sha1').update(template + JSON.stringify(branding)).digest('hex').slice(0, 12);

  return {
    slides,
    keys: slides.map(s => {
      const h = createHash('sha1').update(JSON.stringify(s)).update(chrome);
      if (s.shot) {
        const f = join(HERE, 'shots', `${s.shot}.png`);
        const st = existsSync(f) ? statSync(f) : null;
        h.update(st ? `${st.size}:${st.mtimeMs}` : 'missing');
      }
      return h.digest('hex').slice(0, 16);
    }),
  };
}

/* ── render the frames that changed ─────────────────────────────────────────────────────────────────── */
async function renderFrames(slides, keys) {
  await mkdir(OUT, { recursive: true });
  let cache = {};
  try { cache = JSON.parse(await readFile(CACHE, 'utf8')); } catch { /* first run */ }

  const stale = keys.map((k, i) => {
    if (has('--all')) return i;
    if (only) return only.includes(i) ? i : -1;
    const frame = join(OUT, `slide-${String(i).padStart(2, '0')}.png`);
    return (cache.keys?.[i] === k && existsSync(frame)) ? -1 : i;
  }).filter(i => i >= 0);

  if (!stale.length) {
    say(`frames: all ${slides.length} up to date`);
    return { cache, stale };
  }

  if (!CHROME) throw new Error('No Chromium found. Set one of the paths at the top of build.mjs.');
  // Playwright lives with the client, not here, so this folder needs no node_modules of its own.
  const local = join(HERE, '../../product/client/node_modules/playwright-core');
  const mod = await import(existsSync(local) ? pathToFileURL(join(local, 'index.js')).href : 'playwright-core');
  const chromium = mod.chromium ?? mod.default?.chromium;   // playwright-core is CommonJS
  if (!chromium) throw new Error('playwright-core found but exposed no chromium export.');
  const server = await serve();
  const browser = await chromium.launch({ executablePath: CHROME });
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });

  say(`frames: rendering ${stale.length} of ${slides.length} (${stale.join(', ')})`);
  for (const i of stale) {
    await page.goto(`http://127.0.0.1:${PORT}/template.html?still=${i}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: join(OUT, `slide-${String(i).padStart(2, '0')}.png`) });
    process.stdout.write(`  ${i}`);
  }
  say('');

  await browser.close();
  server.close();
  await writeFile(CACHE, JSON.stringify({ keys, seconds: slides.map(s => s.seconds) }, null, 2));
  return { cache, stale };
}

/* ── ffmpeg: the bundled one if Playwright installed it, otherwise whatever is on PATH ──────────────── */
async function ffmpeg() {
  for (const p of ['/usr/local/lib/python3.11/dist-packages/imageio_ffmpeg/binaries', join(HERE, 'bin')]) {
    if (!existsSync(p)) continue;
    const found = (await readdir(p)).find(f => f.startsWith('ffmpeg'));
    if (found) return join(p, found);
  }
  return 'ffmpeg';
}
const run = (cmd, argv) => new Promise((ok, bad) => {
  const p = spawn(cmd, argv, { stdio: ['ignore', 'ignore', 'pipe'] });
  let err = '';
  p.stderr.on('data', d => { err += d; });
  p.on('close', c => c === 0 ? ok() : bad(new Error(err.slice(-1800))));
});

async function encode(slides) {
  const ff = await ffmpeg();
  const XF = 0.6;
  const inputs = [];
  slides.forEach((s, i) => inputs.push('-loop', '1', '-t', String(s.seconds),
    '-i', join(OUT, `slide-${String(i).padStart(2, '0')}.png`)));

  const parts = [];
  let prev = '0:v', offset = 0;
  for (let i = 1; i < slides.length; i++) {
    offset += slides[i - 1].seconds - XF;
    parts.push(`[${prev}][${i}:v]xfade=transition=fade:duration=${XF}:offset=${offset.toFixed(3)}[x${i}]`);
    prev = `x${i}`;
  }
  const chain = parts.join(';') + `;[${prev}]format=yuv420p[v]`;
  const total = slides.reduce((a, s) => a + s.seconds, 0) - XF * (slides.length - 1);
  const mp4 = join(OUT, 'AeroLink-overview.mp4');

  say(`video : encoding ${slides.length} slides, ${Math.floor(total / 60)}:${String(Math.round(total % 60)).padStart(2, '0')}`);
  await run(ff, ['-y', '-loglevel', 'error', ...inputs, '-filter_complex', chain, '-map', '[v]',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-pix_fmt', 'yuv420p',
    '-movflags', '+faststart', '-r', '30', mp4]);
  say(`video : ${mp4} (${((await stat(mp4)).size / 1048576).toFixed(1)} MB)`);
  return ff;
}

/* ── the shareable single-file page: same frames, embedded, with pause and step controls ────────────── */
async function sharePage(slides, ff) {
  const jpegs = [];
  for (let i = 0; i < slides.length; i++) {
    const src = join(OUT, `slide-${String(i).padStart(2, '0')}.png`);
    const dst = join(OUT, `web-${String(i).padStart(2, '0')}.jpg`);
    await run(ff, ['-y', '-loglevel', 'error', '-i', src, '-vf', 'scale=1600:900', '-q:v', '5', dst]);
    jpegs.push((await readFile(dst)).toString('base64'));
  }
  const chapters = slides.map(s => s.chapter || s.kicker || s.title || '');
  const n = slides.length;

  const html = `<title>AeroLink — a four-minute overview</title>
<style>
  :root { --teal:#4ED8C4; --ink:#E9F1F7; --dim:#7C93A8; }
  * { box-sizing:border-box; margin:0; padding:0; }
  html,body { height:100%; background:#04101d; color:var(--ink);
    font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif; }
  body { display:flex; flex-direction:column; }
  .screen { flex:1; display:grid; place-items:center; padding:16px 16px 0; min-height:0; }
  .reel { position:relative; width:100%; max-width:1600px; aspect-ratio:16/9; border-radius:10px;
    overflow:hidden; background:#04101d; box-shadow:0 24px 70px rgba(0,0,0,.6), 0 0 0 1px rgba(140,190,215,.14); }
  .f { position:absolute; inset:0; width:100%; height:100%; object-fit:contain; opacity:0; transition:opacity .45s ease; }
  .f.on { opacity:1; }
  .bar { position:absolute; left:0; right:0; bottom:0; height:4px; background:rgba(255,255,255,.10); }
  .bar > i { display:block; height:100%; width:0; background:var(--teal); }
  .ctl { display:flex; align-items:center; gap:14px; justify-content:center; padding:14px 16px 20px; flex-wrap:wrap; }
  button { font:inherit; font-size:14px; color:var(--ink); background:rgba(255,255,255,.07);
    border:1px solid rgba(140,190,215,.22); border-radius:7px; padding:9px 15px; cursor:pointer; min-height:40px; }
  button:hover { background:rgba(78,216,196,.16); border-color:var(--teal); }
  button:focus-visible { outline:2px solid var(--teal); outline-offset:2px; }
  .pos { font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace; font-size:13px; color:var(--dim);
    letter-spacing:.08em; min-width:240px; text-align:center; }
  .pos b { color:var(--ink); font-weight:600; letter-spacing:0; }
  @media (prefers-reduced-motion: reduce) { .f { transition:none; } }
</style>
<div class="screen"><div class="reel" id="reel">
${jpegs.map((b, i) => `<img class="f" src="data:image/jpeg;base64,${b}" alt="Slide ${i + 1} of ${n}" data-d="${slides[i].seconds}">`).join('\n')}
  <div class="bar"><i id="bar"></i></div>
</div></div>
<div class="ctl">
  <button id="prev" aria-label="Previous slide">‹ Back</button>
  <button id="play" aria-label="Pause or play">Pause</button>
  <button id="next" aria-label="Next slide">Next ›</button>
  <span class="pos" id="pos"></span>
  <button id="restart">Restart</button>
</div>
<script>
const CH = ${JSON.stringify(chapters)}, N = ${n};
const f = [...document.querySelectorAll('.f')];
const bar = document.getElementById('bar'), pos = document.getElementById('pos'), play = document.getElementById('play');
let i = 0, timer = null, running = true, started = 0, left = 0;
function paint() {
  f.forEach((el, k) => el.classList.toggle('on', k === i));
  pos.innerHTML = '<b>' + CH[i] + '</b> &nbsp; ' + String(i + 1).padStart(2, '0') + ' / ' + N;
}
function run(ms) {
  const d = ms ?? Number(f[i].dataset.d) * 1000;
  left = d; started = Date.now();
  bar.style.transition = 'none'; bar.style.width = (i / N * 100) + '%';
  requestAnimationFrame(() => { bar.style.transition = 'width ' + d + 'ms linear'; bar.style.width = ((i + 1) / N * 100) + '%'; });
  clearTimeout(timer);
  timer = setTimeout(() => { if (i < N - 1) { i++; paint(); run(); } else setRunning(false); }, d);
}
function setRunning(on) {
  running = on; play.textContent = on ? 'Pause' : 'Play';
  if (on) run(left > 0 ? left : undefined);
  else { clearTimeout(timer); left -= Date.now() - started; const w = getComputedStyle(bar).width; bar.style.transition = 'none'; bar.style.width = w; }
}
function go(k) {
  i = Math.max(0, Math.min(N - 1, k)); left = 0; paint();
  if (running) run(); else { bar.style.transition = 'none'; bar.style.width = ((i + 1) / N * 100) + '%'; }
}
document.getElementById('next').onclick = () => go(i + 1);
document.getElementById('prev').onclick = () => go(i - 1);
document.getElementById('restart').onclick = () => { setRunning(true); go(0); };
play.onclick = () => setRunning(!running);
document.getElementById('reel').onclick = () => go(i + 1);
addEventListener('keydown', e => {
  if (e.key === 'ArrowRight') go(i + 1);
  if (e.key === 'ArrowLeft') go(i - 1);
  if (e.key === ' ') { e.preventDefault(); setRunning(!running); }
});
paint(); run();
<\/script>`;

  const out = join(OUT, 'overview.html');
  await writeFile(out, html);
  say(`page  : ${out} (${(Buffer.byteLength(html) / 1048576).toFixed(1)} MB, self-contained)`);
}

/* ── go ─────────────────────────────────────────────────────────────────────────────────────────────── */
const { slides, keys } = await fingerprints();
const { stale } = await renderFrames(slides, keys);

if (has('--frames')) {
  say('stopping after frames (--frames). Drop the flag to produce the MP4 and the shareable page.');
} else {
  const ff = await encode(slides);
  await sharePage(slides, ff);
}
say(`done. ${stale.length ? stale.length + ' slide(s) redrawn' : 'nothing needed redrawing'}.`);

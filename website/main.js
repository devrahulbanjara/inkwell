const esc = (s) => s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
const DL = {
  mac: 'https://github.com/devrahulbanjara/inkwell/releases/latest/download/Inkwell.dmg',
  windows: 'https://github.com/devrahulbanjara/inkwell/releases/latest/download/Inkwell-Setup.exe',
};

// ---------- Platform: what the visitor is on, and what the page is showing ----------
const ua = navigator.userAgent || '';
const visitorOS = /Windows/i.test(ua) ? 'windows' : /Mac/i.test(ua) && !/iPhone|iPad/i.test(ua) ? 'mac' : null;

function setPlatform(p) {
  document.documentElement.dataset.platform = p;
  document.querySelectorAll('[data-set-platform]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.setPlatform === p)));
}
setPlatform(visitorOS || 'mac');
document.querySelectorAll('[data-set-platform]').forEach((b) => b.addEventListener('click', () => setPlatform(b.dataset.setPlatform)));

// Downloads: the visitor's platform goes first and stays solid; the other becomes secondary.
if (visitorOS) {
  document.querySelectorAll('[data-downloads]').forEach((group) => {
    group.querySelectorAll(`[data-os="${visitorOS}"]`).forEach((el) => group.prepend(el));
    group.querySelectorAll('a[data-os]').forEach((el) => el.classList.toggle('ghost', el.dataset.os !== visitorOS));
  });
  const install = document.querySelector('.install');
  const mine = document.querySelector(`[data-os-card="${visitorOS}"]`);
  if (install && mine) { install.prepend(mine); mine.classList.add('mine'); }
}
const navDl = document.querySelector('[data-platform-download]');
if (navDl && visitorOS) {
  navDl.href = DL[visitorOS];
  navDl.querySelector('use').setAttribute('href', visitorOS === 'windows' ? '#i-windows' : '#i-apple');
} else if (navDl) {
  navDl.querySelector('svg').remove();
}

// ---------- Copy buttons ----------
document.querySelectorAll('[data-copy]').forEach((btn) => {
  btn.addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(btn.dataset.copy); } catch (_) { return; }
    const s = btn.querySelector('.copy-state span');
    btn.classList.add('copied'); s.textContent = 'Copied';
    setTimeout(() => { btn.classList.remove('copied'); s.textContent = 'Copy'; }, 1600);
  });
});

// ---------- Headline that fixes itself ----------
(() => {
  const h1 = document.getElementById('headline');
  const replay = document.getElementById('replay');
  if (!h1 || reduce) return;
  // [as typed, corrected]. Words without a correction are typed right.
  const words = [['fix', 'Fix'], ["you're", 'your'], ['writting', 'writing'], ['without'], ['leaving'], ['the'], ['txt', 'text'], ['box', 'box.']];
  const typedText = words.map((w) => w[0]).join(' ');
  const trigger = '?fix';
  let run = 0;
  const wait = (ms, id) => new Promise((res, rej) => setTimeout(() => (id === run ? res() : rej()), ms));

  // Reserve the final height so the page doesn't jump while it types.
  // Measure the tallest state (marked-up, with both the mistake and the fix showing) and reserve that.
  const markedHTML = () => '<span aria-hidden="true">' + words.map(([a, b]) =>
    b ? `<span class="w"><del>${esc(a)}</del><ins>${esc(b)}</ins></span>` : esc(a)).join(' ') +
    '<span class="trigger gone"> ' + trigger + '</span></span>';
  const lockHeight = () => {
    const saved = h1.innerHTML, cls = h1.className;
    h1.style.minHeight = '';
    h1.className = 'typing';
    h1.innerHTML = markedHTML();
    h1.querySelectorAll('.w').forEach((w) => w.classList.add('marked'));
    const tall = h1.offsetHeight;
    h1.innerHTML = saved; h1.className = cls;
    h1.style.minHeight = Math.max(tall, h1.offsetHeight) + 'px';
  };

  async function play() {
    const id = ++run;
    replay.classList.remove('show');
    h1.className = 'typing';
    try {
      // 1. Type the sloppy version, then the trigger.
      for (let k = 0; k <= typedText.length; k++) {
        h1.innerHTML = `<span aria-hidden="true">${esc(typedText.slice(0, k))}<span class="caret"></span></span>`;
        await wait(k === 0 ? 500 : 30 + Math.random() * 40, id);
      }
      for (let k = 1; k <= trigger.length; k++) {
        h1.innerHTML = `<span aria-hidden="true">${esc(typedText)} <span class="trigger">${esc(trigger.slice(0, k))}</span><span class="caret"></span></span>`;
        await wait(90, id);
      }
      // 2. Inkwell is working.
      const tr = h1.querySelector('.trigger');
      tr.classList.add('working');
      h1.querySelector('.caret').remove();
      await wait(1000, id);
      // 3. Proofreading marks: strike the mistakes in red, write the fix in violet.
      h1.innerHTML = markedHTML();
      const marks = [...h1.querySelectorAll('.w')];
      await wait(60, id);
      for (const m of marks) { m.classList.add('marked'); await wait(170, id); }
      await wait(1100, id);
      // 4. Clean copy.
      h1.querySelector('.trigger').remove();
      h1.className = 'settled';
      h1.querySelector('span').insertAdjacentHTML('beforeend', '<span class="caret"></span>');
      replay.classList.add('show');
    } catch (_) { /* replaced by a newer run */ }
  }

  lockHeight();
  addEventListener('resize', () => { if (h1.classList.contains('settled')) lockHeight(); });
  replay.addEventListener('click', play);
  play();
})();

// ---------- Reveal: cross out the old way when it scrolls into view ----------
(() => {
  const els = document.querySelectorAll('[data-reveal]');
  if (!('IntersectionObserver' in window) || reduce) { els.forEach((e) => e.classList.add('seen')); return; }
  const io = new IntersectionObserver((entries) => entries.forEach((e) => {
    if (e.isIntersecting) { setTimeout(() => e.target.classList.add('seen'), 500); io.unobserve(e.target); }
  }), { threshold: 0.35 });
  els.forEach((e) => io.observe(e));
})();

// ---------- Live demo in a chat app ----------
const demos = [
  { name: 'Fixing grammar', trigger: '?fix', incoming: "Can you send me the Q3 report before Friday's review?",
    text: 'yea ill send u the reprot tmrw morning', result: "Yeah, I'll send you the report tomorrow morning." },
  { name: 'Making it formal', trigger: '?formal', incoming: 'Will you be joining the planning session on Thursday?', text: 'cant make it thursday, something came up sry',
    result: "Unfortunately, I won't be able to make it on Thursday as something has come up. My apologies." },
  { name: 'Making it shorter', trigger: '?short', incoming: 'Does 3pm still work for our call?', text: 'hey so i was thinking maybe we could possibly push the call to like 4 if that works for you',
    result: 'Could we push the call to 4pm?' },
  { name: 'Thinking', trigger: '?ask', incoming: 'Dinner came to $64.50. How much should we tip?', text: '18% tip on $64.50',
    result: 'An 18% tip on $64.50 is $11.61.' },
];

const desk = document.querySelector('.desk');
const typed = document.getElementById('typed');
const field = document.getElementById('field');
const hudText = document.getElementById('hudText');
const hud = document.getElementById('hud');
const sent = document.getElementById('sent');
const incoming = document.getElementById('incoming');
const stepBtns = [...document.querySelectorAll('#demoSteps button')];

let run = 0; // bumps to cancel an in-flight sequence
const sleep = (ms, id) => new Promise((res, rej) => setTimeout(() => (id === run ? res() : rej('cancel')), ms));
const render = (text, trig) => { typed.innerHTML = esc(text) + (trig ? `<span class="t">${esc(trig)}</span>` : ''); };

async function play(i, id) {
  const d = demos[i];
  stepBtns.forEach((b, j) => b.classList.toggle('on', j === i));
  sent.hidden = true; desk.classList.remove('working', 'done'); render('', '');
  incoming.textContent = d.incoming;
  if (reduce) { render(d.result, ''); return; }
  await sleep(500, id);
  const base = d.text + ' ';
  for (let k = 1; k <= base.length; k++) { render(base.slice(0, k), ''); await sleep(28 + Math.random() * 45, id); }
  for (let k = 1; k <= d.trigger.length; k++) { render(base, d.trigger.slice(0, k)); await sleep(70, id); }
  await sleep(350, id);
  hudText.textContent = d.name + '…';
  hud.className = 'hud ink-pill is-working';
  desk.classList.add('working');
  await sleep(1800, id);
  // Same as the app: the text is replaced, the ring turns green and says Done, then the pill fades.
  render(d.result, '');
  hudText.textContent = 'Done';
  hud.className = 'hud ink-pill is-done';
  desk.classList.replace('working', 'done');
  field.classList.add('flash');
  await sleep(1100, id);
  desk.classList.remove('done');
  field.classList.remove('flash');
  await sleep(1000, id);
  sent.textContent = d.result; sent.hidden = false; render('', '');
  await sleep(2200, id);
}

async function loop(start) {
  const id = ++run;
  let i = start;
  try {
    for (;;) { await play(i, id); if (reduce) return; i = (i + 1) % demos.length; }
  } catch (_) { /* cancelled */ }
}
stepBtns.forEach((b, j) => b.addEventListener('click', () => loop(j)));

// Play only while the demo is on screen.
let live = false;
if ('IntersectionObserver' in window) {
  new IntersectionObserver(([e]) => {
    if (e.isIntersecting && !live) { live = true; loop(0); }
    else if (!e.isIntersecting && live) { live = false; run++; }
  }, { threshold: 0.3 }).observe(desk);
} else loop(0);

// ---------- Trigger glossary ----------
const triggers = [
  { name: 'Fix grammar', in: 'their going to love the new desing, its much more cleaner', t: '?fix', out: "They're going to love the new design. It's much cleaner." },
  { name: 'Rewrite', t: '?rewrite', in: 'the meeting got moved due to the fact that a lot of people were not going to be able to come on monday',
    out: "We moved the meeting because many people couldn't make it on Monday." },
  { name: 'Make formal', t: '?formal', in: 'thanks for the invite, i cant make it thursday', out: "Thank you for the invitation. Unfortunately, I won't be able to attend on Thursday." },
  { name: 'Make shorter', t: '?short', in: "I just wanted to quickly reach out and let you know that I'm probably going to be running about ten minutes late to our call this afternoon",
    out: 'Running about 10 minutes late to our call this afternoon.' },
  { name: 'Ask', t: '?ask', in: 'regex to match an email address', out: '^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$' },
  { name: 'Reply', t: '?reply', mode: 'append', in: 'Hi! Are you free for a quick call tomorrow to go over the launch plan?',
    out: "Hi! Are you free for a quick call tomorrow to go over the launch plan?\n\nYes, tomorrow works. How about 11am? I'll bring the latest timeline." },
  { name: 'To English', t: '?en', in: '¿Puedes enviarme el informe antes del viernes?', out: 'Can you send me the report before Friday?' },
];
const list = document.getElementById('trigList');
const tsName = document.getElementById('tsName');
const tsIn = document.getElementById('tsIn');
const tsOut = document.getElementById('tsOut');
const tsMode = document.getElementById('tsMode');

// Buttons are in the HTML so the triggers are readable without JavaScript.
[...list.children].forEach((b, i) => b.addEventListener('click', () => show(i)));
function show(i) {
  const tr = triggers[i];
  [...list.children].forEach((b, j) => { b.classList.toggle('on', j === i); b.setAttribute('aria-selected', String(j === i)); });
  tsName.textContent = tr.name;
  tsIn.innerHTML = `${esc(tr.in)} <span class="t">${esc(tr.t)}</span>`;
  tsOut.innerHTML = tr.mode === 'append'
    ? (() => { const [a, b] = tr.out.split('\n\n'); return `<span class="dim">${esc(a)}</span>\n\n${esc(b)}`; })()
    : esc(tr.out);
  tsMode.textContent = tr.mode === 'append' ? 'Adds the result below your text' : 'Replaces your text';
  [tsIn, tsOut].forEach((el) => { el.classList.remove('fade'); void el.offsetWidth; el.classList.add('fade'); });
}
show(0);

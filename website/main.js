// Copy buttons
document.querySelectorAll('[data-copy]').forEach((btn) => {
  btn.addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(btn.dataset.copy); } catch (_) { return; }
    const s = btn.querySelector('.copy-state');
    btn.classList.add('copied'); s.textContent = 'Copied';
    setTimeout(() => { btn.classList.remove('copied'); s.textContent = 'Copy'; }, 1600);
  });
});

const esc = (s) => s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

// ---------- Hero demo ----------
const demos = [
  { name: 'Fix grammar', trigger: '?fix', incoming: "Can you send me the Q3 report before Friday's review?",
    text: 'yea ill send u the reprot tmrw morning', result: "Yeah, I'll send you the report tomorrow morning." },
  { name: 'Make formal', trigger: '?formal', incoming: 'Will you be joining the planning session on Thursday?', text: 'cant make it thursday, something came up sry',
    result: "Unfortunately, I won't be able to make it on Thursday as something has come up. My apologies." },
  { name: 'Make shorter', trigger: '?short', incoming: 'Does 3pm still work for our call?', text: 'hey so i was thinking maybe we could possibly push the call to like 4 if that works for you',
    result: 'Could we push the call to 4pm?' },
  { name: 'Ask', trigger: '?ask', incoming: 'Dinner came to $64.50. How much should we tip?', text: "18% tip on $64.50",
    result: 'An 18% tip on $64.50 is $11.61.' },
];

const desk = document.querySelector('.desk');
const typed = document.getElementById('typed');
const field = document.getElementById('field');
const hudText = document.getElementById('hudText');
const sent = document.getElementById('sent');
const incoming = document.getElementById('incoming');
const stepBtns = [...document.querySelectorAll('#demoSteps button')];
const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;

let run = 0; // bumps to cancel an in-flight sequence
const sleep = (ms, id) => new Promise((res, rej) => setTimeout(() => (id === run ? res() : rej('cancel')), ms));

function render(text, trig) {
  typed.innerHTML = esc(text) + (trig ? `<span class="t">${esc(trig)}</span>` : '');
}

async function play(i, id) {
  const d = demos[i];
  stepBtns.forEach((b, j) => b.classList.toggle('on', j === i));
  sent.hidden = true; desk.classList.remove('working'); render('', '');
  incoming.textContent = d.incoming;
  if (reduce) { render(d.result, ''); return; }
  await sleep(500, id);
  const base = d.text ? d.text + ' ' : '';
  for (let k = 1; k <= base.length; k++) { render(base.slice(0, k), ''); await sleep(28 + Math.random() * 45, id); }
  for (let k = 1; k <= d.trigger.length; k++) { render(base, d.trigger.slice(0, k)); await sleep(70, id); }
  await sleep(350, id);
  hudText.textContent = d.name + '…';
  desk.classList.add('working');
  await sleep(1300, id);
  desk.classList.remove('working');
  render(d.result, '');
  field.classList.add('flash');
  await sleep(500, id);
  field.classList.remove('flash');
  await sleep(1300, id);
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

// Start right away; pause while the demo is scrolled out of view.
let live = true;
loop(0);
if ('IntersectionObserver' in window) {
  new IntersectionObserver(([e]) => {
    if (e.isIntersecting && !live) { live = true; loop(0); }
    else if (!e.isIntersecting && live) { live = false; run++; }
  }).observe(desk);
}

// ---------- Trigger explorer ----------
const triggers = [
  { t: '?fix', name: 'Fix grammar', short: 'Spelling & grammar',
    in: 'their going to love the new desing, its much more cleaner', out: "They're going to love the new design. It's much cleaner." },
  { t: '?rewrite', name: 'Rewrite', short: 'Reads more clearly',
    in: 'the meeting got moved due to the fact that a lot of people were not going to be able to come on monday',
    out: "We moved the meeting because many people couldn't make it on Monday." },
  { t: '?formal', name: 'Make formal', short: 'Polite & professional',
    in: 'thanks for the invite, i cant make it thursday', out: "Thank you for the invitation. Unfortunately, I won't be able to attend on Thursday." },
  { t: '?short', name: 'Make shorter', short: 'Says it in fewer words',
    in: "I just wanted to quickly reach out and let you know that I'm probably going to be running about ten minutes late to our call this afternoon",
    out: 'Running about 10 minutes late to our call this afternoon.' },
  { t: '?ask', name: 'Ask', short: 'Answers the question',
    in: 'regex to match an email address', out: '^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$' },
  { t: '?reply', name: 'Reply', short: 'Drafts a reply below', mode: 'append',
    in: "Hi! Are you free for a quick call tomorrow to go over the launch plan?",
    out: "Hi! Are you free for a quick call tomorrow to go over the launch plan?\n\nYes, tomorrow works. How about 11am? I'll bring the latest timeline." },
  { t: '?en', name: 'To English', short: 'Translates to English',
    in: '¿Puedes enviarme el informe antes del viernes?', out: 'Can you send me the report before Friday?' },
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
  [...list.children].forEach((b, j) => b.classList.toggle('on', j === i));
  tsName.textContent = tr.name;
  tsIn.innerHTML = `${esc(tr.in)} <span class="t">${esc(tr.t)}</span>`;
  tsOut.innerHTML = tr.mode === 'append'
    ? (() => { const [a, b] = tr.out.split('\n\n'); return `<span class="dim">${esc(a)}</span>\n\n${esc(b)}`; })()
    : esc(tr.out);
  tsMode.textContent = tr.mode === 'append' ? 'Adds the result below your text' : 'Replaces your text';
  [tsIn, tsOut].forEach((el) => { el.classList.remove('fade'); void el.offsetWidth; el.classList.add('fade'); });
}
show(0);

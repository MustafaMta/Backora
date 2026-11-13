// Backora V2 interop module: theme, focus management, shortcuts, clipboard, preferences.
export function setTheme(mode) {
  const dark = mode === 'dark' || (mode === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  document.documentElement.setAttribute('data-theme-mode', mode);
  try { localStorage.setItem('bko-theme', mode); } catch (e) { }
  return dark ? 'dark' : 'light';
}
export function getTheme() { try { return localStorage.getItem('bko-theme') || 'system'; } catch (e) { return 'system'; } }
export function isDarkTheme() { return document.documentElement.getAttribute('data-theme') === 'dark'; }

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
const traps = new Map(); let seq = 0;
export function trapFocus(container, initialSelector) {
  if (!container) return 0;
  const id = ++seq; const previous = document.activeElement;
  const onKey = (e) => {
    if (e.key !== 'Tab') return;
    const items = [...container.querySelectorAll(FOCUSABLE)].filter(el => el.offsetParent !== null);
    if (!items.length) { e.preventDefault(); return; }
    const first = items[0], last = items[items.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  };
  container.addEventListener('keydown', onKey);
  traps.set(id, { container, previous, onKey });
  requestAnimationFrame(() => {
    const t = (initialSelector && container.querySelector(initialSelector)) || container.querySelector('[autofocus]') || container.querySelector(FOCUSABLE) || container;
    t?.focus?.();
  });
  document.documentElement.classList.add('is-locked');
  return id;
}
export function releaseFocus(id) {
  const t = traps.get(id); if (!t) return;
  t.container.removeEventListener('keydown', t.onKey); traps.delete(id);
  if (!traps.size) document.documentElement.classList.remove('is-locked');
  if (t.previous?.focus && document.contains(t.previous)) t.previous.focus();
}

let ref = null, pendingG = 0;
// Linear-style list navigation: j/k (or arrows once a row has focus) move between rows, Enter opens.
function moveInList(e) {
  const down = e.key === 'j' || e.key === 'ArrowDown', up = e.key === 'k' || e.key === 'ArrowUp';
  if (!down && !up) return false;
  const rows = [...document.querySelectorAll('.app-main a.ui-list__row, .app-main .ui-list__row > a.row-link')].filter(r => r.offsetParent !== null);
  if (!rows.length) return false;
  const i = rows.indexOf(document.activeElement);
  if (i < 0 && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) return false; // plain arrows keep scrolling the page
  e.preventDefault();
  const next = rows[Math.max(0, Math.min(rows.length - 1, i < 0 ? 0 : i + (down ? 1 : -1)))];
  next.focus({ preventScroll: true });
  next.scrollIntoView({ block: 'nearest' });
  return true;
}
const typing = (el) => el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
function onKey(e) {
  if (!ref) return;
  if ((e.metaKey || e.ctrlKey) && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); ref.invokeMethodAsync('OnShortcut', 'palette'); return; }
  if (e.metaKey || e.ctrlKey || e.altKey || typing(e.target)) return;
  if (e.key === '/') { e.preventDefault(); ref.invokeMethodAsync('OnShortcut', 'palette'); return; }
  if (e.key === '?') { e.preventDefault(); ref.invokeMethodAsync('OnShortcut', 'help'); return; }
  if (e.key === '[') { e.preventDefault(); ref.invokeMethodAsync('OnShortcut', 'rail'); return; }
  if (moveInList(e)) return;
  const now = Date.now();
  if (e.key === 'g') { pendingG = now; return; }
  if (pendingG && now - pendingG < 1200) {
    pendingG = 0;
    const map = { o: '/', b: '/jobs', r: '/recovery', s: '/servers', d: '/storage', n: '/jobs/new' };
    if (map[e.key]) { e.preventDefault(); ref.invokeMethodAsync('OnShortcut', 'nav:' + map[e.key]); }
  }
}
export function registerShortcuts(r) { ref = r; document.removeEventListener('keydown', onKey); document.addEventListener('keydown', onKey); }
export function unregisterShortcuts() { ref = null; document.removeEventListener('keydown', onKey); }

export async function copyText(text) { try { await navigator.clipboard.writeText(text); return true; } catch (e) { return false; } }
export function focusElement(el) { el?.focus?.(); }
export function timeZone() { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch (e) { return 'UTC'; } }
export function clickOutside(el, r, method) {
  const h = (e) => { if (el && !el.contains(e.target)) r.invokeMethodAsync(method); };
  document.addEventListener('mousedown', h);
  return { dispose: () => document.removeEventListener('mousedown', h) };
}
export function resetScroll() { document.querySelector('.app-main')?.scrollTo(0, 0); window.scrollTo(0, 0); }
export function getPref(k) { try { return localStorage.getItem('bko-' + k); } catch (e) { return null; } }
export function setPref(k, v) { try { localStorage.setItem('bko-' + k, v); } catch (e) { } }

// Draws curved connectors between topology nodes: <path data-from="id" data-to="id"> inside the root,
// nodes carry data-node="id". Redraws on resize. Pure presentation; no data leaves the page.
export function drawLinks(root) {
  if (!root) return;
  const draw = () => {
    const box = root.getBoundingClientRect();
    root.querySelectorAll('path[data-from]').forEach(p => {
      const a = root.querySelector(`[data-node="${p.dataset.from}"]`), b = root.querySelector(`[data-node="${p.dataset.to}"]`);
      if (!a || !b) { p.setAttribute('d', ''); return; }
      const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
      const x1 = ra.right - box.left, y1 = ra.top + ra.height / 2 - box.top, x2 = rb.left - box.left, y2 = rb.top + rb.height / 2 - box.top, mx = (x1 + x2) / 2;
      p.setAttribute('d', `M${x1} ${y1}C${mx} ${y1} ${mx} ${y2} ${x2} ${y2}`);
    });
  };
  draw();
  if (!root._bkoRo) { root._bkoRo = new ResizeObserver(draw); root._bkoRo.observe(root); }
}

// Backora V2 — premium motion layer (react-bits-style): a gentle tilt on choice tiles, a click spark on primary actions, and a short "page enter" flag that lets lists,
// stats and titles animate in once per navigation (never on live refreshes). Purely visual; honours reduced motion.
(function () {
  'use strict';
  var reduce = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  var CARDS = '.ui-tile';   // choice tiles tilt toward the pointer; cards have no pointer highlight
  var current = null;

  function leave() { if (current) { current.classList.remove('fx-on'); current = null; } }
  document.addEventListener('pointermove', function (e) {
    if (e.pointerType === 'touch') return;
    var el = e.target && e.target.closest ? e.target.closest(CARDS) : null;
    if (el !== current) { leave(); if (el) { current = el; el.classList.add('fx-on'); } }
    if (!el) return;
    var r = el.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
    if (!reduce && el.classList.contains('ui-tile') && !el.disabled) {
      el.style.setProperty('--rx', ((0.5 - y / r.height) * 5).toFixed(2) + 'deg');
      el.style.setProperty('--ry', ((x / r.width - 0.5) * 6).toFixed(2) + 'deg');
    }
  }, { passive: true });
  document.addEventListener('pointerleave', leave);
  document.addEventListener('pointerout', function (e) { if (!e.relatedTarget) leave(); });

  // Click spark on primary actions (drawn in a body-level overlay, outside Blazor's DOM).
  var layer = null;
  document.addEventListener('pointerdown', function (e) {
    if (reduce || e.button !== 0) return;
    var b = e.target && e.target.closest ? e.target.closest('.ui-btn--primary') : null;
    if (!b || b.disabled) return;
    if (!layer || !layer.isConnected) { layer = document.createElement('div'); layer.className = 'fx-sparks'; layer.setAttribute('aria-hidden', 'true'); document.body.appendChild(layer); }
    for (var i = 0; i < 8; i++) {
      var s = document.createElement('span');
      s.className = 'fx-spark';
      s.style.left = e.clientX + 'px'; s.style.top = e.clientY + 'px';
      s.style.setProperty('--a', (i * 45) + 'deg');
      layer.appendChild(s);
      setTimeout(function (n) { n.remove(); }, 520, s);
    }
  });

  // Page-enter flag: set when the routed page changes, cleared shortly after, so entrance animations run once.
  var flagTimer = 0, lastPage = null;
  function enter() {
    if (reduce) return;
    document.documentElement.classList.add('fx-enter');
    clearTimeout(flagTimer);
    flagTimer = setTimeout(function () { document.documentElement.classList.remove('fx-enter'); }, 1100);
  }
  // Blazor routes client-side, so watch the path (cheap) rather than the DOM.
  function tick() { if (location.pathname !== lastPage) { lastPage = location.pathname; enter(); } }
  tick(); setInterval(tick, 200);
})();

// Backora V2 — applies the saved theme before first paint (no inline script, CSP stays strict).
(function () {
  var mode = 'system';
  try { mode = localStorage.getItem('bko-theme') || 'system'; } catch (e) { }
  var dark = mode === 'dark' || (mode === 'system' && window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  document.documentElement.setAttribute('data-theme-mode', mode);
  if (window.matchMedia) matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function (e) {
    if ((document.documentElement.getAttribute('data-theme-mode') || 'system') === 'system')
      document.documentElement.setAttribute('data-theme', e.matches ? 'dark' : 'light');
  });
})();

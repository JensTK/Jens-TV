// Injected by Jens-TV into every page shown inside the TV window except the launcher itself (i.e. Jellyfin).
// __TV_ORIGINS__ is replaced with a JSON array of origins that should use Jellyfin's TV layout.
(() => {
  if (location.hostname === 'jenstv.local' || !window.chrome || !window.chrome.webview) return;

  const tvOrigins = __TV_ORIGINS__;
  const goHome = () => window.chrome.webview.postMessage(JSON.stringify({ type: 'home' }));

  // Jellyfin web reads its layout ("tv" / "desktop" / "mobile") from localStorage before it renders.
  if (tvOrigins.includes(location.origin)) {
    try {
      if (localStorage.getItem('layout') !== 'tv') localStorage.setItem('layout', 'tv');
    } catch (_) { /* storage unavailable */ }
  }

  const isEditable = (el) => el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));

  // Jellyfin's top-level screens; pressing Back there returns to the Jens-TV launcher.
  const atRoot = () => location.hash === '' || /^#!?\/(home|login|selectserver)(\.html)?(\?|$)/.test(location.hash);
  const dialogOpen = () => !!document.querySelector('.dialogContainer .dialog, .dialog.opened');

  window.addEventListener('keydown', (e) => {
    if (e.key === 'BrowserHome') {
      e.preventDefault();
      goHome();
      return;
    }
    const isBack = e.key === 'Escape' || e.key === 'BrowserBack' || e.key === 'GamepadB' ||
      (e.key === 'Backspace' && !isEditable(e.target));
    if (isBack && atRoot() && !dialogOpen()) {
      e.preventDefault();
      goHome();
    }
  }, true);
})();

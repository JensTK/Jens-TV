// Injected by Jens-TV into every page shown inside the TV window except the launcher itself (Jellyfin, F1 TV …).
// Jens-TV fills in tvOrigins below: a JSON array of Jellyfin origins (web apps with forceTvLayout).
(() => {
  if (location.hostname === 'jenstv.local' || !window.chrome || !window.chrome.webview) return;

  const tvOrigins = __TV_ORIGINS__;
  const isJellyfin = tvOrigins.includes(location.origin);
  const goHome = () => window.chrome.webview.postMessage(JSON.stringify({ type: 'home' }));

  // Jellyfin web reads its layout ("tv" / "desktop" / "mobile") from localStorage before it renders.
  if (isJellyfin) {
    try {
      if (localStorage.getItem('layout') !== 'tv') localStorage.setItem('layout', 'tv');
    } catch (_) { /* storage unavailable */ }
  }

  const isEditable = (el) => el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));

  // Jellyfin's top-level screens; pressing Back there returns to the Jens-TV launcher. Other sites (F1 TV) use
  // Escape for their own things (leaving fullscreen video), so they only get the Home key.
  const atRoot = () => isJellyfin &&
    (location.hash === '' || /^#!?\/(home|login|selectserver)(\.html)?(\?|$)/.test(location.hash));
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

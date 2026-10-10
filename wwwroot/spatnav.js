// Remote-control navigation for websites built for a mouse (e.g. F1 TV), injected only into web apps with
// "spatialNavigation": true. Arrow keys move a visible highlight to the nearest link/button in that direction,
// Enter clicks it, Back goes back, and at the end of a carousel row the carousel's own Next/Previous arrow is pressed.
// Jens-TV fills in the origin list below (don't spell out the placeholder name in comments: it gets replaced).
(() => {
  if (window.top !== window || !__SPATNAV_ORIGINS__.includes(location.origin)) return;

  // A highlight you can see from the couch, on whatever has focus.
  const style = document.createElement('style');
  style.textContent = `
    :focus { outline: 0.25rem solid #fff !important; outline-offset: 0.2rem !important;
             box-shadow: 0 0 0 0.55rem rgba(0, 0, 0, 0.55) !important; border-radius: 0.4rem; }
  `;
  const addStyle = () => { if (document.head && !style.isConnected) document.head.append(style); };
  addStyle();
  document.addEventListener('DOMContentLoaded', addStyle, { once: true });

  const FOCUSABLE = 'a[href], button, input, select, textarea, [tabindex], [role="button"], [role="link"], [role="tab"]';

  // Focusable, visible, and on screen horizontally (cards scrolled out of a carousel don't count; we page the
  // carousel instead). Vertically anything goes: the page scrolls to the target.
  function candidates() {
    return [...document.querySelectorAll(FOCUSABLE)].filter((el) => {
      if (el.tabIndex < 0 || el.disabled || el.closest('[inert], [aria-hidden="true"]')) return false;
      const r = el.getBoundingClientRect();
      if (r.width < 4 || r.height < 4 || r.right <= 0 || r.left >= window.innerWidth) return false;
      const s = getComputedStyle(el);
      return s.visibility !== 'hidden' && s.display !== 'none' && Number(s.opacity) > 0;
    });
  }

  const gap = (a1, a2, b1, b2) => Math.max(0, Math.max(a1, b1) - Math.min(a2, b2)); // 0 when ranges overlap

  // Nearest candidate in the direction, preferring the same row/column. Edges may touch (cards often sit flush).
  function findTarget(from, dir) {
    const o = from.getBoundingClientRect();
    const SLACK = 2;
    let best = null;
    let bestScore = Infinity;
    for (const el of candidates()) {
      if (el === from || el.contains(from) || from.contains(el)) continue;
      const r = el.getBoundingClientRect();
      let along;
      let across;
      if (dir === 'right' || dir === 'left') {
        along = dir === 'right' ? r.left - o.right : o.left - r.right;
        across = gap(o.top, o.bottom, r.top, r.bottom);
      } else {
        along = dir === 'down' ? r.top - o.bottom : o.top - r.bottom;
        across = gap(o.left, o.right, r.left, r.right);
      }
      if (along < -SLACK) continue; // not in that direction (or overlapping, like a carousel arrow on a card)
      const centerOffset = dir === 'right' || dir === 'left'
        ? Math.abs((r.top + r.bottom) / 2 - (o.top + o.bottom) / 2)
        : Math.abs((r.left + r.right) / 2 - (o.left + o.right) / 2);
      const score = Math.max(0, along) + across * 3 + centerOffset * 0.1;
      if (score < bestScore) {
        bestScore = score;
        best = el;
      }
    }
    return best;
  }

  // The carousel's own arrow next to the focused item (Slick and most other sliders label them Next/Previous).
  function findCarouselArrow(from, dir) {
    const pattern = dir === 'right' ? /next|forward/i : /prev|back/i;
    let el = from.parentElement;
    for (let depth = 0; el && depth < 10; depth++, el = el.parentElement) {
      const arrow = [...el.querySelectorAll('button, a, [role="button"]')].find((c) =>
        !c.contains(from) &&
        pattern.test(`${c.getAttribute('aria-label') || ''} ${typeof c.className === 'string' ? c.className : ''}`) &&
        !c.disabled && c.getAttribute('aria-disabled') !== 'true' && !/disabled/i.test(c.className) &&
        c.getBoundingClientRect().width > 0);
      if (arrow) return arrow;
    }
    return null;
  }

  function focus(el) {
    el.focus({ preventScroll: true });
    el.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
  }

  function move(dir) {
    const from = document.activeElement && document.activeElement !== document.body ? document.activeElement : null;
    if (!from) {
      // Nothing highlighted yet: start at the top-left of what's on screen.
      const first = candidates()
        .map((el) => ({ el, r: el.getBoundingClientRect() }))
        .filter(({ r }) => r.top >= 0 && r.top < window.innerHeight)
        .sort((a, b) => a.r.top - b.r.top || a.r.left - b.r.left)[0];
      if (first) focus(first.el);
      return;
    }

    const target = findTarget(from, dir);
    if (target) {
      focus(target);
      return;
    }
    if (dir === 'up' || dir === 'down') {
      // Rows further down are often only created once scrolled near (lazy loading): scroll, then look again.
      window.scrollBy({ top: (dir === 'down' ? 1 : -1) * window.innerHeight * 0.6, behavior: 'smooth' });
      setTimeout(() => { const next = findTarget(from, dir); if (next) focus(next); }, 600);
      return;
    }
    if (dir === 'left' || dir === 'right') {
      const arrow = findCarouselArrow(from, dir);
      if (arrow) {
        arrow.click();
        setTimeout(() => { const next = findTarget(from, dir); if (next) focus(next); }, 450); // after the slide animation
      }
    }
  }

  // Arrow keys, attached right away in the capture phase so they run before the site's own handlers.
  const DIRECTIONS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };
  window.addEventListener('keydown', (e) => {
    const dir = DIRECTIONS[e.key];
    if (!dir || e.altKey || e.ctrlKey || e.metaKey || e.defaultPrevented) return;
    if (document.fullscreenElement) return; // fullscreen video: the player uses arrows to seek / change volume
    const active = document.activeElement;
    if (active && (active.isContentEditable || /^(TEXTAREA|SELECT)$/.test(active.tagName))) return;
    if (active && active.tagName === 'INPUT' && !/^(button|checkbox|radio|submit|reset|range|color|file)$/.test(active.type)
        && (dir === 'left' || dir === 'right')) return; // move the text cursor
    e.preventDefault();
    e.stopPropagation();
    move(dir);
  }, true);

  // Back: only if the site didn't use the key itself (closing a dialog, leaving fullscreen video …).
  const isEditable = (el) => el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));
  window.addEventListener('keydown', (e) => {
    const isBack = e.key === 'BrowserBack' || e.key === 'Escape' || (e.key === 'Backspace' && !isEditable(e.target));
    if (!isBack || e.defaultPrevented || document.fullscreenElement) return;
    if (history.length > 1) {
      e.preventDefault();
      history.back();
    }
  });
})();

(() => {
  'use strict';
  // Messages panel only: hides Instagram's global navigation (the bottom tab
  // bar, or the side rail at wider sizes) so only the conversations remain.
  // Instagram's markup has no stable names, so the bar is recognized the way
  // a person would: a cluster of at least three of the global destinations.
  if (window !== window.top || !['https://www.instagram.com', 'https://instagram.com'].includes(location.origin) ||
      window.__InstaDesktopPanel) return;

  const ROUTES = ['/', '/explore/', '/reels/', '/direct/inbox/'];
  const MARK = 'data-instadesktop-panel-nav';
  // Never hidden: conversation content, dialogs and anything you can type in.
  const PROTECTED = 'main, [role="main"], [role="dialog"], [aria-modal="true"], input, textarea, [contenteditable="true"]';
  const INTERACTIVE = 'a[href], button, [role="button"], [role="link"], input, textarea, [contenteditable="true"]';

  const pathOf = anchor => {
    try {
      const url = new URL(anchor.getAttribute('href'), location.href);
      return url.origin === location.origin ? url.pathname : '';
    } catch { return ''; }
  };
  const routesIn = node => new Set([...node.querySelectorAll('a[href]')].map(pathOf).filter(p => ROUTES.includes(p)));
  const isProtected = node => node.matches(PROTECTED) || !!node.querySelector(PROTECTED);

  function findBars() {
    const bars = new Set();
    for (const anchor of document.querySelectorAll('a[href]')) {
      if (!ROUTES.includes(pathOf(anchor)) || anchor.closest(PROTECTED)) continue;
      // The smallest ancestor holding three distinct destinations is the bar.
      let bar = null;
      for (let node = anchor.parentElement, depth = 0; node && node !== document.body && depth < 10; node = node.parentElement, depth++) {
        if (isProtected(node)) break;
        if (routesIn(node).size >= 3) { bar = node; break; }
      }
      if (!bar) continue;
      // Grow to the outermost wrapper that holds nothing else to interact
      // with and is about the bar's size, so no empty frame of the bar stays
      // behind. The size limit keeps a page container that is still empty
      // while loading from being taken for the bar.
      const controls = bar.querySelectorAll(INTERACTIVE).length;
      const box = bar.getBoundingClientRect();
      const fits = node => {
        const rect = node.getBoundingClientRect();
        return rect.height <= box.height + 24 && rect.width <= box.width + 24;
      };
      for (let parent = bar.parentElement, depth = 0; parent && parent !== document.body && depth < 6 && !isProtected(parent) &&
           parent.querySelectorAll(INTERACTIVE).length === controls && fits(parent); parent = parent.parentElement, depth++) bar = parent;
      bars.add(bar);
    }
    return bars;
  }

  let timer = null;
  function apply() {
    timer = null;
    const bars = findBars();
    for (const old of document.querySelectorAll('[' + MARK + ']')) if (!bars.has(old)) old.removeAttribute(MARK);
    for (const bar of bars) bar.setAttribute(MARK, '');
  }
  const schedule = () => { if (!timer) timer = setTimeout(apply, 150); };

  const start = () => {
    const style = document.createElement('style');
    style.textContent = '[' + MARK + ']{display:none!important;}';
    (document.head || document.documentElement).appendChild(style);
    new MutationObserver(schedule).observe(document.documentElement, { subtree: true, childList: true });
    apply();
  };
  if (document.documentElement) start(); else document.addEventListener('DOMContentLoaded', start, { once: true });
  window.addEventListener('popstate', schedule);
  window.__InstaDesktopPanel = Object.freeze({ apply });
})();

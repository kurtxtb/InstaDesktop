(() => {
  'use strict';
  // Reports the Messages unread count for the taskbar, tray and title badge.
  // Only the number leaves the page; null means the count is not visible.
  if (window !== window.top || !['https://www.instagram.com', 'https://instagram.com'].includes(location.origin) ||
      window.__InstaDesktopUnread) return;

  let last, timer;
  const report = () => {
    timer = null;
    const count = window.__InstaDesktopInboxDom?.readUnreadBadge() ?? null;
    if (count === last) return;
    last = count;
    try { window.chrome?.webview?.postMessage({ type: 'instadesktop:unread', count }); } catch {}
  };
  // Instagram's DOM changes constantly: read at most twice a second.
  const schedule = () => { if (!timer) timer = setTimeout(report, 500); };
  const start = () => {
    new MutationObserver(schedule).observe(document.documentElement, { subtree: true, childList: true, characterData: true });
    schedule();
  };
  if (document.documentElement) start(); else document.addEventListener('DOMContentLoaded', start, { once: true });
  setInterval(schedule, 30000);
  window.__InstaDesktopUnread = Object.freeze({ refresh: () => { last = undefined; schedule(); } });
})();

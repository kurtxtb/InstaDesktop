(() => {
  'use strict';
  if (window !== top || window.__InstaDesktopInboxMonitor || location.origin !== 'https://www.instagram.com') return;
  const inbox = url => {
    try { const u = new URL(url, location.href); return u.origin === 'https://www.instagram.com' && /^\/direct\/inbox\/?$/.test(u.pathname) && !u.search && !u.hash; }
    catch { return false; }
  };
  const post = value => { try { chrome.webview.postMessage(value); } catch {} };
  let stopped = false, scheduled, safety, sequence = 0, previous = '', extractionFailed = false;
  const documentId = crypto.randomUUID();
  const observer = new MutationObserver(schedule);
  function stop() { stopped = true; observer.disconnect(); clearTimeout(scheduled); clearInterval(safety); }
  function blocked() { stop(); post({ type: 'direct-monitor-blocked' }); }
  // This controller must never open a thread, including SPA navigation. A denied
  // route disables the monitor; it does not try clicking or navigating back.
  for (const method of ['pushState', 'replaceState']) {
    const original = history[method];
    history[method] = function (state, title, url) {
      if (url != null && !inbox(url)) { blocked(); return; }
      return original.apply(this, arguments);
    };
  }
  window.addEventListener('popstate', () => { if (!inbox(location.href)) blocked(); else schedule(); });
  window.addEventListener('pagehide', stop);
  function scan() {
    scheduled = null;
    if (stopped) return;
    if (!inbox(location.href)) { blocked(); return; }
    let ready = document.readyState !== 'loading' && !document.querySelector('input[type="password"]');
    let threads = [];
    try {
      if (ready) {
        const rows = window.__InstaDesktopInboxDom?.readRows();
        if (!Array.isArray(rows)) throw new TypeError();
        threads = rows.slice(0, 40);
      }
      extractionFailed = false;
    } catch {
      // Discard stale detail evidence, preserve native/badge delivery, and let
      // the observer/safety timer establish a fresh baseline after recovery.
      ready = false;
      if (!extractionFailed) post({ type: 'direct-monitor-extraction-failed' });
      extractionFailed = true;
    }
    const signature = JSON.stringify({ ready, threads });
    if (signature !== previous) {
      previous = signature;
      post({ type: 'direct-inbox-snapshot', documentId, sequence: ++sequence, ready, threads });
    }
  }
  function schedule() { if (!stopped && !scheduled) scheduled = setTimeout(scan, 500); }
  window.__InstaDesktopInboxMonitor = { stop, scanNow: scan };
  observer.observe(document, { subtree: true, childList: true, characterData: true, attributes: true,
    attributeFilter: ['href', 'src', 'data-unread', 'data-is-unread', 'data-unread-count', 'data-message-direction', 'data-is-own-message', 'aria-busy', 'aria-label', 'aria-hidden', 'class', 'style'] });
  safety = setInterval(schedule, 15000);
  schedule();
})();

(() => {
  'use strict';
  if (window !== window.top || location.protocol !== 'https:' ||
      !['instagram.com', 'www.instagram.com'].includes(location.hostname) ||
      (location.port && location.port !== '443') || window.__InstaDesktopNotifications) return;

  const post = value => { try { window.chrome?.webview?.postMessage(value); } catch {} };
  const text = (value, max = 1000) => String(value || '').replace(/\s+/g, ' ').trim().slice(0, max).replace(/[\uD800-\uDBFF]$/, '');
  const threadPath = href => {
    try {
      const url = new URL(href, location.origin);
      return ['https://www.instagram.com', 'https://instagram.com'].includes(url.origin) &&
        /^\/direct\/t\/[0-9]{1,64}\/?$/.test(url.pathname) && !url.search && !url.hash
        ? url.pathname.replace(/\/?$/, '/') : null;
    } catch { return null; }
  };
  let enabled = false, observer, timer, safety, baselineTimer, baselineSignature = '', armed = false;
  let sequence = 0, badge = null, route = location.pathname;
  const epoch = Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
  const rows = new Map();
  const visible = el => el.getClientRects().length > 0;
  const send = candidate => post({ type: 'instadesktop:notification', sequence: epoch + ':' + (++sequence), ...candidate });
  const diagnostic = (event, count = 0) => post({ type: 'instadesktop:notification-diagnostic', event, count });

  function readBadge() {
    // Only a Messages/inbox link qualifies. A count in document.title can also
    // represent likes or other activity, so it is deliberately not used.
    const links = document.querySelectorAll('a[href="/direct/inbox/"],a[href="/direct/inbox"],a[href="/direct/"],a[href="https://www.instagram.com/direct/inbox/"]');
    let found = false, count = 0;
    for (const link of links) {
      if (!visible(link)) continue;
      found = true;
      for (const el of link.querySelectorAll('span, [data-count], [data-unread-count]')) {
        const raw = el.getAttribute('data-unread-count') ?? el.getAttribute('data-count') ??
          (el.children.length === 0 ? el.textContent : '');
        if (/^\s*[0-9]{1,4}\+?\s*$/.test(raw || '')) count = Math.max(count, parseInt(raw, 10));
      }
    }
    return found ? count : null;
  }

  function readRow(anchor) {
    const path = threadPath(anchor.getAttribute('href'));
    if (!path || !visible(anchor)) return null;
    // Only use explicit unread semantics. Bold names and small colored circles
    // alone are ambiguous (active users, selected rows, themes) and aren't proof.
    const indicator = anchor.querySelector('[data-unread], [data-is-unread], [data-unread-count]');
    const marker = indicator || anchor;
    const rawCount = marker.getAttribute('data-unread-count');
    const count = /^\d{1,4}$/.test(rawCount || '') ? Number(rawCount) : null;
    let unread = count !== null ? count > 0 : null;
    const flag = marker.getAttribute('data-unread') ?? marker.getAttribute('data-is-unread');
    if (flag === 'true' || flag === 'false') unread = flag === 'true';
    // Supplementary accessibility labels, never the sole extraction strategy.
    if (unread === null) {
      const label = anchor.getAttribute('aria-label') || '';
      if (/\bunread\b|未讀|未読/i.test(label)) unread = true;
    }
    const explicitName = anchor.querySelector('[data-conversation-name]');
    const explicitPreview = anchor.querySelector('[data-message-preview]');
    // Only accept a two-line row with one avatar as an unambiguous name/preview
    // layout. More complex/group/time/status rows degrade instead of guessing.
    const lines = String(anchor.innerText || '').split(/\r?\n/).map(x => text(x)).filter(Boolean);
    const images = anchor.querySelectorAll('img');
    const simple = lines.length === 2 && images.length === 1 && !anchor.querySelector('time');
    const title = text(explicitName?.textContent || (simple ? lines[0] : ''), 160);
    const preview = text(explicitPreview?.textContent || (simple ? lines[1] : ''));
    const sender = text(anchor.querySelector('[data-sender-name]')?.textContent, 160);
    return { path, unread, count, title, preview, sender, avatar: images.length === 1 ? images[0].currentSrc || images[0].src : null };
  }

  function scan() {
    timer = null;
    if (!enabled) return;
    const changedRoute = route !== location.pathname;
    route = location.pathname;
    if (changedRoute) {
      rows.clear(); badge = null; armed = false; baselineSignature = '';
      clearTimeout(baselineTimer);
    }
    const now = Date.now(), nextBadge = readBadge();
    const badgeIncreased = badge !== null && nextBadge !== null && nextBadge > badge;
    const previousBadge = badge;
    let detailed = false, baselines = 0;
    const present = new Set();
    for (const anchor of document.querySelectorAll('a[href*="/direct/t/"]')) {
      const current = readRow(anchor);
      if (!current || present.has(current.path)) continue;
      present.add(current.path);
      const previous = rows.get(current.path);
      // Rows absent for a scan (virtualization, navigation) are baselined again.
      // Discovering old unread rows is never evidence of a new message.
      const incoming = armed && previous && current.unread === true &&
        ((previous.unread === false) ||
         (previous.count !== null && current.count !== null && current.count > previous.count) ||
         (badgeIncreased && current.preview && previous.preview && current.preview !== previous.preview));
      if (incoming) {
        const preview = current.sender && current.sender !== current.title && !current.preview.startsWith(current.sender + ':')
          ? current.sender + ': ' + current.preview : current.preview;
        send({ source: 'dom', threadUrl: current.path, unread: true,
          title: current.title || 'Instagram', body: current.title && current.preview ? text(preview) : 'You have a new message',
          ...(current.sender ? { senderName: current.sender } : {}),
          ...(current.avatar ? { avatarUrl: current.avatar } : {}) });
        detailed = true;
      } else if (!previous) baselines++;
      rows.set(current.path, { ...current, at: now });
    }
    for (const key of rows.keys()) if (!present.has(key)) rows.delete(key);
    // Bound state even on an unexpectedly large page.
    while (rows.size > 256) rows.delete(rows.keys().next().value);
    if (armed && badgeIncreased && !detailed) send({ source: 'badge', unread: true });
    if (baselines || (previousBadge === null && nextBadge !== null)) diagnostic('baseline', baselines);
    badge = nextBadge;
    // Hydration often inserts the nav link, badge and old rows in separate
    // mutations. Arm only after the observed notification state settles, not
    // after a process-age blackout. Native/page notifications remain immediate.
    if (!armed && (nextBadge !== null || rows.size)) {
      const signature = JSON.stringify([nextBadge, [...rows.values()].map(r => [r.path, r.unread, r.count, r.preview, r.sender])]);
      if (signature !== baselineSignature) {
        baselineSignature = signature;
        clearTimeout(baselineTimer);
        baselineTimer = setTimeout(() => { armed = true; diagnostic('baseline', rows.size); }, 650);
      }
    }
  }

  function schedule() {
    // A capped debounce: continuous animations cannot postpone a scan forever.
    if (enabled && !timer) timer = setTimeout(scan, 350);
  }

  function setEnabled(value) {
    if (enabled === Boolean(value)) return;
    enabled = Boolean(value);
    observer?.disconnect(); clearTimeout(timer); clearInterval(safety); clearTimeout(baselineTimer);
    timer = safety = baselineTimer = null; rows.clear(); badge = null; armed = false; baselineSignature = '';
    if (!enabled) return;
    // Observe the document so replacement of the body/root does not detach us.
    observer = new MutationObserver(schedule);
    observer.observe(document, { subtree: true, childList: true, characterData: true, attributes: true,
      attributeFilter: ['href', 'aria-label', 'data-unread', 'data-is-unread', 'data-unread-count', 'data-count'] });
    scan();
    safety = setInterval(schedule, 15000);
    diagnostic('permission', { granted: 1, denied: 2, default: 0 }[window.Notification?.permission] ?? 0);
    navigator.serviceWorker?.getRegistrations().then(registrations => {
      if (!enabled) return;
      diagnostic('workers', Math.min(registrations.length, 100));
      diagnostic('workers-active', Math.min(registrations.filter(r => r.active).length, 100));
    }).catch(() => diagnostic('workers-unavailable'));
  }

  // A transparent page-context fallback. The native constructor still runs and
  // all static members keep their original receiver. This does not intercept
  // service workers or implement push delivery.
  try {
    const Native = window.Notification;
    if (Native) window.Notification = new Proxy(Native, {
      construct(target, args) {
        const instance = Reflect.construct(target, args);
        if (enabled && Native.permission === 'granted') {
          const options = args[1] || {};
          const candidate = { source: 'page', title: text(args[0], 160), body: text(options.body),
            silent: options.silent === true };
          if (typeof options.tag === 'string') candidate.tag = text(options.tag, 256);
          if (typeof options.icon === 'string') candidate.avatarUrl = options.icon.slice(0, 4096);
          if (typeof options.image === 'string') candidate.imageUrl = options.image.slice(0, 4096);
          // Only an explicit URL in notification data can be a deep link.
          const path = threadPath(options.data?.url || options.data?.threadUrl || '');
          if (path) candidate.threadUrl = path;
          send(candidate);
        }
        return instance;
      },
      get(target, key) {
        const value = Reflect.get(target, key, target);
        return key === 'requestPermission' && typeof value === 'function' ? value.bind(target) : value;
      }
    });
  } catch { diagnostic('wrapper-unavailable'); }
  window.__InstaDesktopNotifications = { setEnabled };
  window.addEventListener('instadesktop:navigation', schedule);
  window.addEventListener('popstate', schedule);
  window.addEventListener('pagehide', () => setEnabled(false));
  window.addEventListener('pageshow', () => setEnabled(window.__InstaDesktopNotificationsEnabled === true));
  setEnabled(window.__InstaDesktopNotificationsEnabled === true);
})();

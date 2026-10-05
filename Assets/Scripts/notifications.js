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
  let badgeArmed = false, badgeBaselineTimer, badgeDecreaseTimer, badgeMissingTimer, badgeDecrease = null, extractionFailed = false;
  let sequence = 0, badge = null, route = location.pathname;
  const epoch = Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
  const rows = new Map();
  const send = candidate => post({ type: 'instadesktop:notification', sequence: epoch + ':' + (++sequence), ...candidate });
  const diagnostic = (event, count = 0) => post({ type: 'instadesktop:notification-diagnostic', event, count });

  const readBadge = () => window.__InstaDesktopInboxDom?.readUnreadBadge() ?? null;

  function scan() {
    timer = null;
    if (!enabled) return;
    const changedRoute = route !== location.pathname;
    route = location.pathname;
    if (changedRoute) {
      // Navigation replaces conversation rows, but the global inbox count is
      // still the same account state. Keep its independent baseline alive.
      rows.clear(); armed = false; baselineSignature = '';
      clearTimeout(baselineTimer);
    }
    const now = Date.now(), nextBadge = readBadge();
    const previousBadge = badge;
    const badgeIncreased = observeBadge(nextBadge);
    let detailed = false, baselines = 0;
    const present = new Set();
    try {
      for (const row of window.__InstaDesktopInboxDom?.readRows() || []) {
        if (!row || typeof row.rowKey !== 'string') continue;
        // "Typing..." is transient: keep the last real preview as the baseline.
        if (row.typing) { if (rows.has(row.rowKey)) present.add(row.rowKey); continue; }
        const current = { path: row.threadUrl, key: row.rowKey, unread: row.isUnread, count: row.unreadCount,
          title: row.conversationName, preview: row.preview, sender: row.senderName, avatar: row.avatarUrl, outgoing: row.outgoing };
        if (!current || present.has(current.key)) continue;
        present.add(current.key);
        const previous = rows.get(current.key);
        // Rows absent for a scan (virtualization, navigation) are baselined again.
        // Discovering old unread rows is never evidence of a new message.
        const incoming = armed && previous && !current.outgoing && current.unread === true &&
          ((previous.unread === false) ||
           (previous.unread === null && current.preview && previous.preview && current.preview !== previous.preview) ||
           (previous.count !== null && current.count !== null && current.count > previous.count) ||
           (badgeIncreased && current.preview && previous.preview && current.preview !== previous.preview));
        if (incoming) {
          const preview = current.sender && current.sender !== current.title && !current.preview.startsWith(current.sender + ':')
            ? current.sender + ': ' + current.preview : current.preview;
          send({ source: 'dom', ...(current.path ? { threadUrl: current.path } : {}), rowKey: current.key, unread: true,
            title: current.title || 'Instagram', body: current.title && current.preview ? text(preview) : 'You have a new message',
            ...(current.sender ? { senderName: current.sender } : {}),
            ...(current.avatar ? { avatarUrl: current.avatar } : {}) });
          detailed = true;
        } else if (!previous) baselines++;
        rows.set(current.key, { ...current, at: now });
      }
      for (const key of rows.keys()) if (!present.has(key)) rows.delete(key);
      // Bound state even on an unexpectedly large page.
      while (rows.size > 256) rows.delete(rows.keys().next().value);
      extractionFailed = false;
    } catch {
      // Details are optional. A selector/layout failure must not consume a
      // real badge increase or stop future scans. Rebaseline recovered rows.
      rows.clear(); armed = false; baselineSignature = '';
      clearTimeout(baselineTimer);
      if (!extractionFailed) diagnostic('rows-unavailable', 1);
      extractionFailed = true;
    }
    if (badgeIncreased && !detailed) send({ source: 'badge', unread: true });
    if (baselines || (previousBadge === null && nextBadge !== null)) diagnostic('baseline', baselines);
    // Hydration often inserts the nav link, badge and old rows in separate
    // mutations. Settle rows separately so animation or virtualization cannot
    // keep basic badge notifications unarmed indefinitely.
    if (!armed && rows.size) {
      const signature = JSON.stringify([...rows.values()].map(r => [r.key, r.unread, r.count, r.preview, r.sender]));
      if (signature !== baselineSignature) {
        baselineSignature = signature;
        clearTimeout(baselineTimer);
        baselineTimer = setTimeout(() => { armed = true; diagnostic('baseline', rows.size); }, 650);
      }
    }
  }

  function observeBadge(next) {
    if (next === null) {
      // A temporarily hidden/replaced navigation link is not a read event.
      clearTimeout(badgeBaselineTimer); clearTimeout(badgeDecreaseTimer);
      badgeBaselineTimer = badgeDecreaseTimer = null; badgeDecrease = null;
      // One that stays missing past the settle window leaves a stale count
      // (logout, account switch, rebuilt page): rebaseline when it returns.
      if (!badgeMissingTimer) badgeMissingTimer = setTimeout(() => {
        badgeMissingTimer = null;
        if (enabled) { badge = null; badgeArmed = false; }
      }, 650);
      return false;
    }
    clearTimeout(badgeMissingTimer); badgeMissingTimer = null;
    if (!badgeArmed) {
      if (badge !== next || !badgeBaselineTimer) {
        badge = next; clearTimeout(badgeBaselineTimer);
        badgeBaselineTimer = setTimeout(() => {
          badgeBaselineTimer = null;
          if (!enabled) return;
          // Read once more so an unscanned hydration mutation cannot arm an
          // outdated zero just before the initial unread badge is inserted.
          if (readBadge() === badge) { badgeArmed = true; diagnostic('baseline'); }
          else schedule();
        }, 650);
      }
      return false;
    }
    if (next < badge) {
      // React can rebuild a badge as empty, then restore the old count. A
      // decrease must settle before it can establish a new lower baseline.
      if (badgeDecrease !== next) {
        badgeDecrease = next; clearTimeout(badgeDecreaseTimer);
        badgeDecreaseTimer = setTimeout(() => {
          badgeDecreaseTimer = null;
          if (enabled && readBadge() === badgeDecrease) badge = badgeDecrease;
          badgeDecrease = null;
        }, 650);
      }
      return false;
    }
    clearTimeout(badgeDecreaseTimer); badgeDecreaseTimer = null; badgeDecrease = null;
    const increased = next > badge;
    badge = next;
    return increased;
  }

  function schedule() {
    // A capped debounce: continuous animations cannot postpone a scan forever.
    if (enabled && !timer) timer = setTimeout(scan, 350);
  }

  function setEnabled(value) {
    if (enabled === Boolean(value)) return;
    enabled = Boolean(value);
    observer?.disconnect(); clearTimeout(timer); clearInterval(safety); clearTimeout(baselineTimer);
    clearTimeout(badgeBaselineTimer); clearTimeout(badgeDecreaseTimer); clearTimeout(badgeMissingTimer);
    timer = safety = baselineTimer = badgeBaselineTimer = badgeDecreaseTimer = badgeMissingTimer = null;
    rows.clear(); badge = badgeDecrease = null; armed = badgeArmed = extractionFailed = false; baselineSignature = '';
    if (!enabled) return;
    // Observe the document so replacement of the body/root does not detach us.
    observer = new MutationObserver(schedule);
    observer.observe(document, { subtree: true, childList: true, characterData: true, attributes: true,
      attributeFilter: ['href', 'aria-label', 'data-unread', 'data-is-unread', 'data-unread-count', 'data-count', 'class', 'style', 'aria-hidden', 'src', 'data-message-direction', 'data-is-own-message'] });
    safety = setInterval(schedule, 15000);
    scan();
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

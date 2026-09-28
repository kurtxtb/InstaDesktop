(() => {
  'use strict';
  // Diagnostics only (--inbox-diagnostics). Records the inbox's structure and row
  // transitions with every name and message removed: text becomes a length, and
  // identities become hashes salted per run (the salt never leaves the page).
  if (window !== window.top || location.origin !== 'https://www.instagram.com' || window.__InstaDesktopInboxDiagnostics) return;
  const salt = Array.from(crypto.getRandomValues(new Uint32Array(4))).join(':');
  const hash = value => {
    let h = 2166136261;
    for (const c of salt + '|' + String(value)) { h ^= c.codePointAt(0); h = Math.imul(h, 16777619); }
    return (h >>> 0).toString(36);
  };
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
  const started = performance.now();
  const at = () => Math.round(performance.now() - started);
  const events = [];
  let previous = '', structureTaken = 0;

  function redactRow(row) {
    return { key: hash(row.rowKey), url: !!row.threadUrl, name: clean(row.conversationName).length,
      preview: hash(row.preview), previewLength: clean(row.preview).length, sender: clean(row.senderName).length,
      unread: row.isUnread, count: row.unreadCount, outgoing: row.outgoing, avatar: !!row.avatarUrl };
  }

  // A redacted skeleton: tags, roles, attribute names, computed typography and
  // colors, geometry relative to the row, and the length of direct text only.
  function skeleton(node, origin, budget) {
    if (budget.left-- <= 0) return null;
    const style = getComputedStyle(node), rect = node.getBoundingClientRect();
    const direct = [...node.childNodes].filter(child => child.nodeType === 3).map(child => clean(child.textContent)).join(' ').trim();
    const label = node.getAttribute('aria-label');
    const item = {
      tag: node.tagName.toLowerCase(),
      role: node.getAttribute('role') || undefined,
      attrs: [...node.attributes].map(a => a.name).filter(n => !['class', 'style', 'aria-label', 'alt', 'src', 'srcset', 'href', 'title'].includes(n)),
      href: node.hasAttribute('href') ? (/^\/direct\/t\/\d+\/?$/.test(node.getAttribute('href')) ? 'thread' : 'other') : undefined,
      label: label === null ? undefined : { length: label.length, unread: /unread|未讀|未读|未読/i.test(label), mark: /mark|標示|标记/i.test(label) },
      alt: node.hasAttribute('alt') ? { length: node.getAttribute('alt').length, profile: /profile|大頭貼|头像|頭像/i.test(node.getAttribute('alt')) } : undefined,
      box: [Math.round(rect.left - origin.left), Math.round(rect.top - origin.top), Math.round(rect.width), Math.round(rect.height)],
      display: style.display === 'none' || style.visibility === 'hidden' ? 'hidden' : undefined,
      weight: direct ? style.fontWeight : undefined,
      color: direct ? style.color : undefined,
      background: style.backgroundColor !== 'rgba(0, 0, 0, 0)' ? style.backgroundColor : undefined,
      radius: style.backgroundColor !== 'rgba(0, 0, 0, 0)' && style.borderRadius !== '0px' ? style.borderRadius : undefined,
      text: direct ? direct.length : undefined,
      time: node.tagName === 'TIME' || undefined
    };
    if (node.tagName !== 'svg' && node.tagName !== 'SVG') {
      const children = [...node.children].map(child => skeleton(child, origin, budget)).filter(Boolean);
      if (children.length) item.children = children;
    } else item.svg = node.querySelectorAll('*').length;
    return item;
  }

  // Candidate conversation rows as a person sees them: a link or button with a
  // square avatar and at least two text lines. Innermost element wins.
  function rowElements() {
    const nodes = [...document.querySelectorAll('a[href*="/direct/t/"],button,[role="button"]')].filter(node => {
      if (!node.getClientRects().length) return false;
      const images = [...node.querySelectorAll('img')].filter(img => {
        const r = img.getBoundingClientRect(); return r.width >= 20 && r.width <= 96 && Math.abs(r.width - r.height) <= 5;
      });
      return images.length && String(node.innerText || '').split(/\r?\n/).map(clean).filter(Boolean).length >= 2;
    });
    return nodes.filter(node => !nodes.some(other => other !== node && node.contains(other)));
  }

  function structure() {
    const rows = rowElements().slice(0, 12);
    return rows.map(row => {
      const origin = row.getBoundingClientRect();
      const lines = String(row.innerText || '').split(/\r?\n/).map(clean).filter(Boolean);
      const list = row.parentElement;
      return { lines: lines.map(line => line.length), siblings: list ? list.childElementCount : 0,
        ancestors: (() => { const chain = []; let n = row.parentElement; for (let i = 0; n && i < 6; i++, n = n.parentElement)
          chain.push({ tag: n.tagName.toLowerCase(), role: n.getAttribute('role') || undefined, children: n.childElementCount, label: n.hasAttribute('aria-label') || undefined }); return chain; })(),
        tree: skeleton(row, origin, { left: 120 }) };
    });
  }

  function scan(reason) {
    let rows = null, error = null;
    try { rows = window.__InstaDesktopInboxDom?.readRows().map(redactRow) ?? null; }
    catch (e) { error = e?.name || 'Error'; }
    const signature = JSON.stringify({ rows, error, candidates: rowElements().length });
    if (signature === previous) return;
    previous = signature;
    events.push({ at: at(), reason, path: location.pathname === '/direct/inbox/' ? 'inbox' : 'other',
      candidates: rowElements().length, extracted: rows, error });
    if (events.length > 400) events.splice(0, events.length - 400);
  }

  let pending = null;
  new MutationObserver(() => { if (!pending) pending = setTimeout(() => { pending = null; scan('mutation'); }, 120); }).observe(document, { subtree: true, childList: true, characterData: true, attributes: true });
  setInterval(() => scan('interval'), 1000);

  window.__InstaDesktopInboxDiagnostics = {
    // Returns and clears pending events; includes a structure sample on request.
    take(includeStructure) {
      const result = { at: at(), events: events.splice(0) };
      if (includeStructure) { result.structure = structure(); structureTaken++; }
      return JSON.stringify(result);
    }
  };
})();

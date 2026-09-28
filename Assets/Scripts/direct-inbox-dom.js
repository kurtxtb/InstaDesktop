(() => {
  'use strict';
  // Read the rendered inbox only. This module neither opens conversations nor
  // reads application internals or requests Instagram endpoints.
  if (window !== window.top || !['https://www.instagram.com', 'https://instagram.com'].includes(location.origin) ||
      window.__InstaDesktopInboxDom) return;

  const clean = (value, max = 1000) => String(value || '').replace(/\s+/g, ' ').trim().slice(0, max).replace(/[\uD800-\uDBFF]$/, '');
  const visible = node => !!node.getClientRects().length && getComputedStyle(node).visibility !== 'hidden' && getComputedStyle(node).display !== 'none';
  const threadPath = href => {
    if (!href) return '';
    try {
      const url = new URL(href, location.href);
      return ['https://www.instagram.com', 'https://instagram.com'].includes(url.origin) &&
        /^\/direct\/t\/[0-9]{1,64}\/?$/.test(url.pathname) && !url.search && !url.hash
        ? url.pathname.replace(/\/?$/, '/') : '';
    } catch { return ''; }
  };
  const timeText = value => /^(?:now|just now|today|yesterday|\d{1,3}\s*(?:s|m|h|d|w|sec(?:ond)?s?|min(?:ute)?s?|hours?|days?|weeks?)(?:\s+ago)?|\d{1,2}:\d{2}(?:\s*[ap]m)?|剛剛|现在|現在|今天|昨天|\d{1,3}\s*(?:秒|分鐘|分钟|小時|小时|天|週|周)(?:前)?|たった今|昨日|\d{1,3}\s*(?:秒|分|時間|日|週間)前)$/i.test(value);
  const statusText = value => /^(?:active now|active (?:today|yesterday)|active \d+\s*(?:m|h|d|minutes?|hours?|days?) ago|目前在線上|目前在线|剛剛上線|在线|在線上|\d+\s*(?:分鐘|分钟|小時|小时|天)前(?:在線上|在线|上線)|オンライン中)$/i.test(value);
  const unreadText = value => /^(?:unread(?: messages?)?|未讀(?:訊息)?|未读(?:消息)?|未読(?:メッセージ)?)$/i.test(value);
  const outgoingText = value => /^(?:(?:you|你|您)\s*[:：]|(?:你|您)(?:傳送了|发送了|傳送|发送|回覆了|回复了)|you (?:sent|replied|reacted)\b)/i.test(value);
  const controlText = value => /^(?:new message|create (?:a )?message|compose|search|suggested(?: for you)?|suggestions|people you may know|contacts|follow|message requests|新增訊息|新訊息|撰寫訊息|搜尋|搜索|推薦|推荐|建議|聯絡人|联系人|追蹤|关注|訊息邀請)$/i.test(value);

  function parseLines(rawLines) {
    const lines = rawLines.map(line => clean(line)).filter(Boolean);
    // Metadata must be a separate trailing token (or follow a middle-dot
    // separator). A message whose entire text is "now" remains a message.
    let metadata = false;
    while (lines.length > 2 && (timeText(lines.at(-1)) || statusText(lines.at(-1)) || unreadText(lines.at(-1)) || /^[·•]$/.test(lines.at(-1)))) {
      lines.pop(); metadata = true;
    }
    if (lines.length > 2 && statusText(lines[1])) { lines.splice(1, 1); metadata = true; }
    if (lines.length < 2) return null;
    const conversationName = clean(lines.shift(), 160);
    let preview = lines.join(' ');
    const suffix = preview.match(/\s+[·•]\s*([^·•]+)$/);
    if (suffix && (timeText(suffix[1].trim()) || statusText(suffix[1].trim()))) {
      preview = preview.slice(0, suffix.index); metadata = true;
    }
    return { conversationName, preview: clean(preview), metadata };
  }

  function textNode(row, text) {
    // Keep typography attached to the actual text leaf, not a normal-weight
    // layout wrapper around a semibold preview.
    return [...row.querySelectorAll('span,div,p,strong,b')].reverse()
      .find(node => visible(node) && clean(node.innerText || node.textContent) === text) || null;
  }

  function avatars(row) {
    const images = [...row.querySelectorAll('img')].filter(visible);
    if (!images.length || images.length > 3) return [];
    return images.every(image => {
      const rect = image.getBoundingClientRect();
      return image.matches('[data-conversation-avatar]') ||
        /profile picture|profile photo|大頭貼|头像|頭像|プロフィール写真/i.test(image.alt || '') ||
        (rect.width >= 20 && rect.width <= 96 && rect.height >= 20 && rect.height <= 96 && Math.abs(rect.width - rect.height) <= 5);
    }) ? images : [];
  }

  function explicitUnread(row) {
    const selector = '[data-unread],[data-is-unread],[data-unread-count]';
    const marker = row.matches(selector) ? row : row.querySelector(selector);
    const rawCount = marker?.getAttribute('data-unread-count');
    const unreadCount = /^\d{1,4}$/.test(rawCount || '') ? Number(rawCount) : null;
    const flag = marker?.getAttribute('data-unread') ?? marker?.getAttribute('data-is-unread');
    let isUnread = flag === 'true' ? true : flag === 'false' ? false : unreadCount === null ? null : unreadCount > 0;
    if (isUnread === null) {
      const labels = [row, ...row.querySelectorAll('[aria-label]')].map(node => node.getAttribute('aria-label') || '');
      // "Mark as unread" is an action, not the current unread state.
      if (labels.some(label => !/mark|標示|标记/i.test(label) && /\bunread\b|未讀|未读|未読/i.test(label))) isUnread = true;
    }
    return { isUnread, unreadCount };
  }

  function unreadDot(row) {
    const bounds = row.getBoundingClientRect();
    return [...row.querySelectorAll('div,span,i')].some(node => {
      if (node.childElementCount || clean(node.textContent) || !visible(node) || node.closest('svg')) return false;
      const rect = node.getBoundingClientRect(), style = getComputedStyle(node);
      if (rect.width < 3 || rect.width > 14 || rect.height < 3 || rect.height > 14 || Math.abs(rect.width - rect.height) > 2 ||
          rect.left < bounds.right - Math.min(80, bounds.width * .35) || rect.right > bounds.right + 1 ||
          rect.top < bounds.top || rect.bottom > bounds.bottom) return false;
      const radius = parseFloat(style.borderRadius);
      if (!(style.borderRadius.includes('%') ? radius >= 45 : radius >= Math.min(rect.width, rect.height) / 2 - 1)) return false;
      const color = style.backgroundColor.match(/^rgba?\(\s*(\d+)[, ]+\s*(\d+)[, ]+\s*(\d+)(?:\s*[,/]\s*([\d.]+))?\s*\)$/);
      if (!color || (color[4] !== undefined && Number(color[4]) < .8)) return false;
      const [, red, green, blue] = color.map(Number);
      // Require a small blue circle at the row's trailing edge. Green online
      // status, avatar rings, blue text, and arbitrary blue controls do not count.
      return blue >= 150 && blue > red * 1.3 && green >= 50 && green < 230 && blue > green * 1.08;
    });
  }

  function readCandidate(row, allowButtons, knownList = false) {
    if (!visible(row) || row.closest('[role="dialog"],[role="menu"],form')) return null;
    const threadUrl = row.matches('a[href]') ? threadPath(row.getAttribute('href')) : '';
    if (!threadUrl && (!allowButtons || row.matches('a'))) return null;
    const images = avatars(row);
    const explicitName = row.querySelector('[data-conversation-name]');
    const explicitPreview = row.querySelector('[data-message-preview]');
    const parsed = parseLines(String(row.innerText || '').split(/\r?\n/));
    const conversationName = clean(explicitName?.textContent || parsed?.conversationName, 160);
    const preview = clean(explicitPreview?.textContent || parsed?.preview);
    if (!conversationName || !preview || controlText(conversationName) || controlText(row.getAttribute('aria-label') || '')) return null;
    // Inbox activity/Notes tiles also have an avatar and two text lines.
    // Their minute-by-minute online status is not a DM preview. A real thread
    // URL or explicit preview field can still carry this wording as a message.
    if (!threadUrl && !explicitPreview && statusText(preview)) return null;
    if (!images.length && !(threadUrl && explicitName && explicitPreview)) return null;
    const nameNode = explicitName || textNode(row, conversationName);
    const previewNode = explicitPreview || textNode(row, preview) || [...row.querySelectorAll('span,div,p,strong,b')].reverse()
      .find(node => visible(node) && (clean(node.innerText || node.textContent).startsWith(preview + ' ') ||
        String(node.innerText || '').split(/\r?\n/).map(line => clean(line)).includes(preview))) || null;
    const state = explicitUnread(row);
    const dot = state.isUnread === null && unreadDot(row);
    if (dot) state.isUnread = true;
    const nameWeight = nameNode ? parseInt(getComputedStyle(nameNode).fontWeight, 10) : NaN;
    const previewWeight = previewNode ? parseInt(getComputedStyle(previewNode).fontWeight, 10) : NaN;
    const bold = nameWeight >= 600 && previewWeight >= 600;
    const normal = nameWeight >= 100 && nameWeight <= 500 && previewWeight >= 100 && previewWeight <= 500;
    // Button rows have no thread URL. Require an avatar and an actual preview
    // layout with metadata, unread evidence, or meaningful message text; a
    // contact's name and username alone are insufficient.
    const structuredMessage = preview.length >= 2 && !/^@?[\w.]+$/.test(preview) &&
      !/^(?:followed by|suggested for you|follows you|追蹤你|关注了你|推薦給你|推荐给你)/i.test(preview);
    if (!threadUrl && !(explicitName && explicitPreview) &&
        (!nameNode || !previewNode || (!knownList && !parsed?.metadata && !row.querySelector('time') && state.isUnread === null && !bold && !structuredMessage))) return null;
    const direction = explicitPreview?.getAttribute('data-message-direction') ?? row.getAttribute('data-message-direction');
    const own = explicitPreview?.getAttribute('data-is-own-message') ?? row.getAttribute('data-is-own-message');
    const outgoing = direction === 'outgoing' || own === 'true' || outgoingText(preview);
    let senderName = clean(row.querySelector('[data-sender-name]')?.textContent, 160);
    let messagePreview = preview;
    if (!senderName && !outgoing && (images.length > 1 || row.getAttribute('data-conversation-type') === 'group')) {
      const sender = preview.match(/^([^:：\n]{1,80})[:：]\s*(.+)$/);
      if (sender && !/^(?:https?|ftp)$/i.test(sender[1])) { senderName = clean(sender[1], 160); messagePreview = clean(sender[2]); }
    }
    const identity = clean(conversationName).normalize('NFKC').toLocaleLowerCase('en-US');
    return { node: row, bold, normal, value: { threadUrl, rowKey: threadUrl || 'name:' + identity,
      conversationName, preview: messagePreview, senderName,
      avatarUrl: images.length === 1 ? clean(images[0].currentSrc || images[0].src, 4096) : '',
      ...state, outgoing } };
  }

  function readRows() {
    const allowButtons = /^\/direct\//.test(location.pathname);
    const selector = allowButtons ? 'a[href*="/direct/t/"],button,[role="button"]' : 'a[href*="/direct/t/"]';
    let candidates = [...document.querySelectorAll(selector)].map(row => readCandidate(row, allowButtons)).filter(Boolean);
    // Prefer the innermost complete row; a nested avatar button is incomplete
    // and therefore does not suppress its containing conversation row.
    candidates = candidates.filter(row => !candidates.some(other => row !== other && row.node.contains(other.node)));
    const sameList = (a, b) => a.node.parentElement === b.node.parentElement ||
      (a.node.parentElement?.childElementCount === 1 && b.node.parentElement?.childElementCount === 1 &&
       a.node.parentElement?.parentElement === b.node.parentElement?.parentElement);
    // Once another row establishes this container as a conversation list, a
    // short single-word preview is valid too. This keeps "Hi" as an initial
    // baseline without mistaking a lone contact suggestion for a conversation.
    if (allowButtons && candidates.length) {
      for (const node of document.querySelectorAll('button,[role="button"]')) {
        if (candidates.some(row => row.node === node || row.node.contains(node) || node.contains(row.node))) continue;
        const peer = candidates.find(row => sameList(row, { node }));
        if (!peer || !visible(node) || node.closest('[role="dialog"],[role="menu"],form') || !avatars(node).length) continue;
        const parsed = parseLines(String(node.innerText || '').split(/\r?\n/));
        if (!parsed || !textNode(node, parsed.conversationName) || !textNode(node, parsed.preview) ||
            controlText(parsed.conversationName) || parsed.preview.startsWith('@') || statusText(parsed.preview)) continue;
        const extracted = readCandidate(node, allowButtons, true);
        if (extracted) candidates.push(extracted);
      }
    }
    for (const row of candidates) {
      if (row.value.isUnread !== null || (!row.bold && !row.normal)) continue;
      const siblings = candidates.filter(other => other !== row &&
        sameList(row, other));
      // Bold by itself is theme-dependent. Only a contrasting normal/bold pair
      // in the same list establishes unread/read typography for that scan.
      if (row.bold && siblings.some(other => other.normal && other.value.isUnread !== true)) row.value.isUnread = true;
      else if (row.normal && siblings.some(other => other.bold && other.value.isUnread !== false)) row.value.isUnread = false;
    }
    const identities = new Map();
    for (const row of candidates) {
      const name = row.value.conversationName.normalize('NFKC').toLocaleLowerCase('en-US');
      if (!identities.has(name)) identities.set(name, new Set());
      identities.get(name).add(row.value.threadUrl || row.node);
    }
    const seen = new Set(), result = [];
    for (const row of candidates) {
      const value = row.value;
      const name = value.conversationName.normalize('NFKC').toLocaleLowerCase('en-US');
      // Names are a local comparison key, never a made-up URL. Omit ambiguous
      // button identities so two same-name conversations cannot be mixed up.
      if ((!value.threadUrl && identities.get(name).size > 1) || seen.has(value.rowKey)) continue;
      seen.add(value.rowKey); result.push(value);
      if (result.length === 80) break;
    }
    return result;
  }

  window.__InstaDesktopInboxDom = Object.freeze({ readRows });
})();

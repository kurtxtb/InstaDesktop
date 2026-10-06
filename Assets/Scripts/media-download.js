(() => {
  'use strict';
  // Right-click "Download photo / video": finds the media under the pointer and
  // the post it belongs to. Instagram covers media with transparent layers, so
  // the right-clicked element itself is rarely the <img>/<video>. Nothing is
  // sent anywhere; the app asks for the result only when the menu is used.
  if (window !== window.top || !['https://www.instagram.com', 'https://instagram.com'].includes(location.origin) ||
      window.__InstaDesktopMedia) return;

  const POST = /\/(?:p|reel|reels|tv)\/([A-Za-z0-9_-]{5,40})(?:\/|$)/;
  const STORY = /^\/stories\/[^/]+\/([0-9]{5,30})(?:\/|$)/;
  const big = el => { const r = el.getBoundingClientRect(); return r.width >= 120 && r.height >= 120; };
  const fileOf = url => { try { return new URL(url).pathname.split('/').pop() || ''; } catch { return ''; } };
  const https = url => typeof url === 'string' && url.startsWith('https://') ? url : '';

  // The largest picture in srcset, otherwise what is shown.
  function imageSource(img) {
    let best = '', width = 0;
    for (const part of (img.srcset || '').split(',')) {
      const [url, size] = part.trim().split(/\s+/);
      const w = parseInt(size, 10) || 0;
      if (url && w > width) { best = url; width = w; }
    }
    return https(best) || https(img.currentSrc) || https(img.src);
  }

  // The post code: from the page address on a post, otherwise the nearest
  // container holding exactly one post link (a feed card or a grid tile).
  function codeNear(el) {
    const own = location.pathname.match(POST);
    for (let node = el, depth = 0; node && node !== document.body && depth < 14; node = node.parentElement, depth++) {
      const codes = new Set();
      if (node.tagName === 'A') { const m = (node.getAttribute('href') || '').match(POST); if (m) codes.add(m[1]); }
      for (const a of node.querySelectorAll('a[href]')) { const m = (a.getAttribute('href') || '').match(POST); if (m) codes.add(m[1]); }
      if (codes.size === 1) return [...codes][0];
      if (codes.size > 1) break;
    }
    return own ? own[1] : null;
  }

  // Picture files near a video (its preview), to tell album items apart.
  function filesNear(el) {
    const files = new Set();
    if (el.poster) files.add(fileOf(el.poster));
    const box = el.closest('li') || el.parentElement?.parentElement?.parentElement;
    for (const img of box ? box.querySelectorAll('img') : []) files.add(fileOf(img.currentSrc || img.src));
    files.delete('');
    return [...files];
  }

  function describe(x, y) {
    const media = document.elementsFromPoint(x, y).find(el => (el.tagName === 'IMG' || el.tagName === 'VIDEO') && big(el));
    if (!media) return null;
    const video = media.tagName === 'VIDEO';
    const story = location.pathname.match(STORY);
    return {
      kind: video ? 'video' : 'image',
      src: video ? https(media.currentSrc || media.src) : imageSource(media),
      file: video ? '' : fileOf(media.currentSrc || media.src),
      near: video ? filesNear(media) : [],
      code: codeNear(media),
      story: story ? story[1] : null
    };
  }

  // The post's data as Instagram put it in the page (media versions, album).
  function compact(o) {
    const best = iv => {
      const list = (iv && iv.candidates) || [];
      return list.slice().sort((a, b) => (b.width || 0) - (a.width || 0))[0]?.url || null;
    };
    const files = iv => ((iv && iv.candidates) || []).map(c => fileOf(c.url)).filter(Boolean);
    const item = m => ({
      type: m.media_type || 0, video: m.video_versions?.[0]?.url || null,
      image: best(m.image_versions2), files: files(m.image_versions2)
    });
    return {
      ...item(o), code: typeof o.code === 'string' ? o.code : null, pk: o.pk != null ? String(o.pk) : null,
      owner: o.user?.username || o.owner?.username || null, children: (o.carousel_media || []).map(item)
    };
  }

  function find(o, key, depth) {
    if (!o || typeof o !== 'object' || depth > 80) return null;
    if (Array.isArray(o)) { for (const x of o) { const r = find(x, key, depth + 1); if (r) return r; } return null; }
    if ((o.code === key || (o.pk != null && String(o.pk) === key)) && (o.image_versions2 || o.video_versions || o.carousel_media))
      return compact(o);
    for (const k in o) { const r = find(o[k], key, depth + 1); if (r) return r; }
    return null;
  }

  function lookup(key) {
    if (typeof key !== 'string' || !/^[A-Za-z0-9_-]{5,40}$/.test(key)) return null;
    for (const script of document.querySelectorAll('script[type="application/json"]')) {
      const text = script.textContent;
      if (!text.includes(key)) continue;
      try { const r = find(JSON.parse(text), key, 0); if (r) return r; } catch {}
    }
    return null;
  }

  let last = null;
  addEventListener('contextmenu', e => { last = { at: Date.now(), info: describe(e.clientX, e.clientY) }; }, true);
  window.__InstaDesktopMedia = Object.freeze({
    // What the last right-click was on, if it was just now.
    take: () => last && Date.now() - last.at < 5000 ? last.info : null,
    lookup
  });
})();

/* InjectionService supplies customCss, desktopOptions, reportError and emojiModule.
   Ctrl+Shift+R reloads these assets without reloading the document. */
(() => {
    'use strict';
    window.__InstaDesktopCustomization?.dispose();
    const styleId = 'instadesktop-custom-style';
    const protectedContent = 'main, [role="main"], article, [role="dialog"], [aria-modal="true"]';
    const controls = 'a[href], button, [role="button"], [role="link"]';
    const labels = {
        search: ['search', '\u641c\u5c0b', '\u641c\u7d22'],
        notifications: ['notifications', 'activity', '\u901a\u77e5', '\u52d5\u614b', '\u52a8\u6001'],
        profile: ['profile', '\u500b\u4eba\u6a94\u6848', '\u4e2a\u4eba\u4e3b\u9875', '\u500b\u4eba\u4e3b\u9801'],
        create: ['create', 'new post', '\u5efa\u7acb', '\u65b0\u589e\u8cbc\u6587', '\u521b\u5efa', '\u767c\u4f48']
    };
    const navigationRoots = new Set();
    const marked = new Set();
    const pending = new Set();
    const historyHooks = [];
    let scheduled = null, disposed = false, started = false, applyCount = 0;
    let lastPath = '', lastTitle = '', activePanel = null;
    const post = message => { try { window.chrome?.webview?.postMessage(message); } catch { reportError(); } };

    function mark(element, name, value = '') {
        if (element.getAttribute(name) !== value) element.setAttribute(name, value);
        marked.add(element);
    }

    function ensureStyle() {
        if (!document.head) return;
        if (!document.getElementById(styleId)) {
            const style = document.createElement('style');
            style.id = styleId;
            style.textContent = customCss;
            document.head.appendChild(style);
        }
        mark(document.documentElement, 'data-instadesktop');
        // Content-first mode keeps Instagram's own navigation and changes its
        // layout in place; the WPF host does not add a second navigation rail.
        document.documentElement.setAttribute('data-instadesktop-show-nav', 'true');
        setCompact(desktopOptions.compact);
    }

    function setCompact(value) {
        desktopOptions.compact = !!value;
        document.documentElement.toggleAttribute('data-instadesktop-compact', !!value);
    }

    function sendRouteToHost(force = false) {
        const path = location.pathname + location.search + location.hash;
        if (force || path !== lastPath) {
            if (path !== lastPath) activePanel = null;
            lastPath = path;
            const route = location.pathname.startsWith('/direct/') ? 'direct' :
                /^\/reels?\//.test(location.pathname) ? 'reels' :
                location.pathname.startsWith('/explore/') ? 'explore' : location.pathname === '/' ? 'feed' : 'other';
            mark(document.documentElement, 'data-instadesktop-route', route);
            post({ type: 'routeChanged', path });
        }
        if (lastTitle !== document.title) {
            lastTitle = document.title;
            post({ type: 'titleChanged', title: lastTitle.slice(0, 200) });
        }
    }

    function routeChanged() {
        sendRouteToHost();
        if (document.body) pending.add(document.body); // One full pass only on an actual route event.
        scheduleTweaks();
    }

    function setupRouteObserver() {
        if (historyHooks.length) return;
        for (const name of ['pushState', 'replaceState']) {
            const original = history[name];
            const wrapped = function (...args) { const result = original.apply(this, args); routeChanged(); return result; };
            history[name] = wrapped;
            historyHooks.push({ name, original, wrapped });
        }
        window.addEventListener('popstate', routeChanged);
        window.addEventListener('hashchange', routeChanged);
        window.addEventListener('instadesktop:navigation', routeChanged);
    }

    function pathOf(anchor) {
        try {
            const url = new URL(anchor.getAttribute('href'), location.href);
            return url.origin === location.origin ? url.pathname : '';
        } catch { return ''; }
    }

    function discoverNavigation(anchor) {
        if (anchor.closest(protectedContent)) return;
        const path = pathOf(anchor);
        if (!['/', '/explore/', '/reels/', '/direct/inbox/'].includes(path)) return;
        // Only a cluster of three distinct official global routes qualifies as a rail.
        // Never hide arbitrary navigation roles, a conversation list, or a dialog.
        for (let node = anchor.parentElement, depth = 0; node && node !== document.body && depth < 8; node = node.parentElement, depth++) {
            if (node.matches(protectedContent) || node.querySelector(protectedContent)) break;
            if (node.querySelector('input, textarea, [contenteditable="true"]')) break;
            const routes = new Set([...node.querySelectorAll('a[href]')].map(pathOf)
                .filter(p => ['/', '/explore/', '/reels/', '/direct/inbox/'].includes(p)));
            if (routes.size < 3) continue;
            navigationRoots.add(node);
            mark(node, 'data-instadesktop-nav');
            // Remove only a dedicated rail wrapper. An adjacent search/activity panel stays intact.
            const parent = node.parentElement;
            if (parent && parent !== document.body && parent.children.length === 1 && !parent.matches(protectedContent))
                mark(parent, 'data-instadesktop-nav');
            break;
        }
    }

    function discoverSemanticRails(root) {
        for (const nav of root.querySelectorAll('nav')) {
            if (nav.closest('main, [role="main"], [role="dialog"], [aria-modal="true"]')) continue;
            const routes = new Set([...nav.querySelectorAll('a[href]')].map(pathOf)
                .filter(p => ['/', '/explore/', '/reels/', '/direct/inbox/'].includes(p)));
            if (routes.size >= 3) { navigationRoots.add(nav); mark(nav, 'data-instadesktop-nav'); }
        }
        // Instagram's current shell may render the global rail as a div with
        // buttons and no semantic nav. Detect that rail by its dense control
        // cluster, while excluding content, dialogs and editable areas.
        for (const node of root.querySelectorAll('body > div, body > div > div, [role="navigation"]')) {
            if (node.closest('main, [role="main"], [role="dialog"], [aria-modal="true"]') ||
                node.querySelector('input, textarea, [contenteditable="true"]')) continue;
            const controls = node.querySelectorAll('button, a[href], [role="button"]');
            const rect = node.getBoundingClientRect();
            if (controls.length >= 6 && rect.width > 0 && rect.width < 180 && rect.height > 240) {
                navigationRoots.add(node);
                mark(node, 'data-instadesktop-nav');
            }
        }
    }

    function discoverLayout(root) {
        const mains = root.matches('main, [role="main"]') ? [root] : [...root.querySelectorAll('main, [role="main"]')];
        for (const main of mains) mark(main, 'data-instadesktop-main');
        for (const anchor of root.matches('a[href]') ? [root] : root.querySelectorAll('a[href]')) discoverNavigation(anchor);
        discoverSemanticRails(root);
        for (const node of [...marked]) if (!node.isConnected) marked.delete(node);
        for (const rail of [...navigationRoots]) if (!rail.isConnected) navigationRoots.delete(rail);
        document.documentElement.toggleAttribute('data-instadesktop-has-nav', navigationRoots.size > 0);
        if (location.pathname.startsWith('/direct/')) {
            const links = root.querySelectorAll('a[href^="/direct/t/"]');
            for (const link of links) {
                const main = link.closest('main, [role="main"]');
                if (!main) continue;
                let column = link.parentElement;
                while (column && column !== main && column.parentElement !== main) {
                    if (column.querySelector('textarea, [contenteditable="true"], [role="dialog"]')) break;
                    const parent = column.parentElement;
                    const style = getComputedStyle(parent);
                    const siblings = [...parent.children].filter(el => el !== column && el.querySelector('textarea, [contenteditable="true"]'));
                    if (siblings.length === 1 && (style.display.includes('flex') || style.display.includes('grid'))) {
                        mark(parent, 'data-instadesktop-dm');
                        mark(column, 'data-instadesktop-chat-list');
                        mark(siblings[0], 'data-instadesktop-conversation');
                        break;
                    }
                    column = parent;
                }
            }
        }
        for (const footer of root.querySelectorAll('footer')) {
            if (!footer.closest(protectedContent) && footer.querySelector('a[href*="/legal/"], a[href*="/privacy/"]') &&
                !footer.querySelector('button, input, [role="button"]')) mark(footer, 'data-instadesktop-footer');
        }
    }

    function applyInstagramTweaks(root = document.body) {
        if (disposed || !root?.isConnected) return;
        try { ensureStyle(); discoverLayout(root); emojiModule.applyTweaks(root); applyCount++; }
        catch { reportError(); }
    }

    function scheduleTweaks() {
        if (disposed || document.hidden || scheduled !== null) return;
        scheduled = setTimeout(() => {
            scheduled = null;
            ensureStyle();
            sendRouteToHost();
            const roots = [...pending];
            pending.clear();
            for (const root of roots) {
                if (root.isConnected && !roots.some(other => other !== root && other.contains(root))) applyInstagramTweaks(root);
            }
            for (const rail of [...navigationRoots]) if (!rail.isConnected) navigationRoots.delete(rail);
            document.documentElement.toggleAttribute('data-instadesktop-has-nav', navigationRoots.size > 0);
        }, 250);
    }

    const domObserver = new MutationObserver(records => {
        for (const record of records) for (const node of record.addedNodes)
            if (node.nodeType === Node.ELEMENT_NODE) pending.add(node);
        // Removed nodes also trigger cleanup and route fallback, never a full-document scan.
        scheduleTweaks();
    });
    const headObserver = new MutationObserver(() => { if (!document.getElementById(styleId) || document.title !== lastTitle) scheduleTweaks(); });

    function setupDomObserver() {
        if (disposed || document.hidden) return;
        if (document.body) domObserver.observe(document.body, { childList: true, subtree: true });
        if (document.head) headObserver.observe(document.head, { childList: true, subtree: true, characterData: true });
    }

    function pauseObservers() {
        domObserver.disconnect(); headObserver.disconnect(); pending.clear();
        if (scheduled !== null) clearTimeout(scheduled);
        scheduled = null;
    }

    function revealNavigation(show) {
        const html = document.documentElement;
        const value = show === undefined ? !html.hasAttribute('data-instadesktop-show-nav') : !!show;
        html.toggleAttribute('data-instadesktop-show-nav', value);
        if (!value && activePanel) { activePanel = null; sendRouteToHost(true); }
        return value;
    }

    function accessibleLabels(element) {
        return [element.getAttribute('aria-label'), element.getAttribute('title'), element.textContent,
            ...[...element.querySelectorAll('[aria-label], img[alt], svg title')].map(el => el.getAttribute('aria-label') || el.getAttribute('alt') || el.textContent)]
            .filter(Boolean).map(text => text.trim().toLowerCase());
    }

    function invokeAction(action) {
        if (!labels[action]) return false;
        try {
            applyInstagramTweaks();
            for (const rail of navigationRoots) {
                const element = [...rail.querySelectorAll(controls)].find(el => accessibleLabels(el).some(label => labels[action].includes(label)));
                if (!element || element.getAttribute('aria-disabled') === 'true' || element.disabled) continue;
                // Official panels sometimes live inside the global rail. Reveal that UI before invoking it.
                revealNavigation(true);
                activePanel = action;
                element.click();
                post({ type: 'actionResult', action, success: true, fallback: true });
                return true;
            }
            revealNavigation(true);
            return false;
        } catch { revealNavigation(true); reportError(); return false; }
    }

    function initialize() {
        if (disposed) return;
        try {
            if (!started) { emojiModule.initialize(); setupRouteObserver(); started = true; }
            applyInstagramTweaks(); setupDomObserver(); sendRouteToHost(true);
            post({ type: 'pageLoaded', path: location.pathname });
        } catch { reportError(); }
    }
    function visibilityChanged() { if (document.hidden) pauseObservers(); else initialize(); }
    function dispose() {
        disposed = true; pauseObservers();
        document.removeEventListener('DOMContentLoaded', initialize);
        document.removeEventListener('visibilitychange', visibilityChanged);
        window.removeEventListener('popstate', routeChanged);
        window.removeEventListener('hashchange', routeChanged);
        window.removeEventListener('instadesktop:navigation', routeChanged);
        window.removeEventListener('pagehide', pauseObservers);
        window.removeEventListener('pageshow', initialize);
        for (const { name, original, wrapped } of historyHooks) if (history[name] === wrapped) history[name] = original;
        document.getElementById(styleId)?.remove();
        for (const node of marked) for (const attr of [...node.attributes]) if (attr.name.startsWith('data-instadesktop')) node.removeAttribute(attr.name);
        for (const attr of [...document.documentElement.attributes]) if (attr.name.startsWith('data-instadesktop')) document.documentElement.removeAttribute(attr.name);
        marked.clear(); navigationRoots.clear(); emojiModule.dispose();
    }
    window.__InstaDesktopCustomization = Object.freeze({ version: 1, initialize, setupRouteObserver,
        setupDomObserver, applyInstagramTweaks, applyTweaks: applyInstagramTweaks, sendRouteToHost,
        setCompact, invokeAction, revealNavigation, dispose, get applyCount() { return applyCount; } });
    document.addEventListener('visibilitychange', visibilityChanged);
    window.addEventListener('pagehide', pauseObservers);
    window.addEventListener('pageshow', initialize);
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})();

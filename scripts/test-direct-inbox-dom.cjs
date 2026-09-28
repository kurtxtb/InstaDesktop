'use strict';
// Offline DOM regression fixtures. Uses a fresh, sandboxed Playwright browser;
// every request is fulfilled locally and no Instagram account is involved.
// Run: node scripts/test-direct-inbox-dom.cjs (with playwright on NODE_PATH).
// Or: node scripts/test-direct-inbox-dom.cjs --dom (with jsdom on NODE_PATH).
// Add --notifications to also exercise both observers in jsdom.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const extractor = fs.readFileSync(path.join(__dirname, '../Assets/Scripts/direct-inbox-dom.js'), 'utf8');
const avatar = '<img alt="Profile picture" src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7" width="40" height="40">';
const css = '<style>body{font:14px Arial}.inbox{width:360px}.row{display:flex;position:relative;align-items:center;width:340px;min-height:64px;color:black;text-decoration:none;background:white;border:0}.texts{display:flex;flex-direction:column;margin-left:8px}.name,.preview{display:block;font-weight:400}.bold .name,.bold .preview{font-weight:600}.dot{position:absolute;right:12px;width:8px;height:8px;border-radius:50%;background:rgb(0,149,246)}</style>';
const row = (name, preview, opts = {}) => {
  const tag = opts.tag || 'div';
  const attrs = opts.href ? 'href="' + opts.href + '"' : tag === 'div' ? 'role="button" tabindex="0"' : '';
  return `<${tag} class="row ${opts.bold ? 'bold' : ''}" ${attrs} ${opts.attrs || ''}>${opts.avatar === false ? '' : avatar}${opts.group ? avatar : ''}<div class="texts"><span class="name">${name}</span><span class="preview" ${opts.previewAttrs || ''}>${preview}</span>${opts.time === false ? '' : '<span class="time">' + (opts.time || '2m') + '</span>'}</div>${opts.dot ? '<span class="dot"></span>' : ''}</${tag}>`;
};

(async () => {
  // --dom uses jsdom for DOM/selector logic with explicit fixture geometry.
  // It is useful without a launchable browser but does not verify real layout.
  const domOnly = process.argv.includes('--dom');
  const deadline = setTimeout(() => { process.stderr.write('Offline DOM fixtures did not finish within 60 seconds.\n'); process.exit(1); }, 60000);
  const { browser, context, page } = domOnly ? createDomHarness() : await createBrowserHarness();
  let html = '';
  await context.route('**/*', route => route.fulfill({ status: 200, contentType: 'text/html', body: css + '<main class="inbox">' + html + '</main>' }));
  let passed = 0;
  async function fixture(body, url = 'https://www.instagram.com/direct/inbox/') {
    html = body;
    await page.goto(url);
    await page.evaluate(extractor);
    return page.evaluate(() => window.__InstaDesktopInboxDom?.readRows() ?? null);
  }
  function check(label, test) { test(); passed++; process.stdout.write('PASS ' + label + '\n'); }
  try {
    let rows = await fixture(row('Alice', 'Dinner at seven?', { dot: true }));
    check('button without href exposes name, message, unread, and local identity', () => {
      assert.equal(rows.length, 1); assert.equal(rows[0].threadUrl, ''); assert.equal(rows[0].rowKey, 'name:alice');
      assert.equal(rows[0].conversationName, 'Alice'); assert.equal(rows[0].preview, 'Dinner at seven?'); assert.equal(rows[0].isUnread, true);
    });
    rows = await fixture(row('Alice', 'We should go now', { tag: 'button', time: '剛剛' }));
    check('native button strips separate timestamp and preserves message words', () => assert.equal(rows[0].preview, 'We should go now'));
    rows = await fixture(row('Alice', 'now', { time: '1m' }));
    check('standalone now remains message preview', () => assert.equal(rows[0].preview, 'now'));
    rows = await fixture(row('Alice', 'Bring snacks · 5m', { time: false, dot: true }));
    check('inline middle-dot timestamp is removed', () => assert.equal(rows[0].preview, 'Bring snacks'));
    rows = await fixture(row('Alice', 'Hello<br><span>·</span>', { time: '3小時前', dot: true }));
    check('separate separator and localized timestamp are removed', () => assert.equal(rows[0].preview, 'Hello'));
    rows = await fixture(row('Alice', 'Active now<br>Are you free?', { dot: true }));
    check('separate online status before message is removed', () => assert.equal(rows[0].preview, 'Are you free?'));
    rows = await fixture(row('Alice', 'Active 10m ago', { time: false }));
    check('activity-only avatar button cannot masquerade as a DM preview', () => assert.equal(rows.length, 0));
    await page.locator('.preview').evaluate(node => { node.textContent = 'Active 11m ago'; });
    rows = await page.evaluate(() => window.__InstaDesktopInboxDom.readRows());
    check('minute-based activity update does not create a message candidate', () => assert.equal(rows.length, 0));
    rows = await fixture(row('Alice', '目前在線上', { time: false }) + row('Bob', '10分鐘前在線上', { time: false }));
    check('localized activity-only avatar buttons are excluded', () => assert.equal(rows.length, 0));
    rows = await fixture(row('Alice', 'Active 10m ago', { time: false, previewAttrs: 'data-message-preview' }));
    check('explicit message preview preserves literal activity wording', () => assert.equal(rows[0].preview, 'Active 10m ago'));
    rows = await fixture(row('Alice', 'Active 10m ago', { time: false, tag: 'a', href: '/direct/t/123/' }));
    check('identified thread retains literal message matching activity wording', () => assert.equal(rows[0].preview, 'Active 10m ago'));
    rows = await fixture(row('Alice', 'Are you active now?', { time: false }));
    check('message containing activity words remains intact', () => assert.equal(rows[0].preview, 'Are you active now?'));
    rows = await fixture(row('Alice', 'First', { attrs: 'data-unread="false"' }));
    check('explicit read state wins', () => assert.equal(rows[0].isUnread, false));
    await page.locator('.row').evaluate(node => { node.setAttribute('data-unread', 'true'); node.querySelector('.preview').textContent = 'Second'; });
    rows = await page.evaluate(() => window.__InstaDesktopInboxDom.readRows());
    check('same row reports unread and preview transition', () => { assert.equal(rows[0].isUnread, true); assert.equal(rows[0].preview, 'Second'); assert.equal(rows[0].rowKey, 'name:alice'); });
    const repeated = await page.evaluate(() => window.__InstaDesktopInboxDom.readRows());
    check('repeated snapshots are stable', () => assert.deepEqual(repeated, rows));
    rows = await fixture(row('Alice', 'See you later', { time: false }));
    check('two-line message row establishes baseline without unread state or timestamp', () => { assert.equal(rows.length, 1); assert.equal(rows[0].isUnread, null); });
    await page.locator('.row').evaluate(node => { node.querySelector('.preview').textContent = 'See you tomorrow'; node.insertAdjacentHTML('beforeend', '<span class="dot"></span>'); });
    const dotTransition = await page.evaluate(() => window.__InstaDesktopInboxDom.readRows());
    check('known unknown-state row exposes new dot plus changed preview for host confirmation', () => { assert.equal(dotTransition[0].rowKey, rows[0].rowKey); assert.equal(dotTransition[0].isUnread, true); assert.notEqual(dotTransition[0].preview, rows[0].preview); });
    rows = await fixture(row('Alice', 'Hi', { time: false }) + row('Bob', 'Another message'));
    check('short single-word preview is retained within established conversation list', () => { assert.equal(rows.length, 2); assert.equal(rows.find(value => value.conversationName === 'Alice').preview, 'Hi'); });
    rows = await fixture(row('Alice', 'You: sounds good', { dot: true }) + row('Bob', '你：等一下', { dot: true }) + row('Chen', '你傳送了一張相片', { dot: true }));
    check('English and Chinese outgoing previews are marked outgoing', () => assert.ok(rows.every(value => value.outgoing)));
    rows = await fixture(row('Alice', 'Hello', { attrs: 'data-is-own-message="true"' }));
    check('explicit outgoing marker is respected', () => assert.equal(rows[0].outgoing, true));
    rows = await fixture(row('Alice', 'Hello'));
    check('missing unread evidence stays unknown', () => { assert.equal(rows[0].isUnread, null); assert.equal(rows[0].unreadCount, null); });
    rows = await fixture(row('Alice', 'Hello', { attrs: 'data-unread="false"', dot: true }));
    check('explicit read overrides blue decoration', () => assert.equal(rows[0].isUnread, false));
    rows = await fixture(row('Alice', 'First', { bold: true }) + row('Bob', 'Second'));
    check('consistent contrasting row typography establishes unread and read', () => { assert.equal(rows[0].isUnread, true); assert.equal(rows[1].isUnread, false); });
    rows = await fixture(row('Alice', 'First', { bold: true }));
    check('isolated bold styling does not establish unread', () => assert.equal(rows[0].isUnread, null));
    rows = await fixture(row('Alice', 'First', { dot: true }) + row('ＡＬＩＣＥ', 'Second', { dot: true }));
    check('duplicate normalized name-only identities are omitted', () => assert.equal(rows.length, 0));
    rows = await fixture(row('Alice', 'First', { tag: 'a', href: '/direct/t/123', dot: true }) + row('Alice', 'Second', { tag: 'a', href: '/direct/t/456/', dot: true }));
    check('same-name canonical threads retain distinct identities', () => { assert.equal(rows.length, 2); assert.equal(rows[0].rowKey, '/direct/t/123/'); assert.equal(rows[1].rowKey, '/direct/t/456/'); });
    rows = await fixture(row('Team chat', 'Mina: Ship it today', { group: true, dot: true }));
    check('group avatar layout exposes actual sender separately', () => { assert.equal(rows[0].conversationName, 'Team chat'); assert.equal(rows[0].senderName, 'Mina'); assert.equal(rows[0].preview, 'Ship it today'); assert.equal(rows[0].avatarUrl, ''); });
    rows = await fixture(row('群組', '小明：今晚見', { group: true, dot: true }));
    check('localized group sender does not require a space after the colon', () => { assert.equal(rows[0].senderName, '小明'); assert.equal(rows[0].preview, '今晚見'); });
    rows = await fixture('<div role="button">' + row('Alice', 'Nested message', { dot: true }) + '</div>');
    check('nested complete button rows are deduplicated', () => { assert.equal(rows.length, 1); assert.equal(rows[0].conversationName, 'Alice'); });
    rows = await fixture(row('Alice', 'Hello', { dot: true }).replace(avatar, '<div role="button">' + avatar + '</div>'));
    check('nested avatar-only button does not suppress its conversation', () => assert.equal(rows.length, 1));
    rows = await fixture(row('New message', 'Compose', { dot: true }) + row('Contact', '@contact', { time: false }) + row('No avatar', 'Hello', { avatar: false, dot: true }));
    check('compose controls, contact suggestions, and missing avatars are rejected', () => assert.equal(rows.length, 0));
    rows = await fixture(row('Alice', 'Hello', { dot: true }), 'https://www.instagram.com/');
    check('button extraction is scoped to direct routes', () => assert.equal(rows.length, 0));
    rows = await fixture(row('Alice', 'Hello', { dot: true }), 'https://example.test/direct/inbox/');
    check('extractor does not run on untrusted origins', () => assert.equal(rows, null));
    rows = await fixture(row('Alice', 'Hello', { dot: true }), 'http://www.instagram.com/direct/inbox/');
    check('extractor does not run on HTTP', () => assert.equal(rows, null));
    rows = await fixture(row('Alice', 'Hello', { dot: true }).replace('class="dot"', 'class="dot" style="background:rgb(70,190,90)"'));
    check('green online dot is not unread evidence', () => assert.equal(rows[0].isUnread, null));
    rows = await fixture(row('Alice', 'Hello', { attrs: 'aria-label="Mark as unread"' }));
    check('mark-as-unread action is not unread evidence', () => assert.equal(rows[0].isUnread, null));
    rows = await fixture(row('Alice', 'Hello', { attrs: 'aria-label="Alice, unread message"' }));
    check('supplementary accessibility unread state is recognized', () => assert.equal(rows[0].isUnread, true));
    await fixture(row('Alice', 'Old message', { attrs: 'data-unread="false"' }));
    await page.evaluate("window.chrome={webview:{postMessage:value=>window.__fixtureMessages.push(value)}};window.__fixtureMessages=[];window.__InstaDesktopNotificationsEnabled=true;");
    await page.evaluate(fs.readFileSync(path.join(__dirname, '../Assets/Scripts/notifications.js'), 'utf8'));
    await new Promise(resolve => setTimeout(resolve, 800));
    await page.locator('.row').evaluate(node => { node.setAttribute('data-unread', 'true'); node.querySelector('.preview').textContent = 'Dinner at seven?'; });
    await new Promise(resolve => setTimeout(resolve, 650));
    const messages = await page.evaluate(() => window.__fixtureMessages.filter(value => value.type === 'instadesktop:notification'));
    check('primary bridge emits button sender and actual body with local row identity', () => {
      assert.equal(messages.length, 1); assert.equal(messages[0].title, 'Alice');
      assert.equal(messages[0].body, 'Dinner at seven?'); assert.equal(messages[0].rowKey, 'name:alice'); assert.equal(messages[0].threadUrl, undefined);
    });
    await page.evaluate('window.__InstaDesktopNotifications.setEnabled(false)');
    await fixture(row('Alice', 'Old message', { attrs: 'data-unread="false"' }));
    await page.evaluate("window.chrome={webview:{postMessage:value=>window.__fixtureSnapshots.push(value)}};window.__fixtureSnapshots=[];");
    await page.evaluate(fs.readFileSync(path.join(__dirname, '../Assets/Scripts/direct-inbox-monitor.js'), 'utf8'));
    await page.evaluate('window.__InstaDesktopInboxMonitor.scanNow()');
    await page.locator('.row').evaluate(node => { node.setAttribute('data-unread', 'true'); node.querySelector('.preview').textContent = 'Dinner at seven?'; });
    await page.evaluate('window.__InstaDesktopInboxMonitor.scanNow()');
    const snapshots = await page.evaluate(() => window.__fixtureSnapshots.filter(value => value.type === 'direct-inbox-snapshot'));
    check('monitor script emits button snapshots for host sender enrichment', () => {
      assert.equal(snapshots.length, 2); assert.equal(snapshots[0].threads[0].isUnread, false);
      assert.equal(snapshots[1].threads[0].isUnread, true); assert.equal(snapshots[1].threads[0].conversationName, 'Alice');
      assert.equal(snapshots[1].threads[0].preview, 'Dinner at seven?'); assert.equal(snapshots[1].threads[0].rowKey, 'name:alice');
    });
    await page.evaluate('window.__InstaDesktopInboxMonitor.stop()');
    if (domOnly && process.argv.includes('--notifications')) {
      // Deterministic timers exercise recovery without a live account or a
      // 15-second wait. Navigation events request the same capped DOM scan.
      const badgeFailures = [];
      async function badgeFixture(setup) {
        await fixture('<a href="/direct/inbox/"><span id="badge">1</span></a>', 'https://www.instagram.com/');
        await page.evaluate(() => {
          let clock = 0, sequence = 0;
          const jobs = new Map();
          window.setTimeout = (fn, delay = 0) => { const id = ++sequence; jobs.set(id, { fn, due: clock + delay, interval: 0 }); return id; };
          window.setInterval = (fn, delay) => { const id = ++sequence; jobs.set(id, { fn, due: clock + delay, interval: delay }); return id; };
          window.clearTimeout = window.clearInterval = id => jobs.delete(id);
          window.__fixtureAdvance = ms => {
            const end = clock + ms;
            for (let count = 0; count < 1000; count++) {
              const next = [...jobs.entries()].filter(([, job]) => job.due <= end).sort((a, b) => a[1].due - b[1].due)[0];
              if (!next) { clock = end; return; }
              const [id, job] = next; clock = job.due;
              if (job.interval) job.due += job.interval; else jobs.delete(id);
              job.fn();
            }
            throw new Error('Fixture timer loop did not settle');
          };
          window.__fixtureIntervalCount = () => [...jobs.values()].filter(job => job.interval).length;
          window.__fixtureScan = () => { window.dispatchEvent(new Event('instadesktop:navigation')); window.__fixtureAdvance(350); };
          window.__fixtureMessages = [];
          window.chrome = { webview: { postMessage: value => window.__fixtureMessages.push(value) } };
          window.__InstaDesktopNotificationsEnabled = true;
        });
        if (setup) await page.evaluate(setup);
        await page.evaluate(fs.readFileSync(path.join(__dirname, '../Assets/Scripts/notifications.js'), 'utf8'));
      }
      async function badgeCheck(label, run) {
        try { await run(); passed++; process.stdout.write('PASS ' + label + '\n'); }
        catch (error) { badgeFailures.push(label + ': ' + error.message); process.stdout.write('FAIL ' + label + '\n'); }
      }
      const badgeMessages = () => page.evaluate(() => window.__fixtureMessages.filter(value => value.type === 'instadesktop:notification'));
      await badgeCheck('initial extractor exception preserves badge detection and safety timer', async () => {
        await badgeFixture(() => { window.__InstaDesktopInboxDom = { readRows: () => { throw new Error('Fixture extractor failure'); } }; });
        assert.equal(await page.evaluate(() => window.__fixtureIntervalCount()), 1);
        await page.evaluate(() => { window.__fixtureAdvance(800); document.querySelector('#badge').textContent = '2'; window.__fixtureScan(); });
        assert.deepEqual((await badgeMessages()).map(value => value.source), ['badge']);
      });
      await badgeCheck('later extractor exception cannot swallow an increased badge', async () => {
        await badgeFixture();
        await page.evaluate(() => {
          window.__fixtureAdvance(800);
          window.__InstaDesktopInboxDom = { readRows: () => { throw new Error('Fixture extractor failure'); } };
          document.querySelector('#badge').textContent = '2'; window.__fixtureScan();
        });
        assert.deepEqual((await badgeMessages()).map(value => value.source), ['badge']);
      });
      await badgeCheck('continuous row churn cannot postpone badge arming', async () => {
        await badgeFixture(() => {
          let version = 0;
          window.__InstaDesktopInboxDom = { readRows: () => [{ rowKey: 'name:alice', threadUrl: '', isUnread: null,
            unreadCount: null, conversationName: 'Alice', preview: 'Preview ' + (++version), senderName: '', avatarUrl: '', outgoing: false }] };
        });
        await page.evaluate(() => { for (let i = 0; i < 5; i++) window.__fixtureScan(); document.querySelector('#badge').textContent = '2'; window.__fixtureScan(); });
        assert.deepEqual((await badgeMessages()).map(value => value.source), ['badge']);
      });
      await badgeCheck('SPA navigation preserves an already armed increased badge', async () => {
        await badgeFixture();
        await page.evaluate(() => { window.__fixtureAdvance(800); history.pushState({}, '', '/reels/'); document.querySelector('#badge').textContent = '2'; window.__fixtureScan(); });
        assert.deepEqual((await badgeMessages()).map(value => value.source), ['badge']);
      });
      await badgeCheck('temporary missing navigation keeps the last known badge', async () => {
        await badgeFixture();
        await page.evaluate(() => {
          window.__fixtureAdvance(800); document.querySelector('a').style.display = 'none'; window.__fixtureScan();
          document.querySelector('a').style.display = ''; document.querySelector('#badge').textContent = '2'; window.__fixtureScan();
        });
        assert.deepEqual((await badgeMessages()).map(value => value.source), ['badge']);
      });
      await badgeCheck('startup hydration establishes a silent settled badge baseline', async () => {
        await badgeFixture(() => { document.querySelector('#badge').textContent = ''; });
        await page.evaluate(() => { window.__fixtureAdvance(100); document.querySelector('#badge').textContent = '4'; window.__fixtureScan(); window.__fixtureAdvance(800); });
        assert.equal((await badgeMessages()).length, 0);
      });
      await badgeCheck('SPA badge rebuilding does not replay old unread messages', async () => {
        await badgeFixture();
        await page.evaluate(() => {
          window.__fixtureAdvance(800); history.pushState({}, '', '/reels/'); document.querySelector('#badge').textContent = ''; window.__fixtureScan();
          document.querySelector('#badge').textContent = '1'; window.__fixtureScan(); window.__fixtureAdvance(800);
        });
        assert.equal((await badgeMessages()).length, 0);
      });
      await badgeCheck('settled badge decrease allows the next genuine increase', async () => {
        await badgeFixture();
        await page.evaluate(() => {
          window.__fixtureAdvance(800); document.querySelector('#badge').textContent = ''; window.__fixtureScan(); window.__fixtureAdvance(800);
          document.querySelector('#badge').textContent = '1'; window.__fixtureScan();
        });
        assert.deepEqual((await badgeMessages()).map(value => value.source), ['badge']);
      });
      await badgeCheck('safety interval detects a badge change after extractor failure', async () => {
        await badgeFixture(() => { window.__InstaDesktopInboxDom = { readRows: () => { throw new Error('Fixture extractor failure'); } }; });
        await page.evaluate(() => { window.__fixtureAdvance(800); document.querySelector('#badge').textContent = '2'; window.__fixtureAdvance(15000); });
        assert.deepEqual((await badgeMessages()).map(value => value.source), ['badge']);
      });
      await badgeCheck('disabling clears pending baselines and re-enabling silently rebaselines', async () => {
        await badgeFixture();
        await page.evaluate(() => {
          window.__fixtureAdvance(100); window.__InstaDesktopNotifications.setEnabled(false);
          document.querySelector('#badge').textContent = '3'; window.__fixtureAdvance(1000);
          window.__InstaDesktopNotifications.setEnabled(true); window.__fixtureAdvance(800);
        });
        assert.equal((await badgeMessages()).length, 0);
      });
      assert.deepEqual(badgeFailures, [], 'Badge recovery regressions:\n' + badgeFailures.join('\n'));
      await fixture('<a href="/direct/inbox/"><span id="badge">1</span></a>' + row('Alice', 'Earlier preview', { time: false }));
      await page.evaluate(() => { window.__fixtureMessages = []; window.chrome = { webview: { postMessage: value => window.__fixtureMessages.push(value) } }; });
      await page.evaluate(fs.readFileSync(path.join(__dirname, '../Assets/Scripts/notifications.js'), 'utf8'));
      await page.evaluate(() => window.__InstaDesktopNotifications.setEnabled(true));
      await new Promise(resolve => setTimeout(resolve, 1150));
      await page.evaluate(() => { window.__fixtureMessages = []; const node = document.querySelector('.row'); node.querySelector('.preview').textContent = 'Fresh incoming message'; node.insertAdjacentHTML('beforeend', '<span class="dot"></span>'); });
      await new Promise(resolve => setTimeout(resolve, 550));
      const notifications = await page.evaluate(() => window.__fixtureMessages.filter(value => value.type === 'instadesktop:notification'));
      check('primary observer emits real name and message on existing button preview plus unread-dot transition', () => { assert.equal(notifications.length, 1); assert.equal(notifications[0].title, 'Alice'); assert.equal(notifications[0].body, 'Fresh incoming message'); assert.equal(notifications[0].rowKey, 'name:alice'); });
      await page.evaluate(() => { window.__fixtureMessages = []; document.querySelector('.row').setAttribute('data-unread-count', '2'); document.querySelector('.preview').textContent = '你：我自己的回覆'; });
      await new Promise(resolve => setTimeout(resolve, 550));
      const outgoing = await page.evaluate(() => window.__fixtureMessages.filter(value => value.type === 'instadesktop:notification'));
      check('primary observer suppresses outgoing button preview', () => assert.equal(outgoing.length, 0));
      await page.evaluate(() => window.__InstaDesktopNotifications.setEnabled(false));
      await fixture(row('Alice', 'Background preview', { dot: true }));
      await page.evaluate(() => { window.__fixtureMessages = []; window.chrome = { webview: { postMessage: value => window.__fixtureMessages.push(value) } }; });
      await page.evaluate(fs.readFileSync(path.join(__dirname, '../Assets/Scripts/direct-inbox-monitor.js'), 'utf8'));
      await new Promise(resolve => setTimeout(resolve, 600));
      const snapshots = await page.evaluate(() => window.__fixtureMessages.filter(value => value.type === 'direct-inbox-snapshot'));
      check('background inbox observer includes button-row sender and content in its snapshot', () => { assert.equal(snapshots.length, 1); assert.equal(snapshots[0].ready, true); assert.equal(snapshots[0].threads[0].conversationName, 'Alice'); assert.equal(snapshots[0].threads[0].preview, 'Background preview'); });
      await page.evaluate(() => { window.__fixtureMessages = []; window.__InstaDesktopInboxMonitor.scanNow(); });
      const repeatedSnapshots = await page.evaluate(() => window.__fixtureMessages.filter(value => value.type === 'direct-inbox-snapshot'));
      check('background observer omits unchanged snapshots', () => assert.equal(repeatedSnapshots.length, 0));
      await page.evaluate(() => {
        window.__fixtureExtractor = window.__InstaDesktopInboxDom;
        window.__InstaDesktopInboxDom = { readRows: () => { throw new Error('Fixture failure'); } };
        window.__InstaDesktopInboxMonitor.scanNow();
        window.__InstaDesktopInboxMonitor.scanNow();
      });
      const failures = await page.evaluate(() => window.__fixtureMessages);
      check('background extraction failure invalidates stale detail and reports once without private text', () => {
        assert.equal(failures.filter(value => value.type === 'direct-monitor-extraction-failed').length, 1);
        const reset = failures.filter(value => value.type === 'direct-inbox-snapshot');
        assert.equal(reset.length, 1); assert.equal(reset[0].ready, false); assert.deepEqual(reset[0].threads, []);
      });
      await page.evaluate(() => {
        window.__fixtureMessages = []; window.__InstaDesktopInboxDom = window.__fixtureExtractor;
        window.__InstaDesktopInboxMonitor.scanNow();
      });
      const recovery = await page.evaluate(() => window.__fixtureMessages.filter(value => value.type === 'direct-inbox-snapshot'));
      check('background extraction recovery supplies a fresh ready snapshot for silent rebaseline', () => {
        assert.equal(recovery.length, 1); assert.equal(recovery[0].ready, true);
        assert.equal(recovery[0].threads[0].conversationName, 'Alice');
      });
      await page.evaluate(() => window.__InstaDesktopInboxMonitor.stop());
    }
    process.stdout.write(`Offline inbox DOM checks passed: ${passed}${domOnly ? ' (jsdom; fixture geometry, not real browser layout)' : ' (Chromium)'}\n`);
  } finally { await context.close(); await browser.close(); clearTimeout(deadline); }
})().catch(error => { process.stderr.write(String(error.stack || error) + '\n'); process.exit(1); });

async function createBrowserHarness() {
  const { chromium } = require('playwright');
  const browser = await chromium.launch({ headless: true, chromiumSandbox: true, channel: 'chromium' });
  const context = await browser.newContext({ serviceWorkers: 'block' });
  const page = await context.newPage();
  return { browser, context, page };
}

function createDomHarness() {
  const { JSDOM } = require('jsdom');
  let dom, serve;
  const context = { route: async (_pattern, callback) => { serve = callback; }, close: async () => dom?.window.close() };
  const page = {
    goto: async url => {
      let fixture;
      await serve({ fulfill: response => { fixture = response.body; } });
      dom?.window.close();
      dom = new JSDOM(fixture, { url, runScripts: 'outside-only' });
      const window = dom.window;
      Object.defineProperty(window.HTMLElement.prototype, 'innerText', { get() {
        const read = node => {
          if (node.nodeType === 3) return node.nodeValue;
          if (node.nodeType !== 1) return '';
          if (node.tagName === 'BR') return '\n';
          const display = window.getComputedStyle(node).display;
          if (display === 'none') return '';
          const result = [...node.childNodes].map(read).join('');
          return /^(block|flex|grid|list-item)$/.test(display) ? '\n' + result + '\n' : result;
        };
        return [...this.childNodes].map(read).join('').replace(/\n+/g, '\n').trim();
      } });
      window.HTMLElement.prototype.getBoundingClientRect = function () {
        const style = window.getComputedStyle(this);
        const width = this.tagName === 'IMG' ? Number(this.width) : parseFloat(style.width) || 340;
        const height = this.tagName === 'IMG' ? Number(this.height) : parseFloat(style.height) || 64;
        const left = this.classList.contains('dot') ? 340 - 12 - width : 0;
        return { x: left, y: 0, left, top: 0, right: left + width, bottom: height, width, height };
      };
      window.HTMLElement.prototype.getClientRects = function () { return window.getComputedStyle(this).display === 'none' ? [] : [this.getBoundingClientRect()]; };
    },
    evaluate: async script => {
      const result = dom.window.eval(typeof script === 'string' ? script : '(' + script.toString() + ')()');
      return result === undefined ? undefined : JSON.parse(JSON.stringify(result));
    },
    locator: selector => ({ evaluate: async callback => { dom.window.eval('(' + callback.toString() + ')(document.querySelector(' + JSON.stringify(selector) + '))'); } })
  };
  return { browser: { close: async () => {} }, context, page };
}

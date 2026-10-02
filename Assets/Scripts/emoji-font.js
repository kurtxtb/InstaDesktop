// Presentation only: renders Emoji with the bundled Twemoji font (Discord's style).
// Windows 10's Segoe UI Emoji lacks newer Emoji and shows them as boxes.
// The faces shadow font families that do not exist on Windows and that start
// Instagram's font stacks. unicode-range limits them to Emoji, so every other
// character falls through to the next family and text keeps its system font.
(() => {
  'use strict';
  if (window !== window.top || location.protocol !== 'https:' ||
      !['instagram.com', 'www.instagram.com'].includes(location.hostname) ||
      (location.port && location.port !== '443') || window.__InstaDesktopEmojiFont) return;
  window.__InstaDesktopEmojiFont = true;

  // Derived from the font's cmap. ASCII keycap bases (# * 0-9) and (c) (R) TM
  // are excluded so ordinary text never switches to the Emoji font.
  const range = 'U+200D, U+203C, U+2049, U+20E3, U+2139, U+2194-2199, U+21A9-21AA, U+231A-231B, U+2328, ' +
    'U+23CF, U+23E9-23F3, U+23F8-23FA, U+24C2, U+25AA-25AB, U+25B6, U+25C0, U+25FB-25FE, U+2600-2604, ' +
    'U+260E, U+2611, U+2614-2615, U+2618, U+261D, U+2620, U+2622-2623, U+2626, U+262A, U+262E-262F, ' +
    'U+2638-263A, U+2640, U+2642, U+2648-2653, U+265F-2660, U+2663, U+2665-2666, U+2668, U+267B, ' +
    'U+267E-267F, U+2692-2697, U+2699, U+269B-269C, U+26A0-26A1, U+26A7, U+26AA-26AB, U+26B0-26B1, ' +
    'U+26BD-26BE, U+26C4-26C5, U+26C8, U+26CE-26CF, U+26D1, U+26D3-26D4, U+26E9-26EA, U+26F0-26F5, ' +
    'U+26F7-26FA, U+26FD, U+2702, U+2705, U+2708-270D, U+270F, U+2712, U+2714, U+2716, U+271D, U+2721, ' +
    'U+2728, U+2733-2734, U+2744, U+2747, U+274C, U+274E, U+2753-2755, U+2757, U+2763-2764, U+2795-2797, ' +
    'U+27A1, U+27B0, U+27BF, U+2934-2935, U+2B05-2B07, U+2B1B-2B1C, U+2B50, U+2B55, U+3030, U+303D, ' +
    'U+3297, U+3299, U+FE0F, U+1F004-1FAFF, U+E0020-E007F';
  // "Apple Color Emoji" leads the Emoji picker's stack, ahead of "Segoe UI Emoji".
  const families = ['Apple Color Emoji', '-apple-system', 'BlinkMacSystemFont', 'SF Pro Text', 'SF Pro Display', 'Helvetica'];
  const css = families.map(family => `@font-face { font-family: "${family}"; ` +
    `src: url("/__instadesktop/emoji/Twemoji.woff2") format("woff2"); ` +
    `unicode-range: ${range}; font-display: swap; }`).join('\n');
  try {
    // An adopted sheet exists from document creation and survives head rewrites.
    const sheet = new CSSStyleSheet();
    sheet.replaceSync(css);
    document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
  } catch {}
})();

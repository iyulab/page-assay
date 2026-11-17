namespace PageAssay;

/// <summary>
/// The measuring script of the look checks. It runs in an isolated world after the page settled — it shares the page's DOM and
/// computed styles, and the page cannot see it — and returns plain data: what the C# side turns into findings.
/// </summary>
internal static class LookProbe
{
    public const string Script = """
        (() => {
          const selectorOf = (el) => {
            const parts = [];
            for (let e = el; e && e.nodeType === 1 && parts.length < 4; e = e.parentElement) {
              if (e.id) { parts.unshift('#' + CSS.escape(e.id)); break; }
              const tag = e.localName;
              const same = e.parentElement ? [...e.parentElement.children].filter(c => c.localName === tag) : [];
              parts.unshift(same.length > 1 ? `${tag}:nth-of-type(${same.indexOf(e) + 1})` : tag);
            }
            return parts.join(' > ');
          };
          const rendered = (el) => {
            if (el.getClientRects().length === 0) return false;
            const s = getComputedStyle(el);
            return s.visibility !== 'hidden' && s.display !== 'none';
          };

          // Any CSS colour → [r, g, b, a] (0–255, a 0–1), by painting it: the engine parses every syntax it supports.
          const canvas = document.createElement('canvas');
          canvas.width = canvas.height = 1;
          const ctx = canvas.getContext('2d', { willReadFrequently: true });
          const rgba = (css) => {
            ctx.clearRect(0, 0, 1, 1);
            ctx.fillStyle = '#000';
            ctx.fillStyle = css;
            ctx.fillRect(0, 0, 1, 1);
            const d = ctx.getImageData(0, 0, 1, 1).data;
            return [d[0], d[1], d[2], d[3] / 255];
          };
          const over = (top, under) => {
            const a = top[3];
            return [0, 1, 2].map(i => top[i] * a + under[i] * (1 - a)).concat(1);
          };
          const hex = (c) => '#' + c.slice(0, 3).map(v => Math.round(v).toString(16).padStart(2, '0')).join('');
          const luminance = (c) => {
            const [r, g, b] = c.slice(0, 3).map(v => {
              v /= 255;
              return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
            });
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
          };
          const ratio = (a, b) => {
            const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
            return (hi + 0.05) / (lo + 0.05);
          };
          // The colour behind an element: its own and its ancestors' backgrounds, composited, over the canvas (white).
          // A picture or a gradient anywhere on the way makes it unknown — skipped, not guessed.
          const behind = (el) => {
            const layers = [];
            for (let e = el; e; e = e.parentElement) {
              const s = getComputedStyle(e);
              if (s.backgroundImage && s.backgroundImage !== 'none') return null;
              const c = rgba(s.backgroundColor);
              if (c[3] > 0) layers.push(c);
              if (c[3] >= 1) break;
            }
            let base = canvasColour;
            for (let i = layers.length - 1; i >= 0; i--) base = over(layers[i], base);
            return base;
          };
          // The colour of the page's canvas under everything: the system Canvas colour as the root's colour scheme resolves it
          // (dark only when the page opted into a dark scheme), read from a probe element — a page's own background covers it.
          const canvasColour = (() => {
            const probe = document.createElement('div');
            probe.style.cssText = 'position:absolute;width:0;height:0;background-color:Canvas';
            document.documentElement.appendChild(probe);
            const c = rgba(getComputedStyle(probe).backgroundColor);
            probe.remove();
            return c[3] >= 1 ? c : [255, 255, 255, 1];
          })();
          const large = (s) => {
            const px = parseFloat(s.fontSize);
            return px >= 24 || (px >= 18.66 && parseInt(s.fontWeight, 10) >= 700);
          };

          const contrast = new Map();
          const measure = (el, text, colorCss, s) => {
            const fg = rgba(colorCss);
            if (fg[3] === 0) return;                       // transparent text is not read
            const bg = behind(el);
            if (!bg) return;
            const shown = over(fg, bg);
            const r = ratio(shown, bg);
            const needed = large(s) ? 3 : 4.5;
            if (r >= needed) return;
            const key = hex(shown) + '/' + hex(bg) + '/' + needed;
            const seen = contrast.get(key);
            if (seen) { seen.count++; return; }
            contrast.set(key, {
              selector: selectorOf(el), text: text.slice(0, 80), foreground: hex(shown), background: hex(bg),
              ratio: Math.round(r * 100) / 100, needed, count: 1,
            });
          };

          const errorText = [];
          const errorPatterns = [
            /\b(?:ReferenceError|TypeError|SyntaxError|RangeError)\b/,
            /Cannot access '[^']+' before initialization/,
            /\bis not defined\b/, /\bis not a function\b/, /\bis not iterable\b/,
            /Cannot (?:read|set) properties of (?:undefined|null)/,
            /Unexpected token/,
          ];
          const measured = new Set();
          const walker = document.createTreeWalker(document.body || document, NodeFilter.SHOW_TEXT);
          for (let n = walker.nextNode(); n; n = walker.nextNode()) {
            const text = n.textContent.trim();
            const el = n.parentElement;
            if (!text || !el || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'OPTION'].includes(el.tagName)) continue;
            if (!rendered(el)) continue;
            if (errorPatterns.some(p => p.test(text))) errorText.push({ selector: selectorOf(el), text: text.slice(0, 200) });
            if (measured.has(el)) continue;
            measured.add(el);
            const s = getComputedStyle(el);
            measure(el, text, s.color, s);
          }

          const fields = document.querySelectorAll(
            'input:not([type=hidden]):not([type=submit]):not([type=button]):not([type=reset]):not([type=image]), select, textarea');
          const unnamed = [];
          for (const el of fields) {
            if (!rendered(el)) continue;
            // An empty field shows its placeholder in the placeholder's own colour.
            if ((el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') && !el.value && el.placeholder) {
              measure(el, el.placeholder, getComputedStyle(el, '::placeholder').color, getComputedStyle(el));
            }
            const byIds = (el.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean)
              .map(id => document.getElementById(id)?.textContent.trim() || '').join(' ').trim();
            const byLabels = [...(el.labels || [])].map(l => l.textContent.trim()).join(' ').trim();
            const named = byIds || (el.getAttribute('aria-label') || '').trim() || byLabels || (el.getAttribute('title') || '').trim()
              || (el.tagName === 'INPUT' && (el.getAttribute('placeholder') || '').trim());
            if (named) continue;
            let near = '';
            for (let e = el; e && !near && e !== document.body; e = e.parentElement) {
              for (let p = e.previousSibling; p && !near; p = p.previousSibling) near = (p.textContent || '').trim();
            }
            unnamed.push(near ? { selector: selectorOf(el), nearText: near.slice(0, 60) } : { selector: selectorOf(el) });
          }

          const overflow = Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth);
          return { errorText, contrast: [...contrast.values()], unnamed, overflow };
        })()
        """;
}

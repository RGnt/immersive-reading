// Reader Event Bridge (Injected into enriched EPUB chapters)
(function () {
  'use strict';

  let activeTokenId = null;

  document.addEventListener('click', function (e) {
    const spineLink = e.target.closest('a[data-ir-spine-href]');
    if (spineLink) {
      e.preventDefault();
      const spineHref = spineLink.getAttribute('data-ir-spine-href');
      const fragment = spineLink.getAttribute('data-ir-fragment') || '';
      if (spineHref) {
        window.parent.postMessage({
          type: 'ir-navigate-href',
          spineHref: spineHref,
          fragment: fragment
        }, '*');
        return;
      }
    }

    const tok = e.target.closest('.ir-tok');
    if (!tok) {
      clearActiveHighlight();
      window.parent.postMessage({ type: 'ir-clear-lookup' }, '*');
      return;
    }

    const tid = tok.getAttribute('data-tid');
    if (tid == null) return;

    setActiveHighlight(tid);

    const rect = tok.getBoundingClientRect();
    window.parent.postMessage({
      type: 'ir-token-click',
      tokenId: parseInt(tid, 10),
      clientRect: {
        top: rect.top,
        left: rect.left,
        bottom: rect.bottom,
        right: rect.right,
        width: rect.width,
        height: rect.height
      }
    }, '*');
  });

  // Translate vertical mouse-wheel into horizontal scroll in vertical-rl mode
  window.addEventListener('wheel', function (e) {
    if (document.documentElement.classList.contains('ir-mode-vertical')) {
      if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
        window.scrollBy({ left: -e.deltaY, behavior: 'auto' });
      }
    }
  }, { passive: true });

  function setActiveHighlight(tid) {
    clearActiveHighlight();
    activeTokenId = tid;
    const elements = document.querySelectorAll(`.ir-tok[data-tid="${tid}"]`);
    elements.forEach(el => el.classList.add('ir-tok-active'));
  }

  function clearActiveHighlight() {
    if (activeTokenId != null) {
      const elements = document.querySelectorAll(`.ir-tok[data-tid="${activeTokenId}"]`);
      elements.forEach(el => el.classList.remove('ir-tok-active'));
      activeTokenId = null;
    }
  }

  // Auto-fit fixed-layout EPUB pages (e.g. <meta name="viewport" content="width=2000, height=2818">)
  function applyFixedLayoutViewportFit() {
    const vpMeta = document.querySelector('meta[name="viewport"]');
    if (!vpMeta) return;
    const content = vpMeta.getAttribute('content') || '';
    const wMatch = content.match(/width\s*=\s*(\d+)/i);
    const hMatch = content.match(/height\s*=\s*(\d+)/i);
    if (!wMatch || !hMatch) return;

    const vpW = parseInt(wMatch[1], 10);
    const vpH = parseInt(hMatch[1], 10);
    if (vpW <= 0 || vpH <= 0) return;

    const html = document.documentElement;
    html.classList.remove('ir-mode-vertical');
    html.style.overflow = 'hidden';

    const availW = window.innerWidth || vpW;
    const availH = window.innerHeight || vpH;
    const scale = Math.min(availW / vpW, availH / vpH);
    const offsetX = Math.max(0, Math.round((availW - vpW * scale) / 2));
    const offsetY = Math.max(0, Math.round((availH - vpH * scale) / 2));

    if (document.body) {
      document.body.style.margin = '0';
      document.body.style.padding = '0';
      document.body.style.width = vpW + 'px';
      document.body.style.height = vpH + 'px';
      document.body.style.transformOrigin = '0 0';
      document.body.style.transform = `translate(${offsetX}px, ${offsetY}px) scale(${scale})`;
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', applyFixedLayoutViewportFit);
  } else {
    applyFixedLayoutViewportFit();
  }
  window.addEventListener('resize', applyFixedLayoutViewportFit);

  // Forward key events from reader iframe to parent host window for unified Vim / keyboard navigation
  document.addEventListener('keydown', function (e) {
    if (['INPUT', 'TEXTAREA'].includes(e.target?.tagName)) return;
    window.parent.postMessage({
      type: 'ir-reader-keydown',
      key: e.key,
      code: e.code,
      shiftKey: e.shiftKey,
      ctrlKey: e.ctrlKey,
      metaKey: e.metaKey,
      altKey: e.altKey
    }, '*');
  });

  let wordTokens = [];
  let currentWordIndex = -1;

  function refreshWordTokens() {
    wordTokens = Array.from(document.querySelectorAll('.ir-tok'));
  }

  function findFirstVisibleWordIndex() {
    refreshWordTokens();
    if (wordTokens.length === 0) return -1;
    const isVert = document.documentElement.classList.contains('ir-mode-vertical');
    const vpW = window.innerWidth;
    const vpH = window.innerHeight;

    for (let i = 0; i < wordTokens.length; i++) {
      const rect = wordTokens[i].getBoundingClientRect();
      if (isVert) {
        if (rect.right > 0 && rect.left < vpW && rect.bottom > 0 && rect.top < vpH) {
          return i;
        }
      } else {
        if (rect.bottom > 0 && rect.top < vpH && rect.right > 0 && rect.left < vpW) {
          return i;
        }
      }
    }
    return 0;
  }

  function focusWord(idx) {
    refreshWordTokens();
    if (wordTokens.length === 0) return;
    if (idx < 0) idx = 0;
    if (idx >= wordTokens.length) idx = wordTokens.length - 1;

    clearWordCursor();
    currentWordIndex = idx;
    const tok = wordTokens[idx];
    tok.classList.add('ir-tok-cursor');
    tok.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });

    const tid = tok.getAttribute('data-tid');
    const rect = tok.getBoundingClientRect();
    window.parent.postMessage({
      type: 'ir-word-focused',
      tokenId: tid != null ? parseInt(tid, 10) : null,
      surface: tok.textContent,
      clientRect: {
        top: rect.top,
        left: rect.left,
        bottom: rect.bottom,
        right: rect.right,
        width: rect.width,
        height: rect.height
      }
    }, '*');
  }

  function clearWordCursor() {
    document.querySelectorAll('.ir-tok-cursor').forEach(el => el.classList.remove('ir-tok-cursor'));
    currentWordIndex = -1;
  }

  function handleVimMotion(data) {
    const isVert = document.documentElement.classList.contains('ir-mode-vertical');

    switch (data.action) {
      case 'scroll-step': {
        const step = 80;
        let dx = 0, dy = 0;
        if (data.direction === 'up') dy = -step;
        else if (data.direction === 'down') dy = step;
        else if (data.direction === 'left') dx = -step;
        else if (data.direction === 'right') dx = step;
        window.scrollBy({ left: dx, top: dy, behavior: 'auto' });
        break;
      }
      case 'scroll-page': {
        if (isVert) {
          // In vertical-rl, forward is left (-x), backward is right (+x)
          const amount = window.innerWidth * 0.85;
          const dx = data.forward ? -amount : amount;
          window.scrollBy({ left: dx, top: 0, behavior: 'smooth' });
        } else {
          const amount = window.innerHeight * 0.85;
          const dy = data.forward ? amount : -amount;
          window.scrollBy({ left: 0, top: dy, behavior: 'smooth' });
        }
        break;
      }
      case 'scroll-half-page': {
        if (isVert) {
          const amount = window.innerWidth * 0.45;
          const dx = data.forward ? -amount : amount;
          window.scrollBy({ left: dx, top: 0, behavior: 'smooth' });
        } else {
          const amount = window.innerHeight * 0.45;
          const dy = data.forward ? amount : -amount;
          window.scrollBy({ left: 0, top: dy, behavior: 'smooth' });
        }
        break;
      }
      case 'scroll-to': {
        if (data.target === 'start') {
          if (isVert) window.scrollTo({ left: 1000000, top: 0, behavior: 'smooth' });
          else window.scrollTo({ left: 0, top: 0, behavior: 'smooth' });
        } else if (data.target === 'end') {
          if (isVert) window.scrollTo({ left: -1000000, top: 0, behavior: 'smooth' });
          else window.scrollTo({ left: 0, top: document.body.scrollHeight, behavior: 'smooth' });
        }
        break;
      }
      case 'next-word': {
        refreshWordTokens();
        if (wordTokens.length === 0) break;
        if (currentWordIndex < 0) {
          currentWordIndex = findFirstVisibleWordIndex();
        } else {
          currentWordIndex = Math.min(wordTokens.length - 1, currentWordIndex + 1);
        }
        focusWord(currentWordIndex);
        break;
      }
      case 'prev-word': {
        refreshWordTokens();
        if (wordTokens.length === 0) break;
        if (currentWordIndex < 0) {
          currentWordIndex = findFirstVisibleWordIndex();
        } else {
          currentWordIndex = Math.max(0, currentWordIndex - 1);
        }
        focusWord(currentWordIndex);
        break;
      }
      case 'lookup-current-word': {
        if (currentWordIndex >= 0 && currentWordIndex < wordTokens.length) {
          const tok = wordTokens[currentWordIndex];
          const tid = tok.getAttribute('data-tid');
          if (tid != null) {
            setActiveHighlight(tid);
            const rect = tok.getBoundingClientRect();
            window.parent.postMessage({
              type: 'ir-token-click',
              tokenId: parseInt(tid, 10),
              clientRect: {
                top: rect.top,
                left: rect.left,
                bottom: rect.bottom,
                right: rect.right,
                width: rect.width,
                height: rect.height
              }
            }, '*');
          }
        }
        break;
      }
      case 'clear-word-cursor': {
        clearWordCursor();
        break;
      }
      case 'adjust-font-size': {
        const cur = parseFloat(getComputedStyle(document.body).fontSize) || 22;
        const next = data.delta === 0 ? 22 : Math.max(14, Math.min(42, cur + data.delta));
        document.body.style.fontSize = next + 'px';
        document.documentElement.style.setProperty('--reader-font-size', next + 'px');
        break;
      }
    }
  }

  // Handle commands sent from reader host UI
  window.addEventListener('message', function (e) {
    const data = e.data;
    if (!data || typeof data !== 'object') return;

    if (data.type === 'ir-vim-motion') {
      handleVimMotion(data);
    } else if (data.type === 'ir-set-furigana-mode') {
      const html = document.documentElement;
      html.classList.remove('ir-furigana-all', 'ir-furigana-author', 'ir-furigana-unknown', 'ir-furigana-hover', 'ir-furigana-off');
      const mode = data.mode || 'AllKanji';
      if (mode === 'AuthorOnly') html.classList.add('ir-furigana-author');
      else if (mode === 'UnknownAndLearningOnly') html.classList.add('ir-furigana-unknown');
      else if (mode === 'Hover') html.classList.add('ir-furigana-hover');
      else if (mode === 'Off') html.classList.add('ir-furigana-off');
      else html.classList.add('ir-furigana-all');
    } else if (data.type === 'ir-set-writing-mode') {
      const html = document.documentElement;
      html.classList.remove('ir-mode-vertical', 'ir-mode-horizontal');
      if (data.mode === 'vertical-rl') {
        html.classList.add('ir-mode-vertical');
      } else {
        html.classList.add('ir-mode-horizontal');
      }
    } else if (data.type === 'ir-update-token-status') {
      // Live DOM update for word status without page reload
      const statusClass = data.statusClass; // e.g. 'ir-st-known'
      const selector = data.lemma ? `.ir-tok[data-lemma="${CSS.escape(data.lemma)}"]` : `.ir-tok[data-tid="${data.tokenId}"]`;
      const elements = document.querySelectorAll(selector);
      elements.forEach(el => {
        el.classList.remove('ir-st-new', 'ir-st-learning', 'ir-st-known', 'ir-st-ignored');
        if (statusClass) el.classList.add(statusClass);
      });
    }
  });
})();

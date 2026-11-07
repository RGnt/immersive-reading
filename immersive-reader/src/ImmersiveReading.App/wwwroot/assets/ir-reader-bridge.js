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

  // Handle commands sent from reader host UI
  window.addEventListener('message', function (e) {
    const data = e.data;
    if (!data || typeof data !== 'object') return;

    if (data.type === 'ir-set-furigana-mode') {
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

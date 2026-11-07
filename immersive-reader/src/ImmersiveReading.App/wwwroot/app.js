// Photino JSON-RPC 2.0 Client & Phase 1 High-Scale (24k+) Library Manager Controller
const pendingRpc = new Map();
let rpcCounter = 1;

const state = {
  folders: [],
  flatFolders: [],
  tags: [],
  customFields: [],
  viewMode: 'table', // 'table' | 'grid'
  selectedFolderId: null,
  uncategorizedOnly: false,
  selectedTagIds: new Set(),
  selectedBookIds: new Set(),
  activeBookDetail: null,
  currentPage: { items: [], totalCount: 0, offset: 0, limit: 100, queryElapsedMs: 0 },
  defaultScanDir: ''
};

// Setup Photino WebMessage listener
if (window.external && typeof window.external.receiveMessage === 'function') {
  window.external.receiveMessage((rawMessage) => {
    try {
      const msg = JSON.parse(rawMessage);
      if (msg.id && pendingRpc.has(msg.id)) {
        const { resolve, reject } = pendingRpc.get(msg.id);
        pendingRpc.delete(msg.id);
        if (msg.error) {
          reject(new Error(msg.error.message || 'RPC Error'));
        } else {
          resolve(msg.result);
        }
      } else if (msg.event) {
        handlePushEvent(msg.event, msg.data);
      }
    } catch (err) {
      console.error('Failed to parse IPC message:', err);
    }
  });
}

function rpcCall(method, params = {}) {
  const id = `req-${rpcCounter++}`;
  return new Promise((resolve, reject) => {
    pendingRpc.set(id, { resolve, reject });
    const payload = JSON.stringify({ id, method, params });
    if (window.external && typeof window.external.sendMessage === 'function') {
      window.external.sendMessage(payload);
    } else {
      reject(new Error('Photino IPC bridge not available'));
    }
  });
}

function handlePushEvent(eventName, data) {
  if (eventName === 'library.scanProgress') {
    const banner = document.getElementById('scan-progress-banner');
    const text = document.getElementById('scan-progress-text');
    const fill = document.getElementById('scan-progress-fill');
    banner.classList.remove('hidden');
    const pct = data.totalFiles > 0 ? Math.round((data.scannedFiles / data.totalFiles) * 100) : 100;
    fill.style.width = `${pct}%`;
    text.textContent = `Scanning: ${data.scannedFiles} / ${data.totalFiles} (${pct}%) • New: ${data.importedNew} • Deduplicated: ${data.deduplicatedExisting} • ${data.currentFile || ''}`;
  } else if (eventName === 'studypack.buildProgress') {
    const logEl = document.getElementById('insp-studypack-build-log');
    if (logEl && data && data.line) {
      logEl.textContent = data.line;
    }
  } else if (eventName === 'flashcards.mined') {
    if (state.activeBookDetail && data && data.bookId === state.activeBookDetail.summary.bookId) {
      loadInspectorStudyPack(data.bookId);
    }
  } else if (eventName === 'config.changed') {
    if (data && data.config) {
      currentAppConfig = data.config;
      applyConfigToDom(currentAppConfig);
    }
  } else if (eventName === 'ui.runReaderSelfTest') {
    runReaderUiSelfTest();
  }
}

async function runReaderUiSelfTest() {
  try {
    // 1. Test opening a book with Japanese text chapters (e.g. クーク)
    const searchRes = await rpcCall('library.listBooks', { search: 'クーク', limit: 5 });
    const candidates = (searchRes && searchRes.items && searchRes.items.length > 0)
      ? searchRes.items
      : ((state.currentPage && state.currentPage.items) || []);

    if (candidates.length === 0) {
      await rpcCall('ui.reportSelfTestResult', { passed: false, summary: 'FAIL: No books found in library' });
      return;
    }

    const targetBook = candidates[0];
    await openBookInReader(targetBook.bookId);

    // Wait for iframe srcdoc to finish rendering
    await new Promise(r => setTimeout(r, 400));

    const iframe = document.getElementById('reader-frame');
    let iframeDoc = iframe.contentDocument;
    let tokCount = iframeDoc ? iframeDoc.querySelectorAll('.ir-tok').length : 0;

    // If first chapter was cover image with 0 tokens, advance through chapters until we find a text chapter
    const chapters = (readerState.activeSession && readerState.activeSession.book.chapters) || [];
    for (let i = 0; i < Math.min(chapters.length, 6) && tokCount === 0; i++) {
      if (i !== readerState.currentChapterIndex) {
        readerState.currentChapterIndex = i;
        readerState.currentSpineHref = chapters[i].spineHref;
        await loadChapterInReader(readerState.currentSpineHref);
        await new Promise(r => setTimeout(r, 350));
        iframeDoc = iframe.contentDocument;
        tokCount = iframeDoc ? iframeDoc.querySelectorAll('.ir-tok').length : 0;
      }
    }

    if (tokCount === 0) {
      await rpcCall('ui.reportSelfTestResult', { passed: false, summary: `FAIL: 0 .ir-tok elements rendered inside iframe for book ${targetBook.bookId}` });
      return;
    }

    // Trigger token click lookup and verify dictionary popover opens
    await handleReaderTokenClick(0, { left: 120, top: 120, bottom: 145, right: 160, width: 40, height: 25 });
    const popover = document.getElementById('dict-popover');
    const popVisible = popover && !popover.classList.contains('hidden');
    const headword = document.querySelector('.dict-headword')?.textContent || '';

    // Verify Phase 4 Study Pack overview & 1-click flashcard mining from popover
    const spRes = await rpcCall('studypack.getOverview', { bookId: 'book-4e0541b184c4ace9' });
    const vocabSetsCount = (spRes && spRes.overview && spRes.overview.vocabularySets) ? spRes.overview.vocabularySets.length : 0;
    await mineFlashcardFromPopover(0);

    // Verify Phase 5 AI Comprehension Quiz & n+1 Persona Chat via FeaturePluginHost
    const quizQ = await rpcCall('ext.aiCompanion.generateQuestion', {
      bookId: targetBook.bookId,
      chapterIndex: readerState.currentChapterIndex,
      spineHref: readerState.currentSpineHref,
      viewportTokenId: 15
    });
    const quizGrade = await rpcCall('ext.aiCompanion.submitAnswer', {
      questionId: quizQ.questionId,
      selectedOptionIndex: 0,
      userAnswer: '0'
    });

    await rpcCall('ui.reportSelfTestResult', {
      passed: popVisible && headword.length > 0 && vocabSetsCount > 0 && quizGrade && quizGrade.comprehensionScore === 100,
      summary: `UI_WINDOW_READER_OK: book="${targetBook.title}" (${targetBook.bookId}), chapter="${readerState.currentSpineHref}", domTokens=${tokCount}, popoverVisible=${popVisible}, headword="${headword}", vocabSets=${vocabSetsCount}, aiQuizScore=${quizGrade.comprehensionScore}`
    });
  } catch (err) {
    await rpcCall('ui.reportSelfTestResult', { passed: false, summary: `FAIL: ${err.message}` });
  }
}

// Bootstrap on load
window.addEventListener('DOMContentLoaded', async () => {
  try {
    const boot = await rpcCall('library.getBootstrapState', {});
    if (boot.config) {
      currentAppConfig = boot.config;
      applyConfigToDom(currentAppConfig);
    }
    state.folders = boot.folders || [];
    state.tags = boot.tags || [];
    state.customFields = boot.customFields || [];
    state.currentPage = boot.initialPage || state.currentPage;
    state.defaultScanDir = boot.defaultScanDir || '';

    rebuildFlatFolders();
    renderSidebarFolders();
    renderSidebarTags();
    renderSidebarCustomFields();
    renderBooksViewport();
    updatePaginationBar();
    document.getElementById('count-all-books').textContent = state.currentPage.totalCount;
  } catch (err) {
    console.error('Bootstrap error:', err);
  }
});

function rebuildFlatFolders() {
  state.flatFolders = [];
  function walk(nodes, depth, prefix) {
    for (const n of nodes) {
      const crumb = prefix ? `${prefix} / ${n.name}` : n.name;
      state.flatFolders.push({ ...n, depth, breadcrumb: crumb });
      if (n.children && n.children.length > 0) {
        walk(n.children, depth + 1, crumb);
      }
    }
  }
  walk(state.folders, 0, '');
}

// =========================================================================
// Sidebar Rendering (Nested Virtual Folders, Manual Tags, Custom Fields)
// =========================================================================

function renderSidebarFolders() {
  const container = document.getElementById('folder-tree-container');
  container.innerHTML = '';

  for (const f of state.flatFolders) {
    const row = document.createElement('div');
    row.className = 'tree-row' + (state.selectedFolderId === f.folderId && !state.uncategorizedOnly ? ' active' : '');
    row.style.paddingLeft = `${8 + f.depth * 14}px`;
    row.setAttribute('data-folder-id', f.folderId);
    row.setAttribute('draggable', 'true');

    // Drag & Drop
    row.ondragstart = (e) => onFolderDragStart(e, f.folderId);
    row.ondragend = (e) => onFolderDragEnd(e);
    row.ondragover = (e) => onFolderDragOver(e, f.folderId);
    row.ondragleave = (e) => onFolderDragLeave(e, f.folderId);
    row.ondrop = (e) => onFolderDrop(e, f.folderId);

    // Context menu
    row.oncontextmenu = (e) => showFolderContextMenu(e, f.folderId, f.name);

    const icon = f.folderKind === 'collection' ? '🌐'
      : f.folderKind === 'category' ? '📂'
      : f.folderKind === 'series' ? '📚'
      : '📁';

    row.innerHTML = `
      <div class="tree-label" title="${escapeHtml(f.breadcrumb)}">
        <span>${icon}</span>
        <span>${escapeHtml(f.name)}</span>
      </div>
      <span class="badge">${f.totalSubtreeBookCount}</span>
    `;
    row.onclick = () => selectFolder(f.folderId, f.breadcrumb);
    row.ondblclick = (e) => {
      e.stopPropagation();
      openFolderModal(f.folderId);
    };
    container.appendChild(row);
  }
}

function renderSidebarTags() {
  const container = document.getElementById('tag-list-container');
  container.innerHTML = '';

  if (state.tags.length === 0) {
    container.innerHTML = `<span style="font-size:11px;color:var(--text-muted)">No tags yet. Click ＋ to add.</span>`;
    return;
  }

  for (const t of state.tags) {
    const chip = document.createElement('span');
    const isActive = state.selectedTagIds.has(t.tagId);
    chip.className = 'tag-chip' + (isActive ? ' active' : '');
    chip.style.borderLeft = `4px solid ${t.colorHex || '#3b82f6'}`;
    chip.innerHTML = `<span>${escapeHtml(t.name)}</span><span style="opacity:0.6">(${t.bookCount})</span>`;
    chip.onclick = () => {
      if (state.selectedTagIds.has(t.tagId)) {
        state.selectedTagIds.delete(t.tagId);
      } else {
        state.selectedTagIds.add(t.tagId);
      }
      renderSidebarTags();
      refreshBooks(0);
    };
    container.appendChild(chip);
  }
}

function renderSidebarCustomFields() {
  const container = document.getElementById('custom-field-defs-list');
  container.innerHTML = '';
  for (const cf of state.customFields) {
    const row = document.createElement('div');
    row.className = 'custom-def-row';
    row.innerHTML = `
      <span title="Key: ${escapeHtml(cf.fieldKey)}">${escapeHtml(cf.displayName)}</span>
      <span>${escapeHtml(cf.fieldType)}${cf.inheritToChildren ? ' ⇣' : ''}</span>
    `;
    container.appendChild(row);
  }
}

function selectRootView(mode) {
  state.selectedFolderId = null;
  state.uncategorizedOnly = (mode === 'uncategorized');
  document.getElementById('nav-all-books').classList.toggle('active', mode === 'all');
  document.getElementById('nav-uncategorized').classList.toggle('active', mode === 'uncategorized');
  document.getElementById('current-view-title').textContent = mode === 'uncategorized' ? 'Uncategorized Books' : 'All Books';
  document.getElementById('btn-edit-current-folder').classList.add('hidden');
  renderSidebarFolders();
  refreshBooks(0);
}

function selectFolder(folderId, breadcrumb) {
  state.selectedFolderId = folderId;
  state.uncategorizedOnly = false;
  document.getElementById('nav-all-books').classList.remove('active');
  document.getElementById('nav-uncategorized').classList.remove('active');
  document.getElementById('current-view-title').textContent = breadcrumb;
  document.getElementById('btn-edit-current-folder').classList.remove('hidden');
  renderSidebarFolders();
  refreshBooks(0);
}

// =========================================================================
// Book Listing, FTS5 Search & Table / Grid Rendering
// =========================================================================

let searchTimer = null;
function debouncedSearch() {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => refreshBooks(0), 140);
}

async function refreshBooks(offset = 0) {
  const limit = parseInt(document.getElementById('page-size-select').value, 10) || 100;
  const search = document.getElementById('search-input').value.trim();
  const sort = document.getElementById('sort-select').value;
  const includeDescendants = document.getElementById('chk-include-descendants').checked;
  const tagMatchMode = document.getElementById('tag-match-mode').value;

  const query = {
    folderId: state.selectedFolderId,
    includeDescendants,
    uncategorizedOnly: state.uncategorizedOnly,
    tagIds: Array.from(state.selectedTagIds),
    tagMatchMode,
    search: search || null,
    sort,
    offset,
    limit
  };

  const page = await rpcCall('library.listBooks', query);
  state.currentPage = page;
  if (!state.selectedFolderId && !state.uncategorizedOnly && !search && state.selectedTagIds.size === 0) {
    document.getElementById('count-all-books').textContent = page.totalCount;
  }
  renderBooksViewport();
  updatePaginationBar();
}

function setViewMode(mode) {
  state.viewMode = mode;
  document.getElementById('btn-view-table').classList.toggle('active', mode === 'table');
  document.getElementById('btn-view-grid').classList.toggle('active', mode === 'grid');
  renderBooksViewport();
}

function renderBooksViewport() {
  const viewport = document.getElementById('books-viewport');
  const { items, totalCount, queryElapsedMs } = state.currentPage;

  document.getElementById('query-telemetry').textContent = `${totalCount.toLocaleString()} books • ${queryElapsedMs} ms`;
  updateBulkBar();

  if (items.length === 0) {
    viewport.innerHTML = `<div style="padding:48px;text-align:center;color:var(--text-muted)">No books match the current filter. Click <strong>⚡ Scan Folder (24k+)</strong> or <strong>＋ Import EPUB</strong> to add books.</div>`;
    return;
  }

  if (state.viewMode === 'table') {
    renderTableView(viewport, items);
  } else {
    renderGridView(viewport, items);
  }
}

function renderTableView(viewport, items) {
  let html = `
    <table class="books-table">
      <thead>
        <tr>
          <th style="width:32px"><input type="checkbox" onchange="selectAllOnPage(this.checked)" /></th>
          <th style="width:74px">Read</th>
          <th>Title</th>
          <th>Series &amp; Vol</th>
          <th>Author(s)</th>
          <th>Publisher / Imprint</th>
          <th>ISBN</th>
          <th>Folder / Collection</th>
          <th>Custom / jiten.moe</th>
          <th>Tags</th>
        </tr>
      </thead>
      <tbody>
  `;

  for (const b of items) {
    const isSelected = state.selectedBookIds.has(b.bookId);
    const isActive = state.activeBookDetail && state.activeBookDetail.summary.bookId === b.bookId;
    const volBadge = b.seriesIndex != null ? `<span class="vol-pill">#${b.seriesIndex}</span>` : '';
    const jitenField = b.customFields && b.customFields['jiten_moe_url'];
    const jitenHtml = jitenField && jitenField.effectiveValueText
      ? `<a href="${escapeHtml(jitenField.effectiveValueText)}" target="_blank" onclick="event.stopPropagation()" class="jiten-link-pill ${jitenField.isInherited ? 'inherited' : ''}" title="${jitenField.isInherited ? 'Inherited from ' + escapeHtml(jitenField.sourceEntityName) : 'Direct Book Link'}">🔗 jiten.moe${jitenField.isInherited ? ' ⇣' : ''}</a>`
      : '';

    const tagsHtml = (b.tags || []).map(t =>
      `<span class="tag-chip" style="border-left:3px solid ${t.colorHex};padding:1px 6px;font-size:10px">${escapeHtml(t.name)}</span>`
    ).join(' ');

    const pubImprint = [b.publisher, b.imprint].filter(Boolean).join(' · ');

    html += `
      <tr class="${isSelected || isActive ? 'selected-row' : ''}" draggable="true" data-book-id="${b.bookId}"
          ondragstart="onBookDragStart(event, '${b.bookId}')" ondragend="onBookDragEnd(event)"
          oncontextmenu="showBookContextMenu(event, '${b.bookId}')"
          onclick="handleBookRowClick(event, '${b.bookId}')"
          ondblclick="openBookInReader('${b.bookId}')"
          title="Click to inspect • Shift+Click range • Drag onto folders or Trash • Right-click for options">
        <td onclick="event.stopPropagation()">
          <input type="checkbox" ${isSelected ? 'checked' : ''} onchange="handleBookCheckboxChange(event, '${b.bookId}', this.checked)" />
        </td>
        <td onclick="event.stopPropagation()" style="white-space:nowrap">
          <div style="display:inline-flex;gap:4px">
            <button type="button" class="btn btn-primary btn-sm" style="padding:3px 8px;font-size:11px" onclick="openBookInReader('${b.bookId}')" title="Read book">📖 Read</button>
            <button type="button" class="btn btn-secondary btn-sm" style="padding:3px 8px;font-size:11px" onclick="openBookInfoModal('${b.bookId}')" title="Book Details, Metadata &amp; jiten.moe">ℹ️ Info</button>
          </div>
        </td>
        <td>
          <div style="display:flex;align-items:center;gap:6px">
            <span class="reorder-handle" title="Drag onto collection or Trash">⋮⋮</span>
            <div style="font-weight:600">${escapeHtml(b.title)}</div>
          </div>
          ${b.isMetadataEdited ? '<span style="font-size:10px;color:#fcd34d">✎ Edited</span>' : ''}
        </td>
        <td>${escapeHtml(b.seriesName || '')}${volBadge}</td>
        <td>${escapeHtml(b.authors || '—')}</td>
        <td>${escapeHtml(pubImprint || '—')}</td>
        <td style="font-family:monospace;font-size:11px">${escapeHtml(b.isbn || '—')}</td>
        <td style="color:var(--text-muted);font-size:11px">${escapeHtml(b.folderPathBreadcrumb || '—')}</td>
        <td>${jitenHtml}</td>
        <td>${tagsHtml}</td>
      </tr>
    `;
  }

  html += `</tbody></table>`;
  viewport.innerHTML = html;
}

function renderGridView(viewport, items) {
  let html = `<div class="books-grid">`;
  for (const b of items) {
    const isSelected = state.selectedBookIds.has(b.bookId);
    const isActive = state.activeBookDetail && state.activeBookDetail.summary.bookId === b.bookId;
    const coverFile = b.coverCachePath ? b.coverCachePath.split(/[\\/]/).pop() : null;
    const coverHtml = coverFile
      ? `<img src="ir-app://covers/${encodeURIComponent(coverFile)}" alt="" loading="lazy" />`
      : `<div class="card-cover-placeholder">${escapeHtml(b.title)}</div>`;
    const volBadge = b.seriesIndex != null ? `<span class="vol-pill">#${b.seriesIndex}</span>` : '';

    html += `
      <div class="book-card ${isSelected || isActive ? 'selected-row' : ''}" draggable="true" data-book-id="${b.bookId}"
           ondragstart="onBookDragStart(event, '${b.bookId}')" ondragend="onBookDragEnd(event)"
           oncontextmenu="showBookContextMenu(event, '${b.bookId}')"
           onclick="handleBookRowClick(event, '${b.bookId}')"
           ondblclick="openBookInReader('${b.bookId}')"
           title="Click to inspect • Drag onto folders or Trash • Right-click for options">
        <div class="card-cover">
          <label class="card-check-wrap" onclick="event.stopPropagation()">
            <input type="checkbox" ${isSelected ? 'checked' : ''} onchange="handleBookCheckboxChange(event, '${b.bookId}', this.checked)" />
          </label>
          ${coverHtml}
        </div>
        <div class="card-body">
          <div class="card-title" title="${escapeHtml(b.title)}">${escapeHtml(b.title)}</div>
          <div class="card-sub">${escapeHtml(b.seriesName || b.authors || '—')}${volBadge}</div>
          <div class="card-sub" style="font-size:10px">${escapeHtml(b.folderPathBreadcrumb || '')}</div>
          <div style="display:flex;gap:4px;margin-top:6px">
            <button type="button" class="btn btn-primary btn-sm" style="flex:1;font-size:11px;padding:4px 0" onclick="event.stopPropagation(); openBookInReader('${b.bookId}')" title="Read book">📖 Read</button>
            <button type="button" class="btn btn-secondary btn-sm" style="flex:1;font-size:11px;padding:4px 0" onclick="event.stopPropagation(); openBookInfoModal('${b.bookId}')" title="Book Details, Metadata &amp; jiten.moe">ℹ️ Info</button>
          </div>
        </div>
      </div>
    `;
  }
  html += `</div>`;
  viewport.innerHTML = html;
}

function updatePaginationBar() {
  const { totalCount, offset, limit } = state.currentPage;
  const totalPages = Math.max(1, Math.ceil(totalCount / limit));
  const curPage = Math.floor(offset / limit) + 1;
  document.getElementById('page-indicator').textContent = `Page ${curPage} of ${totalPages} (${totalCount.toLocaleString()} total)`;
  document.getElementById('btn-prev-page').disabled = offset <= 0;
  document.getElementById('btn-next-page').disabled = (offset + limit) >= totalCount;
}

function prevPage() {
  const { offset, limit } = state.currentPage;
  refreshBooks(Math.max(0, offset - limit));
}

function nextPage() {
  const { offset, limit, totalCount } = state.currentPage;
  if (offset + limit < totalCount) {
    refreshBooks(offset + limit);
  }
}

function changePageSize() {
  refreshBooks(0);
}

// =========================================================================
// Inspector Panel: Single-Book Editing, Custom Fields & Reset to OPF
// =========================================================================

async function inspectBook(bookId) {
  const detail = await rpcCall('library.getBookDetail', { bookId });
  state.activeBookDetail = detail;
  populateInspector(detail);
  renderBooksViewport();
}

function populateInspector(detail) {
  const emptyEl = document.getElementById('inspector-empty');
  if (emptyEl) emptyEl.classList.add('hidden');
  const contentEl = document.getElementById('inspector-content');
  if (contentEl) contentEl.classList.remove('hidden');

  const s = detail.summary;
  document.getElementById('insp-book-id').textContent = s.bookId;
  document.getElementById('insp-edited-badge').classList.toggle('hidden', !s.isMetadataEdited);

  const modalTitle = document.getElementById('insp-modal-title');
  if (modalTitle) modalTitle.textContent = s.title || 'Book Details & Metadata';
  const modalSub = document.getElementById('insp-modal-sub');
  if (modalSub) {
    const vol = s.seriesIndex != null ? ` #${s.seriesIndex}` : '';
    const series = s.seriesName ? `${s.seriesName}${vol}` : '';
    modalSub.textContent = [series, s.authors, s.publisher].filter(Boolean).join(' • ');
  }

  const jitenQueryInput = document.getElementById('jiten-manual-query');
  if (jitenQueryInput) {
    jitenQueryInput.value = s.title || '';
  }
  const jitenBanner = document.getElementById('insp-jiten-status-banner');
  if (jitenBanner) {
    jitenBanner.classList.add('hidden');
    jitenBanner.innerHTML = '';
  }
  const jitenResults = document.getElementById('insp-jiten-results');
  if (jitenResults) {
    jitenResults.innerHTML = '';
  }

  const coverBox = document.getElementById('insp-cover-box');
  if (s.coverCachePath) {
    const file = s.coverCachePath.split(/[\\/]/).pop();
    coverBox.innerHTML = `<img src="ir-app://covers/${encodeURIComponent(file)}" alt="" />`;
  } else {
    coverBox.innerHTML = `<span style="font-size:22px">📖</span>`;
  }

  // Quick links (e.g. effective jiten.moe URL)
  const quickLinks = document.getElementById('insp-quick-links');
  if (quickLinks) {
    quickLinks.innerHTML = '';
    const jiten = s.customFields && s.customFields['jiten_moe_url'];
    if (jiten && jiten.effectiveValueText) {
      quickLinks.innerHTML = `<a href="${escapeHtml(jiten.effectiveValueText)}" target="_blank" class="jiten-link-pill" style="margin-top:0">🔗 Open on jiten.moe ${jiten.isInherited ? '(from ' + escapeHtml(jiten.sourceEntityName) + ')' : ''}</a>`;
    }
  }

  document.getElementById('insp-title').value = s.title || '';
  document.getElementById('insp-sort-title').value = s.sortTitle || '';
  document.getElementById('insp-series-name').value = s.seriesName || '';
  document.getElementById('insp-series-index').value = s.seriesIndex != null ? s.seriesIndex : '';
  document.getElementById('insp-authors').value = s.authors || '';
  document.getElementById('insp-author-sort').value = s.authorSort || '';
  document.getElementById('insp-isbn').value = s.isbn || '';
  document.getElementById('insp-pub-date').value = s.publishedDate || '';
  document.getElementById('insp-publisher').value = s.publisher || '';
  document.getElementById('insp-imprint').value = s.imprint || '';
  document.getElementById('insp-language').value = s.language || 'ja';
  document.getElementById('insp-writing-mode').value = s.writingMode || 'auto';
  document.getElementById('insp-custom-sort').value = s.customSortOrder || 0;
  document.getElementById('insp-description').value = s.description || '';

  populateFolderSelect(document.getElementById('insp-folder-id'), s.folderId, '-- Uncategorized --');

  // Tags checkboxes
  const assignedTagIds = new Set((s.tags || []).map(t => t.tagId));
  const tagBox = document.getElementById('insp-tags-selector');
  tagBox.innerHTML = state.tags.map(t => `
    <label class="tag-chip" style="border-left:3px solid ${t.colorHex}">
      <input type="checkbox" name="insp-tag" value="${t.tagId}" ${assignedTagIds.has(t.tagId) ? 'checked' : ''} />
      <span>${escapeHtml(t.name)}</span>
    </label>
  `).join('');

  // Custom fields editor (showing direct vs inherited values)
  const cfContainer = document.getElementById('insp-custom-fields');
  cfContainer.innerHTML = '';
  const effMap = {};
  for (const ef of (detail.effectiveCustomFields || [])) {
    effMap[ef.fieldKey] = ef;
  }
  const directMap = detail.directBookCustomFields || {};

  for (const def of state.customFields) {
    if (def.targetScope === 'folder') continue;
    const directVal = directMap[def.fieldKey] || '';
    const eff = effMap[def.fieldKey];
    const inheritedHint = (eff && eff.isInherited)
      ? `<div class="inherited-hint">⇣ Inherited from <strong>${escapeHtml(eff.sourceEntityName)}</strong>: <code>${escapeHtml(eff.effectiveValueText)}</code></div>`
      : '';

    const wrap = document.createElement('div');
    wrap.innerHTML = `
      <label class="form-label">${escapeHtml(def.displayName)} <span style="opacity:0.5">(${escapeHtml(def.fieldType)})</span>
        <input type="${def.fieldType === 'number' ? 'number' : 'text'}" step="any"
               class="form-input insp-cf-input"
               data-field-key="${escapeHtml(def.fieldKey)}"
               value="${escapeHtml(directVal)}"
               placeholder="${eff && eff.isInherited ? 'Inherited: ' + escapeHtml(eff.effectiveValueText) : ''}" />
      </label>
      ${inheritedHint}
    `;
    cfContainer.appendChild(wrap);
  }

  // Raw OPF metadata & file info
  const opf = detail.opfOriginal || {};
  document.getElementById('insp-opf-raw').innerHTML = `
    <div><strong>Disk Folder:</strong> ${escapeHtml(s.folderPath)}</div>
    <div><strong>EPUB File:</strong> ${escapeHtml(s.epubFileName)} (${(s.fileSizeBytes / 1024).toFixed(1)} KB)</div>
    <div><strong>SHA-256:</strong> ${escapeHtml(s.epubSha256)}</div>
    <div><strong>OPF Title:</strong> ${escapeHtml(opf.title || '—')}</div>
    <div><strong>OPF Authors:</strong> ${escapeHtml(opf.authors || '—')}</div>
    <div><strong>OPF ISBN:</strong> ${escapeHtml(opf.isbn || '—')}</div>
    <div><strong>OPF Series:</strong> ${escapeHtml(opf.seriesName || '—')} ${opf.seriesIndex != null ? '#' + opf.seriesIndex : ''}</div>
  `;

  // Chapters (clickable to open directly to that chapter in the Reader)
  const chapters = detail.chapters || [];
  document.getElementById('insp-chapter-count').textContent = chapters.length;

  let storyNum = 0;
  document.getElementById('insp-chapters-list').innerHTML = chapters.map(c => {
    let badge = '';
    let label = c.title;
    if (c.isExcluded) {
      const isBack = storyNum > 0;
      badge = `<span style="font-size:10px;padding:1px 5px;border-radius:3px;background:rgba(100,116,139,0.25);color:var(--text-muted);margin-right:6px">${isBack ? 'Back Matter' : 'Front Matter'}</span>`;
    } else {
      storyNum++;
      const hasNumbering = /^([0-9０-９]+|第[0-9０-９一二三四五六七八九十百]+[章話節回]|chapter\s*[0-9]+|プロローグ|prologue|序章)/i.test(c.title.trim());
      if (!hasNumbering) {
        label = `${storyNum}. ${c.title}`;
      }
    }

    const charsLabel = c.charCount > 0 ? `${c.charCount.toLocaleString()} chars · ` : '';
    return `<div style="cursor:pointer;padding:4px 6px;border-radius:4px;display:flex;align-items:center;gap:4px" onmouseover="this.style.background='rgba(59,130,246,0.15)'" onmouseout="this.style.background='transparent'" onclick="openBookInReader('${s.bookId}', '${escapeHtml(c.spineHref)}')" title="Open chapter in Reader">
      <span>📖</span>
      ${badge}
      <span style="font-weight:${c.isExcluded ? 'normal' : '500'}">${escapeHtml(label)}</span>
      <span style="opacity:0.4;font-size:11px;margin-left:auto">${charsLabel}${escapeHtml(c.spineHref)}</span>
    </div>`;
  }).join('');

  // Phase 4: Load Study Pack PDFs, 20-Word Vocabulary Sets & Mined Flashcards
  loadInspectorStudyPack(s.bookId);
}

let activeInspectorStudyPack = null;

async function loadInspectorStudyPack(bookId) {
  const statusEl = document.getElementById('insp-studypack-status');
  const pdfsEl = document.getElementById('insp-studypack-pdfs');
  const setsEl = document.getElementById('insp-studypack-sets');
  const minedEl = document.getElementById('insp-studypack-mined');
  if (!statusEl || !pdfsEl || !setsEl || !minedEl) return;

  try {
    const res = await rpcCall('studypack.getOverview', { bookId });
    const ov = res.overview;
    activeInspectorStudyPack = ov;

    if (!ov || !ov.available) {
      statusEl.textContent = 'Not Built';
      pdfsEl.innerHTML = `<div style="color:var(--text-muted)">No Python study pack generated yet for <code>${escapeHtml(bookId)}</code>.</div>`;
      setsEl.innerHTML = '';
    } else {
      const totalPdfs = (ov.masterPdfs?.length || 0) + (ov.chapterPdfs?.length || 0);
      const totalSets = ov.vocabularySets?.length || 0;
      statusEl.textContent = `${totalPdfs} PDFs • ${totalSets} Sets`;

      const masterLinks = (ov.masterPdfs || []).map(p =>
        `<button type="button" class="btn btn-sm btn-secondary" style="font-size:11px;padding:3px 7px" onclick="openStudyPackPdf('${escapeHtml(p.fullPath.replace(/\\/g, '\\\\'))}')">📄 ${escapeHtml(p.title)}</button>`
      ).join(' ');

      const chapterLinks = (ov.chapterPdfs || []).slice(0, 8).map(p =>
        `<button type="button" class="btn btn-sm btn-secondary" style="font-size:10px;padding:2px 6px" onclick="openStudyPackPdf('${escapeHtml(p.fullPath.replace(/\\/g, '\\\\'))}')">📑 ${escapeHtml(p.title)}</button>`
      ).join(' ');

      pdfsEl.innerHTML = `
        <div><strong>Printable Master PDFs:</strong></div>
        <div style="display:flex;flex-wrap:wrap;gap:4px;margin-top:3px">${masterLinks || '—'}</div>
        ${chapterLinks ? `<div style="margin-top:6px"><strong>Chapter PDFs (${ov.chapterPdfs.length}):</strong></div><div style="display:flex;flex-wrap:wrap;gap:4px;margin-top:3px">${chapterLinks}</div>` : ''}
      `;

      if (totalSets > 0) {
        const opts = ov.vocabularySets.map((s, idx) =>
          `<option value="${idx}">Set #${s.number} (${s.size} words)</option>`
        ).join('');
        setsEl.innerHTML = `
          <div style="display:flex;justify-content:space-between;align-items:center;margin-top:6px">
            <strong>20-Word Vocabulary Sets (${totalSets}):</strong>
            <select id="insp-vocab-set-select" class="mini-select" onchange="renderSelectedVocabSet(this.value)">${opts}</select>
          </div>
          <div id="insp-vocab-set-words" style="max-height:150px;overflow-y:auto;margin-top:4px;background:rgba(15,23,42,0.6);padding:6px;border-radius:4px"></div>
        `;
        renderSelectedVocabSet(0);
      } else {
        setsEl.innerHTML = '';
      }
    }

    const minedCards = res.minedCards || [];
    if (minedCards.length > 0) {
      const recent = minedCards.slice(-5).reverse().map(c =>
        `<div style="font-size:11px;padding:2px 0;border-bottom:1px solid rgba(148,163,184,0.1)">🎴 <strong>${escapeHtml(c.prompt)}</strong> [${escapeHtml(c.reading)}] — ${escapeHtml(c.meaning)}</div>`
      ).join('');
      minedEl.innerHTML = `
        <div style="margin-top:4px"><strong>Mined Flashcards (${minedCards.length}):</strong> <span style="font-size:10px;opacity:0.7">saved to flashcards/library.json &amp; mined-anki.tsv</span></div>
        <div>${recent}</div>
      `;
    } else {
      minedEl.innerHTML = `<div style="font-size:11px;color:var(--text-muted)">0 mined flashcards yet. Click any word in the Reader and press <strong>🎴 + Mine Sentence Card</strong>.</div>`;
    }
  } catch (err) {
    statusEl.textContent = 'Error';
    pdfsEl.innerHTML = `<div style="color:#f87171">${escapeHtml(err.message)}</div>`;
  }
}

function renderSelectedVocabSet(setIdx) {
  const box = document.getElementById('insp-vocab-set-words');
  if (!box || !activeInspectorStudyPack || !activeInspectorStudyPack.vocabularySets) return;
  const setObj = activeInspectorStudyPack.vocabularySets[Number(setIdx)];
  if (!setObj) return;

  box.innerHTML = (setObj.words || []).map(w =>
    `<div style="display:flex;justify-content:space-between;font-size:11px;padding:2px 4px;border-bottom:1px solid rgba(148,163,184,0.08)">
      <span><strong>#${w.studyNumber} ${escapeHtml(w.word)}</strong> [${escapeHtml(w.reading)}]</span>
      <span style="opacity:0.6">${escapeHtml(w.chapterId)}</span>
    </div>`
  ).join('');
}

async function openStudyPackPdf(fullPath) {
  try {
    await rpcCall('studypack.openPdf', { fullPath, launchExternal: true });
  } catch (err) {
    console.error('Failed to open study pack PDF:', err);
  }
}

async function buildCurrentBookStudyPack(readerOnly) {
  if (!state.activeBookDetail) return;
  const bookId = state.activeBookDetail.summary.bookId;
  const logEl = document.getElementById('insp-studypack-build-log');
  if (logEl) logEl.textContent = `Running Python ${readerOnly ? 'enrich' : 'build'} for ${bookId}...`;

  try {
    const res = await rpcCall('studypack.build', { bookId, readerOnly });
    if (logEl) {
      logEl.textContent = res.success
        ? `✓ Build completed cleanly`
        : `✗ Build exited with code ${res.exitCode}: ${res.outputLog || ''}`;
    }
    await loadInspectorStudyPack(bookId);
    await reloadSidebarAndBooks();
  } catch (err) {
    if (logEl) logEl.textContent = `Error: ${err.message}`;
  }
}

async function saveInspectorBook(e) {
  e.preventDefault();
  if (!state.activeBookDetail) return;

  const bookId = state.activeBookDetail.summary.bookId;
  const volRaw = document.getElementById('insp-series-index').value.trim();
  const folderVal = document.getElementById('insp-folder-id').value;

  const tagIds = Array.from(document.querySelectorAll('input[name="insp-tag"]:checked')).map(el => el.value);
  const customFields = Array.from(document.querySelectorAll('.insp-cf-input')).map(el => ({
    fieldKey: el.getAttribute('data-field-key'),
    valueText: el.value.trim()
  }));

  const req = {
    bookId,
    title: document.getElementById('insp-title').value.trim(),
    sortTitle: document.getElementById('insp-sort-title').value.trim(),
    seriesName: document.getElementById('insp-series-name').value.trim(),
    seriesIndex: volRaw !== '' ? parseFloat(volRaw) : null,
    clearSeriesIndex: volRaw === '',
    authors: document.getElementById('insp-authors').value.trim(),
    authorSort: document.getElementById('insp-author-sort').value.trim(),
    isbn: document.getElementById('insp-isbn').value.trim(),
    publishedDate: document.getElementById('insp-pub-date').value.trim(),
    publisher: document.getElementById('insp-publisher').value.trim(),
    imprint: document.getElementById('insp-imprint').value.trim(),
    language: document.getElementById('insp-language').value.trim(),
    writingMode: document.getElementById('insp-writing-mode').value,
    customSortOrder: parseInt(document.getElementById('insp-custom-sort').value, 10) || 0,
    folderId: folderVal || null,
    clearFolderId: !folderVal,
    description: document.getElementById('insp-description').value.trim(),
    tagIds,
    customFields
  };

  const updated = await rpcCall('library.updateBookMetadata', req);
  state.activeBookDetail = updated;
  populateInspector(updated);
  closeBookInfoModal();
  await reloadSidebarAndBooks();
}

async function resetCurrentBookToOpf() {
  if (!state.activeBookDetail) return;
  const bookId = state.activeBookDetail.summary.bookId;
  const reset = await rpcCall('library.resetBookMetadataToOpf', { bookId });
  state.activeBookDetail = reset;
  populateInspector(reset);
  await reloadSidebarAndBooks();
}

async function deleteCurrentBook() {
  if (!state.activeBookDetail) return;
  const s = state.activeBookDetail.summary;
  if (!confirm(`Are you sure you want to remove "${s.title}" from your library?`)) return;
  await rpcCall('library.deleteBook', { bookId: s.bookId });
  state.activeBookDetail = null;
  closeBookInfoModal();
  await reloadSidebarAndBooks();
}

// Modal Pop-up & Tab Controllers
function switchBookInfoTab(tabKey) {
  const tabs = ['meta', 'jiten', 'chapters', 'studypack', 'opf'];
  for (const t of tabs) {
    const btn = document.getElementById(`tab-btn-${t}`);
    const pane = document.getElementById(`pane-book-${t}`);
    if (btn) btn.classList.toggle('active', t === tabKey);
    if (pane) pane.classList.toggle('hidden', t !== tabKey);
  }
}

async function openBookInfoModal(bookId) {
  if (!bookId && state.activeBookDetail) {
    bookId = state.activeBookDetail.summary.bookId;
  }
  if (!bookId) return;
  await inspectBook(bookId);
  switchBookInfoTab('meta');
  const modal = document.getElementById('book-info-modal');
  if (modal) modal.classList.remove('hidden');
}

function closeBookInfoModal() {
  const modal = document.getElementById('book-info-modal');
  if (modal) modal.classList.add('hidden');
}

function onBookInfoBackdropClick(e) {
  if (e.target.id === 'book-info-modal') {
    closeBookInfoModal();
  }
}

// Jiten.moe Integration (One-Button Press Action)
async function lookupJitenMoeForCurrentBook(customQuery) {
  const detail = state.activeBookDetail;
  if (!detail) return;
  const s = detail.summary;
  const inputQuery = document.getElementById('jiten-manual-query')?.value?.trim();
  const titleField = document.getElementById('insp-title')?.value?.trim();
  const query = (customQuery || inputQuery || titleField || s.title || s.seriesName || '').trim();
  if (!query) {
    alert('Please enter a book title to look up on jiten.moe');
    return;
  }

  const resultsBox = document.getElementById('insp-jiten-results');
  if (!resultsBox) return;
  resultsBox.innerHTML = `<div style="padding:10px;color:var(--text-muted)">🔍 Searching jiten.moe for "<strong>${escapeHtml(query)}</strong>"...</div>`;

  try {
    const res = await rpcCall('library.searchJitenMoe', { query });
    const items = res?.items || [];
    if (items.length === 0) {
      resultsBox.innerHTML = `
        <div style="padding:10px;background:rgba(0,0,0,0.15);border-radius:6px;display:flex;align-items:center;justify-content:space-between;gap:8px">
          <span style="color:var(--text-muted)">No direct matches on jiten.moe for "<strong>${escapeHtml(query)}</strong>".</span>
          <a href="https://jiten.moe/decks/media?search=${encodeURIComponent(query)}" target="_blank" class="btn btn-sm btn-secondary">🌐 Search on jiten.moe in Browser</a>
        </div>
      `;
      return;
    }

    resultsBox.innerHTML = `
      <div class="jiten-results-grid">
        ${items.map(item => `
          <div class="jiten-match-item">
            ${item.coverUrl ? `<img src="${escapeHtml(item.coverUrl)}" class="jiten-match-cover" alt="" onerror="this.style.display='none'" />` : ''}
            <div class="jiten-match-info">
              <div class="jiten-match-title">${escapeHtml(item.originalTitle)}</div>
              <div class="jiten-match-sub">${escapeHtml([item.romajiTitle, item.englishTitle].filter(Boolean).join(' • '))}</div>
              <div class="jiten-match-stats">
                <span class="jiten-diff-pill">★ Diff: ${item.difficulty}/10</span>
                <span>Chars: ${item.characterCount ? item.characterCount.toLocaleString() : 0}</span>
                <span>Words: ${item.wordCount ? item.wordCount.toLocaleString() : 0}</span>
                ${item.childrenDeckCount > 0 ? `<span style="color:var(--accent-color)">${item.childrenDeckCount} Volumes</span>` : ''}
              </div>
              <div style="display:flex;align-items:center;gap:8px;margin-top:6px;flex-wrap:wrap">
                <button type="button" class="btn btn-primary btn-sm" onclick='applyJitenDeck(${JSON.stringify(item)})'>⚡ Apply to Book</button>
                ${item.childrenDeckCount > 0 ? `<button type="button" class="btn btn-secondary btn-sm" onclick="expandJitenSubDecks(${item.deckId})">▼ View Volumes</button>` : ''}
                <a href="${escapeHtml(item.jitenUrl)}" target="_blank" class="btn-link" style="font-size:11px">🔗 View on jiten.moe</a>
              </div>
              <div id="jiten-subdecks-${item.deckId}" class="hidden" style="margin-top:8px;padding-left:8px;border-left:2px solid var(--border-color)"></div>
            </div>
          </div>
        `).join('')}
      </div>
    `;
  } catch (err) {
    resultsBox.innerHTML = `
      <div style="padding:10px;color:var(--danger)">
        Failed to query jiten.moe: ${escapeHtml(err.message || String(err))}
        <div style="margin-top:6px">
          <a href="https://jiten.moe/decks/media?search=${encodeURIComponent(query)}" target="_blank" class="btn btn-sm btn-secondary">🌐 Open Search in Browser</a>
        </div>
      </div>
    `;
  }
}

async function expandJitenSubDecks(deckId) {
  const container = document.getElementById(`jiten-subdecks-${deckId}`);
  if (!container) return;
  if (!container.classList.contains('hidden')) {
    container.classList.add('hidden');
    return;
  }
  container.classList.remove('hidden');
  container.innerHTML = '<div style="font-size:11px;color:var(--text-muted);padding:4px 0">Loading volume subdecks...</div>';

  try {
    const res = await rpcCall('library.getJitenDeckDetail', { deckId });
    const subDecks = res?.subDecks || [];
    if (subDecks.length === 0) {
      container.innerHTML = '<div style="font-size:11px;color:var(--text-muted);padding:4px 0">No volume subdecks found.</div>';
      return;
    }
    container.innerHTML = `
      <div style="display:flex;flex-direction:column;gap:6px;margin-top:4px">
        ${subDecks.map(sd => `
          <div style="display:flex;align-items:center;justify-content:space-between;padding:6px 8px;background:rgba(0,0,0,0.18);border-radius:4px">
            <div>
              <strong style="color:var(--text-primary)">${escapeHtml(sd.originalTitle)}</strong>
              <span class="jiten-diff-pill" style="font-size:10px;margin-left:6px">★ ${sd.difficulty}</span>
              <span style="opacity:0.6;margin-left:6px;font-size:11px">${sd.characterCount ? sd.characterCount.toLocaleString() : 0} chars</span>
            </div>
            <button type="button" class="btn btn-primary btn-sm" style="font-size:10px;padding:2px 8px" onclick='applyJitenDeck(${JSON.stringify(sd)})'>⚡ Apply Vol</button>
          </div>
        `).join('')}
      </div>
    `;
  } catch (err) {
    container.innerHTML = `<div style="color:var(--danger);font-size:11px">Error loading volumes: ${escapeHtml(err.message)}</div>`;
  }
}

function applyJitenDeck(deck) {
  if (!deck) return;
  const cfInputs = document.querySelectorAll('.insp-cf-input');
  for (const input of cfInputs) {
    const key = input.dataset.fieldKey;
    if (key === 'jiten_moe_url') {
      input.value = deck.jitenUrl || `https://jiten.moe/decks/media/${deck.deckId}`;
    } else if (key === 'jiten_difficulty') {
      input.value = deck.difficulty != null ? deck.difficulty : '';
    }
  }

  const descEl = document.getElementById('insp-description');
  if (descEl && !descEl.value.trim() && deck.description) {
    descEl.value = deck.description;
  }

  const quickLinks = document.getElementById('insp-quick-links');
  if (quickLinks) {
    quickLinks.innerHTML = `<a href="${escapeHtml(deck.jitenUrl || `https://jiten.moe/decks/media/${deck.deckId}`)}" target="_blank" class="jiten-link-pill" style="margin-top:0">🔗 Open on jiten.moe (Applied)</a>`;
  }

  const statusEl = document.getElementById('insp-jiten-status-banner');
  if (statusEl) {
    statusEl.innerHTML = `
      <div style="padding:8px 12px;background:rgba(16,185,129,0.15);border:1px solid #10b981;border-radius:6px;color:#34d399;font-size:12px;display:flex;align-items:center;gap:8px">
        <span>✓</span>
        <span>Applied Jiten.moe information (Deck #${deck.deckId}, Difficulty: ${deck.difficulty || '—'})! Click <strong>Save Changes</strong> below to persist.</span>
      </div>
    `;
    statusEl.classList.remove('hidden');
  }
}

// =========================================================================
// Multi-Selection, Drag & Drop, Collection Pruning & Context Menu
// =========================================================================

let lastSelectedBookId = null;
let currentDragPayload = null;

function handleBookRowClick(e, bookId) {
  // If Shift is held, range-select
  if (e.shiftKey && lastSelectedBookId) {
    selectBookRange(lastSelectedBookId, bookId);
    return;
  }

  // If Ctrl/Cmd is held, toggle individual selection
  if (e.ctrlKey || e.metaKey) {
    if (state.selectedBookIds.has(bookId)) {
      state.selectedBookIds.delete(bookId);
    } else {
      state.selectedBookIds.add(bookId);
      lastSelectedBookId = bookId;
    }
    renderBooksViewport();
    return;
  }

  // Regular single click: select book
  lastSelectedBookId = bookId;
  if (!state.selectedBookIds.has(bookId)) {
    state.selectedBookIds.clear();
    state.selectedBookIds.add(bookId);
  }
  renderBooksViewport();
}

function handleBookCheckboxChange(e, bookId, checked) {
  if (e.shiftKey && lastSelectedBookId) {
    selectBookRange(lastSelectedBookId, bookId);
    return;
  }

  if (checked) {
    state.selectedBookIds.add(bookId);
    lastSelectedBookId = bookId;
  } else {
    state.selectedBookIds.delete(bookId);
  }
  renderBooksViewport();
}

function selectBookRange(startId, endId) {
  const items = state.currentPage.items;
  const startIdx = items.findIndex(b => b.bookId === startId);
  const endIdx = items.findIndex(b => b.bookId === endId);
  if (startIdx === -1 || endIdx === -1) return;

  const min = Math.min(startIdx, endIdx);
  const max = Math.max(startIdx, endIdx);
  for (let i = min; i <= max; i++) {
    state.selectedBookIds.add(items[i].bookId);
  }
  lastSelectedBookId = endId;
  renderBooksViewport();
}

function toggleBookSelection(bookId, checked) {
  if (checked) {
    state.selectedBookIds.add(bookId);
    lastSelectedBookId = bookId;
  } else {
    state.selectedBookIds.delete(bookId);
  }
  updateBulkBar();
}

function selectAllOnPage(checked) {
  for (const item of state.currentPage.items) {
    if (checked) state.selectedBookIds.add(item.bookId);
    else state.selectedBookIds.delete(item.bookId);
  }
  renderBooksViewport();
}

function clearSelection() {
  state.selectedBookIds.clear();
  renderBooksViewport();
}

function updateBulkBar() {
  const bar = document.getElementById('bulk-action-bar');
  const count = state.selectedBookIds.size;
  document.getElementById('bulk-selected-count').textContent = count;
  bar.classList.toggle('hidden', count === 0);

  if (count > 0) {
    // Populate quick move dropdown
    const folderSelect = document.getElementById('bulk-quick-folder');
    if (folderSelect) {
      let optHtml = `<option value="">📁 Move to Collection...</option>`;
      optHtml += `<option value="__uncategorized__">📥 -- Uncategorized --</option>`;
      for (const f of state.flatFolders) {
        optHtml += `<option value="${f.folderId}">${escapeHtml(f.breadcrumb)}</option>`;
      }
      folderSelect.innerHTML = optHtml;
    }

    // Populate quick tag dropdown
    const tagSelect = document.getElementById('bulk-quick-tag');
    if (tagSelect) {
      let tagHtml = `<option value="">🏷️ Add Tag...</option>`;
      for (const t of state.tags) {
        tagHtml += `<option value="${t.tagId}">＋ ${escapeHtml(t.name)}</option>`;
      }
      tagSelect.innerHTML = tagHtml;
    }
  }
}

async function onQuickBulkMove(folderId) {
  if (!folderId || state.selectedBookIds.size === 0) return;
  const isUncat = folderId === '__uncategorized__';
  try {
    await rpcCall('library.bulkUpdateBooks', {
      bookIds: Array.from(state.selectedBookIds),
      folderId: isUncat ? null : folderId,
      clearFolderId: isUncat
    });
    clearSelection();
    const sel = document.getElementById('bulk-quick-folder');
    if (sel) sel.value = '';
    await reloadSidebarAndBooks();
  } catch (err) {
    alert(`Failed to move books: ${err.message}`);
  }
}

async function onQuickBulkAddTag(tagId) {
  if (!tagId || state.selectedBookIds.size === 0) return;
  try {
    await rpcCall('library.bulkUpdateBooks', {
      bookIds: Array.from(state.selectedBookIds),
      addTagIds: [tagId]
    });
    clearSelection();
    const sel = document.getElementById('bulk-quick-tag');
    if (sel) sel.value = '';
    await reloadSidebarAndBooks();
  } catch (err) {
    alert(`Failed to add tag: ${err.message}`);
  }
}

async function bulkDeleteSelectedBooks() {
  const count = state.selectedBookIds.size;
  if (count === 0) return;
  if (!confirm(`Are you sure you want to remove ${count} book(s) from the library catalog?\n(Original EPUB files on disk will NOT be deleted)`)) {
    return;
  }
  try {
    await rpcCall('library.bulkDeleteBooks', { bookIds: Array.from(state.selectedBookIds) });
    clearSelection();
    await reloadSidebarAndBooks();
  } catch (err) {
    alert(`Failed to delete books: ${err.message}`);
  }
}

async function pruneEmptyFolders() {
  if (!confirm('Scan and prune all empty collections with zero books and zero sub-collections?')) {
    return;
  }
  try {
    const res = await rpcCall('library.pruneEmptyFolders', {});
    if (res && res.prunedCount > 0) {
      alert(`Pruned ${res.prunedCount} empty collection(s):\n• ` + res.prunedFolderNames.join('\n• '));
      await reloadSidebarAndBooks();
    } else {
      alert('No empty collections found to prune. All collections contain books or active sub-folders.');
    }
  } catch (err) {
    alert(`Failed to prune collections: ${err.message}`);
  }
}

// Drag & Drop Controller
function onBookDragStart(e, bookId) {
  if (!state.selectedBookIds.has(bookId)) {
    if (!e.ctrlKey && !e.metaKey) {
      state.selectedBookIds.clear();
    }
    state.selectedBookIds.add(bookId);
    lastSelectedBookId = bookId;
    renderBooksViewport();
  }

  const bookIds = Array.from(state.selectedBookIds);
  currentDragPayload = { type: 'books', bookIds };
  e.dataTransfer.setData('text/plain', JSON.stringify(currentDragPayload));
  e.dataTransfer.effectAllowed = 'move';

  document.querySelectorAll('tr.selected-row, .book-card.selected-row').forEach(el => {
    el.classList.add('book-dragging');
  });
}

function onBookDragEnd(e) {
  document.querySelectorAll('.book-dragging').forEach(el => el.classList.remove('book-dragging'));
  document.querySelectorAll('.tree-drop-target').forEach(el => el.classList.remove('tree-drop-target'));
  const trash = document.getElementById('trash-drop-zone');
  if (trash) trash.classList.remove('drag-trash-active');
  currentDragPayload = null;
}

function onFolderDragStart(e, folderId) {
  e.stopPropagation();
  currentDragPayload = { type: 'folder', folderId };
  e.dataTransfer.setData('text/plain', JSON.stringify(currentDragPayload));
  e.dataTransfer.effectAllowed = 'move';
  e.currentTarget.classList.add('folder-dragging');
}

function onFolderDragEnd(e) {
  document.querySelectorAll('.folder-dragging').forEach(el => el.classList.remove('folder-dragging'));
  document.querySelectorAll('.tree-drop-target').forEach(el => el.classList.remove('tree-drop-target'));
  const trash = document.getElementById('trash-drop-zone');
  if (trash) trash.classList.remove('drag-trash-active');
  currentDragPayload = null;
}

function onFolderDragOver(e, targetFolderId) {
  e.preventDefault();
  e.stopPropagation();
  if (currentDragPayload && currentDragPayload.type === 'folder' && currentDragPayload.folderId === targetFolderId) {
    return;
  }
  e.dataTransfer.dropEffect = 'move';
  const targetEl = document.querySelector(`.tree-row[data-folder-id="${targetFolderId}"]`);
  if (targetEl) targetEl.classList.add('tree-drop-target');
}

function onFolderDragLeave(e, targetFolderId) {
  e.stopPropagation();
  const targetEl = document.querySelector(`.tree-row[data-folder-id="${targetFolderId}"]`);
  if (targetEl) targetEl.classList.remove('tree-drop-target');
}

async function onFolderDrop(e, targetFolderId) {
  e.preventDefault();
  e.stopPropagation();
  const targetEl = document.querySelector(`.tree-row[data-folder-id="${targetFolderId}"]`);
  if (targetEl) targetEl.classList.remove('tree-drop-target');

  if (!currentDragPayload) return;

  if (currentDragPayload.type === 'books') {
    const bookIds = currentDragPayload.bookIds;
    if (bookIds.length === 0) return;
    try {
      await rpcCall('library.bulkUpdateBooks', { bookIds, folderId: targetFolderId });
      clearSelection();
      await reloadSidebarAndBooks();
    } catch (err) {
      alert(`Failed to move books: ${err.message}`);
    }
  } else if (currentDragPayload.type === 'folder') {
    const sourceFolderId = currentDragPayload.folderId;
    if (sourceFolderId === targetFolderId) return;
    try {
      await rpcCall('library.moveFolder', { folderId: sourceFolderId, newParentFolderId: targetFolderId });
      await reloadSidebarAndBooks();
    } catch (err) {
      alert(`Failed to move folder: ${err.message}`);
    }
  }
}

function onRootFolderDragOver(e) {
  if (currentDragPayload && currentDragPayload.type === 'folder') {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    document.getElementById('nav-all-books').classList.add('tree-drop-target');
  }
}

function onRootFolderDragLeave(e) {
  document.getElementById('nav-all-books').classList.remove('tree-drop-target');
}

async function onRootFolderDrop(e) {
  e.preventDefault();
  document.getElementById('nav-all-books').classList.remove('tree-drop-target');
  if (currentDragPayload && currentDragPayload.type === 'folder') {
    try {
      await rpcCall('library.moveFolder', { folderId: currentDragPayload.folderId, newParentFolderId: null });
      await reloadSidebarAndBooks();
    } catch (err) {
      alert(`Failed to move folder to root: ${err.message}`);
    }
  }
}

function onUncategorizedDragOver(e) {
  if (currentDragPayload && currentDragPayload.type === 'books') {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    document.getElementById('nav-uncategorized').classList.add('tree-drop-target');
  }
}

function onUncategorizedDragLeave(e) {
  document.getElementById('nav-uncategorized').classList.remove('tree-drop-target');
}

async function onUncategorizedDrop(e) {
  e.preventDefault();
  document.getElementById('nav-uncategorized').classList.remove('tree-drop-target');
  if (currentDragPayload && currentDragPayload.type === 'books') {
    try {
      await rpcCall('library.bulkUpdateBooks', {
        bookIds: currentDragPayload.bookIds,
        clearFolderId: true
      });
      clearSelection();
      await reloadSidebarAndBooks();
    } catch (err) {
      alert(`Failed to unassign books from folders: ${err.message}`);
    }
  }
}

function onTrashDragOver(e) {
  e.preventDefault();
  e.dataTransfer.dropEffect = 'move';
  const trash = document.getElementById('trash-drop-zone');
  if (trash) trash.classList.add('drag-trash-active');
}

function onTrashDragLeave(e) {
  const trash = document.getElementById('trash-drop-zone');
  if (trash) trash.classList.remove('drag-trash-active');
}

async function onTrashDrop(e) {
  e.preventDefault();
  const trash = document.getElementById('trash-drop-zone');
  if (trash) trash.classList.remove('drag-trash-active');

  if (!currentDragPayload) return;

  if (currentDragPayload.type === 'books') {
    const count = currentDragPayload.bookIds.length;
    if (confirm(`Remove ${count} selected book(s) from the library catalog?\n(Original EPUB files on disk will NOT be deleted)`)) {
      try {
        await rpcCall('library.bulkDeleteBooks', { bookIds: currentDragPayload.bookIds });
        clearSelection();
        await reloadSidebarAndBooks();
      } catch (err) {
        alert(`Failed to remove books: ${err.message}`);
      }
    }
  } else if (currentDragPayload.type === 'folder') {
    const fId = currentDragPayload.folderId;
    const f = state.flatFolders.find(x => x.folderId === fId);
    const fName = f ? f.name : 'this collection';
    if (confirm(`Delete collection '${fName}'?\nAny books inside will be preserved and reassigned to parent collection.`)) {
      try {
        await rpcCall('library.deleteFolder', { folderId: fId });
        await reloadSidebarAndBooks();
      } catch (err) {
        alert(`Failed to delete folder: ${err.message}`);
      }
    }
  }
}

// Custom Context Menu Controller
function showBookContextMenu(e, bookId) {
  e.preventDefault();
  e.stopPropagation();

  if (!state.selectedBookIds.has(bookId)) {
    if (!e.ctrlKey && !e.metaKey) {
      state.selectedBookIds.clear();
    }
    state.selectedBookIds.add(bookId);
    lastSelectedBookId = bookId;
    renderBooksViewport();
  }

  const count = state.selectedBookIds.size;
  const menu = document.getElementById('custom-context-menu');
  const itemsContainer = document.getElementById('context-menu-items');

  let itemsHtml = `
    <div class="context-menu-item" onclick="openBookInReader('${bookId}'); hideContextMenu()">
      <span>📖</span>
      <span>Read Now</span>
    </div>
    <div class="context-menu-item" onclick="openBookInfoModal('${bookId}'); hideContextMenu()">
      <span>ℹ️</span>
      <span>Book Info &amp; Metadata...</span>
    </div>
    <div class="context-menu-item" onclick="openBulkEditModal(); hideContextMenu()">
      <span>✎</span>
      <span>Edit Metadata (${count})</span>
    </div>
    <div class="context-menu-separator"></div>
    <div class="context-menu-submenu-header">Move to Collection</div>
    <div class="context-menu-item" onclick="onQuickBulkMove('__uncategorized__'); hideContextMenu()">
      <span>📥</span>
      <span>-- Uncategorized --</span>
    </div>
  `;

  for (const f of state.flatFolders.slice(0, 10)) {
    const icon = f.folderKind === 'collection' ? '🌐' : f.folderKind === 'series' ? '📚' : '📁';
    itemsHtml += `
      <div class="context-menu-item" onclick="onQuickBulkMove('${f.folderId}'); hideContextMenu()">
        <span>${icon}</span>
        <span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:160px">${escapeHtml(f.name)}</span>
      </div>
    `;
  }

  itemsHtml += `
    <div class="context-menu-separator"></div>
    <div class="context-menu-item danger" onclick="bulkDeleteSelectedBooks(); hideContextMenu()">
      <span>🗑️</span>
      <span>Remove from Library (${count})</span>
    </div>
  `;

  itemsContainer.innerHTML = itemsHtml;
  positionContextMenu(e, menu);
}

function showFolderContextMenu(e, folderId, folderName) {
  e.preventDefault();
  e.stopPropagation();

  const menu = document.getElementById('custom-context-menu');
  const itemsContainer = document.getElementById('context-menu-items');

  itemsContainer.innerHTML = `
    <div class="context-menu-item" onclick="openFolderModal('${folderId}'); hideContextMenu()">
      <span>⚙</span>
      <span>Edit Collection Details</span>
    </div>
    <div class="context-menu-item" onclick="openFolderModal(null, '${folderId}'); hideContextMenu()">
      <span>＋</span>
      <span>Add Sub-Collection</span>
    </div>
    <div class="context-menu-separator"></div>
    <div class="context-menu-item" onclick="pruneEmptyFolders(); hideContextMenu()">
      <span>🧹</span>
      <span>Prune Empty Sub-Collections</span>
    </div>
    <div class="context-menu-item danger" onclick="deleteFolderById('${folderId}', '${escapeHtml(folderName)}'); hideContextMenu()">
      <span>🗑️</span>
      <span>Delete Collection</span>
    </div>
  `;

  positionContextMenu(e, menu);
}

function positionContextMenu(e, menu) {
  menu.classList.remove('hidden');
  const menuWidth = 220;
  const menuHeight = 260;
  let x = e.clientX;
  let y = e.clientY;

  if (x + menuWidth > window.innerWidth) x = window.innerWidth - menuWidth - 8;
  if (y + menuHeight > window.innerHeight) y = window.innerHeight - menuHeight - 8;

  menu.style.left = `${Math.max(8, x)}px`;
  menu.style.top = `${Math.max(8, y)}px`;
}

function hideContextMenu() {
  const menu = document.getElementById('custom-context-menu');
  if (menu) menu.classList.add('hidden');
}

async function deleteFolderById(folderId, folderName) {
  if (!confirm(`Delete collection '${folderName}'?\nBooks will be preserved and reassigned to parent collection.`)) {
    return;
  }
  try {
    await rpcCall('library.deleteFolder', { folderId });
    if (state.selectedFolderId === folderId) {
      state.selectedFolderId = null;
    }
    await reloadSidebarAndBooks();
  } catch (err) {
    alert(`Failed to delete collection: ${err.message}`);
  }
}

window.addEventListener('click', () => hideContextMenu());
window.addEventListener('contextmenu', (e) => {
  if (!e.target.closest('.books-table tr, .book-card, .tree-row')) {
    hideContextMenu();
  }
});

function openBulkEditModal() {
  document.getElementById('bulk-modal-count').textContent = state.selectedBookIds.size;
  populateFolderSelect(document.getElementById('bulk-folder-id'), '', '-- Keep Existing Folder --');
  document.getElementById('bulk-series-name').value = '';
  document.getElementById('bulk-auto-vol-start').value = '';
  document.getElementById('bulk-authors').value = '';
  document.getElementById('bulk-publisher').value = '';
  document.getElementById('bulk-jiten-url').value = '';

  document.getElementById('bulk-add-tags').innerHTML = state.tags.map(t => `
    <label class="tag-chip" style="border-left:3px solid ${t.colorHex}">
      <input type="checkbox" name="bulk-tag" value="${t.tagId}" />
      <span>${escapeHtml(t.name)}</span>
    </label>
  `).join('');

  document.getElementById('modal-bulk').classList.remove('hidden');
}

async function submitBulkEditModal(e) {
  e.preventDefault();
  const folderId = document.getElementById('bulk-folder-id').value;
  const seriesName = document.getElementById('bulk-series-name').value.trim();
  const autoVolRaw = document.getElementById('bulk-auto-vol-start').value.trim();
  const authors = document.getElementById('bulk-authors').value.trim();
  const publisher = document.getElementById('bulk-publisher').value.trim();
  const jitenUrl = document.getElementById('bulk-jiten-url').value.trim();
  const addTagIds = Array.from(document.querySelectorAll('input[name="bulk-tag"]:checked')).map(el => el.value);

  const patch = {
    bookIds: Array.from(state.selectedBookIds),
    folderId: folderId || null,
    seriesName: seriesName || null,
    autoNumberVolumes: autoVolRaw !== '',
    autoNumberStart: autoVolRaw !== '' ? parseFloat(autoVolRaw) : 1.0,
    authors: authors || null,
    publisher: publisher || null,
    addTagIds: addTagIds.length > 0 ? addTagIds : null,
    customFields: jitenUrl ? [{ fieldKey: 'jiten_moe_url', valueText: jitenUrl }] : null
  };

  await rpcCall('library.bulkUpdateBooks', patch);
  closeModals();
  await reloadSidebarAndBooks();
}

// =========================================================================
// Modals: Folders/Series, Batch Directory Scan, Import, Tags, Custom Fields
// =========================================================================

function populateFolderSelect(selectEl, selectedId, emptyLabel) {
  selectEl.innerHTML = `<option value="">${escapeHtml(emptyLabel)}</option>`;
  for (const f of state.flatFolders) {
    const opt = document.createElement('option');
    opt.value = f.folderId;
    opt.textContent = f.breadcrumb;
    if (f.folderId === selectedId) opt.selected = true;
    selectEl.appendChild(opt);
  }
}

function editSelectedFolder() {
  if (state.selectedFolderId) {
    openFolderModal(state.selectedFolderId);
  }
}

function openFolderModal(folderId) {
  const existing = folderId ? state.flatFolders.find(f => f.folderId === folderId) : null;
  document.getElementById('modal-folder-title').textContent = existing ? `Edit Folder: ${existing.name}` : 'New Virtual Folder / Series';
  document.getElementById('fld-id').value = existing ? existing.folderId : '';
  document.getElementById('fld-name').value = existing ? existing.name : '';
  document.getElementById('fld-kind').value = existing ? existing.folderKind : 'series';
  document.getElementById('fld-sort-order').value = existing ? existing.sortOrder : 0;
  document.getElementById('fld-default-sort').value = existing ? existing.defaultBookSort : 'series_or_title';

  populateFolderSelect(
    document.getElementById('fld-parent-id'),
    existing ? existing.parentFolderId : state.selectedFolderId,
    '-- Top-Level Root Collection --'
  );

  document.getElementById('btn-delete-folder').classList.toggle('hidden', !existing);

  const cfBox = document.getElementById('fld-custom-fields-container');
  cfBox.innerHTML = '';
  const existingMap = {};
  if (existing && existing.customFields) {
    for (const cf of existing.customFields) {
      if (!cf.isInherited) existingMap[cf.fieldKey] = cf.effectiveValueText;
    }
  }

  for (const def of state.customFields) {
    if (def.targetScope === 'book') continue;
    const val = existingMap[def.fieldKey] || '';
    const div = document.createElement('div');
    div.innerHTML = `
      <label class="form-label">${escapeHtml(def.displayName)} (${escapeHtml(def.fieldType)})
        <input type="text" class="form-input fld-cf-input" data-field-key="${escapeHtml(def.fieldKey)}"
               value="${escapeHtml(val)}" placeholder="Set on folder to cascade to child books..." />
      </label>
    `;
    cfBox.appendChild(div);
  }

  document.getElementById('modal-folder').classList.remove('hidden');
}

async function submitFolderModal(e) {
  e.preventDefault();
  const folderId = document.getElementById('fld-id').value || null;
  const parentFolderId = document.getElementById('fld-parent-id').value || null;
  const name = document.getElementById('fld-name').value.trim();
  const folderKind = document.getElementById('fld-kind').value;
  const sortOrder = parseInt(document.getElementById('fld-sort-order').value, 10) || 0;
  const defaultBookSort = document.getElementById('fld-default-sort').value;

  const customFields = Array.from(document.querySelectorAll('.fld-cf-input')).map(el => ({
    fieldKey: el.getAttribute('data-field-key'),
    valueText: el.value.trim()
  }));

  await rpcCall('library.saveFolder', {
    folderId,
    parentFolderId,
    name,
    folderKind,
    sortOrder,
    defaultBookSort,
    customFields
  });

  closeModals();
  await reloadSidebarAndBooks();
}

async function deleteFolderFromModal() {
  const folderId = document.getElementById('fld-id').value;
  if (!folderId) return;
  await rpcCall('library.deleteFolder', { folderId });
  if (state.selectedFolderId === folderId) {
    state.selectedFolderId = null;
  }
  closeModals();
  await reloadSidebarAndBooks();
}

function openScanDirectoryModal() {
  document.getElementById('scan-root-dir').value = state.defaultScanDir || '';
  populateFolderSelect(document.getElementById('scan-target-folder'), state.selectedFolderId, '-- Uncategorized --');
  document.getElementById('modal-scan').classList.remove('hidden');
}

async function submitScanModal(e) {
  e.preventDefault();
  const rootDirectory = document.getElementById('scan-root-dir').value.trim();
  const targetFolderId = document.getElementById('scan-target-folder').value || null;
  const recursive = document.getElementById('scan-recursive').checked;
  const autoCreateFolders = document.getElementById('scan-auto-folders').checked;

  closeModals();
  const res = await rpcCall('library.scanDirectory', {
    rootDirectory,
    targetFolderId,
    recursive,
    autoCreateFolders
  });

  const text = document.getElementById('scan-progress-text');
  text.textContent = `Scan complete in ${res.elapsedSeconds}s • Discovered: ${res.totalFilesDiscovered} • Imported: ${res.importedNew} • Deduplicated: ${res.deduplicatedExisting}`;
  await reloadSidebarAndBooks();
}

function openImportSingleModal() {
  document.getElementById('imp-epub-path').value = '';
  document.getElementById('imp-series-name').value = '';
  document.getElementById('imp-series-index').value = '';
  populateFolderSelect(document.getElementById('imp-folder-id'), state.selectedFolderId, '-- Uncategorized --');
  document.getElementById('modal-import').classList.remove('hidden');
}

async function submitImportSingleModal(e) {
  e.preventDefault();
  const epubPath = document.getElementById('imp-epub-path').value.trim();
  const folderId = document.getElementById('imp-folder-id').value || null;
  const seriesName = document.getElementById('imp-series-name').value.trim() || null;
  const volRaw = document.getElementById('imp-series-index').value.trim();
  const seriesIndex = volRaw !== '' ? parseFloat(volRaw) : null;

  const detail = await rpcCall('library.importBook', { epubPath, folderId, seriesName, seriesIndex });
  closeModals();
  state.activeBookDetail = detail;
  populateInspector(detail);
  await reloadSidebarAndBooks();
}

function openTagModal() {
  document.getElementById('tag-name-input').value = '';
  document.getElementById('modal-tag').classList.remove('hidden');
}

async function submitTagModal(e) {
  e.preventDefault();
  const name = document.getElementById('tag-name-input').value.trim();
  const colorHex = document.getElementById('tag-color-input').value;
  const res = await rpcCall('library.saveTag', { name, colorHex });
  state.tags = res.tags || state.tags;
  renderSidebarTags();
  closeModals();
}

function openCustomFieldDefModal() {
  document.getElementById('cfd-key').value = '';
  document.getElementById('cfd-name').value = '';
  document.getElementById('modal-custom-field').classList.remove('hidden');
}

async function submitCustomFieldDefModal(e) {
  e.preventDefault();
  const fieldKey = document.getElementById('cfd-key').value.trim();
  const displayName = document.getElementById('cfd-name').value.trim();
  const fieldType = document.getElementById('cfd-type').value;
  const targetScope = document.getElementById('cfd-scope').value;
  const inheritToChildren = document.getElementById('cfd-inherit').checked;

  const res = await rpcCall('library.saveCustomFieldDef', {
    fieldKey,
    displayName,
    fieldType,
    targetScope,
    inheritToChildren
  });
  state.customFields = res.customFields || state.customFields;
  renderSidebarCustomFields();
  if (state.activeBookDetail) {
    inspectBook(state.activeBookDetail.summary.bookId);
  }
  closeModals();
}

async function reloadSidebarAndBooks() {
  const [folders, tags, customFields] = await Promise.all([
    rpcCall('library.getFolderTree', {}),
    rpcCall('library.listTags', {}),
    rpcCall('library.listCustomFieldDefs', {})
  ]);
  state.folders = folders;
  state.tags = tags;
  state.customFields = customFields;
  rebuildFlatFolders();
  renderSidebarFolders();
  renderSidebarTags();
  renderSidebarCustomFields();
  await refreshBooks(state.currentPage.offset);
}

function closeModals() {
  for (const el of document.querySelectorAll('.modal-backdrop')) {
    el.classList.add('hidden');
  }
}

// =========================================================================
// EPUB Reader View Controller (Phase 2)
// =========================================================================

const readerState = {
  activeSession: null,
  currentChapterIndex: 0,
  currentSpineHref: '',
  furiganaMode: 'AllKanji',
  writingMode: 'vertical-rl'
};

async function openCurrentBookInReader() {
  if (!state.activeBookDetail) return;
  await openBookInReader(state.activeBookDetail.summary.bookId);
}

async function openBookInReader(bookId, optionalSpineHref) {
  if (!bookId) return;
  closeDictPopover();
  closeBookInfoModal();

  const params = { bookId };
  if (optionalSpineHref) {
    params.spineHref = optionalSpineHref;
  }

  const session = await rpcCall('reader.openBook', params);
  state.activeBookDetail = session.book;
  populateInspector(session.book);

  readerState.activeSession = session;
  readerState.currentChapterIndex = session.initialChapterIndex || 0;
  readerState.currentSpineHref = session.initialSpineHref || '';
  readerState.writingMode = session.book.summary.writingMode === 'horizontal-tb' ? 'horizontal-tb' : 'vertical-rl';

  const fModeMap = ['AllKanji', 'AuthorOnly', 'UnknownAndLearningOnly', 'Hover', 'Off'];
  let rawFMode = session.enrichmentOptions ? session.enrichmentOptions.furiganaMode : 'AllKanji';
  if (typeof rawFMode === 'number') {
    rawFMode = fModeMap[rawFMode] || 'AllKanji';
  }
  readerState.furiganaMode = rawFMode || 'AllKanji';

  document.getElementById('reader-book-title').textContent = session.book.summary.title;
  document.getElementById('reader-fullscreen').classList.remove('hidden');

  populateReaderChaptersDropdown(session.book.chapters);
  updateReaderWritingModeButton();
  document.getElementById('reader-furigana-select').value = readerState.furiganaMode;

  await loadChapterInReader(readerState.currentSpineHref, session.initialChapterHtml);
}

function closeReaderView() {
  closeDictPopover();
  document.getElementById('reader-fullscreen').classList.add('hidden');
  const iframe = document.getElementById('reader-frame');
  iframe.removeAttribute('srcdoc');
  iframe.src = 'about:blank';
  readerState.activeSession = null;
  refreshBooks(state.currentPage.offset);
}

function populateReaderChaptersDropdown(chapters) {
  const select = document.getElementById('reader-chapter-dropdown');
  select.innerHTML = '';

  const frontMatter = [];
  const mainChapters = [];
  const backMatter = [];

  let storyIndex = 0;
  chapters.forEach((c, idx) => {
    if (c.isExcluded) {
      if (storyIndex > 0) {
        backMatter.push({ chapter: c, idx });
      } else {
        frontMatter.push({ chapter: c, idx });
      }
    } else {
      storyIndex++;
      mainChapters.push({ chapter: c, idx, storyIndex });
    }
  });

  const appendOpt = (parentEl, c, label) => {
    const opt = document.createElement('option');
    opt.value = c.spineHref;
    opt.textContent = label;
    if (c.spineHref === readerState.currentSpineHref || c.chapterIndex === readerState.currentChapterIndex) {
      opt.selected = true;
    }
    parentEl.appendChild(opt);
  };

  if (frontMatter.length > 0) {
    const grp = document.createElement('optgroup');
    grp.label = 'Front Matter / 序文';
    frontMatter.forEach(({ chapter }) => {
      appendOpt(grp, chapter, chapter.title);
    });
    select.appendChild(grp);
  }

  const mainGrp = document.createElement('optgroup');
  mainGrp.label = 'Chapters / 本編';
  mainChapters.forEach(({ chapter, storyIndex }) => {
    const hasNumbering = /^([0-9０-９]+|第[0-9０-９一二三四五六七八九十百]+[章話節回]|chapter\s*[0-9]+|プロローグ|prologue|序章)/i.test(chapter.title.trim());
    const label = hasNumbering ? chapter.title : `${storyIndex}. ${chapter.title}`;
    appendOpt(mainGrp, chapter, label);
  });
  select.appendChild(mainGrp);

  if (backMatter.length > 0) {
    const grp = document.createElement('optgroup');
    grp.label = 'Back Matter / 後付';
    backMatter.forEach(({ chapter }) => {
      appendOpt(grp, chapter, chapter.title);
    });
    select.appendChild(grp);
  }
}

async function loadChapterInReader(spineHref, preloadedHtml) {
  if (!readerState.activeSession || !spineHref) return;
  closeDictPopover();

  const bookId = readerState.activeSession.book.summary.bookId;
  const iframe = document.getElementById('reader-frame');

  let htmlContent = preloadedHtml;
  if (!htmlContent) {
    try {
      const res = await rpcCall('reader.getChapterContent', { bookId, spineHref });
      htmlContent = res ? res.html : '';
    } catch (err) {
      console.error('Failed to load chapter content via RPC:', err);
    }
  }

  if (htmlContent) {
    iframe.srcdoc = htmlContent;
    iframe.onload = () => applyReaderThemeToIframe();
  } else {
    iframe.removeAttribute('srcdoc');
    iframe.src = `ir-book://${bookId}/${spineHref}`;
    iframe.onload = () => applyReaderThemeToIframe();
  }

  // Update progress indicator badge
  const progressBadge = document.getElementById('reader-progress-indicator');
  if (progressBadge && readerState.activeSession) {
    const chapters = readerState.activeSession.book.chapters || [];
    const cur = readerState.currentChapterIndex + 1;
    const total = Math.max(1, chapters.length);
    const pct = Math.round((cur / total) * 100);
    progressBadge.textContent = `Ch ${cur}/${total} • ${pct}%`;
  }

  // Persist progress to backend
  rpcCall('reader.saveProgress', {
    bookId,
    chapterIndex: readerState.currentChapterIndex,
    spineHref,
    tokenId: null,
    charOffset: 0,
    ratio: 0,
    completion: (readerState.currentChapterIndex + 1) / Math.max(1, readerState.activeSession.book.chapters.length)
  });
}

function onChapterDropdownChange(spineHref) {
  if (!readerState.activeSession) return;
  const chapters = readerState.activeSession.book.chapters;
  const idx = chapters.findIndex(c => c.spineHref === spineHref);
  if (idx >= 0) {
    readerState.currentChapterIndex = idx;
    readerState.currentSpineHref = spineHref;
    loadChapterInReader(spineHref);
  }
}

function navigateChapter(delta) {
  if (!readerState.activeSession) return;
  const chapters = readerState.activeSession.book.chapters;
  const nextIdx = readerState.currentChapterIndex + delta;
  if (nextIdx >= 0 && nextIdx < chapters.length) {
    readerState.currentChapterIndex = nextIdx;
    readerState.currentSpineHref = chapters[nextIdx].spineHref;
    document.getElementById('reader-chapter-dropdown').value = readerState.currentSpineHref;
    loadChapterInReader(readerState.currentSpineHref);
    showReaderToast(`📖 ${chapters[nextIdx].title}`);
  } else if (nextIdx < 0) {
    showReaderToast('Already at the first chapter');
  } else {
    showReaderToast('Reached the end of the book');
  }
}

function onFuriganaModeChange(mode) {
  readerState.furiganaMode = mode;
  rpcCall('reader.setEnrichmentOptions', {
    furiganaMode: mode,
    writingMode: readerState.writingMode
  });
  const iframe = document.getElementById('reader-frame');
  if (iframe && iframe.contentWindow) {
    iframe.contentWindow.postMessage({ type: 'ir-set-furigana-mode', mode }, '*');
  }
}

function toggleReaderWritingMode() {
  readerState.writingMode = (readerState.writingMode === 'vertical-rl') ? 'horizontal-tb' : 'vertical-rl';
  updateReaderWritingModeButton();
  rpcCall('reader.setEnrichmentOptions', {
    furiganaMode: readerState.furiganaMode,
    writingMode: readerState.writingMode
  });
  const iframe = document.getElementById('reader-frame');
  if (iframe && iframe.contentWindow) {
    iframe.contentWindow.postMessage({ type: 'ir-set-writing-mode', mode: readerState.writingMode }, '*');
  }
  showReaderToast(`Writing Mode: ${readerState.writingMode === 'vertical-rl' ? 'Vertical 縦書き' : 'Horizontal 横書き'}`);
}

function updateReaderWritingModeButton() {
  const btn = document.getElementById('btn-reader-writing-mode');
  btn.textContent = readerState.writingMode === 'vertical-rl' ? 'Writing Mode: Vertical 縦書き' : 'Writing Mode: Horizontal 横書き';
}

// Reader Vim Motion & Keyboard Navigation Controller
function sendVimMotion(payload) {
  const iframe = document.getElementById('reader-frame');
  if (iframe && iframe.contentWindow) {
    iframe.contentWindow.postMessage({ type: 'ir-vim-motion', ...payload }, '*');
  }
}

function cycleFuriganaMode() {
  const modes = ['AllKanji', 'UnknownAndLearningOnly', 'AuthorOnly', 'Hover', 'Off'];
  const labels = {
    'AllKanji': 'All Kanji',
    'UnknownAndLearningOnly': 'Unknown & Learning Only',
    'AuthorOnly': 'Author Only',
    'Hover': 'On Hover',
    'Off': 'Off'
  };
  const curIdx = modes.indexOf(readerState.furiganaMode);
  const nextMode = modes[(curIdx + 1) % modes.length];
  onFuriganaModeChange(nextMode);
  const select = document.getElementById('reader-furigana-select');
  if (select) select.value = nextMode;
  showReaderToast(`Furigana: ${labels[nextMode] || nextMode}`);
}

function adjustReaderFontSize(delta) {
  if (!readerState.fontSizePx) readerState.fontSizePx = 22;
  if (delta === 0) {
    readerState.fontSizePx = 22;
  } else {
    readerState.fontSizePx = Math.max(14, Math.min(42, readerState.fontSizePx + delta));
  }
  document.documentElement.style.setProperty('--reader-font-size', readerState.fontSizePx + 'px');
  sendVimMotion({ action: 'adjust-font-size', delta });
  showReaderToast(`Font Size: ${readerState.fontSizePx}px`);
}

let readerToastTimeout = null;
function showReaderToast(message) {
  let toast = document.getElementById('reader-toast');
  if (!toast) return;
  toast.textContent = message;
  toast.classList.remove('hidden');
  clearTimeout(readerToastTimeout);
  readerToastTimeout = setTimeout(() => {
    toast.classList.add('hidden');
  }, 1400);
}

function toggleReaderShortcutsHelp(forceOpen) {
  const modal = document.getElementById('reader-shortcuts-modal');
  if (!modal) return;
  if (forceOpen === true) {
    modal.classList.remove('hidden');
  } else if (forceOpen === false) {
    modal.classList.add('hidden');
  } else {
    modal.classList.toggle('hidden');
  }
}

let lastVimKeyTime = 0;
let lastVimKeyChar = '';

function handleReaderKeydown(e) {
  const isReaderActive = !document.getElementById('reader-fullscreen').classList.contains('hidden');
  if (!isReaderActive) return false;

  const isInput = ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName);
  if (isInput) return false;

  const key = e.key;

  // Double 'g' sequence for jump to start
  if (key === 'g' && !e.ctrlKey && !e.metaKey && !e.altKey) {
    const now = Date.now();
    if (lastVimKeyChar === 'g' && (now - lastVimKeyTime < 500)) {
      lastVimKeyChar = '';
      sendVimMotion({ action: 'scroll-to', target: 'start' });
      showReaderToast('Jump to Chapter Start (gg)');
      return true;
    }
    lastVimKeyChar = 'g';
    lastVimKeyTime = now;
    return true;
  }
  lastVimKeyChar = '';

  switch (key) {
    case 'j':
    case 'ArrowDown':
      sendVimMotion({ action: 'scroll-step', direction: 'down' });
      return true;

    case 'k':
    case 'ArrowUp':
      sendVimMotion({ action: 'scroll-step', direction: 'up' });
      return true;

    case 'h':
    case 'ArrowLeft':
      if (readerState.writingMode === 'vertical-rl') {
        sendVimMotion({ action: 'scroll-page', forward: true });
        showReaderToast('Page Forward (h)');
      } else {
        sendVimMotion({ action: 'scroll-step', direction: 'left' });
      }
      return true;

    case 'l':
    case 'ArrowRight':
      if (readerState.writingMode === 'vertical-rl') {
        sendVimMotion({ action: 'scroll-page', forward: false });
        showReaderToast('Page Backward (l)');
      } else {
        sendVimMotion({ action: 'scroll-step', direction: 'right' });
      }
      return true;

    case ' ':
      if (e.shiftKey) {
        sendVimMotion({ action: 'scroll-page', forward: false });
        showReaderToast('Page Backward (⇧ Space)');
      } else {
        sendVimMotion({ action: 'scroll-page', forward: true });
        showReaderToast('Page Forward (Space)');
      }
      return true;

    case 'PageDown':
      sendVimMotion({ action: 'scroll-page', forward: true });
      return true;

    case 'PageUp':
      sendVimMotion({ action: 'scroll-page', forward: false });
      return true;

    case 'd':
      if (e.ctrlKey || e.metaKey) {
        sendVimMotion({ action: 'scroll-half-page', forward: true });
        showReaderToast('Half Page Down (Ctrl+d)');
        return true;
      }
      sendVimMotion({ action: 'lookup-current-word' });
      return true;

    case 'u':
      if (e.ctrlKey || e.metaKey) {
        sendVimMotion({ action: 'scroll-half-page', forward: false });
        showReaderToast('Half Page Up (Ctrl+u)');
        return true;
      }
      return false;

    case 'G':
    case 'End':
      sendVimMotion({ action: 'scroll-to', target: 'end' });
      showReaderToast('Jump to Chapter End (G)');
      return true;

    case 'Home':
      sendVimMotion({ action: 'scroll-to', target: 'start' });
      showReaderToast('Jump to Chapter Start (Home)');
      return true;

    case 'w':
      sendVimMotion({ action: 'next-word' });
      return true;

    case 'b':
      sendVimMotion({ action: 'prev-word' });
      return true;

    case 'e':
    case 'Enter':
      sendVimMotion({ action: 'lookup-current-word' });
      return true;

    case '1':
    case '2':
    case '3':
    case '4': {
      const popover = document.getElementById('dict-popover');
      if (popover && !popover.classList.contains('hidden')) {
        const statusIdx = parseInt(key, 10) - 1;
        updateWordStatusFromPopover(0, statusIdx);
        return true;
      }
      return false;
    }

    case 'm': {
      const popover = document.getElementById('dict-popover');
      if (popover && !popover.classList.contains('hidden')) {
        mineFlashcardFromPopover(0);
        return true;
      }
      return false;
    }

    case ']':
    case 'n':
      navigateChapter(1);
      return true;

    case '[':
    case 'p':
      navigateChapter(-1);
      return true;

    case 'f':
      cycleFuriganaMode();
      return true;

    case 'v':
      toggleReaderWritingMode();
      return true;

    case '+':
    case '=':
      adjustReaderFontSize(+2);
      return true;

    case '-':
    case '_':
      adjustReaderFontSize(-2);
      return true;

    case '0':
      adjustReaderFontSize(0);
      return true;

    case 'a':
      toggleAiCompanionDrawer();
      return true;

    case '?':
      toggleReaderShortcutsHelp();
      return true;

    case 'q':
      closeReaderView();
      return true;

    case 'Escape': {
      const shortcutsModal = document.getElementById('reader-shortcuts-modal');
      if (shortcutsModal && !shortcutsModal.classList.contains('hidden')) {
        toggleReaderShortcutsHelp(false);
        return true;
      }
      const popover = document.getElementById('dict-popover');
      if (popover && !popover.classList.contains('hidden')) {
        closeDictPopover();
        return true;
      }
      sendVimMotion({ action: 'clear-word-cursor' });
      closeReaderView();
      return true;
    }
  }

  return false;
}

// =========================================================================
// Dictionary Popover Controller (Phase 3)
// =========================================================================

let activeLookupResult = null;

async function handleReaderTokenClick(tokenId, clientRect) {
  if (!readerState.activeSession) return;
  const bookId = readerState.activeSession.book.summary.bookId;
  const chapterHref = readerState.currentSpineHref;

  try {
    const result = await rpcCall('lookup.query', {
      bookId,
      chapterHref,
      tokenId
    });

    if (!result || !result.terms || result.terms.length === 0) {
      closeDictPopover();
      return;
    }

    activeLookupResult = result;
    renderDictionaryPopover(result, clientRect);
  } catch (err) {
    console.error('Dictionary lookup failed:', err);
    closeDictPopover();
  }
}

function renderDictionaryPopover(result, clientRect) {
  const popover = document.getElementById('dict-popover');
  if (!popover) return;

  renderWordsTab(result.terms);
  renderKanjiTab(result.kanji);

  // Position popover relative to iframe client rect
  const iframe = document.getElementById('reader-frame');
  const iframeRect = iframe.getBoundingClientRect();

  let left = iframeRect.left + (clientRect ? clientRect.left : 100);
  let top = iframeRect.top + (clientRect ? clientRect.bottom + 8 : 100);

  // Bounds clamping
  const popWidth = 390;
  const popHeight = 490;
  if (left + popWidth > window.innerWidth - 16) {
    left = window.innerWidth - popWidth - 16;
  }
  if (left < 16) left = 16;

  if (top + popHeight > window.innerHeight - 16) {
    // Flip above token if overflowing bottom
    top = iframeRect.top + (clientRect ? clientRect.top - popHeight - 8 : 100);
    if (top < 50) top = 50;
  }

  popover.style.left = `${Math.round(left)}px`;
  popover.style.top = `${Math.round(top)}px`;
  popover.classList.remove('hidden');

  switchDictTab('words');
}

function renderWordsTab(terms) {
  const pane = document.getElementById('dict-words-pane');
  if (!pane) return;

  pane.innerHTML = terms.map((t, idx) => {
    const isRubyVerified = t.readingMatchStatus === 'epub_ruby_verified';
    const rubyBadge = isRubyVerified ? '<span class="dict-badge-ruby">✓ Matches Book Ruby</span>' : '';
    const ruleBadge = (t.inflectionChain && t.inflectionChain.length > 0)
      ? `<span class="dict-badge-rule">← ${escapeHtml(t.inflectionChain.join(' ← '))}</span>`
      : '';

    const statusNames = ['new', 'learning', 'known', 'ignored'];
    const curStatusName = statusNames[t.userStatus] || 'new';

    const statusBtns = [
      { id: 0, label: '1: New' },
      { id: 1, label: '2: Learn' },
      { id: 2, label: '3: Known' },
      { id: 3, label: '4: Ignore' }
    ].map(b => `
      <button class="dict-status-btn ${t.userStatus === b.id ? 'active-status' : ''}"
              onclick="updateWordStatusFromPopover(${idx}, ${b.id})"
              title="Shortcut: press '${b.id + 1}'">
        ${b.label}
      </button>
    `).join('');

    const freqBadges = (t.frequencies || []).map(f =>
      `<span class="dict-badge-rule" style="background:#0284c7" title="${escapeHtml(f.dictionaryTitle)}">${escapeHtml(f.frequencyText)}</span>`
    ).join(' ');

    const sensesHtml = (t.senses || []).map(s => `
      <div class="dict-sense-item">
        <div class="dict-sense-dict-title">${escapeHtml(s.dictionaryTitle)} ${s.tags ? '• ' + escapeHtml(s.tags.join(', ')) : ''}</div>
        <div>${(s.glosses || []).map(g => escapeHtml(g)).join('; ')}</div>
      </div>
    `).join('');

    const sentencePreview = (activeLookupResult && activeLookupResult.sentenceText)
      ? `<div style="margin-top:6px;padding:5px 7px;background:rgba(15,23,42,0.55);border-left:2px solid #38bdf8;border-radius:4px;font-size:11px;color:#cbd5e1">
           <span style="opacity:0.65">Context Sentence (Untranslated):</span> ${escapeHtml(activeLookupResult.sentenceText)}
         </div>`
      : '';

    const minedBadge = t._minedCardId
      ? `<span class="dict-badge-ruby" style="margin-left:6px">✓ Mined (${escapeHtml(t._minedCardId)})</span>`
      : `<button class="dict-status-btn" style="background:rgba(16,185,129,0.18);border-color:rgba(16,185,129,0.45);color:#6ee7b7" onclick="mineFlashcardFromPopover(${idx})" title="Export word + untranslated Japanese book sentence to flashcards/library.json &amp; mined-anki.tsv">🎴 + Mine Sentence Card</button>`;

    return `
      <div class="dict-term-card">
        <div class="dict-term-head">
          <span class="dict-headword">${escapeHtml(t.expression)}</span>
          <span class="dict-reading">[${escapeHtml(t.reading)}]</span>
          ${rubyBadge}
          ${ruleBadge}
          ${freqBadges}
        </div>
        <div class="dict-status-btn-group">
          ${statusBtns}
          ${minedBadge}
        </div>
        <div class="dict-senses">
          ${sensesHtml}
        </div>
        ${sentencePreview}
      </div>
    `;
  }).join('');
}

async function mineFlashcardFromPopover(termIndex) {
  if (!activeLookupResult || !activeLookupResult.terms || !activeLookupResult.terms[termIndex]) return;
  if (!readerState.activeSession) return;

  const term = activeLookupResult.terms[termIndex];
  const book = readerState.activeSession.book;
  const chIdx = readerState.currentChapterIndex || 0;
  const chObj = (book.chapters && book.chapters[chIdx]) || { chapterIndex: chIdx, title: `Chapter ${chIdx + 1}` };

  const firstSense = (term.senses && term.senses[0] && term.senses[0].glosses)
    ? term.senses[0].glosses.join('; ')
    : term.expression;

  const sentenceText = activeLookupResult.sentenceText || term.expression;
  const sentenceRuby = activeLookupResult.sentenceRuby || sentenceText;

  try {
    const res = await rpcCall('flashcards.mine', {
      bookId: book.summary.bookId,
      bookTitle: book.summary.title,
      chapterNumber: (chObj.chapterIndex || 0) + 1,
      chapterTitle: chObj.title || `Chapter ${chIdx + 1}`,
      word: term.expression,
      reading: term.reading || '',
      meaningGloss: firstSense,
      sentenceText,
      sentenceRuby
    });

    term._minedCardId = res.card ? res.card.id : 'mined';
    renderWordsTab(activeLookupResult.terms);
  } catch (err) {
    console.error('Failed to mine flashcard:', err);
  }
}

function renderKanjiTab(kanjiList) {
  const pane = document.getElementById('dict-kanji-pane');
  if (!pane) return;

  if (!kanjiList || kanjiList.length === 0) {
    pane.innerHTML = '<div style="color:var(--text-muted);padding:12px;text-align:center;">No kanji in this word.</div>';
    return;
  }

  pane.innerHTML = kanjiList.map(k => {
    let svgStrokesHtml = '';
    if (k.kanjiVgStrokes && k.kanjiVgStrokes.length > 0) {
      const paths = k.kanjiVgStrokes.map((d, sIdx) => {
        const isLast = sIdx === k.kanjiVgStrokes.length - 1;
        const color = isLast ? '#ef4444' : '#60a5fa';
        return `<path d="${escapeHtml(d)}" fill="none" stroke="${color}" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />`;
      }).join('');
      svgStrokesHtml = `
        <svg class="dict-kanji-svg" viewBox="0 0 109 109">
          ${paths}
        </svg>
      `;
    }

    return `
      <div class="dict-kanji-card">
        <div class="dict-kanji-left">
          <div class="dict-kanji-char">${escapeHtml(k.character)}</div>
          ${svgStrokesHtml}
        </div>
        <div class="dict-kanji-details">
          <div class="dict-kanji-meanings">${(k.primaryMeanings || []).map(m => escapeHtml(m)).join(', ')}</div>
          <div class="dict-kanji-readings"><strong>ON:</strong> ${(k.onReadings || []).join('、 ') || '—'}</div>
          <div class="dict-kanji-readings"><strong>KUN:</strong> ${(k.kunReadings || []).join('、 ') || '—'}</div>
          <div style="font-size:11px;color:var(--text-muted)">
            Strokes: ${k.strokeCount != null ? k.strokeCount : '—'} • Grade: ${escapeHtml(k.grade || '—')} • Source: ${escapeHtml(k.primarySource)}
          </div>
        </div>
      </div>
    `;
  }).join('');
}

async function updateWordStatusFromPopover(termIndex, statusInt) {
  if (!activeLookupResult || !activeLookupResult.terms || !activeLookupResult.terms[termIndex]) return;
  const term = activeLookupResult.terms[termIndex];

  try {
    await rpcCall('vocab.setStatus', {
      language: 'ja',
      lemma: term.expression,
      reading: term.reading,
      status: statusInt
    });

    term.userStatus = statusInt;
    renderWordsTab(activeLookupResult.terms);

    // Live update open chapter DOM without page reload!
    const statusClasses = ['ir-st-new', 'ir-st-learning', 'ir-st-known', 'ir-st-ignored'];
    const newClass = statusClasses[statusInt];
    const iframe = document.getElementById('reader-frame');
    if (iframe && iframe.contentWindow) {
      iframe.contentWindow.postMessage({
        type: 'ir-update-token-status',
        lemma: term.expression,
        statusClass: newClass
      }, '*');
    }
  } catch (err) {
    console.error('Failed to set vocabulary status:', err);
  }
}

function switchDictTab(tab) {
  const wordsBtn = document.getElementById('dict-tab-words');
  const kanjiBtn = document.getElementById('dict-tab-kanji');
  const wordsPane = document.getElementById('dict-words-pane');
  const kanjiPane = document.getElementById('dict-kanji-pane');

  if (tab === 'kanji') {
    wordsBtn.classList.remove('active');
    kanjiBtn.classList.add('active');
    wordsPane.classList.add('hidden');
    kanjiPane.classList.remove('hidden');
  } else {
    wordsBtn.classList.add('active');
    kanjiBtn.classList.remove('active');
    wordsPane.classList.remove('hidden');
    kanjiPane.classList.add('hidden');
  }
}

function closeDictPopover() {
  const popover = document.getElementById('dict-popover');
  if (popover) {
    popover.classList.add('hidden');
  }
  activeLookupResult = null;
}

// Global keyboard shortcuts
window.addEventListener('keydown', function (e) {
  // If reader view is active, delegate to reader Vim / keyboard controller first
  const isReaderActive = !document.getElementById('reader-fullscreen').classList.contains('hidden');
  if (isReaderActive) {
    if (handleReaderKeydown(e)) {
      e.preventDefault();
      return;
    }
  }

  // Settings modal shortcut: Ctrl+, or Cmd+,
  if ((e.ctrlKey || e.metaKey) && e.key === ',') {
    e.preventDefault();
    openSettingsModal();
    return;
  }

  // Ctrl+A / Cmd+A: Select all books on page if not typing in input
  const isInput = ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName);
  if ((e.ctrlKey || e.metaKey) && (e.key === 'a' || e.key === 'A') && !isInput) {
    e.preventDefault();
    selectAllOnPage(true);
    return;
  }

  // Delete key: Remove selected books from library
  if (e.key === 'Delete' && !isInput && state.selectedBookIds.size > 0) {
    e.preventDefault();
    bulkDeleteSelectedBooks();
    return;
  }

  // Escape key: close context menu, settings modal, or clear selection
  if (e.key === 'Escape') {
    hideContextMenu();

    const infoModal = document.getElementById('book-info-modal');
    if (infoModal && !infoModal.classList.contains('hidden')) {
      closeBookInfoModal();
      return;
    }

    const settingsModal = document.getElementById('modal-settings');
    if (settingsModal && !settingsModal.classList.contains('hidden')) {
      closeSettingsModal();
      return;
    }

    if (state.selectedBookIds.size > 0) {
      clearSelection();
      return;
    }
  }

  const popover = document.getElementById('dict-popover');
  if (!popover || popover.classList.contains('hidden')) return;

  if (e.key === 'Escape') {
    closeDictPopover();
  } else if (['1', '2', '3', '4'].includes(e.key)) {
    const statusIdx = parseInt(e.key, 10) - 1;
    updateWordStatusFromPopover(0, statusIdx);
  }
});

// Listen for messages posted by reader bridge inside iframe
window.addEventListener('message', function (e) {
  const data = e.data;
  if (!data || typeof data !== 'object') return;
  if (data.type === 'ir-reader-keydown') {
    handleReaderKeydown(data);
  } else if (data.type === 'ir-word-focused') {
    if (data.surface) {
      showReaderToast(`Word: ${data.surface}`);
    }
  } else if (data.type === 'ir-token-click') {
    handleReaderTokenClick(data.tokenId, data.clientRect);
  } else if (data.type === 'ir-clear-lookup') {
    closeDictPopover();
  } else if (data.type === 'ir-navigate-href') {
    if (readerState.activeSession && data.spineHref) {
      const chapters = readerState.activeSession.book.chapters || [];
      const idx = chapters.findIndex(c => c.spineHref.toLowerCase() === data.spineHref.toLowerCase());
      if (idx >= 0) {
        readerState.currentChapterIndex = idx;
        readerState.currentSpineHref = chapters[idx].spineHref;
        document.getElementById('reader-chapter-dropdown').value = readerState.currentSpineHref;
      } else {
        readerState.currentSpineHref = data.spineHref;
      }
      loadChapterInReader(readerState.currentSpineHref);
    }
  }
});

// =========================================================================
// Dictionaries Manager Modal (Phase 3)
// =========================================================================

async function openDictionariesModal() {
  document.getElementById('dict-import-status').textContent = '';
  document.getElementById('modal-dictionaries').classList.remove('hidden');
  await refreshDictionariesList();
}

async function refreshDictionariesList() {
  const box = document.getElementById('dictionaries-list-box');
  if (!box) return;
  try {
    const res = await rpcCall('dictionaries.list', {});
    const dicts = (res && res.dictionaries) || [];
    if (dicts.length === 0) {
      box.innerHTML = '<div style="color:var(--text-muted);padding:8px">No Yomitan dictionaries imported yet. Built-in MeCab morphology &amp; KANJIDIC2 kanji lookup are active.</div>';
      return;
    }
    box.innerHTML = dicts.map(d => `
      <div style="display:flex;justify-content:space-between;align-items:center;padding:6px 8px;background:rgba(30,41,59,0.7);border-radius:4px;border-left:3px solid #38bdf8">
        <div>
          <strong>${escapeHtml(d.title)}</strong>
          <span style="color:var(--text-muted);font-size:11px;margin-left:6px">rev: ${escapeHtml(d.revision)} • lang: ${escapeHtml(d.language)}</span>
        </div>
        <span class="vol-pill">Priority #${d.priorityOrder}</span>
      </div>
    `).join('');
  } catch (err) {
    box.innerHTML = `<div style="color:#f87171">${escapeHtml(err.message)}</div>`;
  }
}

async function submitImportYomitanModal(e) {
  e.preventDefault();
  const zipPath = document.getElementById('dict-zip-path').value.trim();
  const priorityOrder = parseInt(document.getElementById('dict-priority').value, 10) || 10;
  const statusEl = document.getElementById('dict-import-status');
  statusEl.textContent = 'Importing Yomitan ZIP into SQLite...';
  try {
    const res = await rpcCall('dictionaries.importYomitan', { zipPath, priorityOrder });
    statusEl.textContent = `Imported "${res.title}" (${res.termsCount.toLocaleString()} terms, ${res.metaCount.toLocaleString()} meta, ${res.kanjiCount.toLocaleString()} kanji)`;
    document.getElementById('dict-zip-path').value = '';
    await refreshDictionariesList();
  } catch (err) {
    statusEl.textContent = `Error: ${err.message}`;
  }
}

// =========================================================================
// Phase 5: AI Reading Comprehension & n+1 Persona Chat Controller
// =========================================================================

let activeQuizQuestion = null;
let activePersonas = [];

async function toggleAiCompanionDrawer() {
  const drawer = document.getElementById('ai-companion-drawer');
  if (!drawer) return;
  const isHidden = drawer.classList.toggle('hidden');
  if (!isHidden && readerState.activeSession) {
    const bookId = readerState.activeSession.book.summary.bookId;
    const bookTitle = readerState.activeSession.book.summary.title;
    try {
      const stageRes = await rpcCall('ext.aiCompanion.getStage', { bookId });
      if (stageRes && stageRes.stage) {
        document.getElementById('ai-quiz-stage-badge').textContent = stageRes.stage;
      }
      await loadPersonasForCurrentBook(bookId, bookTitle);
    } catch (err) {
      console.error('Failed to initialize AI companion drawer:', err);
    }
  }
}

function switchAiCompanionTab(tab) {
  const quizBtn = document.getElementById('ai-tab-quiz');
  const chatBtn = document.getElementById('ai-tab-chat');
  const quizPane = document.getElementById('ai-pane-quiz');
  const chatPane = document.getElementById('ai-pane-chat');

  if (tab === 'chat') {
    quizBtn.className = 'btn btn-sm btn-secondary';
    chatBtn.className = 'btn btn-sm btn-primary';
    quizPane.classList.add('hidden');
    chatPane.classList.remove('hidden');
  } else {
    quizBtn.className = 'btn btn-sm btn-primary';
    chatBtn.className = 'btn btn-sm btn-secondary';
    quizPane.classList.remove('hidden');
    chatPane.classList.add('hidden');
  }
}

async function generateAiComprehensionQuestion() {
  if (!readerState.activeSession) return;
  const bookId = readerState.activeSession.book.summary.bookId;
  const modeOverride = document.getElementById('ai-quiz-mode-override').value || null;
  const qBox = document.getElementById('ai-quiz-question-box');
  const fbBox = document.getElementById('ai-quiz-feedback-box');
  fbBox.innerHTML = '';
  qBox.innerHTML = '<div style="font-size:12px;color:var(--text-muted)">Generating spoiler-free question from just-read passage...</div>';

  try {
    const q = await rpcCall('ext.aiCompanion.generateQuestion', {
      bookId,
      stage: modeOverride,
      chapterIndex: readerState.currentChapterIndex,
      spineHref: readerState.currentSpineHref
    });
    activeQuizQuestion = q;
    document.getElementById('ai-quiz-stage-badge').textContent = q.stage || q.questionMode;

    let answerControlsHtml = '';
    if (q.questionMode === 'mcq' && q.options && q.options.length > 0) {
      answerControlsHtml = q.options.map((opt, idx) => `
        <button type="button" class="btn btn-secondary btn-sm" style="text-align:left;justify-content:flex-start;padding:8px" onclick="submitMcqOption(${idx})">
          <strong style="margin-right:6px">${String.fromCharCode(65 + idx)}.</strong>
          <span class="ir-enriched-snippet" data-snippet-href="${escapeHtml(opt.syntheticChapterHref)}">${opt.enrichedHtml}</span>
        </button>
      `).join('');
    } else {
      answerControlsHtml = `
        <textarea id="ai-quiz-freeform-input" class="form-textarea" rows="3" placeholder="Answer in Japanese or English..."></textarea>
        <button type="button" class="btn btn-primary btn-sm" onclick="submitFreeformQuizAnswer()">Submit Answer</button>
      `;
    }

    qBox.innerHTML = `
      <div style="font-size:11px;color:var(--text-muted)">Grounded in Chapter ${q.chapterIndex + 1} (Tokens ${q.startTokenId}..${q.endTokenId})</div>
      <div class="ir-enriched-snippet" data-snippet-href="${escapeHtml(q.promptSyntheticChapterHref)}" style="font-size:14px;line-height:1.7;background:rgba(30,41,59,0.7);padding:8px;border-radius:6px">
        ${q.promptHtml}
      </div>
      <div style="display:flex;flex-direction:column;gap:6px;margin-top:4px">
        ${answerControlsHtml}
      </div>
    `;
    bindEnrichedSnippetTokenClicks(qBox);
  } catch (err) {
    qBox.innerHTML = `<div style="color:#f87171">${escapeHtml(err.message)}</div>`;
  }
}

async function submitMcqOption(optionIndex) {
  if (!activeQuizQuestion) return;
  await submitQuizAnswerInternal(String(optionIndex), optionIndex);
}

async function submitFreeformQuizAnswer() {
  if (!activeQuizQuestion) return;
  const input = document.getElementById('ai-quiz-freeform-input');
  const ans = input ? input.value.trim() : '';
  if (!ans) return;
  await submitQuizAnswerInternal(ans, null);
}

async function submitQuizAnswerInternal(userAnswer, selectedOptionIndex) {
  const fbBox = document.getElementById('ai-quiz-feedback-box');
  fbBox.innerHTML = '<div style="font-size:12px;color:var(--text-muted)">Grading answer against passage evidence...</div>';

  try {
    const res = await rpcCall('ext.aiCompanion.submitAnswer', {
      questionId: activeQuizQuestion.questionId,
      userAnswer,
      selectedOptionIndex
    });

    const color = res.comprehensionScore >= 75 ? '#10b981' : '#f59e0b';
    fbBox.innerHTML = `
      <div style="background:rgba(15,23,42,0.85);border-left:3px solid ${color};padding:10px;border-radius:6px;display:flex;flex-direction:column;gap:6px;font-size:12px">
        <div><strong>Score: ${res.comprehensionScore}%</strong> ${res.isCorrect ? '✓ Correct' : ''}</div>
        <div><strong>Evidence Quote:</strong> 「${escapeHtml(res.evidenceQuote)}」 <a href="#" onclick="jumpToReaderParagraph(${res.paragraphId});return false;" style="color:#38bdf8">${escapeHtml(res.jumpToParagraphLabel)}</a></div>
        ${res.missingOrMisunderstood ? `<div><strong>Notes:</strong> ${escapeHtml(res.missingOrMisunderstood)}</div>` : ''}
        <div><strong>Model Answer:</strong></div>
        <div class="ir-enriched-snippet" data-snippet-href="${escapeHtml(res.modelAnswerSyntheticChapterHref)}" style="background:rgba(30,41,59,0.65);padding:6px;border-radius:4px;line-height:1.7">
          ${res.modelAnswerHtml}
        </div>
      </div>
    `;
    bindEnrichedSnippetTokenClicks(fbBox);
  } catch (err) {
    fbBox.innerHTML = `<div style="color:#f87171">${escapeHtml(err.message)}</div>`;
  }
}

function jumpToReaderParagraph(paragraphId) {
  const iframe = document.getElementById('reader-frame');
  if (iframe && iframe.contentDocument) {
    const pEl = iframe.contentDocument.querySelector(`[data-ir-pid="${paragraphId}"]`);
    if (pEl) {
      pEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
  }
}

async function loadPersonasForCurrentBook(bookId, bookTitle) {
  const res = await rpcCall('ext.aiCompanion.listPersonas', { bookId, bookTitle });
  activePersonas = (res && res.personas) || [];
  const select = document.getElementById('ai-persona-select');
  select.innerHTML = activePersonas.map(p =>
    `<option value="${escapeHtml(p.personaId)}">${escapeHtml(p.name)} — ${escapeHtml(p.roleSummary)}</option>`
  ).join('');
}

function onSelectPersonaChange() {
  document.getElementById('ai-chat-messages').innerHTML = '';
}

function toggleNewPersonaForm() {
  document.getElementById('ai-new-persona-form').classList.toggle('hidden');
}

async function createNewBookPersona() {
  if (!readerState.activeSession) return;
  const bookId = readerState.activeSession.book.summary.bookId;
  const name = document.getElementById('ai-prs-name').value.trim();
  const speechStyle = document.getElementById('ai-prs-style').value.trim();
  const sample = document.getElementById('ai-prs-sample').value.trim();
  if (!name) return;

  await rpcCall('ext.aiCompanion.createPersona', {
    bookId,
    name,
    roleSummary: 'Book Character Persona',
    speechStyle,
    voiceSamples: sample ? [sample] : []
  });

  document.getElementById('ai-prs-name').value = '';
  document.getElementById('ai-prs-style').value = '';
  document.getElementById('ai-prs-sample').value = '';
  document.getElementById('ai-new-persona-form').classList.add('hidden');
  await loadPersonasForCurrentBook(bookId, readerState.activeSession.book.summary.title);
}

async function submitAiPersonaChatMessage(e) {
  e.preventDefault();
  if (!readerState.activeSession) return;
  const personaId = document.getElementById('ai-persona-select').value;
  const input = document.getElementById('ai-chat-input');
  const message = input.value.trim();
  if (!personaId || !message) return;

  input.value = '';
  const chatBox = document.getElementById('ai-chat-messages');

  try {
    const turn = await rpcCall('ext.aiCompanion.sendChatMessage', {
      personaId,
      message,
      chapterIndex: readerState.currentChapterIndex,
      spineHref: readerState.currentSpineHref
    });

    const plusOneBadges = (turn.personaMessage.plusOneWordsUsed || []).map(w =>
      `<span class="dict-badge-ruby" style="margin-left:4px">+1 Target: ${escapeHtml(w)}</span>`
    ).join('');

    const msgHtml = `
      <div style="align-self:flex-end;background:#1e3a8a;padding:7px 10px;border-radius:8px;max-width:85%;font-size:13px">
        <div class="ir-enriched-snippet" data-snippet-href="${escapeHtml(turn.userMessage.syntheticChapterHref)}">${turn.userMessage.enrichedHtml}</div>
      </div>
      <div style="align-self:flex-start;background:#1e293b;border:1px solid rgba(56,189,248,0.3);padding:8px 10px;border-radius:8px;max-width:90%;font-size:13px;line-height:1.7">
        <div style="font-size:10px;color:#94a3b8;margin-bottom:3px">
          <strong>${escapeHtml(turn.persona.name)}</strong> • Spoiler Cap: Ch ${turn.cappedChapterIndex + 1} ${plusOneBadges}
        </div>
        <div class="ir-enriched-snippet" data-snippet-href="${escapeHtml(turn.personaMessage.syntheticChapterHref)}">${turn.personaMessage.enrichedHtml}</div>
      </div>
    `;
    const wrapper = document.createElement('div');
    wrapper.style.display = 'flex';
    wrapper.style.flexDirection = 'column';
    wrapper.style.gap = '6px';
    wrapper.innerHTML = msgHtml;
    bindEnrichedSnippetTokenClicks(wrapper);
    chatBox.appendChild(wrapper);
    chatBox.scrollTop = chatBox.scrollHeight;
  } catch (err) {
    console.error('Persona chat failed:', err);
  }
}

function bindEnrichedSnippetTokenClicks(containerEl) {
  if (!containerEl) return;
  for (const tokEl of containerEl.querySelectorAll('.ir-tok')) {
    tokEl.style.cursor = 'pointer';
    tokEl.onclick = async (ev) => {
      ev.stopPropagation();
      if (!readerState.activeSession) return;
      const tid = parseInt(tokEl.getAttribute('data-tid'), 10);
      const snippetHref = tokEl.getAttribute('data-ir-snippet-href')
        || tokEl.closest('[data-snippet-href]')?.getAttribute('data-snippet-href');
      if (isNaN(tid) || !snippetHref) return;

      const bookId = readerState.activeSession.book.summary.bookId;
      const res = await rpcCall('lookup.query', {
        bookId,
        chapterHref: snippetHref,
        tokenId: tid
      });
      if (res && res.terms && res.terms.length > 0) {
        activeLookupResult = res;
        const rect = tokEl.getBoundingClientRect();
        renderDictionaryPopover(res, { left: rect.left, top: rect.top, bottom: rect.bottom });
      }
    };
  }
}

function escapeHtml(str) {
  if (str == null) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

// =========================================================================
// Global Configuration & Settings Controller (Spec 07)
// =========================================================================

let currentAppConfig = {
  directories: {
    defaultScanDirectory: '',
    extractorRootDirectory: '',
    pythonExecutablePath: '',
    flashcardsDirectory: ''
  },
  appearance: {
    themePreset: 'slate',
    bgPrimary: '#0f172a',
    bgSurface: '#1e293b',
    textPrimary: '#f8fafc',
    textMuted: '#94a3b8',
    borderColor: '#334155',
    accentColor: '#38bdf8',
    rubyColor: '#e2e8f0',
    uiFontFamily: 'system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Hiragino Sans", "Yu Gothic UI", sans-serif'
  },
  reader: {
    fontFamily: "'Noto Serif JP', 'Yu Mincho', serif",
    fontSizePx: 22,
    lineHeight: 1.85,
    writingMode: 'vertical-rl',
    furiganaMode: 'AllKanji',
    autoSavePosition: true,
    columnMaxWidthPx: 900
  },
  llm: {
    preset: 'ollama',
    endpointUrl: 'http://localhost:11434/v1/chat/completions',
    modelId: 'llama3.2',
    apiKey: '',
    timeoutSeconds: 15,
    temperature: 0.7,
    quizThresholdPercent: 80,
    personaUnknownThreshold: 1
  },
  dictionaries: {
    maxScanLength: 16,
    maxDeinflections: 4,
    maxResults: 12,
    cardExportFormat: 'both',
    autoClipboard: true
  }
};

const THEME_PRESETS = {
  'slate': {
    bgPrimary: '#0f172a',
    bgSurface: '#1e293b',
    textPrimary: '#f8fafc',
    textMuted: '#94a3b8',
    borderColor: '#334155',
    accentColor: '#38bdf8',
    rubyColor: '#e2e8f0'
  },
  'warm-paper': {
    bgPrimary: '#fbf0d9',
    bgSurface: '#f4e3c1',
    textPrimary: '#2d241e',
    textMuted: '#7c6f64',
    borderColor: '#d5c4a1',
    accentColor: '#af3a03',
    rubyColor: '#9d0006'
  },
  'tokyo-night': {
    bgPrimary: '#1a1b26',
    bgSurface: '#24283b',
    textPrimary: '#c0caf5',
    textMuted: '#7aa2f7',
    borderColor: '#414868',
    accentColor: '#bb9af7',
    rubyColor: '#f7768e'
  },
  'e-ink': {
    bgPrimary: '#ffffff',
    bgSurface: '#f0f0f0',
    textPrimary: '#000000',
    textMuted: '#555555',
    borderColor: '#cccccc',
    accentColor: '#000000',
    rubyColor: '#444444'
  }
};

const LLM_PRESETS = {
  'ollama': {
    endpointUrl: 'http://localhost:11434/v1/chat/completions',
    modelId: 'llama3.2'
  },
  'lmstudio': {
    endpointUrl: 'http://localhost:1234/v1/chat/completions',
    modelId: 'qwen2.5:7b'
  },
  'openai': {
    endpointUrl: 'https://api.openai.com/v1/chat/completions',
    modelId: 'gpt-4o-mini'
  },
  'offline': {
    endpointUrl: 'http://localhost:0/offline-heuristic',
    modelId: 'heuristic-local'
  }
};

let activeSettingsTab = 'general';

function applyConfigToDom(config) {
  if (!config) return;
  const root = document.documentElement;

  // Appearance
  if (config.appearance) {
    const app = config.appearance;
    if (app.bgPrimary) root.style.setProperty('--bg-primary', app.bgPrimary);
    if (app.bgSurface) root.style.setProperty('--bg-surface', app.bgSurface);
    if (app.textPrimary) root.style.setProperty('--text-primary', app.textPrimary);
    if (app.textMuted) root.style.setProperty('--text-muted', app.textMuted);
    if (app.borderColor) root.style.setProperty('--border-color', app.borderColor);
    if (app.accentColor) root.style.setProperty('--accent-color', app.accentColor);
    if (app.rubyColor) root.style.setProperty('--ruby-color', app.rubyColor);
    if (app.uiFontFamily) {
      root.style.setProperty('--ui-font-family', app.uiFontFamily);
      document.body.style.fontFamily = app.uiFontFamily;
    }
  }

  // Reader Settings
  if (config.reader) {
    const r = config.reader;
    if (r.fontFamily) root.style.setProperty('--reader-font', r.fontFamily);
    if (r.fontSizePx) root.style.setProperty('--reader-font-size', `${r.fontSizePx}px`);
    if (r.lineHeight) root.style.setProperty('--reader-line-height', r.lineHeight);
    if (r.columnMaxWidthPx) root.style.setProperty('--reader-column-width', `${r.columnMaxWidthPx}px`);

    if (r.writingMode) {
      readerState.writingMode = r.writingMode;
      updateReaderWritingModeButton();
      const iframe = document.getElementById('reader-frame');
      if (iframe && iframe.contentWindow) {
        iframe.contentWindow.postMessage({ type: 'ir-set-writing-mode', mode: r.writingMode }, '*');
      }
    }
    if (r.furiganaMode) {
      readerState.furiganaMode = r.furiganaMode;
      const fSelect = document.getElementById('reader-furigana-select');
      if (fSelect) fSelect.value = r.furiganaMode;
      const iframe = document.getElementById('reader-frame');
      if (iframe && iframe.contentWindow) {
        iframe.contentWindow.postMessage({ type: 'ir-set-furigana-mode', mode: r.furiganaMode }, '*');
      }
    }
  }

  // Update open Reader iframe if present
  applyReaderThemeToIframe();

  // Directory scan default
  if (config.directories && config.directories.defaultScanDirectory) {
    state.defaultScanDir = config.directories.defaultScanDirectory;
  }
}

function applyReaderThemeToIframe() {
  const iframe = document.getElementById('reader-frame');
  if (!iframe || !iframe.contentDocument) return;
  try {
    const doc = iframe.contentDocument;
    const rRoot = doc.documentElement;
    if (currentAppConfig.appearance) {
      const app = currentAppConfig.appearance;
      if (app.bgPrimary) rRoot.style.setProperty('--bg-primary', app.bgPrimary);
      if (app.textPrimary) rRoot.style.setProperty('--text-primary', app.textPrimary);
      if (app.rubyColor) rRoot.style.setProperty('--ruby-color', app.rubyColor);
    }
    if (currentAppConfig.reader) {
      const r = currentAppConfig.reader;
      if (r.fontFamily) rRoot.style.setProperty('--reader-font', r.fontFamily);
      if (r.fontSizePx) rRoot.style.setProperty('--reader-font-size', `${r.fontSizePx}px`);
      if (r.lineHeight) rRoot.style.setProperty('--reader-line-height', r.lineHeight);
    }
  } catch (e) {
    // Cross-origin guard (about:blank or same-origin)
  }
}

function openSettingsModal() {
  populateSettingsForm(currentAppConfig);
  switchSettingsTab(activeSettingsTab);
  document.getElementById('modal-settings').classList.remove('hidden');
}

function closeSettingsModal() {
  document.getElementById('modal-settings').classList.add('hidden');
  // Re-apply saved config in case user tweaked colors but didn't save
  applyConfigToDom(currentAppConfig);
}

function switchSettingsTab(tabName) {
  activeSettingsTab = tabName;
  document.querySelectorAll('.settings-tab-btn').forEach(btn => {
    btn.classList.toggle('active', btn.getAttribute('data-tab') === tabName);
  });
  document.querySelectorAll('.settings-pane').forEach(pane => {
    pane.classList.remove('active');
  });
  const targetPane = document.getElementById(`settings-pane-${tabName}`);
  if (targetPane) {
    targetPane.classList.add('active');
  }
}

function populateSettingsForm(cfg) {
  if (!cfg) return;

  // Directories
  const d = cfg.directories || {};
  document.getElementById('cfg-dir-scan').value = d.defaultScanDirectory || '';
  document.getElementById('cfg-dir-extractor').value = d.extractorRootDirectory || '';
  document.getElementById('cfg-dir-python').value = d.pythonExecutablePath || '';
  document.getElementById('cfg-dir-flashcards').value = d.flashcardsDirectory || '';

  // Appearance
  const a = cfg.appearance || {};
  document.getElementById('cfg-app-preset').value = a.themePreset || 'slate';
  document.getElementById('cfg-app-font').value = a.uiFontFamily || '';
  setPaletteField('cfg-col-bg', 'cfg-hex-bg', a.bgPrimary || '#0f172a');
  setPaletteField('cfg-col-surface', 'cfg-hex-surface', a.bgSurface || '#1e293b');
  setPaletteField('cfg-col-text', 'cfg-hex-text', a.textPrimary || '#f8fafc');
  setPaletteField('cfg-col-muted', 'cfg-hex-muted', a.textMuted || '#94a3b8');
  setPaletteField('cfg-col-border', 'cfg-hex-border', a.borderColor || '#334155');
  setPaletteField('cfg-col-accent', 'cfg-hex-accent', a.accentColor || '#38bdf8');
  setPaletteField('cfg-col-ruby', 'cfg-hex-ruby', a.rubyColor || '#e2e8f0');

  // Reader
  const r = cfg.reader || {};
  document.getElementById('cfg-reader-font').value = r.fontFamily || "'Noto Serif JP', 'Yu Mincho', serif";
  document.getElementById('cfg-reader-writing').value = r.writingMode || 'vertical-rl';
  document.getElementById('cfg-reader-fontsize').value = r.fontSizePx || 22;
  document.getElementById('cfg-reader-lineheight').value = r.lineHeight || 1.85;
  document.getElementById('cfg-reader-furigana').value = r.furiganaMode || 'AllKanji';
  document.getElementById('cfg-reader-maxwidth').value = r.columnMaxWidthPx || 900;
  document.getElementById('cfg-reader-savepos').checked = r.autoSavePosition !== false;

  // LLM
  const l = cfg.llm || {};
  document.getElementById('cfg-llm-preset').value = l.preset || 'ollama';
  document.getElementById('cfg-llm-endpoint').value = l.endpointUrl || '';
  document.getElementById('cfg-llm-model').value = l.modelId || '';
  document.getElementById('cfg-llm-apikey').value = l.apiKey || '';
  document.getElementById('cfg-llm-timeout').value = l.timeoutSeconds || 15;
  document.getElementById('cfg-llm-temp').value = l.temperature || 0.7;
  document.getElementById('cfg-llm-quiz-threshold').value = l.quizThresholdPercent || 80;
  document.getElementById('cfg-llm-persona-unknown').value = l.personaUnknownThreshold || 1;
  document.getElementById('cfg-llm-test-result').textContent = '';

  // Dictionaries
  const dict = cfg.dictionaries || {};
  document.getElementById('cfg-dict-max-scan').value = dict.maxScanLength || 16;
  document.getElementById('cfg-dict-max-depth').value = dict.maxDeinflections || 4;
  document.getElementById('cfg-dict-max-results').value = dict.maxResults || 12;
  document.getElementById('cfg-dict-export-format').value = dict.cardExportFormat || 'both';
  document.getElementById('cfg-dict-autocopy').checked = dict.autoClipboard !== false;
}

function setPaletteField(pickerId, hexId, value) {
  const picker = document.getElementById(pickerId);
  const hex = document.getElementById(hexId);
  if (picker) picker.value = value;
  if (hex) hex.value = value;
}

function syncColorInput(pickerId, hexValue) {
  const picker = document.getElementById(pickerId);
  if (picker && /^#[0-9a-fA-F]{6}$/.test(hexValue)) {
    picker.value = hexValue;
  }
  document.getElementById('cfg-app-preset').value = 'custom';
  previewFormColors();
}

function onCustomColorChange() {
  document.getElementById('cfg-hex-bg').value = document.getElementById('cfg-col-bg').value;
  document.getElementById('cfg-hex-surface').value = document.getElementById('cfg-col-surface').value;
  document.getElementById('cfg-hex-text').value = document.getElementById('cfg-col-text').value;
  document.getElementById('cfg-hex-muted').value = document.getElementById('cfg-col-muted').value;
  document.getElementById('cfg-hex-border').value = document.getElementById('cfg-col-border').value;
  document.getElementById('cfg-hex-accent').value = document.getElementById('cfg-col-accent').value;
  document.getElementById('cfg-hex-ruby').value = document.getElementById('cfg-col-ruby').value;
  document.getElementById('cfg-app-preset').value = 'custom';
  previewFormColors();
}

function onThemePresetSelect(presetKey) {
  const preset = THEME_PRESETS[presetKey];
  if (!preset) return;
  setPaletteField('cfg-col-bg', 'cfg-hex-bg', preset.bgPrimary);
  setPaletteField('cfg-col-surface', 'cfg-hex-surface', preset.bgSurface);
  setPaletteField('cfg-col-text', 'cfg-hex-text', preset.textPrimary);
  setPaletteField('cfg-col-muted', 'cfg-hex-muted', preset.textMuted);
  setPaletteField('cfg-col-border', 'cfg-hex-border', preset.borderColor);
  setPaletteField('cfg-col-accent', 'cfg-hex-accent', preset.accentColor);
  setPaletteField('cfg-col-ruby', 'cfg-hex-ruby', preset.rubyColor);
  previewFormColors();
}

function previewFormColors() {
  const preview = {
    appearance: {
      bgPrimary: document.getElementById('cfg-col-bg').value,
      bgSurface: document.getElementById('cfg-col-surface').value,
      textPrimary: document.getElementById('cfg-col-text').value,
      textMuted: document.getElementById('cfg-col-muted').value,
      borderColor: document.getElementById('cfg-col-border').value,
      accentColor: document.getElementById('cfg-col-accent').value,
      rubyColor: document.getElementById('cfg-col-ruby').value,
      uiFontFamily: document.getElementById('cfg-app-font').value
    }
  };
  applyConfigToDom(preview);
}

function onLlmPresetSelect(presetKey) {
  const preset = LLM_PRESETS[presetKey];
  if (!preset) return;
  document.getElementById('cfg-llm-endpoint').value = preset.endpointUrl;
  document.getElementById('cfg-llm-model').value = preset.modelId;
}

async function testLlmConnectivity() {
  const statusEl = document.getElementById('cfg-llm-test-result');
  statusEl.textContent = 'Testing connection...';
  statusEl.style.color = 'var(--text-muted)';
  try {
    const res = await rpcCall('config.testLlmEndpoint', {
      endpointUrl: document.getElementById('cfg-llm-endpoint').value,
      modelId: document.getElementById('cfg-llm-model').value,
      apiKey: document.getElementById('cfg-llm-apikey').value,
      timeoutSeconds: parseInt(document.getElementById('cfg-llm-timeout').value, 10) || 15
    });
    if (res && res.reachable) {
      const modelsText = (res.models && res.models.length > 0)
        ? ` (${res.models.length} models: ${res.models.slice(0, 3).join(', ')}${res.models.length > 3 ? '...' : ''})`
        : '';
      statusEl.textContent = `✓ ${res.message}${modelsText}`;
      statusEl.style.color = '#10b981';
    } else {
      statusEl.textContent = `✗ ${res.message || 'Connection failed'}`;
      statusEl.style.color = '#ef4444';
    }
  } catch (err) {
    statusEl.textContent = `✗ ${err.message || 'Connection error'}`;
    statusEl.style.color = '#ef4444';
  }
}

function collectConfigFromForm() {
  return {
    directories: {
      defaultScanDirectory: document.getElementById('cfg-dir-scan').value.trim(),
      extractorRootDirectory: document.getElementById('cfg-dir-extractor').value.trim(),
      pythonExecutablePath: document.getElementById('cfg-dir-python').value.trim(),
      flashcardsDirectory: document.getElementById('cfg-dir-flashcards').value.trim()
    },
    appearance: {
      themePreset: document.getElementById('cfg-app-preset').value,
      bgPrimary: document.getElementById('cfg-col-bg').value,
      bgSurface: document.getElementById('cfg-col-surface').value,
      textPrimary: document.getElementById('cfg-col-text').value,
      textMuted: document.getElementById('cfg-col-muted').value,
      borderColor: document.getElementById('cfg-col-border').value,
      accentColor: document.getElementById('cfg-col-accent').value,
      rubyColor: document.getElementById('cfg-col-ruby').value,
      uiFontFamily: document.getElementById('cfg-app-font').value.trim()
    },
    reader: {
      fontFamily: document.getElementById('cfg-reader-font').value.trim(),
      writingMode: document.getElementById('cfg-reader-writing').value,
      fontSizePx: parseInt(document.getElementById('cfg-reader-fontsize').value, 10) || 22,
      lineHeight: parseFloat(document.getElementById('cfg-reader-lineheight').value) || 1.85,
      furiganaMode: document.getElementById('cfg-reader-furigana').value,
      columnMaxWidthPx: parseInt(document.getElementById('cfg-reader-maxwidth').value, 10) || 900,
      autoSavePosition: document.getElementById('cfg-reader-savepos').checked
    },
    llm: {
      preset: document.getElementById('cfg-llm-preset').value,
      endpointUrl: document.getElementById('cfg-llm-endpoint').value.trim(),
      modelId: document.getElementById('cfg-llm-model').value.trim(),
      apiKey: document.getElementById('cfg-llm-apikey').value.trim(),
      timeoutSeconds: parseInt(document.getElementById('cfg-llm-timeout').value, 10) || 15,
      temperature: parseFloat(document.getElementById('cfg-llm-temp').value) || 0.7,
      quizThresholdPercent: parseInt(document.getElementById('cfg-llm-quiz-threshold').value, 10) || 80,
      personaUnknownThreshold: parseInt(document.getElementById('cfg-llm-persona-unknown').value, 10) || 1
    },
    dictionaries: {
      maxScanLength: parseInt(document.getElementById('cfg-dict-max-scan').value, 10) || 16,
      maxDeinflections: parseInt(document.getElementById('cfg-dict-max-depth').value, 10) || 4,
      maxResults: parseInt(document.getElementById('cfg-dict-max-results').value, 10) || 12,
      cardExportFormat: document.getElementById('cfg-dict-export-format').value,
      autoClipboard: document.getElementById('cfg-dict-autocopy').checked
    }
  };
}

async function saveSettingsModal() {
  const newConfig = collectConfigFromForm();
  try {
    const res = await rpcCall('config.update', { config: newConfig });
    currentAppConfig = (res && res.updatedConfig) ? res.updatedConfig : (res || newConfig);
    applyConfigToDom(currentAppConfig);
    closeSettingsModal();
  } catch (err) {
    alert(`Failed to save settings: ${err.message}`);
  }
}

async function resetSettingsCategory() {
  const categoryMap = {
    general: 'directories',
    appearance: 'appearance',
    reader: 'reader',
    llm: 'llm',
    dictionaries: 'dictionaries'
  };
  const cat = categoryMap[activeSettingsTab] || activeSettingsTab;
  try {
    const res = await rpcCall('config.resetDefaults', { category: cat });
    currentAppConfig = (res && res.updatedConfig) ? res.updatedConfig : (res || currentAppConfig);
    populateSettingsForm(currentAppConfig);
    applyConfigToDom(currentAppConfig);
  } catch (err) {
    alert(`Failed to reset ${cat} settings: ${err.message}`);
  }
}

async function resetAllSettings() {
  if (!confirm('Are you sure you want to reset ALL configuration to factory defaults?')) {
    return;
  }
  try {
    const res = await rpcCall('config.resetDefaults', { category: 'all' });
    currentAppConfig = (res && res.updatedConfig) ? res.updatedConfig : (res || currentAppConfig);
    populateSettingsForm(currentAppConfig);
    applyConfigToDom(currentAppConfig);
  } catch (err) {
    alert(`Failed to reset settings: ${err.message}`);
  }
}


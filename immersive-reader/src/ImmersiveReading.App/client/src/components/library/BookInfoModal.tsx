import React, { useState, useEffect } from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { useReaderStore } from '../../stores/useReaderStore';
import { useStudyPackStore } from '../../stores/useStudyPackStore';
import { rpc } from '../../services/rpc';
import type { BookDetail } from '../../types/api';
import {
  X,
  BookOpen,
  Sparkles,
  RotateCcw,
  Trash2,
  Save,
  Search,
  ExternalLink,
  CheckCircle,
  FileCode,
  FileText,
  BookCheck,
  Download,
  AlertTriangle,
  FileCheck,
  Loader2,
} from 'lucide-react';

export const BookInfoModal: React.FC = () => {
  const {
    activeBookDetail,
    isInspectorOpen,
    closeInspector,
    folders,
    tags,
    customFields: libraryCustomFields,
    fetchBooks,
  } = useLibraryStore();

  const openBook = useReaderStore((s) => s.openBook);
  const openStudyPack = useStudyPackStore((s) => s.openDrawer);

  const [activeTab, setActiveTab] = useState<'meta' | 'jiten' | 'chapters' | 'studypack' | 'opf'>('meta');
  const [isSaving, setIsSaving] = useState(false);
  const [statusMessage, setStatusMessage] = useState<{ text: string; type: 'success' | 'error' } | null>(null);

  // Form Fields
  const [title, setTitle] = useState('');
  const [sortTitle, setSortTitle] = useState('');
  const [seriesName, setSeriesName] = useState('');
  const [seriesIndex, setSeriesIndex] = useState('');
  const [authors, setAuthors] = useState('');
  const [authorSort, setAuthorSort] = useState('');
  const [isbn, setIsbn] = useState('');
  const [publishedDate, setPublishedDate] = useState('');
  const [publisher, setPublisher] = useState('');
  const [imprint, setImprint] = useState('');
  const [language, setLanguage] = useState('ja');
  const [writingMode, setWritingMode] = useState<'vertical-rl' | 'horizontal-tb'>('vertical-rl');
  const [customSortOrder, setCustomSortOrder] = useState('0');
  const [folderId, setFolderId] = useState('');
  const [selectedTagIds, setSelectedTagIds] = useState<string[]>([]);
  const [description, setDescription] = useState('');
  const [bookCustomFields, setBookCustomFields] = useState<Record<string, string>>({});

  // Jiten.moe Search State
  const [jitenQuery, setJitenQuery] = useState('');
  const [isJitenSearching, setIsJitenSearching] = useState(false);
  const [jitenResults, setJitenResults] = useState<any[]>([]);
  const [jitenStatus, setJitenStatus] = useState<string | null>(null);
  const [hasDeckCsv, setHasDeckCsv] = useState(false);
  const [deckCsvFileName, setDeckCsvFileName] = useState<string | null>(null);
  const [isDownloadingDeck, setIsDownloadingDeck] = useState(false);

  useEffect(() => {
    if (!activeBookDetail) return;
    const s = activeBookDetail.summary;
    setTitle(s.title || '');
    setSortTitle(s.sortTitle || '');
    setSeriesName(s.seriesName || '');
    setSeriesIndex(s.seriesIndex !== undefined && s.seriesIndex !== null ? String(s.seriesIndex) : '');
    setAuthors(s.authors || '');
    setAuthorSort(s.authorSort || '');
    setIsbn(s.isbn || '');
    setPublishedDate(s.publishedDate || '');
    setPublisher(s.publisher || '');
    setImprint(s.imprint || '');
    setLanguage(s.language || 'ja');
    setWritingMode(s.writingMode || 'vertical-rl');
    setCustomSortOrder(String(s.customSortOrder || 0));
    setFolderId(s.folderId || '');
    setHasDeckCsv(!!activeBookDetail.hasDeckCsv);
    setDeckCsvFileName(activeBookDetail.deckCsvFileName || null);
    
    // Extract tag IDs
    const currentTags = Array.isArray(s.tags)
      ? s.tags.map((t: any) => (typeof t === 'string' ? t : t.tagId))
      : [];
    setSelectedTagIds(currentTags);

    setDescription(s.description || '');

    // Extract custom fields
    const cf: Record<string, string> = {};
    if (s.customFields) {
      for (const [k, v] of Object.entries(s.customFields)) {
        cf[k] = typeof v === 'string' ? v : (v as any).valueText || '';
      }
    }
    setBookCustomFields(cf);

    setJitenQuery(s.seriesName || s.title || '');
    setJitenResults([]);
    setJitenStatus(null);
    setStatusMessage(null);
    setActiveTab('meta');
  }, [activeBookDetail]);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && isInspectorOpen) {
        closeInspector();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isInspectorOpen, closeInspector]);

  if (!isInspectorOpen || !activeBookDetail) return null;

  const book = activeBookDetail.summary;
  const chapters = activeBookDetail.chapters || [];
  const coverUrl = book.coverCachePath ? `ir-app://covers/${book.bookId}.jpg` : '';

  const handleSave = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    setIsSaving(true);
    setStatusMessage(null);

    try {
      const volRaw = seriesIndex.trim();
      const customFieldInputs = Object.entries(bookCustomFields).map(([k, v]) => ({
        fieldKey: k,
        valueText: v.trim(),
      }));

      const req = {
        bookId: book.bookId,
        title: title.trim(),
        sortTitle: sortTitle.trim(),
        seriesName: seriesName.trim(),
        seriesIndex: volRaw !== '' ? parseFloat(volRaw) : null,
        clearSeriesIndex: volRaw === '',
        authors: authors.trim(),
        authorSort: authorSort.trim(),
        isbn: isbn.trim(),
        publishedDate: publishedDate.trim(),
        publisher: publisher.trim(),
        imprint: imprint.trim(),
        language: language.trim(),
        writingMode,
        customSortOrder: parseInt(customSortOrder, 10) || 0,
        folderId: folderId || null,
        clearFolderId: !folderId,
        description: description.trim(),
        tagIds: selectedTagIds,
        customFields: customFieldInputs,
      };

      await rpc.call('library.updateBookMetadata', req);
      setStatusMessage({ text: 'Changes saved successfully!', type: 'success' });
      await fetchBooks();
      setTimeout(() => {
        closeInspector();
      }, 500);
    } catch (err: any) {
      console.error('Failed to update book metadata:', err);
      setStatusMessage({ text: `Failed to save: ${err.message || String(err)}`, type: 'error' });
    } finally {
      setIsSaving(false);
    }
  };

  const handleResetToOpf = async () => {
    if (!confirm(`Reset "${book.title}" to original EPUB metadata? Custom edits will be restored from OPF.`)) return;
    try {
      const reset = await rpc.call<BookDetail>('library.resetBookMetadataToOpf', { bookId: book.bookId });
      if (reset) {
        useLibraryStore.setState({ activeBookDetail: reset });
        setStatusMessage({ text: 'Restored to original OPF metadata.', type: 'success' });
        await fetchBooks();
      }
    } catch (err: any) {
      alert(`Failed to reset: ${err.message || String(err)}`);
    }
  };

  const handleDelete = async () => {
    if (!confirm(`Are you sure you want to remove "${book.title}" from your library?`)) return;
    try {
      await rpc.call('library.deleteBook', { bookId: book.bookId });
      closeInspector();
      await fetchBooks();
    } catch (err: any) {
      alert(`Failed to delete book: ${err.message || String(err)}`);
    }
  };

  const handleJitenSearch = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    const query = jitenQuery.trim();
    if (!query) return;

    setIsJitenSearching(true);
    setJitenStatus(null);
    try {
      const res = await rpc.call<any>('library.searchJitenMoe', { query });
      const items = res?.items || [];
      setJitenResults(items);
      if (items.length === 0) {
        setJitenStatus(`No direct matches found on jiten.moe for "${query}".`);
      }
    } catch (err: any) {
      setJitenStatus(`Search failed: ${err.message || String(err)}`);
    } finally {
      setIsJitenSearching(false);
    }
  };

  const applyJitenDeck = (deck: any) => {
    const updatedCf = { ...bookCustomFields };
    updatedCf['jiten_moe_url'] = deck.jitenUrl || `https://jiten.moe/decks/media/${deck.deckId}`;
    if (deck.difficulty !== undefined && deck.difficulty !== null) {
      updatedCf['jiten_difficulty'] = String(deck.difficulty);
    }
    setBookCustomFields(updatedCf);

    if (!description.trim() && deck.description) {
      setDescription(deck.description);
    }

    setJitenStatus(`Applied jiten.moe deck #${deck.deckId} (Diff: ${deck.difficulty || '—'}). Click Save Changes below to persist!`);
  };

  const extractDeckId = (val?: string): number | null => {
    if (!val) return null;
    const match = val.match(/(\d+)(?!.*\d)/);
    return match ? parseInt(match[1], 10) : null;
  };

  const handleDownloadDeck = async (deckId?: number, deckMeta?: any) => {
    const targetDeckId = deckId ?? extractDeckId(bookCustomFields['jiten_moe_url']);
    if (!targetDeckId) {
      setJitenStatus('No valid Jiten deck ID found to download. Search for a deck below.');
      return;
    }

    setIsDownloadingDeck(true);
    setJitenStatus('Downloading deck CSV from jiten.moe and attaching to book folder...');
    try {
      const res = await rpc.call<any>('library.downloadJitenDeckCsv', {
        bookId: book.bookId,
        deckId: targetDeckId,
      });

      if (res?.ok) {
        setHasDeckCsv(true);
        setDeckCsvFileName(res.fileName || 'deck.csv');
        if (deckMeta) {
          applyJitenDeck(deckMeta);
        }
        setJitenStatus(`⚡ Successfully downloaded and attached deck.csv (${Math.round((res.fileSizeBytes || 0) / 1024)} KB)!`);
        await fetchBooks();
      }
    } catch (err: any) {
      setJitenStatus(`Download failed: ${err.message || String(err)}`);
    } finally {
      setIsDownloadingDeck(false);
    }
  };

  const toggleTag = (tagId: string) => {
    setSelectedTagIds((prev) =>
      prev.includes(tagId) ? prev.filter((id) => id !== tagId) : [...prev, tagId]
    );
  };

  return (
    <div
      className="modal-backdrop fixed inset-0 bg-black/75 backdrop-blur-sm flex items-center justify-center z-50 p-4"
      onClick={closeInspector}
    >
      <div
        className="book-info-modal-card bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-xl shadow-2xl flex flex-col w-[960px] max-w-[96vw] h-[740px] max-h-[92vh] overflow-hidden select-none animate-in fade-in zoom-in-95 duration-150"
        onClick={(e) => e.stopPropagation()}
      >
        {/* MODAL HEADER */}
        <div className="book-info-modal-header flex items-center justify-between px-5 py-3 border-b border-[var(--border-color)] bg-black/20 gap-3">
          <div className="book-info-header-meta flex items-center space-x-3.5 min-w-0 flex-1">
            <div className="book-info-thumb w-11 h-15 rounded bg-black/40 overflow-hidden border border-[var(--border-color)] shrink-0 flex items-center justify-center">
              {coverUrl ? (
                <img src={coverUrl} alt={book.title} className="w-full h-full object-cover" />
              ) : (
                <FileText className="w-6 h-6 text-[var(--text-muted)] opacity-40" />
              )}
            </div>
            <div className="min-w-0 flex-1">
              <div className="flex items-center space-x-2 flex-wrap">
                <h3 className="text-sm font-bold text-[var(--text-primary)] truncate max-w-lg" title={book.title}>
                  {book.title}
                </h3>
                <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-muted)]">
                  {book.bookId}
                </span>
                {book.isMetadataEdited && (
                  <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded bg-amber-500/20 text-amber-300 border border-amber-500/30">
                    Edited (OPF Preserved)
                  </span>
                )}
              </div>
              <div className="text-xs text-[var(--text-muted)] truncate mt-0.5">
                {book.authors || 'Unknown Author'} {book.seriesName ? `• ${book.seriesName} #${book.seriesIndex ?? 1}` : ''}
              </div>
            </div>
          </div>

          <div className="flex items-center space-x-2 shrink-0">
            <button
              onClick={() => {
                closeInspector();
                openBook(book.bookId);
              }}
              className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--accent-color)] text-black text-xs font-semibold hover:opacity-90 shadow-sm"
            >
              <BookOpen className="w-3.5 h-3.5" />
              <span>Read Book</span>
            </button>
            <button
              onClick={closeInspector}
              className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]"
              title="Close (Esc)"
            >
              <X className="w-5 h-5" />
            </button>
          </div>
        </div>

        {/* TAB NAVIGATION */}
        <div className="book-info-tab-nav flex items-center px-4 bg-black/10 border-b border-[var(--border-color)] space-x-1 overflow-x-auto">
          <button
            type="button"
            onClick={() => setActiveTab('meta')}
            className={`book-info-tab-btn py-2.5 px-3.5 text-xs font-medium border-b-2 transition-colors ${
              activeTab === 'meta'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-semibold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            📋 Bibliographic Metadata
          </button>
          <button
            type="button"
            onClick={() => setActiveTab('jiten')}
            className={`book-info-tab-btn py-2.5 px-3.5 text-xs font-medium border-b-2 transition-colors ${
              activeTab === 'jiten'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-semibold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            🌐 jiten.moe & Custom Fields
          </button>
          <button
            type="button"
            onClick={() => setActiveTab('chapters')}
            className={`book-info-tab-btn py-2.5 px-3.5 text-xs font-medium border-b-2 transition-colors ${
              activeTab === 'chapters'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-semibold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            📑 Chapters ({chapters.length})
          </button>
          <button
            type="button"
            onClick={() => setActiveTab('studypack')}
            className={`book-info-tab-btn py-2.5 px-3.5 text-xs font-medium border-b-2 transition-colors ${
              activeTab === 'studypack'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-semibold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            📚 Study Pack
          </button>
          <button
            type="button"
            onClick={() => setActiveTab('opf')}
            className={`book-info-tab-btn py-2.5 px-3.5 text-xs font-medium border-b-2 transition-colors ${
              activeTab === 'opf'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-semibold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            ℹ️ File & OPF Info
          </button>
        </div>

        {/* STATUS BANNER */}
        {statusMessage && (
          <div
            className={`px-4 py-2 text-xs flex items-center justify-between ${
              statusMessage.type === 'success'
                ? 'bg-emerald-950/60 text-emerald-300 border-b border-emerald-800'
                : 'bg-red-950/60 text-red-300 border-b border-red-800'
            }`}
          >
            <span>{statusMessage.text}</span>
            <button onClick={() => setStatusMessage(null)} className="opacity-70 hover:opacity-100">
              ✕
            </button>
          </div>
        )}

        {/* SCROLLABLE BODY */}
        <div className="book-info-body flex-1 overflow-y-auto p-5 text-xs text-[var(--text-primary)]">
          {/* TAB 1: METADATA */}
          {activeTab === 'meta' && (
            <div className="space-y-4 max-w-3xl">
              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Title
                </label>
                <input
                  type="text"
                  value={title}
                  onChange={(e) => setTitle(e.target.value)}
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  required
                />
              </div>

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Sort Title (Kana / Reading Sort)
                </label>
                <input
                  type="text"
                  value={sortTitle}
                  onChange={(e) => setSortTitle(e.target.value)}
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                />
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Series Name
                  </label>
                  <input
                    type="text"
                    value={seriesName}
                    onChange={(e) => setSeriesName(e.target.value)}
                    placeholder="e.g. クークの森の学校"
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Volume #
                  </label>
                  <input
                    type="number"
                    step="0.1"
                    value={seriesIndex}
                    onChange={(e) => setSeriesIndex(e.target.value)}
                    placeholder="1.0"
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Author(s)
                  </label>
                  <input
                    type="text"
                    value={authors}
                    onChange={(e) => setAuthors(e.target.value)}
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Author Sort (Kana)
                  </label>
                  <input
                    type="text"
                    value={authorSort}
                    onChange={(e) => setAuthorSort(e.target.value)}
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    ISBN-13 / ISBN-10
                  </label>
                  <input
                    type="text"
                    value={isbn}
                    onChange={(e) => setIsbn(e.target.value)}
                    placeholder="978..."
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Published Date
                  </label>
                  <input
                    type="text"
                    value={publishedDate}
                    onChange={(e) => setPublishedDate(e.target.value)}
                    placeholder="YYYY-MM-DD"
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Publisher
                  </label>
                  <input
                    type="text"
                    value={publisher}
                    onChange={(e) => setPublisher(e.target.value)}
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Imprint / Label
                  </label>
                  <input
                    type="text"
                    value={imprint}
                    onChange={(e) => setImprint(e.target.value)}
                    placeholder="e.g. 電撃文庫"
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Language
                  </label>
                  <input
                    type="text"
                    value={language}
                    onChange={(e) => setLanguage(e.target.value)}
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Writing Mode
                  </label>
                  <select
                    value={writingMode}
                    onChange={(e) => setWritingMode(e.target.value as any)}
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  >
                    <option value="vertical-rl">Vertical (vertical-rl)</option>
                    <option value="horizontal-tb">Horizontal (horizontal-tb)</option>
                  </select>
                </div>
              </div>

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Virtual Collection / Folder
                </label>
                <select
                  value={folderId}
                  onChange={(e) => setFolderId(e.target.value)}
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                >
                  <option value="">(Uncategorized / No Folder)</option>
                  {folders.map((f) => (
                    <option key={f.folderId} value={f.folderId}>
                      {f.name}
                    </option>
                  ))}
                </select>
              </div>

              {tags.length > 0 && (
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1.5">
                    Manual Tags
                  </label>
                  <div className="flex flex-wrap gap-1.5">
                    {tags.map((t) => {
                      const isChecked = selectedTagIds.includes(t.tagId);
                      return (
                        <button
                          key={t.tagId}
                          type="button"
                          onClick={() => toggleTag(t.tagId)}
                          className={`px-2.5 py-1 rounded text-xs border transition-colors flex items-center space-x-1 ${
                            isChecked
                              ? 'bg-[var(--accent-color)] border-[var(--accent-color)] text-black font-semibold'
                              : 'bg-[var(--bg-primary)] border-[var(--border-color)] text-[var(--text-muted)] hover:border-[var(--text-primary)]'
                          }`}
                        >
                          <span>{isChecked ? '✓' : '＋'}</span>
                          <span>{t.name}</span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              )}

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Description / Synopsis
                </label>
                <textarea
                  rows={4}
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  placeholder="Book synopsis or notes..."
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)] resize-y"
                />
              </div>
            </div>
          )}

          {/* TAB 2: JITEN.MOE & CUSTOM FIELDS */}
          {activeTab === 'jiten' && (
            <div className="space-y-5 max-w-3xl">
              {/* Jiten Integration Card */}
              <div className="jiten-integration-card p-4 rounded-lg border border-sky-500/30 bg-sky-950/20 space-y-3">
                <div className="flex items-center justify-between">
                  <div className="flex items-center space-x-2">
                    <span className="font-semibold text-sm text-[var(--text-primary)]">🌐 Jiten.moe Media Integration</span>
                    <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-sky-600 text-white uppercase">
                      JITEN.MOE
                    </span>
                  </div>
                  {bookCustomFields['jiten_moe_url'] && (
                    <a
                      href={bookCustomFields['jiten_moe_url']}
                      target="_blank"
                      rel="noreferrer"
                      className="text-xs text-[var(--accent-color)] hover:underline inline-flex items-center space-x-1"
                    >
                      <span>Open Deck</span>
                      <ExternalLink className="w-3 h-3" />
                    </a>
                  )}
                </div>

                {/* 1-Click Deck Status & Download */}
                <div className="p-3 rounded bg-black/30 border border-[var(--border-color)] flex items-center justify-between">
                  <div className="flex items-center space-x-2.5">
                    {hasDeckCsv ? (
                      <FileCheck className="w-5 h-5 text-emerald-400 shrink-0" />
                    ) : (
                      <AlertTriangle className="w-5 h-5 text-amber-400 shrink-0" />
                    )}
                    <div>
                      <div className="font-semibold text-xs text-[var(--text-primary)] flex items-center space-x-2">
                        <span>Vocabulary Deck:</span>
                        {hasDeckCsv ? (
                          <span className="text-emerald-400 font-bold">✓ {deckCsvFileName || 'deck.csv'} Attached</span>
                        ) : (
                          <span className="text-amber-400 font-bold">⚠️ No deck.csv in book folder</span>
                        )}
                      </div>
                      <div className="text-[11px] text-[var(--text-muted)]">
                        {hasDeckCsv
                          ? 'Enrichment pipeline and study packs will use this deck for vocabulary & pitch data.'
                          : 'Download the vocabulary deck below to enable frequency & JLPT metadata, definitions, and pitch accent.'}
                      </div>
                    </div>
                  </div>

                  <div>
                    {extractDeckId(bookCustomFields['jiten_moe_url']) ? (
                      <button
                        type="button"
                        onClick={() => handleDownloadDeck()}
                        disabled={isDownloadingDeck}
                        className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-50 shadow-sm"
                      >
                        {isDownloadingDeck ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Download className="w-3.5 h-3.5" />}
                        <span>{hasDeckCsv ? '⚡ Re-download CSV Deck' : '⚡ 1-Click Download & Attach Deck'}</span>
                      </button>
                    ) : (
                      !hasDeckCsv && (
                        <span className="text-[10px] text-[var(--text-muted)] italic">
                          Search below to 1-click install
                        </span>
                      )
                    )}
                  </div>
                </div>

                <form onSubmit={handleJitenSearch} className="flex space-x-2">
                  <input
                    type="text"
                    value={jitenQuery}
                    onChange={(e) => setJitenQuery(e.target.value)}
                    placeholder="Search series or book title on jiten.moe..."
                    className="flex-1 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                  <button
                    type="submit"
                    disabled={isJitenSearching}
                    className="flex items-center space-x-1 px-3 py-1.5 rounded bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-50"
                  >
                    <Search className="w-3.5 h-3.5" />
                    <span>{isJitenSearching ? 'Searching...' : 'Search'}</span>
                  </button>
                </form>

                {jitenStatus && (
                  <div className="p-2.5 rounded bg-black/20 border border-[var(--border-color)] text-xs text-[var(--text-muted)] flex items-center space-x-2">
                    <CheckCircle className="w-4 h-4 text-emerald-400 shrink-0" />
                    <span>{jitenStatus}</span>
                  </div>
                )}

                {jitenResults.length > 0 && (
                  <div className="space-y-2 max-h-72 overflow-y-auto pr-1 pt-1">
                    {jitenResults.map((item: any) => (
                      <div
                        key={item.deckId}
                        className="p-3 rounded bg-[var(--bg-surface)] border border-[var(--border-color)] hover:border-[var(--accent-color)] transition-colors flex space-x-3 items-start"
                      >
                        {item.coverUrl && (
                          <img
                            src={item.coverUrl}
                            alt=""
                            className="w-12 h-16 rounded object-cover bg-black/40 shrink-0"
                            onError={(e) => ((e.target as HTMLElement).style.display = 'none')}
                          />
                        )}
                        <div className="flex-1 min-w-0">
                          <div className="font-semibold text-xs text-[var(--text-primary)] truncate">
                            {item.originalTitle}
                          </div>
                          <div className="text-[11px] text-[var(--text-muted)] truncate">
                            {[item.romajiTitle, item.englishTitle].filter(Boolean).join(' • ')}
                          </div>
                          <div className="flex items-center space-x-2.5 text-[10px] text-[var(--text-muted)] mt-1.5">
                            {item.difficulty !== undefined && (
                              <span className="px-1.5 py-0.5 rounded bg-amber-500/20 text-amber-300 font-bold">
                                ★ Diff: {item.difficulty}/10
                              </span>
                            )}
                            <span>Words: {(item.wordCount || 0).toLocaleString()}</span>
                            <span>Chars: {(item.characterCount || 0).toLocaleString()}</span>
                            {item.childrenDeckCount > 0 && (
                              <span className="text-[var(--accent-color)]">{item.childrenDeckCount} Volumes</span>
                            )}
                          </div>
                          <div className="flex items-center space-x-2 mt-2">
                            <button
                              type="button"
                              onClick={() => handleDownloadDeck(item.deckId, item)}
                              disabled={isDownloadingDeck}
                              className="px-2.5 py-1 rounded bg-[var(--accent-color)] text-black text-[11px] font-semibold hover:opacity-90 flex items-center space-x-1"
                            >
                              {isDownloadingDeck ? <Loader2 className="w-3 h-3 animate-spin" /> : <Download className="w-3 h-3" />}
                              <span>⚡ 1-Click Download CSV & Apply</span>
                            </button>
                            <button
                              type="button"
                              onClick={() => applyJitenDeck(item)}
                              className="px-2.5 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-primary)] text-[11px] hover:border-[var(--accent-color)]"
                            >
                              Apply Metadata Only
                            </button>
                            {item.jitenUrl && (
                              <a
                                href={item.jitenUrl}
                                target="_blank"
                                rel="noreferrer"
                                className="text-[11px] text-[var(--accent-color)] hover:underline inline-flex items-center space-x-0.5 ml-1"
                              >
                                <span>View on jiten.moe</span>
                                <ExternalLink className="w-2.5 h-2.5" />
                              </a>
                            )}
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>

              {/* Custom Fields List */}
              <div className="space-y-3">
                <div className="font-semibold text-xs uppercase tracking-wider text-[var(--text-muted)]">
                  Custom Library Fields
                </div>
                {libraryCustomFields.length === 0 && Object.keys(bookCustomFields).length === 0 ? (
                  <p className="text-xs text-[var(--text-muted)]">No custom fields defined in this library.</p>
                ) : (
                  <div className="grid grid-cols-2 gap-3">
                    {libraryCustomFields.map((field) => (
                      <div key={field.fieldId}>
                        <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                          {field.name} ({field.fieldId})
                        </label>
                        <input
                          type="text"
                          value={bookCustomFields[field.fieldId] || ''}
                          onChange={(e) =>
                            setBookCustomFields({ ...bookCustomFields, [field.fieldId]: e.target.value })
                          }
                          className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                        />
                      </div>
                    ))}
                    {Object.entries(bookCustomFields)
                      .filter(([k]) => !libraryCustomFields.some((f) => f.fieldId === k))
                      .map(([k, v]) => (
                        <div key={k}>
                          <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                            {k}
                          </label>
                          <input
                            type="text"
                            value={v}
                            onChange={(e) => setBookCustomFields({ ...bookCustomFields, [k]: e.target.value })}
                            className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                          />
                        </div>
                      ))}
                  </div>
                )}
              </div>
            </div>
          )}

          {/* TAB 3: CHAPTERS */}
          {activeTab === 'chapters' && (
            <div className="space-y-3 max-w-3xl">
              <div className="flex items-center justify-between text-xs text-[var(--text-muted)]">
                <span>Spine Chapters ({chapters.length})</span>
                <span>Total Characters: {book.totalCharCount?.toLocaleString() || 0}</span>
              </div>
              <div className="space-y-1.5 max-h-[460px] overflow-y-auto pr-1">
                {chapters.map((ch, idx) => (
                  <div
                    key={ch.spineHref || idx}
                    className="p-2.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] flex items-center justify-between hover:border-[var(--accent-color)] transition-colors group"
                  >
                    <div className="min-w-0 flex-1 pr-3">
                      <div className="font-medium text-xs text-[var(--text-primary)] truncate">
                        {ch.chapterIndex + 1}. {ch.title || 'Untitled Chapter'}
                      </div>
                      <div className="text-[10px] text-[var(--text-muted)] font-mono truncate">{ch.spineHref}</div>
                    </div>
                    <div className="flex items-center space-x-3 shrink-0">
                      <span className="text-[10px] text-[var(--text-muted)]">
                        {ch.charCount?.toLocaleString() || 0} chars
                      </span>
                      <button
                        type="button"
                        onClick={() => {
                          closeInspector();
                          openBook(book.bookId, ch.spineHref);
                        }}
                        className="px-2.5 py-1 rounded bg-[var(--accent-color)] text-black text-[11px] font-semibold opacity-0 group-hover:opacity-100 transition-opacity"
                      >
                        Read Chapter →
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* TAB 4: STUDY PACK */}
          {activeTab === 'studypack' && (
            <div className="space-y-4 max-w-2xl">
              <div className="p-4 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] space-y-3">
                <div className="flex items-center justify-between">
                  <div className="flex items-center space-x-2">
                    <Sparkles className="w-4 h-4 text-emerald-400" />
                    <span className="font-semibold text-xs">Offline Study Pack Status</span>
                  </div>
                  <span
                    className={`text-[10px] font-bold px-2 py-0.5 rounded ${
                      book.hasStudyPack
                        ? 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/30'
                        : 'bg-slate-700 text-slate-300'
                    }`}
                  >
                    {book.hasStudyPack ? 'READY' : 'NOT GENERATED'}
                  </span>
                </div>

                {!hasDeckCsv && (
                  <div className="p-2.5 rounded bg-amber-500/10 border border-amber-500/30 text-amber-300 text-xs flex items-center justify-between">
                    <div className="flex items-center space-x-2">
                      <AlertTriangle className="w-4 h-4 shrink-0 text-amber-400" />
                      <span>No vocabulary deck CSV attached. Vocabulary cards & pitch accents require a deck.csv.</span>
                    </div>
                    <button
                      type="button"
                      onClick={() => setActiveTab('jiten')}
                      className="px-2.5 py-1 rounded bg-amber-500 text-black font-semibold text-[11px] hover:opacity-90 shrink-0"
                    >
                      1-Click Download Deck ➔
                    </button>
                  </div>
                )}

                <p className="text-xs text-[var(--text-muted)] leading-relaxed">
                  Study packs include offline Kanji Master PDFs, 20-word Vocabulary set PDFs, and Anki-compatible
                  flashcards generated deterministically by the Python enrichment bridge.
                </p>

                <div className="flex flex-wrap gap-2 pt-2">
                  <button
                    type="button"
                    onClick={() => {
                      closeInspector();
                      openStudyPack(book.bookId);
                    }}
                    className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 shadow-sm"
                  >
                    <BookCheck className="w-3.5 h-3.5" />
                    <span>Open Study Cards & Sets</span>
                  </button>
                  <button
                    type="button"
                    onClick={async () => {
                      try {
                        await rpc.call('studyPack.generate', { bookId: book.bookId });
                        alert('Study pack generation queued/completed!');
                        await fetchBooks();
                      } catch (err: any) {
                        alert(`Generation failed: ${err.message || String(err)}`);
                      }
                    }}
                    className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--bg-surface)] border border-[var(--border-color)] text-xs hover:border-[var(--accent-color)]"
                  >
                    <RotateCcw className="w-3.5 h-3.5" />
                    <span>Generate / Rebuild Pack</span>
                  </button>
                </div>
              </div>
            </div>
          )}

          {/* TAB 5: OPF INFO */}
          {activeTab === 'opf' && (
            <div className="space-y-3 max-w-3xl">
              <div className="grid grid-cols-2 gap-3 text-xs">
                <div className="p-3 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] space-y-1">
                  <div className="text-[10px] text-[var(--text-muted)] uppercase font-semibold">File Location</div>
                  <div className="font-mono text-[11px] truncate" title={book.folderPath}>
                    {book.folderPath || '—'}
                  </div>
                  <div className="text-[10px] text-[var(--text-muted)] font-mono">{book.epubFileName}</div>
                </div>

                <div className="p-3 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] space-y-1">
                  <div className="text-[10px] text-[var(--text-muted)] uppercase font-semibold">SHA256 & Size</div>
                  <div className="font-mono text-[11px] truncate" title={book.epubSha256}>
                    {book.epubSha256}
                  </div>
                  <div className="text-[10px] text-[var(--text-muted)]">
                    {book.fileSizeBytes ? `${(book.fileSizeBytes / 1024 / 1024).toFixed(2)} MB` : 'Unknown'}
                  </div>
                </div>
              </div>

              {activeBookDetail.opfOriginal && (
                <div className="space-y-1.5">
                  <div className="text-[10px] font-semibold text-[var(--text-muted)] uppercase flex items-center space-x-1">
                    <FileCode className="w-3.5 h-3.5" />
                    <span>Original Raw OPF Metadata</span>
                  </div>
                  <pre className="p-3 rounded bg-black/40 border border-[var(--border-color)] text-[11px] font-mono text-[var(--text-muted)] max-h-60 overflow-y-auto whitespace-pre-wrap leading-relaxed select-text">
                    {activeBookDetail.opfOriginal.rawOpfXml || JSON.stringify(activeBookDetail.opfOriginal, null, 2)}
                  </pre>
                </div>
              )}
            </div>
          )}
        </div>

        {/* MODAL FOOTER */}
        <div className="book-info-modal-footer flex items-center justify-between px-5 py-3 border-t border-[var(--border-color)] bg-black/20">
          <div className="flex items-center space-x-2">
            <button
              type="button"
              onClick={handleResetToOpf}
              className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:border-amber-500/50"
              title="Restore original EPUB metadata from OPF"
            >
              <RotateCcw className="w-3.5 h-3.5" />
              <span>Reset to OPF</span>
            </button>
            <button
              type="button"
              onClick={handleDelete}
              className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-red-950/40 border border-red-900/60 text-xs text-red-300 hover:bg-red-900/60"
              title="Delete book from library"
            >
              <Trash2 className="w-3.5 h-3.5" />
              <span>Delete Book</span>
            </button>
          </div>

          <div className="flex items-center space-x-2">
            <button
              type="button"
              onClick={closeInspector}
              className="px-4 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)]"
            >
              Close
            </button>
            <button
              type="button"
              onClick={handleSave}
              disabled={isSaving}
              className="flex items-center space-x-1.5 px-4 py-1.5 rounded bg-[var(--accent-color)] text-black text-xs font-semibold hover:opacity-90 disabled:opacity-50 shadow-sm"
            >
              <Save className="w-3.5 h-3.5" />
              <span>{isSaving ? 'Saving...' : 'Save Changes'}</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};

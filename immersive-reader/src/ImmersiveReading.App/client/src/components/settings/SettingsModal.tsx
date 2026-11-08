import React, { useState, useEffect } from 'react';
import { useConfigStore } from '../../stores/useConfigStore';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { rpc } from '../../services/rpc';
import {
  X,
  Palette,
  BookOpen,
  Bot,
  Library,
  Folder,
  Layers,
  Check,
  AlertCircle,
  Loader2,
  RotateCcw,
  Save,
  Zap,
  Download,
  RefreshCw,
  HardDrive,
  AlertTriangle,
  Trash2,
} from 'lucide-react';
import type { AppConfig, DictionaryItem, DictionaryPreset, PythonDetectInfo } from '../../types/api';
import { PluginSettingsTab } from './PluginSettingsTab';

const THEME_PRESETS: Record<string, {
  bgPrimary: string;
  bgSurface: string;
  textPrimary: string;
  textMuted: string;
  borderColor: string;
  accentColor: string;
  rubyColor: string;
}> = {
  slate: {
    bgPrimary: '#0f172a',
    bgSurface: '#1e293b',
    textPrimary: '#f8fafc',
    textMuted: '#94a3b8',
    borderColor: '#334155',
    accentColor: '#38bdf8',
    rubyColor: '#e2e8f0',
  },
  'warm-paper': {
    bgPrimary: '#faf8f5',
    bgSurface: '#f2ece4',
    textPrimary: '#2d2720',
    textMuted: '#7c7267',
    borderColor: '#dcd3c6',
    accentColor: '#b45309',
    rubyColor: '#78350f',
  },
  'tokyo-night': {
    bgPrimary: '#1a1b26',
    bgSurface: '#24283b',
    textPrimary: '#c0caf5',
    textMuted: '#7aa2f7',
    borderColor: '#414868',
    accentColor: '#bb9af7',
    rubyColor: '#f7768e',
  },
  'e-ink': {
    bgPrimary: '#ffffff',
    bgSurface: '#f5f5f5',
    textPrimary: '#000000',
    textMuted: '#555555',
    borderColor: '#cccccc',
    accentColor: '#000000',
    rubyColor: '#333333',
  },
};

export const SettingsModal: React.FC = () => {
  const { config, isSettingsOpen, closeSettings, activeTab, setActiveTab, saveConfig, resetDefaults } =
    useConfigStore();
  const { folders, tags } = useLibraryStore();

  const [localConfig, setLocalConfig] = useState<AppConfig | null>(null);
  const [isSaving, setIsSaving] = useState(false);
  const [ankiTestStatus, setAnkiTestStatus] = useState<{ reachable: boolean; message: string } | null>(null);
  const [isTestingAnki, setIsTestingAnki] = useState(false);

  // Dictionaries state
  const [installedDicts, setInstalledDicts] = useState<DictionaryItem[]>([]);
  const [dictPresets, setDictPresets] = useState<DictionaryPreset[]>([]);
  const [installingDictId, setInstallingDictId] = useState<string | null>(null);
  const [dictStatusMessage, setDictStatusMessage] = useState<{ text: string; ok: boolean } | null>(null);
  const [customZipInput, setCustomZipInput] = useState('');

  // Python state
  const [pythonInfo, setPythonInfo] = useState<PythonDetectInfo | null>(null);
  const [isDetectingPython, setIsDetectingPython] = useState(false);
  const [isSettingUpPython, setIsSettingUpPython] = useState(false);
  const [pythonStatusMessage, setPythonStatusMessage] = useState<{ text: string; ok: boolean } | null>(null);

  // Danger zone clear library state
  const [showClearConfirm, setShowClearConfirm] = useState(false);
  const [clearInputText, setClearInputText] = useState('');
  const [clearRemoveFolders, setClearRemoveFolders] = useState(true);
  const [clearResetVocab, setClearResetVocab] = useState(false);
  const [isClearing, setIsClearing] = useState(false);
  const [clearStatusMessage, setClearStatusMessage] = useState<{ text: string; ok: boolean } | null>(null);

  const handleClearLibrary = async () => {
    if (clearInputText.trim().toUpperCase() !== 'CLEAR') return;
    setIsClearing(true);
    setClearStatusMessage(null);
    try {
      const res = await rpc.call<any>('library.clearLibrary', {
        removeFolders: clearRemoveFolders,
        resetVocabulary: clearResetVocab,
      });
      if (res?.ok) {
        setClearStatusMessage({
          text: `Library cleared successfully (${res.result?.booksDeleted ?? 0} books, ${res.result?.foldersDeleted ?? 0} folders removed).`,
          ok: true,
        });
        setShowClearConfirm(false);
        setClearInputText('');
        await useLibraryStore.getState().loadBootstrap();
      } else {
        setClearStatusMessage({ text: 'Failed to clear library.', ok: false });
      }
    } catch (err: any) {
      setClearStatusMessage({ text: err?.message || 'Error occurred while clearing library.', ok: false });
    } finally {
      setIsClearing(false);
    }
  };

  const loadDictionaries = async () => {
    try {
      const [listRes, presetsRes] = await Promise.all([
        rpc.call<any>('dictionaries.list'),
        rpc.call<any>('dictionaries.getPresets'),
      ]);
      if (listRes?.dictionaries) {
        setInstalledDicts(listRes.dictionaries);
      }
      if (presetsRes?.presets) {
        setDictPresets(presetsRes.presets);
      }
    } catch {
      // Ignore
    }
  };

  const loadPythonInfo = async () => {
    setIsDetectingPython(true);
    try {
      const res = await rpc.call<PythonDetectInfo>('config.detectPython');
      setPythonInfo(res);
    } catch {
      // Ignore
    } finally {
      setIsDetectingPython(false);
    }
  };

  useEffect(() => {
    if (config) {
      setLocalConfig(JSON.parse(JSON.stringify(config)));
    }
  }, [config, isSettingsOpen]);

  useEffect(() => {
    if (!isSettingsOpen) return;
    if (activeTab === 'dictionaries') {
      loadDictionaries();
    } else if (activeTab === 'general') {
      loadPythonInfo();
    }
  }, [isSettingsOpen, activeTab]);

  if (!isSettingsOpen || !localConfig) return null;

  const handleSave = async () => {
    setIsSaving(true);
    try {
      await saveConfig(localConfig);
      closeSettings();
    } finally {
      setIsSaving(false);
    }
  };

  const handleResetTab = async () => {
    if (!confirm(`Reset settings in the '${activeTab}' tab to defaults?`)) return;
    await resetDefaults(activeTab);
  };

  const handleResetAll = async () => {
    if (!confirm('Are you sure you want to reset ALL application settings to factory defaults?')) return;
    await resetDefaults();
  };

  const handleThemePresetChange = (preset: string) => {
    if (THEME_PRESETS[preset]) {
      setLocalConfig({
        ...localConfig,
        appearance: {
          ...localConfig.appearance,
          themePreset: preset,
          ...THEME_PRESETS[preset],
        },
      });
    } else {
      setLocalConfig({
        ...localConfig,
        appearance: {
          ...localConfig.appearance,
          themePreset: 'custom',
        },
      });
    }
  };

  const handleTestAnki = async () => {
    setIsTestingAnki(true);
    setAnkiTestStatus(null);
    try {
      const res = await rpc.call<any>('anki.ping', {
        url: localConfig.dictionaries.ankiConnectUrl || 'http://127.0.0.1:8765',
      });
      if (res?.reachable) {
        setAnkiTestStatus({
          reachable: true,
          message: `Connected to Anki! (AnkiConnect version ${res.version})`,
        });
      } else {
        setAnkiTestStatus({
          reachable: false,
          message: res?.error || 'Could not connect. Is Anki open with AnkiConnect installed?',
        });
      }
    } catch (err: any) {
      setAnkiTestStatus({
        reachable: false,
        message: err.message || 'Failed to ping AnkiConnect.',
      });
    } finally {
      setIsTestingAnki(false);
    }
  };

  const toggleExcludedFolder = (folderId: string) => {
    const current = localConfig.library?.excludedFolderIds || [];
    const next = current.includes(folderId) ? current.filter((id) => id !== folderId) : [...current, folderId];
    setLocalConfig({
      ...localConfig,
      library: {
        ...localConfig.library,
        excludedFolderIds: next,
      },
    });
  };

  const toggleExcludedTag = (tagId: string) => {
    const current = localConfig.library?.excludedTagIds || [];
    const next = current.includes(tagId) ? current.filter((id) => id !== tagId) : [...current, tagId];
    setLocalConfig({
      ...localConfig,
      library: {
        ...localConfig.library,
        excludedTagIds: next,
      },
    });
  };

  const handleInstallDictionary = async (preset: DictionaryPreset) => {
    setInstallingDictId(preset.id);
    setDictStatusMessage({ text: `Downloading and installing ${preset.title}...`, ok: true });
    try {
      const res = await rpc.call<any>('dictionaries.downloadAndInstall', {
        presetId: preset.id,
        priorityOrder: preset.defaultPriority,
      });
      if (res?.ok) {
        setDictStatusMessage({
          text: `Successfully installed ${res.title} (${(res.termsCount || 0).toLocaleString()} terms, ${(res.kanjiCount || 0).toLocaleString()} kanji)!`,
          ok: true,
        });
        await loadDictionaries();
      }
    } catch (err: any) {
      setDictStatusMessage({ text: `Install failed: ${err.message || String(err)}`, ok: false });
    } finally {
      setInstallingDictId(null);
    }
  };

  const handleInstallCustomDictionary = async () => {
    if (!customZipInput.trim()) return;
    setInstallingDictId('custom');
    setDictStatusMessage({ text: `Downloading / importing custom Yomitan dictionary...`, ok: true });
    try {
      const res = await rpc.call<any>('dictionaries.downloadAndInstall', {
        url: customZipInput.trim(),
        priorityOrder: 50,
      });
      if (res?.ok) {
        setDictStatusMessage({
          text: `Successfully installed ${res.title} (${(res.termsCount || 0).toLocaleString()} terms)!`,
          ok: true,
        });
        setCustomZipInput('');
        await loadDictionaries();
      }
    } catch (err: any) {
      setDictStatusMessage({ text: `Install failed: ${err.message || String(err)}`, ok: false });
    } finally {
      setInstallingDictId(null);
    }
  };

  const handleAutoDetectPython = async () => {
    setIsDetectingPython(true);
    setPythonStatusMessage(null);
    try {
      const res = await rpc.call<PythonDetectInfo>('config.detectPython');
      setPythonInfo(res);
      if (res?.found && res.pythonPath) {
        setLocalConfig({
          ...localConfig,
          directories: {
            ...localConfig.directories,
            pythonExecutablePath: res.pythonPath,
          },
        });
        setPythonStatusMessage({
          text: `Detected ${res.version || 'Python'} at ${res.pythonPath}! Click 'Save Changes' to persist.`,
          ok: true,
        });
      } else {
        setPythonStatusMessage({
          text: 'No Python 3.12+ installation detected in standard locations.',
          ok: false,
        });
      }
    } catch (err: any) {
      setPythonStatusMessage({ text: `Detection error: ${err.message || String(err)}`, ok: false });
    } finally {
      setIsDetectingPython(false);
    }
  };

  const handleSetupPortablePython = async () => {
    setIsSettingUpPython(true);
    setPythonStatusMessage({ text: 'Setting up portable Python runtime...', ok: true });
    try {
      const res = await rpc.call<any>('config.setupPortablePython');
      if (res?.ok) {
        setLocalConfig({
          ...localConfig,
          directories: {
            ...localConfig.directories,
            pythonExecutablePath: res.pythonPath,
          },
        });
        setPythonStatusMessage({
          text: res.message || `Activated portable Python (${res.version})!`,
          ok: true,
        });
        await loadPythonInfo();
      }
    } catch (err: any) {
      setPythonStatusMessage({ text: `Setup failed: ${err.message || String(err)}`, ok: false });
    } finally {
      setIsSettingUpPython(false);
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 bg-black/70 backdrop-blur-sm flex items-center justify-center p-4 select-none"
      onClick={closeSettings}
    >
      <div
        className="w-full max-w-3xl bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-xl shadow-2xl flex flex-col overflow-hidden max-h-[92vh] text-xs text-[var(--text-primary)] animate-in fade-in zoom-in-95 duration-150"
        onClick={(e) => e.stopPropagation()}
      >
        {/* MODAL HEADER */}
        <div className="p-4 border-b border-[var(--border-color)] flex items-center justify-between bg-black/20">
          <div className="flex items-center space-x-2.5">
            <span className="text-xl">⚙️</span>
            <div>
              <h3 className="text-sm font-bold text-[var(--text-primary)]">Global Application Settings</h3>
              <p className="text-[11px] text-[var(--text-muted)]">
                Centralized configuration for directories, library shelves, themes, typography, and AI providers
              </p>
            </div>
          </div>
          <button
            onClick={closeSettings}
            className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]"
            title="Close (Esc)"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        {/* TAB NAVIGATION */}
        <div className="flex border-b border-[var(--border-color)] bg-black/10 px-4 text-xs overflow-x-auto">
          <button
            onClick={() => setActiveTab('general')}
            className={`flex items-center space-x-1.5 py-2.5 px-3 border-b-2 font-medium transition-colors ${
              activeTab === 'general'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            <Folder className="w-3.5 h-3.5" />
            <span>Directories</span>
          </button>
          <button
            onClick={() => setActiveTab('library')}
            className={`flex items-center space-x-1.5 py-2.5 px-3 border-b-2 font-medium transition-colors ${
              activeTab === 'library'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            <Layers className="w-3.5 h-3.5" />
            <span>Library & Shelves</span>
          </button>
          <button
            onClick={() => setActiveTab('appearance')}
            className={`flex items-center space-x-1.5 py-2.5 px-3 border-b-2 font-medium transition-colors ${
              activeTab === 'appearance'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            <Palette className="w-3.5 h-3.5" />
            <span>Appearance</span>
          </button>
          <button
            onClick={() => setActiveTab('reader')}
            className={`flex items-center space-x-1.5 py-2.5 px-3 border-b-2 font-medium transition-colors ${
              activeTab === 'reader'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            <BookOpen className="w-3.5 h-3.5" />
            <span>Reader</span>
          </button>
          <button
            onClick={() => setActiveTab('llm')}
            className={`flex items-center space-x-1.5 py-2.5 px-3 border-b-2 font-medium transition-colors ${
              activeTab === 'llm'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            <Bot className="w-3.5 h-3.5" />
            <span>Plugins</span>
          </button>
          <button
            onClick={() => setActiveTab('dictionaries')}
            className={`flex items-center space-x-1.5 py-2.5 px-3 border-b-2 font-medium transition-colors ${
              activeTab === 'dictionaries'
                ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
                : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
            }`}
          >
            <Library className="w-3.5 h-3.5" />
            <span>Dictionaries</span>
          </button>
        </div>

        {/* TAB PANES */}
        <div className="flex-1 overflow-y-auto p-6 space-y-4">
          {/* TAB: DIRECTORIES */}
          {activeTab === 'general' && (
            <div className="space-y-4 max-w-2xl">
              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Default Library Scan Directory
                </label>
                <input
                  type="text"
                  value={localConfig.directories.defaultScanDirectory}
                  onChange={(e) =>
                    setLocalConfig({
                      ...localConfig,
                      directories: { ...localConfig.directories, defaultScanDirectory: e.target.value },
                    })
                  }
                  placeholder="e.g. C:\Books or /mnt/storage/epubs"
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                />
                <span className="text-[10px] text-[var(--text-muted)] mt-1 block">
                  Initial folder opened when clicking '⚡ Scan Folder' in the library.
                </span>
              </div>

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Python Extractor Root Directory
                </label>
                <input
                  type="text"
                  value={localConfig.directories.extractorRootDirectory}
                  onChange={(e) =>
                    setLocalConfig({
                      ...localConfig,
                      directories: { ...localConfig.directories, extractorRootDirectory: e.target.value },
                    })
                  }
                  placeholder="Leave blank to use default workspace 'enrichement-extractor'"
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                />
              </div>

              {/* 1-Click Portable Python Runtime Card */}
              <div className="p-3.5 rounded-lg border border-[var(--border-color)] bg-black/25 space-y-3">
                <div className="flex items-center justify-between">
                  <div className="flex items-center space-x-2">
                    <HardDrive className="w-4 h-4 text-[var(--accent-color)]" />
                    <span className="font-semibold text-xs text-[var(--text-primary)]">Python Runtime Environment (1-Click)</span>
                  </div>
                  {pythonInfo?.found ? (
                    <span className="text-[10px] font-bold px-2 py-0.5 rounded bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 flex items-center space-x-1">
                      <Check className="w-3 h-3" />
                      <span>{pythonInfo.version || 'Python Found'}</span>
                    </span>
                  ) : (
                    <span className="text-[10px] font-bold px-2 py-0.5 rounded bg-amber-500/20 text-amber-300 border border-amber-500/30">
                      Not Configured
                    </span>
                  )}
                </div>

                <p className="text-[11px] text-[var(--text-muted)]">
                  The Python pipeline powers reader enrichment, tokenization, and study pack master sheets.
                  Use 1-click auto-detect for local environments or set up the portable standalone distribution.
                </p>

                {pythonStatusMessage && (
                  <div className={`p-2 rounded text-xs flex items-center space-x-1.5 ${
                    pythonStatusMessage.ok ? 'bg-emerald-500/10 text-emerald-300 border border-emerald-500/30' : 'bg-amber-500/10 text-amber-300 border border-amber-500/30'
                  }`}>
                    {pythonStatusMessage.ok ? <Check className="w-3.5 h-3.5 text-emerald-400 shrink-0" /> : <AlertCircle className="w-3.5 h-3.5 text-amber-400 shrink-0" />}
                    <span>{pythonStatusMessage.text}</span>
                  </div>
                )}

                <div className="flex flex-wrap items-center gap-2 pt-1">
                  <button
                    type="button"
                    onClick={handleAutoDetectPython}
                    disabled={isDetectingPython}
                    className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-xs text-[var(--text-primary)] disabled:opacity-50"
                  >
                    {isDetectingPython ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <RefreshCw className="w-3.5 h-3.5 text-[var(--accent-color)]" />}
                    <span>⚡ 1-Click Auto-Detect Python</span>
                  </button>

                  <button
                    type="button"
                    onClick={handleSetupPortablePython}
                    disabled={isSettingUpPython}
                    className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-50 shadow-sm"
                  >
                    {isSettingUpPython ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Download className="w-3.5 h-3.5" />}
                    <span>📦 1-Click Setup Portable Runtime</span>
                  </button>
                </div>
              </div>

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Python Executable Path
                </label>
                <input
                  type="text"
                  value={localConfig.directories.pythonExecutablePath}
                  onChange={(e) =>
                    setLocalConfig({
                      ...localConfig,
                      directories: { ...localConfig.directories, pythonExecutablePath: e.target.value },
                    })
                  }
                  placeholder="Leave blank to auto-detect .venv\Scripts\python.exe"
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                />
              </div>

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Custom Flashcards / Anki Export Directory
                </label>
                <input
                  type="text"
                  value={localConfig.directories.flashcardsDirectory}
                  onChange={(e) =>
                    setLocalConfig({
                      ...localConfig,
                      directories: { ...localConfig.directories, flashcardsDirectory: e.target.value },
                    })
                  }
                  placeholder="Leave blank for default output/data/flashcards"
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                />
              </div>
            </div>
          )}

          {/* TAB: LIBRARY & SHELVES */}
          {activeTab === 'library' && (
            <div className="space-y-4 max-w-2xl">
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Primary Shelf Grouping (Persists)
                  </label>
                  <select
                    value={localConfig.library?.primaryGrouping || 'series'}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        library: { ...localConfig.library, primaryGrouping: e.target.value as any },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  >
                    <option value="series">Series (e.g. 本好きの下剋上, クーク)</option>
                    <option value="author">Author (e.g. 香月美夜)</option>
                    <option value="category">Category / Virtual Folder</option>
                    <option value="publisher">Publisher / Imprint</option>
                    <option value="tag">Primary Tag</option>
                    <option value="none">None (Flat Catalog)</option>
                  </select>
                  <span className="text-[10px] text-[var(--text-muted)] mt-1 block">
                    Groups books onto distinct shelves under category/series headers.
                  </span>
                </div>

                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Sort Within Shelves (Default: Series & Vol #)
                  </label>
                  <select
                    value={localConfig.library?.sortWithinGrouping || 'series_and_volume'}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        library: { ...localConfig.library, sortWithinGrouping: e.target.value as any },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  >
                    <option value="series_and_volume">Series & Volume # (Vol 1, Vol 2...)</option>
                    <option value="title">Title (Kana / Reading Sort)</option>
                    <option value="author">Author</option>
                    <option value="added">Date Added (Newest First)</option>
                    <option value="custom">Custom Manual Order</option>
                    <option value="completion">Reading Progress (%)</option>
                  </select>
                  <span className="text-[10px] text-[var(--text-muted)] mt-1 block">
                    How books are ordered inside each shelf.
                  </span>
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4 pt-2 border-t border-[var(--border-color)]">
                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={localConfig.library?.enableShelves ?? true}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        library: { ...localConfig.library, enableShelves: e.target.checked },
                      })
                    }
                    className="rounded border-[var(--border-color)]"
                  />
                  <span>Render visual shelf rails & rack headers</span>
                </label>

                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={localConfig.library?.hideCompleted ?? false}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        library: { ...localConfig.library, hideCompleted: e.target.checked },
                      })
                    }
                    className="rounded border-[var(--border-color)]"
                  />
                  <span>Hide 100% completed books</span>
                </label>
              </div>

              {/* FILTERING / EXCLUSIONS */}
              <div className="space-y-3 pt-3 border-t border-[var(--border-color)]">
                <div className="font-semibold text-xs text-[var(--text-primary)]">
                  Filter What to Leave Out (Exclusions)
                </div>
                <p className="text-[11px] text-[var(--text-muted)]">
                  Selected categories or tags will be automatically filtered out from your library view.
                </p>

                {folders.length > 0 && (
                  <div>
                    <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1.5">
                      Exclude Categories / Virtual Folders
                    </label>
                    <div className="flex flex-wrap gap-1.5 max-h-32 overflow-y-auto p-1.5 bg-black/20 rounded border border-[var(--border-color)]">
                      {folders.map((f) => {
                        const isExcluded = localConfig.library?.excludedFolderIds?.includes(f.folderId);
                        return (
                          <button
                            key={f.folderId}
                            type="button"
                            onClick={() => toggleExcludedFolder(f.folderId)}
                            className={`px-2 py-0.5 rounded text-[11px] border transition-colors ${
                              isExcluded
                                ? 'bg-red-950/60 border-red-800 text-red-300 font-semibold'
                                : 'bg-[var(--bg-primary)] border-[var(--border-color)] text-[var(--text-muted)]'
                            }`}
                          >
                            <span>{isExcluded ? '✕ Excluded: ' : '＋ '}</span>
                            <span>{f.name}</span>
                          </button>
                        );
                      })}
                    </div>
                  </div>
                )}

                {tags.length > 0 && (
                  <div>
                    <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1.5">
                      Exclude Tags
                    </label>
                    <div className="flex flex-wrap gap-1.5 max-h-32 overflow-y-auto p-1.5 bg-black/20 rounded border border-[var(--border-color)]">
                      {tags.map((t) => {
                        const isExcluded = localConfig.library?.excludedTagIds?.includes(t.tagId);
                        return (
                          <button
                            key={t.tagId}
                            type="button"
                            onClick={() => toggleExcludedTag(t.tagId)}
                            className={`px-2 py-0.5 rounded text-[11px] border transition-colors ${
                              isExcluded
                                ? 'bg-red-950/60 border-red-800 text-red-300 font-semibold'
                                : 'bg-[var(--bg-primary)] border-[var(--border-color)] text-[var(--text-muted)]'
                            }`}
                          >
                            <span>{isExcluded ? '✕ Excluded: ' : '＋ '}</span>
                            <span>{t.name}</span>
                          </button>
                        );
                      })}
                    </div>
                  </div>
                )}
              </div>

              {/* DANGER ZONE */}
              <div className="pt-4 border-t border-[var(--border-color)]">
                <div className="p-4 rounded-lg border border-rose-500/40 bg-rose-950/20 space-y-3">
                  <div className="flex items-center space-x-2 text-rose-400">
                    <AlertTriangle className="w-4 h-4 shrink-0" />
                    <span className="font-bold text-xs uppercase tracking-wide">Danger Zone</span>
                  </div>

                  <div>
                    <h4 className="text-xs font-semibold text-[var(--text-primary)]">Clear Library Catalog</h4>
                    <p className="text-[11px] text-[var(--text-muted)] mt-0.5">
                      Permanently clears all imported books, chapters, bookmarks, reading progress, and AI reading sessions from your local database. Your original EPUB files on disk will NOT be touched or deleted.
                    </p>
                  </div>

                  {clearStatusMessage && (
                    <div
                      className={`p-2 rounded text-xs flex items-center space-x-1.5 ${
                        clearStatusMessage.ok
                          ? 'bg-emerald-500/10 text-emerald-300 border border-emerald-500/30'
                          : 'bg-rose-500/10 text-rose-300 border border-rose-500/30'
                      }`}
                    >
                      {clearStatusMessage.ok ? (
                        <Check className="w-3.5 h-3.5 text-emerald-400 shrink-0" />
                      ) : (
                        <AlertCircle className="w-3.5 h-3.5 text-rose-400 shrink-0" />
                      )}
                      <span>{clearStatusMessage.text}</span>
                    </div>
                  )}

                  {!showClearConfirm ? (
                    <div>
                      <button
                        type="button"
                        onClick={() => {
                          setShowClearConfirm(true);
                          setClearInputText('');
                          setClearStatusMessage(null);
                        }}
                        className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-rose-600 hover:bg-rose-500 text-white font-medium text-xs transition-colors cursor-pointer shadow-sm"
                      >
                        <Trash2 className="w-3.5 h-3.5" />
                        <span>Clear Entire Library...</span>
                      </button>
                    </div>
                  ) : (
                    <div className="p-3.5 rounded-lg bg-black/40 border border-rose-500/30 space-y-3">
                      <div className="text-xs text-rose-300 font-semibold flex items-center space-x-1.5">
                        <AlertTriangle className="w-3.5 h-3.5 text-rose-400 shrink-0" />
                        <span>Are you absolutely sure you want to clear your library?</span>
                      </div>

                      <div className="space-y-1.5 text-xs text-[var(--text-muted)]">
                        <label className="flex items-center space-x-2 cursor-pointer">
                          <input
                            type="checkbox"
                            checked={clearRemoveFolders}
                            onChange={(e) => setClearRemoveFolders(e.target.checked)}
                            className="rounded border-[var(--border-color)] accent-rose-600"
                          />
                          <span>Also remove virtual folders and categories</span>
                        </label>
                        <label className="flex items-center space-x-2 cursor-pointer">
                          <input
                            type="checkbox"
                            checked={clearResetVocab}
                            onChange={(e) => setClearResetVocab(e.target.checked)}
                            className="rounded border-[var(--border-color)] accent-rose-600"
                          />
                          <span>Also reset vocabulary learning progress (Known / Learning word states)</span>
                        </label>
                      </div>

                      <p className="text-[11px] text-[var(--text-muted)]">
                        To confirm deletion, please type <strong className="text-rose-400 font-mono">CLEAR</strong> below:
                      </p>

                      <div className="flex flex-wrap items-center gap-2">
                        <input
                          type="text"
                          value={clearInputText}
                          onChange={(e) => setClearInputText(e.target.value)}
                          placeholder="Type CLEAR to confirm"
                          className="px-2.5 py-1 rounded bg-[var(--bg-primary)] border border-rose-500/40 text-xs text-[var(--text-primary)] w-48 font-mono focus:outline-none focus:border-rose-400"
                        />
                        <button
                          type="button"
                          onClick={handleClearLibrary}
                          disabled={clearInputText.trim().toUpperCase() !== 'CLEAR' || isClearing}
                          className="flex items-center space-x-1.5 px-3 py-1 rounded bg-rose-600 hover:bg-rose-500 disabled:opacity-40 disabled:hover:bg-rose-600 text-white font-bold text-xs transition-colors cursor-pointer"
                        >
                          {isClearing ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Trash2 className="w-3.5 h-3.5" />}
                          <span>Confirm & Clear</span>
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setShowClearConfirm(false);
                            setClearInputText('');
                          }}
                          disabled={isClearing}
                          className="px-2.5 py-1 rounded border border-[var(--border-color)] hover:bg-white/5 text-xs text-[var(--text-muted)] transition-colors"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            </div>
          )}

          {/* TAB: APPEARANCE */}
          {activeTab === 'appearance' && (
            <div className="space-y-4 max-w-2xl">
              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Theme Preset
                </label>
                <div className="grid grid-cols-4 gap-2">
                  {[
                    { id: 'slate', label: 'Midnight Slate (Dark)' },
                    { id: 'warm-paper', label: 'Warm Paper (Cream)' },
                    { id: 'tokyo-night', label: 'Tokyo Night (Indigo)' },
                    { id: 'e-ink', label: 'E-Ink (Contrast)' },
                  ].map((preset) => (
                    <button
                      key={preset.id}
                      type="button"
                      onClick={() => handleThemePresetChange(preset.id)}
                      className={`p-2.5 rounded border text-left text-xs transition-colors ${
                        localConfig.appearance.themePreset === preset.id
                          ? 'border-[var(--accent-color)] bg-[var(--accent-color)]/10 font-bold text-[var(--accent-color)]'
                          : 'border-[var(--border-color)] text-[var(--text-muted)] hover:border-[var(--text-primary)]'
                      }`}
                    >
                      {preset.label}
                    </button>
                  ))}
                </div>
              </div>

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  UI Interface Font Family
                </label>
                <input
                  type="text"
                  value={localConfig.appearance.uiFontFamily}
                  onChange={(e) =>
                    setLocalConfig({
                      ...localConfig,
                      appearance: { ...localConfig.appearance, uiFontFamily: e.target.value },
                    })
                  }
                  placeholder="system-ui, Segoe UI, sans-serif"
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                />
              </div>

              <div className="pt-2 border-t border-[var(--border-color)]">
                <div className="text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-2">
                  Custom Palette Colors (Live Preview)
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="block text-[10px] text-[var(--text-muted)] mb-1">Primary Background</label>
                    <div className="flex space-x-2">
                      <input
                        type="color"
                        value={localConfig.appearance.bgPrimary}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              bgPrimary: e.target.value,
                            },
                          })
                        }
                        className="w-9 h-7 rounded border border-[var(--border-color)] bg-transparent cursor-pointer"
                      />
                      <input
                        type="text"
                        value={localConfig.appearance.bgPrimary}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              bgPrimary: e.target.value,
                            },
                          })
                        }
                        className="flex-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] font-mono text-[11px]"
                      />
                    </div>
                  </div>

                  <div>
                    <label className="block text-[10px] text-[var(--text-muted)] mb-1">Surface / Panel Background</label>
                    <div className="flex space-x-2">
                      <input
                        type="color"
                        value={localConfig.appearance.bgSurface}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              bgSurface: e.target.value,
                            },
                          })
                        }
                        className="w-9 h-7 rounded border border-[var(--border-color)] bg-transparent cursor-pointer"
                      />
                      <input
                        type="text"
                        value={localConfig.appearance.bgSurface}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              bgSurface: e.target.value,
                            },
                          })
                        }
                        className="flex-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] font-mono text-[11px]"
                      />
                    </div>
                  </div>

                  <div>
                    <label className="block text-[10px] text-[var(--text-muted)] mb-1">Accent Highlight Color</label>
                    <div className="flex space-x-2">
                      <input
                        type="color"
                        value={localConfig.appearance.accentColor}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              accentColor: e.target.value,
                            },
                          })
                        }
                        className="w-9 h-7 rounded border border-[var(--border-color)] bg-transparent cursor-pointer"
                      />
                      <input
                        type="text"
                        value={localConfig.appearance.accentColor}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              accentColor: e.target.value,
                            },
                          })
                        }
                        className="flex-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] font-mono text-[11px]"
                      />
                    </div>
                  </div>

                  <div>
                    <label className="block text-[10px] text-[var(--text-muted)] mb-1">Ruby Furigana Reading Color</label>
                    <div className="flex space-x-2">
                      <input
                        type="color"
                        value={localConfig.appearance.rubyColor}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              rubyColor: e.target.value,
                            },
                          })
                        }
                        className="w-9 h-7 rounded border border-[var(--border-color)] bg-transparent cursor-pointer"
                      />
                      <input
                        type="text"
                        value={localConfig.appearance.rubyColor}
                        onChange={(e) =>
                          setLocalConfig({
                            ...localConfig,
                            appearance: {
                              ...localConfig.appearance,
                              themePreset: 'custom',
                              rubyColor: e.target.value,
                            },
                          })
                        }
                        className="flex-1 px-2 py-1 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] font-mono text-[11px]"
                      />
                    </div>
                  </div>
                </div>
              </div>
            </div>
          )}

          {/* TAB: READER */}
          {activeTab === 'reader' && (
            <div className="space-y-4 max-w-2xl">
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Japanese Font Family
                  </label>
                  <input
                    type="text"
                    value={localConfig.reader.fontFamily}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        reader: { ...localConfig.reader, fontFamily: e.target.value },
                      })
                    }
                    placeholder="'Noto Serif JP', 'Yu Mincho', serif"
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>

                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Default Writing Mode
                  </label>
                  <select
                    value={localConfig.reader.writingMode}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        reader: { ...localConfig.reader, writingMode: e.target.value as any },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  >
                    <option value="vertical-rl">Vertical (縦書き vertical-rl)</option>
                    <option value="horizontal-tb">Horizontal (横書き horizontal-tb)</option>
                  </select>
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <div className="flex justify-between items-center mb-1">
                    <label className="text-[11px] font-semibold text-[var(--text-muted)] uppercase">
                      Font Size ({localConfig.reader.fontSizePx}px)
                    </label>
                  </div>
                  <input
                    type="range"
                    min="14"
                    max="42"
                    value={localConfig.reader.fontSizePx}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        reader: { ...localConfig.reader, fontSizePx: parseInt(e.target.value, 10) },
                      })
                    }
                    className="w-full accent-[var(--accent-color)]"
                  />
                </div>

                <div>
                  <div className="flex justify-between items-center mb-1">
                    <label className="text-[11px] font-semibold text-[var(--text-muted)] uppercase">
                      Line Height ({localConfig.reader.lineHeight})
                    </label>
                  </div>
                  <input
                    type="range"
                    min="1.2"
                    max="3.0"
                    step="0.05"
                    value={localConfig.reader.lineHeight}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        reader: { ...localConfig.reader, lineHeight: parseFloat(e.target.value) },
                      })
                    }
                    className="w-full accent-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Furigana Mode
                  </label>
                  <select
                    value={localConfig.reader.furiganaMode}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        reader: { ...localConfig.reader, furiganaMode: e.target.value as any },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  >
                    <option value="AllKanji">All Kanji (Generated + Source Ruby)</option>
                    <option value="Uncommon">Uncommon / Unknown Only</option>
                    <option value="None">Hide Generated Furigana</option>
                  </select>
                </div>

                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Column Max Width (px)
                  </label>
                  <input
                    type="number"
                    min="400"
                    max="1800"
                    step="50"
                    value={localConfig.reader.columnMaxWidthPx}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        reader: { ...localConfig.reader, columnMaxWidthPx: parseInt(e.target.value, 10) || 900 },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="pt-2">
                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={localConfig.reader.autoSavePosition}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        reader: { ...localConfig.reader, autoSavePosition: e.target.checked },
                      })
                    }
                    className="rounded border-[var(--border-color)]"
                  />
                  <span>Automatically remember reading chapter and scroll position</span>
                </label>
              </div>
            </div>
          )}

          {/* TAB: PLUGINS */}
          {activeTab === 'llm' && (
            <div className="py-1">
              <PluginSettingsTab />
            </div>
          )}

          {/* TAB: DICTIONARIES */}
          {activeTab === 'dictionaries' && (
            <div className="space-y-5 max-w-2xl">
              {/* 1-Click Curated Dictionaries Section */}
              <div className="p-4 rounded-lg border border-[var(--border-color)] bg-black/20 space-y-3">
                <div className="flex items-center justify-between">
                  <div className="flex items-center space-x-2">
                    <Library className="w-4 h-4 text-[var(--accent-color)]" />
                    <span className="font-semibold text-xs text-[var(--text-primary)]">
                      Curated Offline Yomitan Dictionaries (1-Click Install)
                    </span>
                  </div>
                  <button
                    type="button"
                    onClick={loadDictionaries}
                    className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)]"
                    title="Refresh Dictionaries"
                  >
                    <RefreshCw className="w-3.5 h-3.5" />
                  </button>
                </div>

                <p className="text-[11px] text-[var(--text-muted)]">
                  Download and import high-quality open-source Japanese dictionaries directly into your local database.
                  All lookup remains 100% offline and deterministic.
                </p>

                {dictStatusMessage && (
                  <div className={`p-2 rounded text-xs flex items-center space-x-1.5 ${
                    dictStatusMessage.ok ? 'bg-emerald-500/10 text-emerald-300 border border-emerald-500/30' : 'bg-red-500/10 text-red-300 border border-red-500/30'
                  }`}>
                    {dictStatusMessage.ok ? <Check className="w-3.5 h-3.5 text-emerald-400 shrink-0" /> : <AlertCircle className="w-3.5 h-3.5 text-red-400 shrink-0" />}
                    <span>{dictStatusMessage.text}</span>
                  </div>
                )}

                <div className="space-y-2 pt-1">
                  {(dictPresets.length > 0 ? dictPresets : [
                    { id: 'jmdict_english', title: 'JMdict (English)', description: 'General vocabulary dictionary with parts of speech and senses (EDRDG).', downloadUrl: '', approxSizeMb: 15.6, defaultPriority: 10 },
                    { id: 'kanjidic2', title: 'KANJIDIC2 (English)', description: 'Kanji characters, stroke counts, Onyomi, Kunyomi, and English meanings.', downloadUrl: '', approxSizeMb: 0.7, defaultPriority: 20 },
                    { id: 'jmnedict', title: 'JMnedict (Names)', description: 'Japanese names of persons, places, surnames, and company names.', downloadUrl: '', approxSizeMb: 11.4, defaultPriority: 30 },
                    { id: 'kireicake_accents', title: 'KireiCake (Slang & Manga)', description: 'Supplemental Japanese slang, colloquialisms, and manga vocabulary.', downloadUrl: '', approxSizeMb: 6.1, defaultPriority: 40 },
                    { id: 'kanjium_accents', title: 'Kanjium (Pitch Accents)', description: 'Pitch accent contours and downstep indicators for standard Japanese.', downloadUrl: '', approxSizeMb: 1.1, defaultPriority: 50 },
                  ]).map((preset) => {
                    const isInstalled = installedDicts.some(
                      (d) =>
                        d.title.toLowerCase().includes(preset.title.toLowerCase().split(' ')[0]) ||
                        d.dictionaryId.toLowerCase().includes(preset.id)
                    );
                    const isInstalling = installingDictId === preset.id;

                    return (
                      <div
                        key={preset.id}
                        className="p-2.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] flex items-center justify-between hover:border-[var(--accent-color)] transition-colors"
                      >
                        <div className="min-w-0 flex-1 pr-3">
                          <div className="flex items-center space-x-2">
                            <span className="font-semibold text-xs text-[var(--text-primary)]">{preset.title}</span>
                            <span className="text-[10px] text-[var(--text-muted)] font-mono">~{preset.approxSizeMb} MB</span>
                            {isInstalled && (
                              <span className="text-[10px] font-bold px-1.5 py-0.2 rounded bg-emerald-500/20 text-emerald-300">
                                Installed
                              </span>
                            )}
                          </div>
                          <p className="text-[10px] text-[var(--text-muted)] mt-0.5 truncate">{preset.description}</p>
                        </div>

                        <button
                          type="button"
                          onClick={() => handleInstallDictionary(preset)}
                          disabled={isInstalling}
                          className={`flex items-center space-x-1.5 px-3 py-1.5 rounded text-xs font-semibold shrink-0 transition-all ${
                            isInstalled
                              ? 'bg-[var(--bg-surface)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-[var(--text-muted)] hover:text-[var(--text-primary)]'
                              : 'bg-[var(--accent-color)] text-black hover:opacity-90 shadow-sm'
                          } disabled:opacity-50`}
                        >
                          {isInstalling ? (
                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                          ) : (
                            <Download className="w-3.5 h-3.5" />
                          )}
                          <span>{isInstalling ? 'Installing...' : isInstalled ? 'Re-install' : '1-Click Install'}</span>
                        </button>
                      </div>
                    );
                  })}
                </div>

                {/* Custom Yomitan ZIP / URL input */}
                <div className="pt-2 border-t border-[var(--border-color)]/60">
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Install Custom Yomitan Archive (URL or Local ZIP path)
                  </label>
                  <div className="flex space-x-2">
                    <input
                      type="text"
                      value={customZipInput}
                      onChange={(e) => setCustomZipInput(e.target.value)}
                      placeholder="e.g. https://.../dictionary.zip or C:\Downloads\dictionary.zip"
                      className="flex-1 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] font-mono focus:outline-none focus:border-[var(--accent-color)]"
                    />
                    <button
                      type="button"
                      onClick={handleInstallCustomDictionary}
                      disabled={!customZipInput.trim() || installingDictId === 'custom'}
                      className="flex items-center space-x-1 px-3 py-1.5 rounded bg-[var(--bg-surface)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-xs font-semibold text-[var(--text-primary)] disabled:opacity-50"
                    >
                      {installingDictId === 'custom' ? (
                        <Loader2 className="w-3.5 h-3.5 animate-spin" />
                      ) : (
                        <Download className="w-3.5 h-3.5 text-[var(--accent-color)]" />
                      )}
                      <span>Import ZIP</span>
                    </button>
                  </div>
                </div>
              </div>

              {/* Installed Dictionaries List */}
              <div className="space-y-2">
                <div className="flex items-center justify-between text-xs text-[var(--text-muted)]">
                  <span className="font-semibold uppercase tracking-wider text-[11px]">
                    Installed Dictionaries ({installedDicts.length})
                  </span>
                </div>

                {installedDicts.length === 0 ? (
                  <div className="p-3 rounded bg-black/10 border border-[var(--border-color)] text-center text-xs text-[var(--text-muted)]">
                    No offline dictionaries currently imported. Click 1-Click Install above for JMdict or KANJIDIC2!
                  </div>
                ) : (
                  <div className="space-y-1.5 max-h-48 overflow-y-auto pr-1">
                    {installedDicts.map((d) => (
                      <div
                        key={d.dictionaryId}
                        className="p-2 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] flex items-center justify-between text-xs"
                      >
                        <div className="min-w-0 flex-1 truncate pr-2">
                          <div className="font-medium text-[var(--text-primary)] truncate">{d.title}</div>
                          <div className="text-[10px] text-[var(--text-muted)] font-mono">
                            Revision: {d.revision} • Priority: {d.priorityOrder} • Lang: {d.language}
                          </div>
                        </div>
                        <span className="text-[10px] font-semibold px-2 py-0.5 rounded bg-emerald-500/20 text-emerald-300">
                          Active
                        </span>
                      </div>
                    ))}
                  </div>
                )}
              </div>

              <div className="border-t border-[var(--border-color)] pt-3">
                <h4 className="text-xs font-bold text-[var(--text-primary)] mb-3">Lookup & Mining Settings</h4>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Max Substring Scan Characters
                  </label>
                  <input
                    type="number"
                    min="4"
                    max="32"
                    value={localConfig.dictionaries.maxScanLength}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        dictionaries: {
                          ...localConfig.dictionaries,
                          maxScanLength: parseInt(e.target.value, 10) || 16,
                        },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Max Deinflection Depth
                  </label>
                  <input
                    type="number"
                    min="1"
                    max="8"
                    value={localConfig.dictionaries.maxDeinflections}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        dictionaries: {
                          ...localConfig.dictionaries,
                          maxDeinflections: parseInt(e.target.value, 10) || 4,
                        },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Max Candidates in Popover
                  </label>
                  <input
                    type="number"
                    min="3"
                    max="30"
                    value={localConfig.dictionaries.maxResults}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        dictionaries: {
                          ...localConfig.dictionaries,
                          maxResults: parseInt(e.target.value, 10) || 12,
                        },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Flashcard Mining Export Format
                  </label>
                  <select
                    value={localConfig.dictionaries.cardExportFormat}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        dictionaries: {
                          ...localConfig.dictionaries,
                          cardExportFormat: e.target.value as any,
                        },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  >
                    <option value="both">Both (library.json & mined-anki.tsv)</option>
                    <option value="tsv">Anki TSV Only</option>
                    <option value="json">JSON Library Only</option>
                  </select>
                </div>
              </div>

              <div className="pt-2">
                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={localConfig.dictionaries.autoClipboard}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        dictionaries: { ...localConfig.dictionaries, autoClipboard: e.target.checked },
                      })
                    }
                    className="rounded border-[var(--border-color)]"
                  />
                  <span>Automatically copy Japanese sentence text to clipboard when mining cards</span>
                </label>
              </div>

              {/* ANKICONNECT DIRECT SYNC INTEGRATION */}
              <div className="pt-4 border-t border-[var(--border-color)] space-y-3">
                <div className="flex items-center justify-between">
                  <div>
                    <h4 className="text-xs font-bold text-[var(--text-primary)] flex items-center space-x-1.5">
                      <Zap className="w-4 h-4 text-amber-400" />
                      <span>AnkiConnect Direct Sync (Local Port 8765)</span>
                    </h4>
                    <p className="text-[11px] text-[var(--text-muted)] mt-0.5">
                      Instantly sends mined flashcards into your local Anki installation without manual file imports.
                    </p>
                  </div>
                  <button
                    type="button"
                    onClick={handleTestAnki}
                    disabled={isTestingAnki}
                    className="flex items-center space-x-1 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-xs font-semibold text-[var(--text-primary)] disabled:opacity-50 transition-colors"
                  >
                    {isTestingAnki ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Zap className="w-3.5 h-3.5 text-amber-400" />}
                    <span>Test AnkiConnect</span>
                  </button>
                </div>

                {ankiTestStatus && (
                  <div
                    className={`p-2.5 rounded text-xs flex items-center space-x-2 ${
                      ankiTestStatus.reachable
                        ? 'bg-emerald-500/10 border border-emerald-500/30 text-emerald-300'
                        : 'bg-red-500/10 border border-red-500/30 text-red-300'
                    }`}
                  >
                    {ankiTestStatus.reachable ? (
                      <Check className="w-4 h-4 text-emerald-400 shrink-0" />
                    ) : (
                      <AlertCircle className="w-4 h-4 text-red-400 shrink-0" />
                    )}
                    <span>{ankiTestStatus.message}</span>
                  </div>
                )}

                <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
                  <div>
                    <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                      Endpoint URL
                    </label>
                    <input
                      type="text"
                      value={localConfig.dictionaries.ankiConnectUrl || 'http://127.0.0.1:8765'}
                      onChange={(e) =>
                        setLocalConfig({
                          ...localConfig,
                          dictionaries: { ...localConfig.dictionaries, ankiConnectUrl: e.target.value },
                        })
                      }
                      placeholder="http://127.0.0.1:8765"
                      className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] font-mono focus:outline-none focus:border-[var(--accent-color)]"
                    />
                  </div>

                  <div>
                    <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                      Target Deck Name
                    </label>
                    <input
                      type="text"
                      value={localConfig.dictionaries.ankiDeckName || 'Japanese::ImmersiveReading'}
                      onChange={(e) =>
                        setLocalConfig({
                          ...localConfig,
                          dictionaries: { ...localConfig.dictionaries, ankiDeckName: e.target.value },
                        })
                      }
                      placeholder="Japanese::ImmersiveReading"
                      className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                    />
                  </div>

                  <div>
                    <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                      Note Type / Model
                    </label>
                    <input
                      type="text"
                      value={localConfig.dictionaries.ankiModelName || 'Japanese (Recognition)'}
                      onChange={(e) =>
                        setLocalConfig({
                          ...localConfig,
                          dictionaries: { ...localConfig.dictionaries, ankiModelName: e.target.value },
                        })
                      }
                      placeholder="Japanese (Recognition)"
                      className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                    />
                  </div>
                </div>

                <div className="pt-1">
                  <label className="flex items-center space-x-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={localConfig.dictionaries.autoSyncAnki || false}
                      onChange={(e) =>
                        setLocalConfig({
                          ...localConfig,
                          dictionaries: { ...localConfig.dictionaries, autoSyncAnki: e.target.checked },
                        })
                      }
                      className="rounded border-[var(--border-color)]"
                    />
                    <span className="text-xs">
                      ⚡ Automatically sync flashcard directly to Anki whenever "+ Mine Card" is clicked
                    </span>
                  </label>
                </div>
              </div>
            </div>
          )}
        </div>

        {/* MODAL FOOTER */}
        <div className="p-4 border-t border-[var(--border-color)] flex items-center justify-between bg-black/20">
          <div className="flex items-center space-x-2">
            <button
              type="button"
              onClick={handleResetTab}
              className="flex items-center space-x-1 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)]"
            >
              <RotateCcw className="w-3.5 h-3.5" />
              <span>Reset Tab Defaults</span>
            </button>
            <button
              type="button"
              onClick={handleResetAll}
              className="px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-muted)] hover:text-red-400"
            >
              Reset All Settings
            </button>
          </div>

          <div className="flex items-center space-x-2">
            <button
              type="button"
              onClick={closeSettings}
              className="px-4 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)]"
            >
              Cancel
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

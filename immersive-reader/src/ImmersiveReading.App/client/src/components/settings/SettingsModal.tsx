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
} from 'lucide-react';
import type { AppConfig } from '../../types/api';

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
  const [testResult, setTestResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [isTesting, setIsTesting] = useState(false);
  const [isSaving, setIsSaving] = useState(false);

  useEffect(() => {
    if (config) {
      setLocalConfig(JSON.parse(JSON.stringify(config)));
    }
  }, [config, isSettingsOpen]);

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

  const handleLlmPresetChange = (preset: string) => {
    let endpoint = localConfig.llm.endpointUrl;
    let model = localConfig.llm.modelId;
    let timeout = localConfig.llm.timeoutSeconds;

    if (preset === 'ollama') {
      endpoint = 'http://localhost:11434/v1/chat/completions';
      model = 'llama3.2';
      timeout = 15;
    } else if (preset === 'lmstudio') {
      endpoint = 'http://localhost:1234/v1/chat/completions';
      model = 'qwen2.5-7b';
      timeout = 20;
    } else if (preset === 'openai') {
      endpoint = 'https://api.openai.com/v1/chat/completions';
      model = 'gpt-4o-mini';
      timeout = 30;
    } else if (preset === 'offline') {
      endpoint = '';
      model = 'offline-heuristic';
      timeout = 5;
    }

    setLocalConfig({
      ...localConfig,
      llm: {
        ...localConfig.llm,
        preset,
        endpointUrl: endpoint,
        modelId: model,
        timeoutSeconds: timeout,
      },
    });
  };

  const handleTestLlm = async () => {
    setIsTesting(true);
    setTestResult(null);
    try {
      const res = await rpc.call<any>('config.testLlmEndpoint', {
        endpointUrl: localConfig.llm.endpointUrl,
        modelId: localConfig.llm.modelId,
        apiKey: localConfig.llm.apiKey,
        timeoutSeconds: localConfig.llm.timeoutSeconds,
      });
      setTestResult({
        ok: res?.ok ?? true,
        message: res?.message || (res?.ok ? 'Endpoint is reachable and responding!' : 'Failed to reach endpoint'),
      });
    } catch (err: any) {
      setTestResult({ ok: false, message: err.message || 'Connection failed' });
    } finally {
      setIsTesting(false);
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
            <span>AI & LLM</span>
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

          {/* TAB: LLM */}
          {activeTab === 'llm' && (
            <div className="space-y-4 max-w-2xl">
              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Quick Provider Preset
                </label>
                <select
                  value={localConfig.llm.preset}
                  onChange={(e) => handleLlmPresetChange(e.target.value)}
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                >
                  <option value="ollama">Ollama Local (http://localhost:11434)</option>
                  <option value="lmstudio">LM Studio / llama.cpp (http://localhost:1234)</option>
                  <option value="openai">OpenAI / Compatible API (api.openai.com)</option>
                  <option value="offline">Offline Deterministic Heuristic</option>
                </select>
              </div>

              <div>
                <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                  Endpoint URL (chat/completions)
                </label>
                <input
                  type="url"
                  value={localConfig.llm.endpointUrl}
                  onChange={(e) =>
                    setLocalConfig({
                      ...localConfig,
                      llm: { ...localConfig.llm, endpointUrl: e.target.value },
                    })
                  }
                  placeholder="http://localhost:11434/v1/chat/completions"
                  className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                />
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Model Name / ID
                  </label>
                  <input
                    type="text"
                    value={localConfig.llm.modelId}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        llm: { ...localConfig.llm, modelId: e.target.value },
                      })
                    }
                    placeholder="llama3.2, qwen2.5:7b, gpt-4o-mini"
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    API Key (optional for local)
                  </label>
                  <input
                    type="password"
                    value={localConfig.llm.apiKey}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        llm: { ...localConfig.llm, apiKey: e.target.value },
                      })
                    }
                    placeholder="sk-..."
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Timeout (seconds)
                  </label>
                  <input
                    type="number"
                    min="3"
                    max="120"
                    value={localConfig.llm.timeoutSeconds}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        llm: { ...localConfig.llm, timeoutSeconds: parseInt(e.target.value, 10) || 15 },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
                <div>
                  <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1">
                    Temperature (0.0 - 1.5)
                  </label>
                  <input
                    type="number"
                    min="0.0"
                    max="1.5"
                    step="0.1"
                    value={localConfig.llm.temperature}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        llm: { ...localConfig.llm, temperature: parseFloat(e.target.value) || 0.7 },
                      })
                    }
                    className="w-full px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                  />
                </div>
              </div>

              <div className="flex items-center space-x-3 pt-1">
                <button
                  type="button"
                  onClick={handleTestLlm}
                  disabled={isTesting}
                  className="flex items-center space-x-1.5 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-xs font-medium"
                >
                  {isTesting ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Zap className="w-3.5 h-3.5 text-amber-400" />}
                  <span>{isTesting ? 'Testing...' : '⚡ Test Connection & Fetch Models'}</span>
                </button>
                {testResult && (
                  <span
                    className={`flex items-center space-x-1 text-xs ${
                      testResult.ok ? 'text-emerald-400' : 'text-red-400'
                    }`}
                  >
                    {testResult.ok ? <Check className="w-3.5 h-3.5" /> : <AlertCircle className="w-3.5 h-3.5" />}
                    <span>{testResult.message}</span>
                  </span>
                )}
              </div>

              <div className="pt-2 border-t border-[var(--border-color)]">
                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={localConfig.llm.enableOfflineHeuristicFallback}
                    onChange={(e) =>
                      setLocalConfig({
                        ...localConfig,
                        llm: { ...localConfig.llm, enableOfflineHeuristicFallback: e.target.checked },
                      })
                    }
                    className="rounded border-[var(--border-color)]"
                  />
                  <span>Enable offline deterministic fallback if remote LLM is unreachable</span>
                </label>
              </div>
            </div>
          )}

          {/* TAB: DICTIONARIES */}
          {activeTab === 'dictionaries' && (
            <div className="space-y-4 max-w-2xl">
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

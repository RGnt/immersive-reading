import React, { useState, useEffect } from 'react';
import { useLibraryStore } from '../../stores/useLibraryStore';
import { useConfigStore } from '../../stores/useConfigStore';
import { rpc } from '../../services/rpc';
import {
  FolderSearch,
  FolderOpen,
  Play,
  CheckCircle2,
  AlertTriangle,
  X,
  Loader2,
} from 'lucide-react';

export const ScanFolderModal: React.FC = () => {
  const { isScanModalOpen, closeScanModal, defaultScanDir, folders, loadBootstrap } = useLibraryStore();
  const { config } = useConfigStore();

  const [directoryPath, setDirectoryPath] = useState('');
  const [recursive, setRecursive] = useState(true);
  const [autoCreateFolders, setAutoCreateFolders] = useState(false);
  const [targetFolderId, setTargetFolderId] = useState<string>('');

  const [isScanning, setIsScanning] = useState(false);
  const [scanProgress, setScanProgress] = useState<{
    scannedFiles: number;
    totalFiles?: number;
    importedNew?: number;
    deduplicatedExisting?: number;
    failedFiles?: number;
    currentFile?: string;
  } | null>(null);

  const [scanResult, setScanResult] = useState<{
    totalFilesDiscovered: number;
    importedNew: number;
    deduplicatedExisting: number;
    failedFiles: number;
    elapsedSeconds: number;
    errors: string[];
  } | null>(null);

  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  // Initialize directory path when modal opens
  useEffect(() => {
    if (isScanModalOpen) {
      const initialPath =
        config?.directories.defaultScanDirectory || defaultScanDir || '';
      setDirectoryPath(initialPath);
      setScanProgress(null);
      setScanResult(null);
      setErrorMessage(null);
      setIsScanning(false);
    }
  }, [isScanModalOpen, config?.directories.defaultScanDirectory, defaultScanDir]);

  // Subscribe to live scan progress events
  useEffect(() => {
    if (!isScanModalOpen) return;

    const unsubscribe = rpc.on('library.scanProgress', (data: any) => {
      setScanProgress({
        scannedFiles: data.scannedFiles ?? data.ScannedFiles ?? 0,
        totalFiles: data.totalFiles ?? data.TotalFiles ?? 0,
        importedNew: data.importedNew ?? data.ImportedNew ?? 0,
        deduplicatedExisting: data.deduplicatedExisting ?? data.DeduplicatedExisting ?? 0,
        failedFiles: data.failedFiles ?? data.FailedFiles ?? 0,
        currentFile: data.currentFile ?? data.CurrentFile ?? '',
      });
    });

    return () => {
      unsubscribe();
    };
  }, [isScanModalOpen]);

  // Close on Escape when not scanning
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && isScanModalOpen && !isScanning) {
        closeScanModal();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isScanModalOpen, isScanning, closeScanModal]);

  if (!isScanModalOpen) return null;

  const handleBrowse = async () => {
    try {
      const res = await rpc.call<any>('library.pickDirectory', {
        defaultPath: directoryPath.trim() || undefined,
      });
      if (res && res.path && !res.cancelled) {
        setDirectoryPath(res.path);
      }
    } catch (err: any) {
      console.error('Failed to pick directory:', err);
    }
  };

  const handleStartScan = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    const path = directoryPath.trim();
    if (!path) {
      setErrorMessage('Please enter or select a directory path to scan.');
      return;
    }

    setIsScanning(true);
    setErrorMessage(null);
    setScanResult(null);
    setScanProgress({
      scannedFiles: 0,
      totalFiles: 0,
      importedNew: 0,
      deduplicatedExisting: 0,
      failedFiles: 0,
      currentFile: 'Starting scan...',
    });

    try {
      const res = await rpc.call<any>('library.scanDirectory', {
        rootDirectory: path,
        targetFolderId: targetFolderId ? targetFolderId : undefined,
        recursive,
        autoCreateFolders,
      });

      if (res) {
        setScanResult({
          totalFilesDiscovered: res.totalFilesDiscovered ?? res.TotalFilesDiscovered ?? 0,
          importedNew: res.importedNew ?? res.ImportedNew ?? 0,
          deduplicatedExisting: res.deduplicatedExisting ?? res.DeduplicatedExisting ?? 0,
          failedFiles: res.failedFiles ?? res.FailedFiles ?? 0,
          elapsedSeconds: res.elapsedSeconds ?? res.ElapsedSeconds ?? 0,
          errors: res.errors ?? res.Errors ?? [],
        });
        // Immediately reload library catalog and folders
        await loadBootstrap();
      }
    } catch (err: any) {
      console.error('Scan directory failed:', err);
      setErrorMessage(`Scan failed: ${err.message || String(err)}`);
    } finally {
      setIsScanning(false);
    }
  };

  return (
    <div
      className="modal-backdrop fixed inset-0 bg-black/75 backdrop-blur-sm flex items-center justify-center z-50 p-4"
      onClick={() => {
        if (!isScanning) closeScanModal();
      }}
    >
      <div
        className="scan-modal-card bg-[var(--bg-surface)] border border-[var(--border-color)] rounded-xl shadow-2xl flex flex-col w-[640px] max-w-[95vw] overflow-hidden select-none animate-in fade-in zoom-in-95 duration-150"
        onClick={(e) => e.stopPropagation()}
      >
        {/* HEADER */}
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-[var(--border-color)] bg-black/20">
          <div className="flex items-center space-x-2.5">
            <div className="p-1.5 rounded-lg bg-[var(--accent-color)]/20 text-[var(--accent-color)] border border-[var(--accent-color)]/30">
              <FolderSearch className="w-5 h-5" />
            </div>
            <div>
              <h3 className="text-sm font-bold text-[var(--text-primary)]">
                Scan Directory for Books
              </h3>
              <p className="text-[11px] text-[var(--text-muted)]">
                Index Japanese EPUBs, extract OPF metadata & covers, and populate your library
              </p>
            </div>
          </div>
          <button
            onClick={() => {
              if (!isScanning) closeScanModal();
            }}
            disabled={isScanning}
            className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-primary)] disabled:opacity-40"
            title="Close (Esc)"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* BODY */}
        <div className="p-5 space-y-4 text-xs text-[var(--text-primary)]">
          {errorMessage && (
            <div className="p-3 rounded-lg bg-rose-950/60 border border-rose-800 text-rose-300 flex items-start space-x-2">
              <AlertTriangle className="w-4 h-4 text-rose-400 shrink-0 mt-0.5" />
              <div className="flex-1">{errorMessage}</div>
            </div>
          )}

          {/* PATH SELECTION */}
          <div>
            <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1.5">
              Root Directory Path
            </label>
            <div className="flex space-x-2">
              <input
                type="text"
                value={directoryPath}
                onChange={(e) => setDirectoryPath(e.target.value)}
                disabled={isScanning}
                placeholder="e.g. C:\Books or /Users/name/Books"
                className="flex-1 px-3 py-2 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] font-mono focus:outline-none focus:border-[var(--accent-color)] disabled:opacity-50"
              />
              <button
                type="button"
                onClick={handleBrowse}
                disabled={isScanning}
                className="flex items-center space-x-1.5 px-3.5 py-2 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] hover:border-[var(--accent-color)] text-[var(--text-primary)] font-medium text-xs transition-colors disabled:opacity-50"
              >
                <FolderOpen className="w-4 h-4 text-amber-400" />
                <span>Browse...</span>
              </button>
            </div>
          </div>

          {/* DESTINATION FOLDER */}
          <div>
            <label className="block text-[11px] font-semibold text-[var(--text-muted)] uppercase mb-1.5">
              Destination Collection / Folder
            </label>
            <select
              value={targetFolderId}
              onChange={(e) => setTargetFolderId(e.target.value)}
              disabled={isScanning}
              className="w-full px-3 py-2 rounded-lg bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)] disabled:opacity-50"
            >
              <option value="">📥 (Uncategorized / Library Root)</option>
              {folders.map((f) => (
                <option key={f.folderId} value={f.folderId}>
                  📁 {f.name} ({f.totalSubtreeBookCount} books)
                </option>
              ))}
            </select>
          </div>

          {/* OPTIONS */}
          <div className="p-3 rounded-lg bg-black/20 border border-[var(--border-color)] space-y-2.5">
            <label className="flex items-center space-x-2.5 cursor-pointer">
              <input
                type="checkbox"
                checked={recursive}
                onChange={(e) => setRecursive(e.target.checked)}
                disabled={isScanning}
                className="rounded border-[var(--border-color)] text-[var(--accent-color)] focus:ring-0"
              />
              <div>
                <span className="font-semibold text-xs">Recursive Subdirectory Scan</span>
                <p className="text-[11px] text-[var(--text-muted)]">
                  Scan all nested folders and subdirectories for .epub files
                </p>
              </div>
            </label>

            <label className="flex items-center space-x-2.5 cursor-pointer">
              <input
                type="checkbox"
                checked={autoCreateFolders}
                onChange={(e) => setAutoCreateFolders(e.target.checked)}
                disabled={isScanning}
                className="rounded border-[var(--border-color)] text-[var(--accent-color)] focus:ring-0"
              />
              <div>
                <span className="font-semibold text-xs">Auto-Create Collections from Subdirectories</span>
                <p className="text-[11px] text-[var(--text-muted)]">
                  Mirror disk subfolder hierarchy into virtual library collections
                </p>
              </div>
            </label>
          </div>

          {/* LIVE SCAN PROGRESS */}
          {isScanning && (
            <div className="p-4 rounded-lg bg-sky-950/30 border border-sky-800/60 space-y-3 animate-in fade-in">
              <div className="flex items-center justify-between text-xs">
                <div className="flex items-center space-x-2 text-sky-300 font-semibold">
                  <Loader2 className="w-4 h-4 animate-spin text-[var(--accent-color)]" />
                  <span>Scanning files...</span>
                </div>
                <span className="font-mono text-sky-200">
                  {scanProgress?.scannedFiles ?? 0} files scanned
                </span>
              </div>

              {/* Progress bar track */}
              <div className="w-full bg-black/40 h-2 rounded-full overflow-hidden border border-white/10">
                <div className="bg-[var(--accent-color)] h-full w-full animate-pulse transition-all" />
              </div>

              <div className="flex items-center justify-between text-[11px] text-[var(--text-muted)]">
                <span>New: <b className="text-emerald-400 font-bold">{scanProgress?.importedNew ?? 0}</b></span>
                <span>Existing: <b className="text-sky-400 font-bold">{scanProgress?.deduplicatedExisting ?? 0}</b></span>
                <span>Failed: <b className="text-rose-400 font-bold">{scanProgress?.failedFiles ?? 0}</b></span>
              </div>

              {scanProgress?.currentFile && (
                <div className="text-[10px] font-mono text-[var(--text-muted)] truncate max-w-full">
                  {scanProgress.currentFile}
                </div>
              )}
            </div>
          )}

          {/* SCAN RESULT SUMMARY */}
          {scanResult && !isScanning && (
            <div className="p-4 rounded-lg bg-emerald-950/40 border border-emerald-800/80 space-y-2.5 animate-in fade-in">
              <div className="flex items-center space-x-2 text-emerald-300 font-bold text-sm">
                <CheckCircle2 className="w-5 h-5 text-emerald-400" />
                <span>Scan Completed Successfully!</span>
              </div>

              <div className="grid grid-cols-4 gap-2 pt-1 text-center">
                <div className="p-2 rounded bg-black/30 border border-emerald-800/40">
                  <div className="text-[10px] text-[var(--text-muted)] uppercase">Discovered</div>
                  <div className="text-sm font-bold text-[var(--text-primary)] font-mono">
                    {scanResult.totalFilesDiscovered}
                  </div>
                </div>
                <div className="p-2 rounded bg-black/30 border border-emerald-800/40">
                  <div className="text-[10px] text-[var(--text-muted)] uppercase">Added New</div>
                  <div className="text-sm font-bold text-emerald-400 font-mono">
                    {scanResult.importedNew}
                  </div>
                </div>
                <div className="p-2 rounded bg-black/30 border border-emerald-800/40">
                  <div className="text-[10px] text-[var(--text-muted)] uppercase">Updated</div>
                  <div className="text-sm font-bold text-sky-400 font-mono">
                    {scanResult.deduplicatedExisting}
                  </div>
                </div>
                <div className="p-2 rounded bg-black/30 border border-emerald-800/40">
                  <div className="text-[10px] text-[var(--text-muted)] uppercase">Time</div>
                  <div className="text-sm font-bold text-amber-400 font-mono">
                    {scanResult.elapsedSeconds.toFixed(1)}s
                  </div>
                </div>
              </div>

              {scanResult.failedFiles > 0 && (
                <div className="text-[11px] text-rose-300">
                  ⚠️ {scanResult.failedFiles} files could not be imported (non-standard EPUB or corrupted).
                </div>
              )}
            </div>
          )}
        </div>

        {/* FOOTER */}
        <div className="flex items-center justify-between px-5 py-3 border-t border-[var(--border-color)] bg-black/10">
          <div className="text-[11px] text-[var(--text-muted)]">
            {isScanning ? 'Scan in progress, please do not close...' : 'EPUB files are cataloged without modifying originals.'}
          </div>

          <div className="flex items-center space-x-2">
            <button
              type="button"
              onClick={closeScanModal}
              disabled={isScanning}
              className="px-4 py-1.5 rounded-lg border border-[var(--border-color)] hover:bg-[var(--bg-primary)] text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] transition-colors disabled:opacity-40"
            >
              {scanResult ? 'Done' : 'Cancel'}
            </button>
            <button
              type="button"
              onClick={handleStartScan}
              disabled={isScanning || !directoryPath.trim()}
              className="flex items-center space-x-1.5 px-4 py-1.5 rounded-lg bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-40 shadow-sm transition-opacity"
            >
              {isScanning ? (
                <>
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  <span>Scanning...</span>
                </>
              ) : (
                <>
                  <Play className="w-3.5 h-3.5 fill-black" />
                  <span>{scanResult ? 'Scan Again' : '⚡ Start Scan'}</span>
                </>
              )}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};

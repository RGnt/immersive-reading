import { useEffect, useRef, useState } from 'react';
import type * as Monaco from 'monaco-editor/editor/editor.api';
import { applyAppTheme, loadMonaco } from './loadMonaco';

export interface EditorMarker {
  message: string;
  /** 1-based; 0 or missing = the first line. */
  line?: number;
  severity?: 'error' | 'warning';
}

interface Props {
  /** Identifies the document: switching it swaps the text (and its undo history) instead of editing the previous one. */
  documentKey: string;
  value: string;
  language: string;
  onChange?: (value: string) => void;
  onSave?: () => void;
  markers?: EditorMarker[];
  readOnly?: boolean;
  height?: number | string;
  /** Test hook: receives the live editor so a self-test can read what the user would see. */
  onReady?: (editor: Monaco.editor.IStandaloneCodeEditor) => void;
}

/** A Monaco editor that loads on demand and follows the app's colours. */
export function CodeEditor({ documentKey, value, language, onChange, onSave, markers, readOnly, height = 420, onReady }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null);
  const monacoRef = useRef<typeof Monaco | null>(null);
  const models = useRef(new Map<string, Monaco.editor.ITextModel>());
  const seen = useRef(new Map<string, string>());       // the last prop value shown for each document
  const latest = useRef({ onChange, onSave, documentKey, value, language });
  latest.current = { onChange, onSave, documentKey, value, language };
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  // shows the document that was asked for; text changed from outside (reset, revert) replaces the model's text
  const showDocument = () => {
    const monaco = monacoRef.current;
    const editor = editorRef.current;
    if (!monaco || !editor) return;
    const { documentKey: key, value: text, language: lang } = latest.current;
    let model = models.current.get(key);
    if (!model) {
      model = monaco.editor.createModel(text, lang);
      models.current.set(key, model);
    } else if (seen.current.get(key) !== text && model.getValue() !== text) {
      // only a prop that CHANGED is pushed into the editor (reset, revert, save). A re-render that still carries the old prop must never
      // overwrite what the user typed a moment ago and the parent has not received yet.
      model.setValue(text);
    }
    seen.current.set(key, text);
    if (editor.getModel() !== model) editor.setModel(model);
  };

  useEffect(() => {
    let disposed = false;
    let observer: MutationObserver | null = null;
    loadMonaco()
      .then((monaco) => {
        if (disposed || !host.current) return;
        monacoRef.current = monaco;
        applyAppTheme(monaco);
        const editor = monaco.editor.create(host.current, {
          automaticLayout: true,
          minimap: { enabled: false },
          wordWrap: 'on',
          fontSize: 13,
          scrollBeyondLastLine: false,
          renderWhitespace: 'selection',
          tabSize: 2,
          fixedOverflowWidgets: true,
          readOnly,
          model: null,
        });
        editorRef.current = editor;
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => latest.current.onSave?.());
        editor.onDidChangeModelContent(() => latest.current.onChange?.(editor.getValue()));
        // the app's themes change CSS variables on <html>: follow them
        observer = new MutationObserver(() => applyAppTheme(monaco));
        observer.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class'] });
        showDocument();
        onReady?.(editor);
        setReady(true);
      })
      .catch((error: unknown) => setFailed(error instanceof Error ? error.message : String(error)));

    return () => {
      disposed = true;
      observer?.disconnect();
      editorRef.current?.dispose();
      editorRef.current = null;
      models.current.forEach((m) => m.dispose());
      models.current.clear();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    showDocument();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [documentKey, value, language, ready]);

  useEffect(() => {
    editorRef.current?.updateOptions({ readOnly });
  }, [readOnly, ready]);

  useEffect(() => {
    const monaco = monacoRef.current;
    const model = editorRef.current?.getModel();
    if (!monaco || !model) return;
    monaco.editor.setModelMarkers(model, 'ir', (markers ?? []).map((m) => {
      const line = Math.min(Math.max(m.line || 1, 1), model.getLineCount());
      return {
        severity: m.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Error,
        message: m.message,
        startLineNumber: line, endLineNumber: line, startColumn: 1, endColumn: model.getLineMaxColumn(line),
      };
    }));
  }, [markers, documentKey, value, ready]);

  if (failed) {
    return <div className="text-xs text-red-400 p-3 border border-red-500/40 rounded">The editor could not be loaded: {failed}</div>;
  }
  return (
    <div
      ref={host}
      data-testid="code-editor"
      className="border border-[var(--border-color)] rounded overflow-hidden"
      style={{ height }}
    />
  );
}

import React, { useState, useEffect } from 'react';
import { Bot, Sparkles, BrainCircuit } from 'lucide-react';
import {
  usePluginRpc,
  usePluginConfig,
  useActiveBook,
  EnrichedText,
  PluginDrawerShell,
  PluginPageLayout,
  Button,
} from '@ir/sdk';

const PLUGIN_ID = 'aiCompanion';

export function activate(ctx) {
  // 1. Register Dedicated Full-Page View (Chat + Quiz Archive)
  ctx.registerView({
    id: 'ai-study-hub',
    title: 'AI Companion',
    icon: Bot,
    component: AiStudyHubPage,
  });

  // 2. Register In-Reader Persona Chat Drawer
  ctx.registerReaderDrawer({
    id: 'ai-persona-chat-drawer',
    title: 'AI Persona Chat',
    icon: Sparkles,
    location: 'right',
    defaultSize: 380,
    component: PersonaChatDrawer,
  });

  // 3. Register In-Reader Quiz Drawer
  ctx.registerReaderDrawer({
    id: 'ai-quiz-drawer',
    title: 'Comprehension Quiz',
    icon: BrainCircuit,
    location: 'right',
    defaultSize: 420,
    component: ({ onClose }) => (
      <PluginDrawerShell title="Comprehension Quiz" onClose={onClose}>
        <QuizInteractiveWidget />
      </PluginDrawerShell>
    ),
  });

  // 4. Register Reader Toolbar Action (Respects user's quizDisplayMode setting)
  ctx.registerReaderToolbarAction({
    id: 'ai-take-quiz-btn',
    label: 'Quiz',
    icon: BrainCircuit,
    onClick: ({ openModal, toggleDrawer, navigateToView, getPluginConfig }) => {
      const cfg = getPluginConfig();
      const mode = cfg.quizDisplayMode || 'modal';

      if (mode === 'modal') {
        openModal({
          id: 'ai-quiz-modal',
          title: 'Reading Comprehension Quiz (Zero-Spoiler)',
          size: 'lg',
          component: () => <QuizInteractiveWidget />,
        });
      } else if (mode === 'drawer') {
        toggleDrawer('ai-quiz-drawer');
      } else {
        navigateToView('ai-study-hub', { initialTab: 'quiz' });
      }
    },
  });
}

// --- Persona Chat Drawer Component ---
function PersonaChatDrawer({ onClose }) {
  const { call } = usePluginRpc(PLUGIN_ID);
  const { bookId, chapterIndex, viewportTokenId } = useActiveBook();
  const [messages, setMessages] = useState([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [personaId, setPersonaId] = useState('');
  const [error, setError] = useState('');

  // The character of this book (the server creates one the first time). Without a persona id the server looks for one called "default",
  // which does not exist, and every message failed silently.
  useEffect(() => {
    if (!bookId) return;
    call('listPersonas', { bookId })
      .then((res) => setPersonaId(res.personas?.[0]?.personaId || ''))
      .catch((err) => setError(`Could not load the character: ${err?.message || err}`));
  }, [bookId, call]);

  useEffect(() => {
    if (!bookId) return;
    call('getHistory', { bookId })
      .then((res) => setMessages(res.messages || []))
      .catch((err) => console.error('[PersonaChat] Failed to load history:', err));
  }, [bookId, call]);

  const send = async () => {
    if (!input.trim() || !bookId) return;
    if (!personaId) {
      setError('There is no character to talk to yet. Wait a moment and try again.');
      return;
    }
    const text = input;
    setInput('');
    setError('');
    setLoading(true);
    try {
      const reply = await call('sendMessage', {
        bookId,
        personaId,
        chapterIndex,
        viewportTokenId,
        message: text,
      });
      setMessages((prev) => [
        ...prev,
        { role: 'user', enrichedHtml: reply.userHtml },
        { role: 'assistant', enrichedHtml: reply.assistantHtml },
      ]);
    } catch (err) {
      console.error('[PersonaChat] Failed to send message:', err);
      setError(`The message could not be sent: ${err?.message || err}`);
      setInput(text);
    } finally {
      setLoading(false);
    }
  };

  return React.createElement(
    PluginDrawerShell,
    {
      title: 'AI Persona Chat',
      onClose,
      footer: React.createElement(
        'div',
        { className: 'flex gap-2' },
        React.createElement('input', {
          value: input,
          onChange: (e) => setInput(e.target.value),
          onKeyDown: (e) => e.key === 'Enter' && send(),
          placeholder: 'Ask about what you just read...',
          className: 'flex-1 px-3 py-1.5 rounded bg-black/40 border border-[var(--border-color)] text-sm text-[var(--text-primary)]',
        }),
        React.createElement(
          Button,
          { size: 'sm', onClick: send, disabled: loading },
          loading ? '...' : 'Send'
        )
      ),
    },
    React.createElement(
      'div',
      { className: 'space-y-3' },
      error ? React.createElement('div', { className: 'text-xs text-[var(--danger-text)]' }, error) : null,
      messages.map((m, i) =>
        React.createElement(
          'div',
          {
            key: i,
            className: `p-3 rounded-lg text-sm ${
              m.role === 'user'
                ? 'bg-indigo-600/20 border border-indigo-500/30 ml-6 text-[var(--text-primary)]'
                : 'bg-[var(--hover-bg)] border border-[var(--border-color)] mr-4 text-[var(--text-primary)]'
            }`,
          },
          React.createElement(EnrichedText, { html: m.enrichedHtml })
        )
      )
    )
  );
}

// --- Reusable Comprehension Quiz Widget ---
function Score({ label, value }) {
  return (
    <span className="text-[var(--text-muted)]">
      {label} <b className="text-[var(--text-primary)]">{value === null || value === undefined ? 'not scored' : `${value}/100`}</b>
    </span>
  );
}

// The written-answer result: understanding first, then grammar, then vocabulary, then what moved in the vocabulary.
function RubricFeedback({ result }) {
  const rubric = result?.rubric;
  const nudges = result?.vocabularyNudges || [];
  return (
    <div className="space-y-3 text-xs" data-testid="quiz-rubric">
      <div className="space-y-1">
        <div className="font-semibold text-[var(--text-primary)]">1. Did you answer the question? <Score label="" value={rubric?.comprehension ?? result?.comprehensionScore} /></div>
        {result?.missingOrMisunderstood ? <div className="text-[var(--text-muted)]">{result.missingOrMisunderstood}</div> : null}
        {result?.evidenceQuote ? <div className="text-[var(--text-muted)]">In the text: 「{result.evidenceQuote}」</div> : null}
      </div>

      {rubric ? (
        <>
          <div className="space-y-1">
            <div className="font-semibold text-[var(--text-primary)]">2. Grammar <Score label="" value={rubric.grammar} /></div>
            {!rubric.counted ? <div className="text-[var(--text-muted)]">Grammar and vocabulary are scored once the answer addresses the question.</div> : null}
            {(rubric.grammarIssues || []).map((issue, i) => (
              <div key={i} className="text-[var(--text-muted)]">
                <span className="text-[var(--danger-text)]">{issue.original}</span> → <span className="text-[var(--success-text)]">{issue.correction}</span>
                {issue.note ? <span> · {issue.note}</span> : null}
              </div>
            ))}
            {rubric.counted && (rubric.grammarIssues || []).length === 0 ? <div className="text-[var(--success-text)]">No mistakes found.</div> : null}
          </div>

          <div className="space-y-1">
            <div className="font-semibold text-[var(--text-primary)]">3. Vocabulary <Score label="" value={rubric.vocabulary} /></div>
            {(rubric.wellUsed || []).length > 0 ? (
              <div className="text-[var(--text-muted)]">Used well: <span className="text-[var(--success-text)]">{rubric.wellUsed.map((w) => w.surface).join('、')}</span></div>
            ) : null}
            {(rubric.misused || []).map((w, i) => (
              <div key={i} className="text-[var(--text-muted)]">
                <span className="text-[var(--danger-text)]">{w.surface}</span>
                {w.suggestion ? <span> → <span className="text-[var(--success-text)]">{w.suggestion}</span></span> : null}
                {w.note ? <span> · {w.note}</span> : null}
              </div>
            ))}
          </div>
          <div className="text-[var(--text-muted)]">Overall <b className="text-[var(--text-primary)]">{rubric.overall}/100</b> (understanding 60%, grammar 25%, vocabulary 15%). Only understanding decides your quiz level.</div>
        </>
      ) : null}

      {nudges.length > 0 ? (
        <div className="p-2 border border-[var(--border-color)] bg-[var(--hover-bg)] space-y-0.5" data-testid="quiz-nudges">
          <div className="font-semibold text-[var(--text-primary)]">Your vocabulary moved</div>
          {nudges.map((n, i) => (
            <div key={i} className="text-[var(--text-muted)]"><b className="text-[var(--text-primary)]">{n.lemma}</b>: {n.from} → {n.to} ({n.reason})</div>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function QuizInteractiveWidget() {
  const { call } = usePluginRpc(PLUGIN_ID);
  const { config } = usePluginConfig(PLUGIN_ID);
  const { bookId, chapterIndex } = useActiveBook();

  const [quiz, setQuiz] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [results, setResults] = useState({});
  const [texts, setTexts] = useState({});
  const [busy, setBusy] = useState({});

  const generate = async () => {
    if (!bookId) return;
    setLoading(true);
    setError('');
    setResults({});
    setTexts({});
    try {
      // the question is about the last chapter you COMPLETED: the one before the chapter you are reading
      setQuiz(await call('generateQuiz', { bookId, chapterIndex }));
    } catch (err) {
      setQuiz(null);
      setError(err?.message || String(err));
    } finally {
      setLoading(false);
    }
  };

  const submit = async (qId, payload) => {
    setBusy((b) => ({ ...b, [qId]: true }));
    setError('');
    try {
      const res = await call('submitQuizAnswer', { questionId: qId, ...payload });
      setResults((prev) => ({ ...prev, [qId]: res }));
    } catch (err) {
      setError(err?.message || String(err));
    } finally {
      setBusy((b) => ({ ...b, [qId]: false }));
    }
  };

  if (!quiz) {
    return (
      <div className="text-center py-8 space-y-4">
        <p className="text-sm text-[var(--text-primary)]">
          A question about the last chapter you finished. Nothing from the chapter you are reading now is used.
        </p>
        {error ? <p className="text-xs text-[var(--danger-text)]" data-testid="quiz-error">{error}</p> : null}
        <Button onClick={generate} disabled={loading || !bookId}>{loading ? 'Writing a question…' : 'Start Comprehension Quiz'}</Button>
      </div>
    );
  }

  const questions = quiz.questions || [];
  const freeForm = quiz.questionMode !== 'mcq';

  return (
    <div className="space-y-6">
      {quiz.sourceChapterTitle ? <div className="text-xs text-[var(--text-muted)]">About the chapter you finished: <b className="text-[var(--text-primary)]">{quiz.sourceChapterTitle}</b></div> : null}
      {error ? <p className="text-xs text-[var(--danger-text)]" data-testid="quiz-error">{error}</p> : null}
      {questions.map((q, qIdx) => {
        const result = results[q.id];
        return (
          <div key={q.id || qIdx} className="p-4 bg-[var(--hover-bg)] border border-[var(--border-color)] space-y-3">
            <div className="font-medium text-sm text-[var(--text-primary)] flex items-start gap-2">
              <span className="text-[var(--text-muted)] font-bold shrink-0">{`Q${qIdx + 1}.`}</span>
              <EnrichedText html={q.promptEnrichedHtml} className="inline" />
            </div>

            {!freeForm ? (
              <div className="space-y-2">
                {(q.options || []).map((optHtml, oIdx) => (
                  <button
                    key={oIdx}
                    disabled={!!result || busy[q.id]}
                    onClick={() => submit(q.id, { selectedOptionIndex: oIdx, userAnswer: String(oIdx) })}
                    className="w-full text-left p-2.5 border border-[var(--border-color)] hover:bg-[var(--hover-bg-strong)] disabled:opacity-60 text-sm text-[var(--text-primary)] cursor-pointer transition-colors"
                  >
                    <EnrichedText html={optHtml} />
                  </button>
                ))}
              </div>
            ) : (
              <div className="space-y-2">
                <textarea
                  rows={3}
                  value={texts[q.id] || ''}
                  disabled={!!result || busy[q.id]}
                  onChange={(e) => setTexts((t) => ({ ...t, [q.id]: e.target.value }))}
                  placeholder="Answer in Japanese, in one to three sentences"
                  className="w-full px-3 py-2 bg-[var(--bg-input)] border border-[var(--border-color)] text-sm text-[var(--text-primary)]"
                />
                {!result ? (
                  <Button size="sm" disabled={busy[q.id] || !(texts[q.id] || '').trim()} onClick={() => submit(q.id, { userAnswer: texts[q.id] })}>
                    {busy[q.id] ? 'Checking…' : 'Check my answer'}
                  </Button>
                ) : null}
              </div>
            )}

            {result ? (
              <div className="space-y-3">
                <div className={`p-2 text-xs border ${result.isCorrect ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/30' : 'bg-rose-500/20 text-rose-300 border-rose-500/30'}`}>
                  {result.isCorrect ? 'Correct!' : (freeForm ? 'Not quite.' : 'Not quite: that is not what the text says.')}
                </div>
                {freeForm ? <RubricFeedback result={result.result || result} /> : (
                  result.evidenceQuote ? <div className="text-xs text-[var(--text-muted)]">In the text: 「{result.evidenceQuote}」</div> : null
                )}
                {result.modelAnswer ? (
                  <div className="text-xs text-[var(--text-muted)]">A good answer: <EnrichedText html={result.modelAnswer} className="inline text-[var(--text-primary)]" /></div>
                ) : null}
              </div>
            ) : null}
          </div>
        );
      })}
      <div className="pt-2 flex justify-end">
        <Button variant="secondary" size="sm" onClick={generate} disabled={loading}>{loading ? 'Writing…' : 'Another question'}</Button>
      </div>
    </div>
  );
}

// --- Full-Screen AI Study Hub View ---
function AiStudyHubPage({ viewParams }) {
  const [tab, setTab] = useState(viewParams?.initialTab || 'quiz');

  return React.createElement(
    PluginPageLayout,
    {
      title: 'AI Companion Study Hub',
      subtitle: 'Review comprehension quizzes and in-character discussions',
      actions: React.createElement(
        'div',
        { className: 'flex gap-2' },
        React.createElement(
          Button,
          {
            variant: tab === 'quiz' ? 'primary' : 'secondary',
            size: 'sm',
            onClick: () => setTab('quiz'),
          },
          'Comprehension Quiz'
        ),
        React.createElement(
          Button,
          {
            variant: tab === 'chat' ? 'primary' : 'secondary',
            size: 'sm',
            onClick: () => setTab('chat'),
          },
          'Persona Chat'
        )
      ),
    },
    tab === 'quiz'
      ? React.createElement(QuizInteractiveWidget, null)
      : React.createElement(
          'div',
          { className: 'max-w-xl mx-auto h-[550px]' },
          React.createElement(PersonaChatDrawer, { onClose: () => {} })
        )
  );
}

import React, { useState } from 'react';
import { useCompanionStore } from '../../stores/useCompanionStore';
import { useReaderStore } from '../../stores/useReaderStore';
import { X, Bot, MessageSquare, HelpCircle, Send, CheckCircle2, AlertCircle, Loader2 } from 'lucide-react';

export const AiCompanionDrawer: React.FC = () => {
  const {
    isOpen,
    closeCompanion,
    activeTab,
    setActiveTab,
    currentQuestion,
    lastSubmission,
    isGeneratingQuiz,
    isSubmittingQuiz,
    generateQuestion,
    submitAnswer,
    chatMessages,
    isSendingChat,
    sendChatMessage,
  } = useCompanionStore();

  const { bookDetail, currentChapterIndex, currentSpineHref } = useReaderStore();
  const [selectedOption, setSelectedOption] = useState<number | null>(null);
  const [freeformAnswer, setFreeformAnswer] = useState('');
  const [chatInput, setChatInput] = useState('');

  if (!isOpen || !bookDetail) return null;

  const bookId = bookDetail.summary.bookId;

  const handleGenerateQuiz = () => {
    setSelectedOption(null);
    setFreeformAnswer('');
    generateQuestion({
      bookId,
      chapterIndex: currentChapterIndex,
      spineHref: currentSpineHref,
      viewportTokenId: 15,
    });
  };

  const handleSubmitQuiz = () => {
    if (!currentQuestion) return;
    const ans = currentQuestion.questionMode === 'mcq' ? String(selectedOption ?? 0) : freeformAnswer;

    submitAnswer({
      questionId: currentQuestion.questionId,
      selectedOptionIndex: selectedOption ?? 0,
      userAnswer: ans,
    });
  };

  const handleSendChat = (e: React.FormEvent) => {
    e.preventDefault();
    if (!chatInput.trim()) return;
    sendChatMessage(bookId, chatInput);
    setChatInput('');
  };

  return (
    <div
      id="ai-companion-drawer"
      className="fixed inset-y-0 right-0 w-96 bg-[var(--bg-surface)] border-l border-[var(--border-color)] shadow-2xl flex flex-col z-40 select-none animate-in slide-in-from-right duration-200"
    >
      <div className="p-3 border-b border-[var(--border-color)] flex items-center justify-between bg-[var(--bg-primary)]">
        <div className="flex items-center space-x-2">
          <Bot className="w-4 h-4 text-[var(--accent-color)]" />
          <span className="text-xs font-bold text-[var(--text-primary)]">AI Reading Companion</span>
        </div>
        <button
          onClick={closeCompanion}
          className="p-1 rounded text-[var(--text-muted)] hover:text-[var(--text-primary)] hover:bg-[var(--bg-surface)]"
        >
          <X className="w-4 h-4" />
        </button>
      </div>

      <div className="flex border-b border-[var(--border-color)] bg-[var(--bg-primary)] px-2 text-xs">
        <button
          onClick={() => setActiveTab('quiz')}
          className={`flex items-center space-x-1.5 py-2 px-3 border-b-2 font-medium transition-colors ${
            activeTab === 'quiz'
              ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
              : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
          }`}
        >
          <HelpCircle className="w-3.5 h-3.5" />
          <span>Comprehension Quiz</span>
        </button>
        <button
          onClick={() => setActiveTab('chat')}
          className={`flex items-center space-x-1.5 py-2 px-3 border-b-2 font-medium transition-colors ${
            activeTab === 'chat'
              ? 'border-[var(--accent-color)] text-[var(--accent-color)] font-bold'
              : 'border-transparent text-[var(--text-muted)] hover:text-[var(--text-primary)]'
          }`}
        >
          <MessageSquare className="w-3.5 h-3.5" />
          <span>Persona Chat</span>
        </button>
      </div>

      <div className="flex-1 overflow-y-auto p-4 flex flex-col">
        {activeTab === 'quiz' && (
          <div className="space-y-4 flex-1 flex flex-col">
            <div className="flex items-center justify-between">
              <span className="text-xs text-[var(--text-muted)]">Test your reading comprehension</span>
              <button
                onClick={handleGenerateQuiz}
                disabled={isGeneratingQuiz}
                id="btn-generate-quiz"
                className="px-2.5 py-1 rounded bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-40"
              >
                {isGeneratingQuiz ? 'Generating...' : currentQuestion ? 'New Question' : 'Start Quiz'}
              </button>
            </div>

            {isGeneratingQuiz && (
              <div className="flex-1 flex flex-col items-center justify-center space-y-2 text-[var(--text-muted)]">
                <Loader2 className="w-6 h-6 animate-spin text-[var(--accent-color)]" />
                <span className="text-xs">Analyzing chapter context...</span>
              </div>
            )}

            {!isGeneratingQuiz && currentQuestion && (
              <div className="space-y-4 flex-1 flex flex-col">
                <div className="p-3 rounded bg-[var(--bg-primary)] border border-[var(--border-color)]">
                  <div className="text-[10px] uppercase font-bold text-[var(--accent-color)] tracking-wider mb-1">
                    Mode: {currentQuestion.questionMode}
                  </div>
                  <p className="text-sm font-semibold text-[var(--text-primary)] leading-snug">
                    {currentQuestion.questionText}
                  </p>
                  {currentQuestion.evidenceQuote && (
                    <blockquote className="mt-2 text-xs italic text-[var(--text-muted)] border-l-2 border-[var(--accent-color)] pl-2">
                      "{currentQuestion.evidenceQuote}"
                    </blockquote>
                  )}
                </div>

                {currentQuestion.questionMode === 'mcq' && currentQuestion.options && (
                  <div className="space-y-2">
                    {currentQuestion.options.map((opt, idx) => (
                      <button
                        key={idx}
                        onClick={() => setSelectedOption(idx)}
                        disabled={!!lastSubmission}
                        className={`w-full text-left p-2.5 rounded text-xs border transition-all ${
                          selectedOption === idx
                            ? 'border-[var(--accent-color)] bg-[var(--accent-color)]/10 text-[var(--text-primary)] font-medium'
                            : 'border-[var(--border-color)] bg-[var(--bg-primary)] text-[var(--text-muted)] hover:text-[var(--text-primary)]'
                        }`}
                      >
                        <span className="font-bold mr-2">{String.fromCharCode(65 + idx)}.</span>
                        <span>{opt}</span>
                      </button>
                    ))}
                  </div>
                )}

                {currentQuestion.questionMode !== 'mcq' && (
                  <div className="space-y-2">
                    <textarea
                      value={freeformAnswer}
                      onChange={(e) => setFreeformAnswer(e.target.value)}
                      disabled={!!lastSubmission}
                      rows={3}
                      placeholder="Write your explanation in Japanese or English..."
                      className="w-full p-2.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
                    />
                  </div>
                )}

                {!lastSubmission && (
                  <button
                    onClick={handleSubmitQuiz}
                    disabled={isSubmittingQuiz || (currentQuestion.questionMode === 'mcq' && selectedOption === null)}
                    id="btn-submit-quiz"
                    className="w-full py-2 rounded bg-[var(--accent-color)] text-black font-semibold text-xs hover:opacity-90 disabled:opacity-40"
                  >
                    {isSubmittingQuiz ? 'Grading...' : 'Submit Answer'}
                  </button>
                )}

                {lastSubmission && (
                  <div
                    className={`p-3 rounded border text-xs space-y-2 animate-in fade-in duration-200 ${
                      lastSubmission.isCorrect
                        ? 'bg-emerald-500/10 border-emerald-500/30 text-emerald-300'
                        : 'bg-amber-500/10 border-amber-500/30 text-amber-300'
                    }`}
                  >
                    <div className="flex items-center space-x-1.5 font-bold">
                      {lastSubmission.isCorrect ? (
                        <CheckCircle2 className="w-4 h-4 text-emerald-400" />
                      ) : (
                        <AlertCircle className="w-4 h-4 text-amber-400" />
                      )}
                      <span>Score: {lastSubmission.comprehensionScore} / 100</span>
                    </div>
                    {lastSubmission.critiqueExplanation && (
                      <p className="text-[11px] text-[var(--text-primary)] leading-relaxed">
                        {lastSubmission.critiqueExplanation}
                      </p>
                    )}
                    {lastSubmission.modelAnswer && (
                      <div className="pt-1 text-[11px] text-[var(--text-muted)] border-t border-[var(--border-color)]/30">
                        <span className="font-semibold text-[var(--text-primary)]">Model Answer: </span>
                        {lastSubmission.modelAnswer}
                      </div>
                    )}
                  </div>
                )}
              </div>
            )}
          </div>
        )}

        {activeTab === 'chat' && (
          <div className="flex-1 flex flex-col justify-between h-full">
            <div className="flex-1 overflow-y-auto space-y-3 pr-1 mb-3">
              {chatMessages.length === 0 ? (
                <div className="h-full flex flex-col items-center justify-center text-[var(--text-muted)] text-xs text-center p-4">
                  <Bot className="w-8 h-8 opacity-40 mb-2" />
                  <p>Ask a character or narrator anything about the story so far!</p>
                </div>
              ) : (
                chatMessages.map((msg) => (
                  <div
                    key={msg.messageId}
                    className={`flex flex-col text-xs space-y-1 ${
                      msg.sender === 'user' ? 'items-end' : 'items-start'
                    }`}
                  >
                    <span className="text-[10px] text-[var(--text-muted)]">{msg.personaName}</span>
                    <div
                      className={`p-2.5 rounded-lg max-w-[85%] leading-relaxed ${
                        msg.sender === 'user'
                          ? 'bg-[var(--accent-color)] text-black font-medium'
                          : 'bg-[var(--bg-primary)] border border-[var(--border-color)] text-[var(--text-primary)]'
                      }`}
                    >
                      {msg.content}
                    </div>
                  </div>
                ))
              )}
            </div>

            <form onSubmit={handleSendChat} className="flex space-x-1.5 pt-2 border-t border-[var(--border-color)]">
              <input
                type="text"
                value={chatInput}
                onChange={(e) => setChatInput(e.target.value)}
                placeholder="Type a message..."
                disabled={isSendingChat}
                className="flex-1 px-3 py-1.5 rounded bg-[var(--bg-primary)] border border-[var(--border-color)] text-xs text-[var(--text-primary)] focus:outline-none focus:border-[var(--accent-color)]"
              />
              <button
                type="submit"
                disabled={isSendingChat || !chatInput.trim()}
                className="p-2 rounded bg-[var(--accent-color)] text-black hover:opacity-90 disabled:opacity-40"
              >
                <Send className="w-3.5 h-3.5" />
              </button>
            </form>
          </div>
        )}
      </div>
    </div>
  );
};


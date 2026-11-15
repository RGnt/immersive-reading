import React, { useEffect, useState } from "react";
import { Bot, BrainCircuit, Sparkles } from "lucide-react";
import { Button, EnrichedText, PluginDrawerShell, PluginPageLayout, useActiveBook, usePluginConfig, usePluginRpc } from "@ir/sdk";
import { Fragment, jsx, jsxs } from "react/jsx-runtime";
// ../../Plugins/ImmersiveReading.Plugins.AiCompanion/Assets/ui.src.jsx
const PLUGIN_ID = "aiCompanion";
function activate(ctx) {
	ctx.registerView({
		id: "ai-study-hub",
		title: "AI Companion",
		icon: Bot,
		component: AiStudyHubPage
	});
	ctx.registerReaderDrawer({
		id: "ai-persona-chat-drawer",
		title: "AI Persona Chat",
		icon: Sparkles,
		location: "right",
		defaultSize: 380,
		component: PersonaChatDrawer
	});
	ctx.registerReaderDrawer({
		id: "ai-quiz-drawer",
		title: "Comprehension Quiz",
		icon: BrainCircuit,
		location: "right",
		defaultSize: 420,
		component: ({ onClose }) => /* @__PURE__ */ jsx(PluginDrawerShell, {
			title: "Comprehension Quiz",
			onClose,
			children: /* @__PURE__ */ jsx(QuizInteractiveWidget, {})
		})
	});
	ctx.registerReaderToolbarAction({
		id: "ai-take-quiz-btn",
		label: "Quiz",
		icon: BrainCircuit,
		onClick: ({ openModal, toggleDrawer, navigateToView, getPluginConfig }) => {
			const mode = getPluginConfig().quizDisplayMode || "modal";
			if (mode === "modal") openModal({
				id: "ai-quiz-modal",
				title: "Reading Comprehension Quiz (Zero-Spoiler)",
				size: "lg",
				component: () => /* @__PURE__ */ jsx(QuizInteractiveWidget, {})
			});
			else if (mode === "drawer") toggleDrawer("ai-quiz-drawer");
			else navigateToView("ai-study-hub", { initialTab: "quiz" });
		}
	});
}
function PersonaChatDrawer({ onClose }) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const { bookId, chapterIndex, viewportTokenId } = useActiveBook();
	const [messages, setMessages] = useState([]);
	const [input, setInput] = useState("");
	const [loading, setLoading] = useState(false);
	const [personaId, setPersonaId] = useState("");
	const [error, setError] = useState("");
	useEffect(() => {
		if (!bookId) return;
		call("listPersonas", { bookId }).then((res) => setPersonaId(res.personas?.[0]?.personaId || "")).catch((err) => setError(`Could not load the character: ${err?.message || err}`));
	}, [bookId, call]);
	useEffect(() => {
		if (!bookId) return;
		call("getHistory", { bookId }).then((res) => setMessages(res.messages || [])).catch((err) => console.error("[PersonaChat] Failed to load history:", err));
	}, [bookId, call]);
	const send = async () => {
		if (!input.trim() || !bookId) return;
		if (!personaId) {
			setError("There is no character to talk to yet. Wait a moment and try again.");
			return;
		}
		const text = input;
		setInput("");
		setError("");
		setLoading(true);
		try {
			const reply = await call("sendMessage", {
				bookId,
				personaId,
				chapterIndex,
				viewportTokenId,
				message: text
			});
			setMessages((prev) => [
				...prev,
				{
					role: "user",
					enrichedHtml: reply.userHtml
				},
				{
					role: "assistant",
					enrichedHtml: reply.assistantHtml
				}
			]);
		} catch (err) {
			console.error("[PersonaChat] Failed to send message:", err);
			setError(`The message could not be sent: ${err?.message || err}`);
			setInput(text);
		} finally {
			setLoading(false);
		}
	};
	return React.createElement(PluginDrawerShell, {
		title: "AI Persona Chat",
		onClose,
		footer: React.createElement("div", { className: "flex gap-2" }, React.createElement("input", {
			value: input,
			onChange: (e) => setInput(e.target.value),
			onKeyDown: (e) => e.key === "Enter" && send(),
			placeholder: "Ask about what you just read...",
			className: "flex-1 px-3 py-1.5 rounded bg-black/40 border border-[var(--border-color)] text-sm text-[var(--text-primary)]"
		}), React.createElement(Button, {
			size: "sm",
			onClick: send,
			disabled: loading
		}, loading ? "..." : "Send"))
	}, React.createElement("div", { className: "space-y-3" }, error ? React.createElement("div", { className: "text-xs text-[var(--danger-text)]" }, error) : null, messages.map((m, i) => React.createElement("div", {
		key: i,
		className: `p-3 rounded-lg text-sm ${m.role === "user" ? "bg-indigo-600/20 border border-indigo-500/30 ml-6 text-[var(--text-primary)]" : "bg-[var(--hover-bg)] border border-[var(--border-color)] mr-4 text-[var(--text-primary)]"}`
	}, React.createElement(EnrichedText, { html: m.enrichedHtml })))));
}
function Score({ label, value }) {
	return /* @__PURE__ */ jsxs("span", {
		className: "text-[var(--text-muted)]",
		children: [
			label,
			" ",
			/* @__PURE__ */ jsx("b", {
				className: "text-[var(--text-primary)]",
				children: value === null || value === void 0 ? "not scored" : `${value}/100`
			})
		]
	});
}
function RubricFeedback({ result }) {
	const rubric = result?.rubric;
	const nudges = result?.vocabularyNudges || [];
	return /* @__PURE__ */ jsxs("div", {
		className: "space-y-3 text-xs",
		"data-testid": "quiz-rubric",
		children: [
			/* @__PURE__ */ jsxs("div", {
				className: "space-y-1",
				children: [
					/* @__PURE__ */ jsxs("div", {
						className: "font-semibold text-[var(--text-primary)]",
						children: ["1. Did you answer the question? ", /* @__PURE__ */ jsx(Score, {
							label: "",
							value: rubric?.comprehension ?? result?.comprehensionScore
						})]
					}),
					result?.missingOrMisunderstood ? /* @__PURE__ */ jsx("div", {
						className: "text-[var(--text-muted)]",
						children: result.missingOrMisunderstood
					}) : null,
					result?.evidenceQuote ? /* @__PURE__ */ jsxs("div", {
						className: "text-[var(--text-muted)]",
						children: [
							"In the text: 「",
							result.evidenceQuote,
							"」"
						]
					}) : null
				]
			}),
			rubric ? /* @__PURE__ */ jsxs(Fragment, { children: [
				/* @__PURE__ */ jsxs("div", {
					className: "space-y-1",
					children: [
						/* @__PURE__ */ jsxs("div", {
							className: "font-semibold text-[var(--text-primary)]",
							children: ["2. Grammar ", /* @__PURE__ */ jsx(Score, {
								label: "",
								value: rubric.grammar
							})]
						}),
						!rubric.counted ? /* @__PURE__ */ jsx("div", {
							className: "text-[var(--text-muted)]",
							children: "Grammar and vocabulary are scored once the answer addresses the question."
						}) : null,
						(rubric.grammarIssues || []).map((issue, i) => /* @__PURE__ */ jsxs("div", {
							className: "text-[var(--text-muted)]",
							children: [
								/* @__PURE__ */ jsx("span", {
									className: "text-[var(--danger-text)]",
									children: issue.original
								}),
								" → ",
								/* @__PURE__ */ jsx("span", {
									className: "text-[var(--success-text)]",
									children: issue.correction
								}),
								issue.note ? /* @__PURE__ */ jsxs("span", { children: [" · ", issue.note] }) : null
							]
						}, i)),
						rubric.counted && (rubric.grammarIssues || []).length === 0 ? /* @__PURE__ */ jsx("div", {
							className: "text-[var(--success-text)]",
							children: "No mistakes found."
						}) : null
					]
				}),
				/* @__PURE__ */ jsxs("div", {
					className: "space-y-1",
					children: [
						/* @__PURE__ */ jsxs("div", {
							className: "font-semibold text-[var(--text-primary)]",
							children: ["3. Vocabulary ", /* @__PURE__ */ jsx(Score, {
								label: "",
								value: rubric.vocabulary
							})]
						}),
						(rubric.wellUsed || []).length > 0 ? /* @__PURE__ */ jsxs("div", {
							className: "text-[var(--text-muted)]",
							children: ["Used well: ", /* @__PURE__ */ jsx("span", {
								className: "text-[var(--success-text)]",
								children: rubric.wellUsed.map((w) => w.surface).join("、")
							})]
						}) : null,
						(rubric.misused || []).map((w, i) => /* @__PURE__ */ jsxs("div", {
							className: "text-[var(--text-muted)]",
							children: [
								/* @__PURE__ */ jsx("span", {
									className: "text-[var(--danger-text)]",
									children: w.surface
								}),
								w.suggestion ? /* @__PURE__ */ jsxs("span", { children: [" → ", /* @__PURE__ */ jsx("span", {
									className: "text-[var(--success-text)]",
									children: w.suggestion
								})] }) : null,
								w.note ? /* @__PURE__ */ jsxs("span", { children: [" · ", w.note] }) : null
							]
						}, i))
					]
				}),
				/* @__PURE__ */ jsxs("div", {
					className: "text-[var(--text-muted)]",
					children: [
						"Overall ",
						/* @__PURE__ */ jsxs("b", {
							className: "text-[var(--text-primary)]",
							children: [rubric.overall, "/100"]
						}),
						" (understanding 60%, grammar 25%, vocabulary 15%). Only understanding decides your quiz level."
					]
				})
			] }) : null,
			nudges.length > 0 ? /* @__PURE__ */ jsxs("div", {
				className: "p-2 border border-[var(--border-color)] bg-[var(--hover-bg)] space-y-0.5",
				"data-testid": "quiz-nudges",
				children: [/* @__PURE__ */ jsx("div", {
					className: "font-semibold text-[var(--text-primary)]",
					children: "Your vocabulary moved"
				}), nudges.map((n, i) => /* @__PURE__ */ jsxs("div", {
					className: "text-[var(--text-muted)]",
					children: [
						/* @__PURE__ */ jsx("b", {
							className: "text-[var(--text-primary)]",
							children: n.lemma
						}),
						": ",
						n.from,
						" → ",
						n.to,
						" (",
						n.reason,
						")"
					]
				}, i))]
			}) : null
		]
	});
}
function QuizInteractiveWidget() {
	const { call } = usePluginRpc(PLUGIN_ID);
	const { config } = usePluginConfig(PLUGIN_ID);
	const { bookId, chapterIndex } = useActiveBook();
	const [quiz, setQuiz] = useState(null);
	const [loading, setLoading] = useState(false);
	const [error, setError] = useState("");
	const [results, setResults] = useState({});
	const [texts, setTexts] = useState({});
	const [busy, setBusy] = useState({});
	const generate = async () => {
		if (!bookId) return;
		setLoading(true);
		setError("");
		setResults({});
		setTexts({});
		try {
			setQuiz(await call("generateQuiz", {
				bookId,
				chapterIndex
			}));
		} catch (err) {
			setQuiz(null);
			setError(err?.message || String(err));
		} finally {
			setLoading(false);
		}
	};
	const submit = async (qId, payload) => {
		setBusy((b) => ({
			...b,
			[qId]: true
		}));
		setError("");
		try {
			const res = await call("submitQuizAnswer", {
				questionId: qId,
				...payload
			});
			setResults((prev) => ({
				...prev,
				[qId]: res
			}));
		} catch (err) {
			setError(err?.message || String(err));
		} finally {
			setBusy((b) => ({
				...b,
				[qId]: false
			}));
		}
	};
	if (!quiz) return /* @__PURE__ */ jsxs("div", {
		className: "text-center py-8 space-y-4",
		children: [
			/* @__PURE__ */ jsx("p", {
				className: "text-sm text-[var(--text-primary)]",
				children: "A question about the last chapter you finished. Nothing from the chapter you are reading now is used."
			}),
			error ? /* @__PURE__ */ jsx("p", {
				className: "text-xs text-[var(--danger-text)]",
				"data-testid": "quiz-error",
				children: error
			}) : null,
			/* @__PURE__ */ jsx(Button, {
				onClick: generate,
				disabled: loading || !bookId,
				children: loading ? "Writing a question…" : "Start Comprehension Quiz"
			})
		]
	});
	const questions = quiz.questions || [];
	const freeForm = quiz.questionMode !== "mcq";
	return /* @__PURE__ */ jsxs("div", {
		className: "space-y-6",
		children: [
			quiz.sourceChapterTitle ? /* @__PURE__ */ jsxs("div", {
				className: "text-xs text-[var(--text-muted)]",
				children: ["About the chapter you finished: ", /* @__PURE__ */ jsx("b", {
					className: "text-[var(--text-primary)]",
					children: quiz.sourceChapterTitle
				})]
			}) : null,
			error ? /* @__PURE__ */ jsx("p", {
				className: "text-xs text-[var(--danger-text)]",
				"data-testid": "quiz-error",
				children: error
			}) : null,
			questions.map((q, qIdx) => {
				const result = results[q.id];
				return /* @__PURE__ */ jsxs("div", {
					className: "p-4 bg-[var(--hover-bg)] border border-[var(--border-color)] space-y-3",
					children: [
						/* @__PURE__ */ jsxs("div", {
							className: "font-medium text-sm text-[var(--text-primary)] flex items-start gap-2",
							children: [/* @__PURE__ */ jsx("span", {
								className: "text-[var(--text-muted)] font-bold shrink-0",
								children: `Q${qIdx + 1}.`
							}), /* @__PURE__ */ jsx(EnrichedText, {
								html: q.promptEnrichedHtml,
								className: "inline"
							})]
						}),
						!freeForm ? /* @__PURE__ */ jsx("div", {
							className: "space-y-2",
							children: (q.options || []).map((optHtml, oIdx) => /* @__PURE__ */ jsx("button", {
								disabled: !!result || busy[q.id],
								onClick: () => submit(q.id, {
									selectedOptionIndex: oIdx,
									userAnswer: String(oIdx)
								}),
								className: "w-full text-left p-2.5 border border-[var(--border-color)] hover:bg-[var(--hover-bg-strong)] disabled:opacity-60 text-sm text-[var(--text-primary)] cursor-pointer transition-colors",
								children: /* @__PURE__ */ jsx(EnrichedText, { html: optHtml })
							}, oIdx))
						}) : /* @__PURE__ */ jsxs("div", {
							className: "space-y-2",
							children: [/* @__PURE__ */ jsx("textarea", {
								rows: 3,
								value: texts[q.id] || "",
								disabled: !!result || busy[q.id],
								onChange: (e) => setTexts((t) => ({
									...t,
									[q.id]: e.target.value
								})),
								placeholder: "Answer in Japanese, in one to three sentences",
								className: "w-full px-3 py-2 bg-[var(--bg-input)] border border-[var(--border-color)] text-sm text-[var(--text-primary)]"
							}), !result ? /* @__PURE__ */ jsx(Button, {
								size: "sm",
								disabled: busy[q.id] || !(texts[q.id] || "").trim(),
								onClick: () => submit(q.id, { userAnswer: texts[q.id] }),
								children: busy[q.id] ? "Checking…" : "Check my answer"
							}) : null]
						}),
						result ? /* @__PURE__ */ jsxs("div", {
							className: "space-y-3",
							children: [
								/* @__PURE__ */ jsx("div", {
									className: `p-2 text-xs border ${result.isCorrect ? "bg-emerald-500/20 text-emerald-300 border-emerald-500/30" : "bg-rose-500/20 text-rose-300 border-rose-500/30"}`,
									children: result.isCorrect ? "Correct!" : freeForm ? "Not quite." : "Not quite: that is not what the text says."
								}),
								freeForm ? /* @__PURE__ */ jsx(RubricFeedback, { result: result.result || result }) : result.evidenceQuote ? /* @__PURE__ */ jsxs("div", {
									className: "text-xs text-[var(--text-muted)]",
									children: [
										"In the text: 「",
										result.evidenceQuote,
										"」"
									]
								}) : null,
								result.modelAnswer ? /* @__PURE__ */ jsxs("div", {
									className: "text-xs text-[var(--text-muted)]",
									children: ["A good answer: ", /* @__PURE__ */ jsx(EnrichedText, {
										html: result.modelAnswer,
										className: "inline text-[var(--text-primary)]"
									})]
								}) : null
							]
						}) : null
					]
				}, q.id || qIdx);
			}),
			/* @__PURE__ */ jsx("div", {
				className: "pt-2 flex justify-end",
				children: /* @__PURE__ */ jsx(Button, {
					variant: "secondary",
					size: "sm",
					onClick: generate,
					disabled: loading,
					children: loading ? "Writing…" : "Another question"
				})
			})
		]
	});
}
function AiStudyHubPage({ viewParams }) {
	const [tab, setTab] = useState(viewParams?.initialTab || "quiz");
	return React.createElement(PluginPageLayout, {
		title: "AI Companion Study Hub",
		subtitle: "Review comprehension quizzes and in-character discussions",
		actions: React.createElement("div", { className: "flex gap-2" }, React.createElement(Button, {
			variant: tab === "quiz" ? "primary" : "secondary",
			size: "sm",
			onClick: () => setTab("quiz")
		}, "Comprehension Quiz"), React.createElement(Button, {
			variant: tab === "chat" ? "primary" : "secondary",
			size: "sm",
			onClick: () => setTab("chat")
		}, "Persona Chat"))
	}, tab === "quiz" ? React.createElement(QuizInteractiveWidget, null) : React.createElement("div", { className: "max-w-xl mx-auto h-[550px]" }, React.createElement(PersonaChatDrawer, { onClose: () => {} })));
}
export { activate };

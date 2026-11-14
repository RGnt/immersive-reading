import React, { useEffect, useState } from "react";
import { Bot, BrainCircuit, Sparkles } from "lucide-react";
import { Button, EnrichedText, PluginDrawerShell, PluginPageLayout, useActiveBook, usePluginConfig, usePluginRpc } from "@ir/sdk";
import { jsx } from "react/jsx-runtime";
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
	useEffect(() => {
		if (!bookId) return;
		call("getHistory", { bookId }).then((res) => setMessages(res.messages || [])).catch((err) => console.error("[PersonaChat] Failed to load history:", err));
	}, [bookId, call]);
	const send = async () => {
		if (!input.trim() || !bookId) return;
		const text = input;
		setInput("");
		setLoading(true);
		try {
			const reply = await call("sendMessage", {
				bookId,
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
			className: "flex-1 px-3 py-1.5 rounded bg-black/40 border border-white/15 text-sm text-white"
		}), React.createElement(Button, {
			size: "sm",
			onClick: send,
			disabled: loading
		}, loading ? "..." : "Send"))
	}, React.createElement("div", { className: "space-y-3" }, messages.map((m, i) => React.createElement("div", {
		key: i,
		className: `p-3 rounded-lg text-sm ${m.role === "user" ? "bg-indigo-600/20 border border-indigo-500/30 ml-6 text-indigo-100" : "bg-white/5 border border-white/10 mr-4 text-white/90"}`
	}, React.createElement(EnrichedText, { html: m.enrichedHtml })))));
}
function QuizInteractiveWidget() {
	const { call } = usePluginRpc(PLUGIN_ID);
	const { config } = usePluginConfig(PLUGIN_ID);
	const { bookId, chapterIndex, viewportTokenId } = useActiveBook();
	const [quiz, setQuiz] = useState(null);
	const [loading, setLoading] = useState(false);
	const [feedback, setFeedback] = useState({});
	const generate = async () => {
		if (!bookId) return;
		setLoading(true);
		setFeedback({});
		try {
			const res = await call("generateQuiz", {
				bookId,
				chapterIndex,
				viewportTokenId,
				questionCount: config.questionCount || 3
			});
			setQuiz(res);
		} catch (err) {
			console.error("[Quiz] Failed to generate quiz:", err);
		} finally {
			setLoading(false);
		}
	};
	const handleSelectOption = async (qId, optionIdx) => {
		try {
			const res = await call("submitQuizAnswer", {
				questionId: qId,
				selectedOptionIndex: optionIdx
			});
			setFeedback((prev) => ({
				...prev,
				[qId]: {
					correct: res?.isCorrect,
					message: res?.explanation || (res?.isCorrect ? "Correct!" : "Incorrect.")
				}
			}));
		} catch (err) {
			console.error("[Quiz] Failed to submit answer:", err);
		}
	};
	if (!quiz) return React.createElement("div", { className: "text-center py-8 space-y-4" }, React.createElement("p", { className: "text-sm text-white/80" }, "Generate a zero-spoiler reading comprehension quiz up to your current reading position."), React.createElement(Button, {
		onClick: generate,
		disabled: loading || !bookId
	}, loading ? "Generating Quiz..." : "Start Comprehension Quiz"));
	const questions = quiz.questions || [];
	return React.createElement("div", { className: "space-y-6" }, questions.map((q, qIdx) => React.createElement("div", {
		key: q.id || qIdx,
		className: "p-4 rounded-lg bg-white/5 border border-white/10 space-y-3"
	}, React.createElement("div", { className: "font-medium text-sm text-white flex items-start gap-2" }, React.createElement("span", { className: "text-white/60 font-bold shrink-0" }, `Q${qIdx + 1}.`), React.createElement(EnrichedText, {
		html: q.promptEnrichedHtml,
		className: "inline"
	})), React.createElement("div", { className: "space-y-2" }, (q.options || []).map((optHtml, oIdx) => React.createElement("button", {
		key: oIdx,
		onClick: () => handleSelectOption(q.id, oIdx),
		className: "w-full text-left p-2.5 rounded border border-white/10 hover:bg-indigo-600/20 text-sm text-white/90 cursor-pointer transition-colors"
	}, React.createElement(EnrichedText, { html: optHtml })))), feedback[q.id] ? React.createElement("div", { className: `p-2 rounded text-xs ${feedback[q.id].correct ? "bg-emerald-500/20 text-emerald-300 border border-emerald-500/30" : "bg-rose-500/20 text-rose-300 border border-rose-500/30"}` }, feedback[q.id].message) : null)), React.createElement("div", { className: "pt-2 flex justify-end" }, React.createElement(Button, {
		variant: "secondary",
		size: "sm",
		onClick: generate,
		disabled: loading
	}, "Generate Another Quiz")));
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

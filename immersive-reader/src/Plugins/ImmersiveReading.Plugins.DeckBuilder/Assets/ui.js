import React, { useEffect, useRef, useState } from "react";
import { BarChart3, Hammer, Layers, Loader2, Sparkles } from "lucide-react";
import { refreshLibrary, usePluginConfig, usePluginRpc } from "@ir/sdk";
import { Fragment, jsx, jsxs } from "react/jsx-runtime";
// ../../Plugins/ImmersiveReading.Plugins.DeckBuilder/Assets/ui.src.jsx
const PLUGIN_ID = "deckBuilder";
const errMsg = (e) => e && e.message || String(e);
const fmt = (n) => (n ?? 0).toLocaleString();
function Status({ status }) {
	if (!status) return null;
	const cls = status.type === "success" ? "db-success" : status.type === "error" ? "db-error" : "";
	return /* @__PURE__ */ jsx("div", {
		className: `db-status ${cls}`,
		children: status.text
	});
}
function Stat({ label, value }) {
	return /* @__PURE__ */ jsxs("div", {
		className: "db-stat",
		children: [/* @__PURE__ */ jsx("b", { children: value }), /* @__PURE__ */ jsx("span", { children: label })]
	});
}
function Example({ text }) {
	if (!text) return /* @__PURE__ */ jsx("span", {
		className: "db-muted",
		children: "—"
	});
	const parts = text.split("**");
	return /* @__PURE__ */ jsx("span", {
		className: "db-ex",
		children: parts.map((p, i) => i % 2 === 1 ? /* @__PURE__ */ jsx("b", { children: p }, i) : /* @__PURE__ */ jsx(React.Fragment, { children: p }, i))
	});
}
function EnrichmentPanel({ bookId, call, onChanged }) {
	const [est, setEst] = useState(null);
	const [st, setSt] = useState(null);
	const [error, setError] = useState(null);
	const [open, setOpen] = useState(false);
	const refresh = async () => {
		const s = await call("getEnrichmentStatus", {
			bookId,
			limit: 40
		});
		setSt(s);
		return s;
	};
	useEffect(() => {
		setEst(null);
		setSt(null);
		setError(null);
		(async () => {
			try {
				setEst(await call("estimateEnrichment", { bookId }));
				await refresh();
			} catch (e) {
				setError(errMsg(e));
			}
		})();
	}, [bookId]);
	useEffect(() => {
		if (st?.state !== "running") return void 0;
		const timer = setInterval(async () => {
			try {
				if ((await refresh()).state !== "running") onChanged?.();
			} catch {}
		}, 1500);
		return () => clearInterval(timer);
	}, [st?.state]);
	const start = async () => {
		setError(null);
		try {
			await call("startEnrichment", { bookId });
			await refresh();
		} catch (e) {
			setError(errMsg(e));
		}
	};
	const cancel = async () => {
		try {
			await call("cancelEnrichment", { bookId });
			await refresh();
		} catch (e) {
			setError(errMsg(e));
		}
	};
	const running = st?.state === "running";
	const done = st && (st.state === "done" || st.grammarPointCount > 0 || st.functionWordNoteCount > 0);
	const tokensK = est ? Math.max(1, Math.round(est.approxInputTokens / 1e3)) : 0;
	return /* @__PURE__ */ jsxs("div", {
		className: "db-box",
		children: [
			/* @__PURE__ */ jsxs("div", {
				className: "db-row db-between",
				children: [/* @__PURE__ */ jsxs("div", {
					className: "db-row",
					children: [/* @__PURE__ */ jsx("span", {
						className: "db-title",
						children: "✨ Improve the deck with an LLM"
					}), /* @__PURE__ */ jsx("span", {
						className: "db-tag",
						children: "optional"
					})]
				}), est && (est.configured ? /* @__PURE__ */ jsxs("span", {
					className: "db-muted",
					children: [
						est.model || "loaded model",
						" @ ",
						est.endpointHost || "endpoint",
						" (",
						est.source === "plugin" ? "plugin endpoint" : "Settings → AI",
						")"
					]
				}) : /* @__PURE__ */ jsx("span", {
					className: "db-warn",
					children: "no LLM configured"
				}))]
			}),
			/* @__PURE__ */ jsxs("div", {
				className: "db-muted",
				children: [
					"Reads the book chapter by chapter and writes (1) the ",
					/* @__PURE__ */ jsx("b", { children: "grammar used in each chapter" }),
					" and (2) a ",
					/* @__PURE__ */ jsx("b", { children: "usage note for every particle and auxiliary" }),
					". Particles and auxiliaries are already marked in the deck from the tokenizer, so they print in their own section, apart from the vocabulary. Every example is a real sentence of the book: anything the model cites that cannot be found verbatim is discarded. Story sentences are never translated."
				]
			}),
			est && !est.configured && /* @__PURE__ */ jsx("div", {
				className: "db-status db-error",
				children: "Set an LLM endpoint in this plugin's settings (Settings → Installed Plugins → Deck Builder → LLM endpoint, for example your FreeToken URL) or the endpoint URL, model and key in Settings → AI first."
			}),
			est && /* @__PURE__ */ jsxs("div", {
				className: "db-muted",
				children: [
					est.chapters,
					" chapters · ",
					fmt(est.characters),
					" characters → ",
					/* @__PURE__ */ jsx("b", { children: est.requests }),
					" request",
					est.requests === 1 ? "" : "s",
					", about",
					" ",
					/* @__PURE__ */ jsxs("b", { children: [fmt(tokensK), "k"] }),
					" input tokens in total. This is the only step that sends book text to the model, and only when you press the button.",
					est.existing && /* @__PURE__ */ jsxs(Fragment, { children: [
						" Last run: ",
						est.existing.grammarPoints,
						" grammar points, ",
						est.existing.functionWordNotes,
						" function words (",
						est.existing.model,
						")."
					] })
				]
			}),
			/* @__PURE__ */ jsxs("div", {
				className: "db-row",
				children: [!running ? /* @__PURE__ */ jsxs("button", {
					className: "db-btn db-primary",
					onClick: start,
					disabled: !est?.configured,
					children: [
						/* @__PURE__ */ jsx(Sparkles, { size: 13 }),
						" ",
						done ? "Run again" : "Improve deck with LLM"
					]
				}) : /* @__PURE__ */ jsx("button", {
					className: "db-btn",
					onClick: cancel,
					children: "Cancel"
				}), running && /* @__PURE__ */ jsxs("span", {
					className: "db-row db-muted",
					children: [
						/* @__PURE__ */ jsx(Loader2, {
							size: 13,
							className: "animate-spin"
						}),
						" ",
						st.done,
						" / ",
						st.total,
						" requests",
						st.chapter ? ` · ${st.chapter}` : ""
					]
				})]
			}),
			running && /* @__PURE__ */ jsxs("div", {
				className: "db-muted",
				children: [
					"So far: ",
					/* @__PURE__ */ jsx("b", { children: st.grammarPointCount }),
					" grammar points · ",
					/* @__PURE__ */ jsx("b", { children: st.functionWordNoteCount }),
					" particles / auxiliaries with usage notes",
					st.chunksFailed > 0 && /* @__PURE__ */ jsxs("span", {
						className: "db-warn",
						children: [
							" · ",
							st.chunksFailed,
							" request",
							st.chunksFailed === 1 ? "" : "s",
							" failed"
						]
					}),
					st.warningCount > 0 && /* @__PURE__ */ jsxs("span", {
						className: "db-warn",
						children: [
							" · ",
							st.warningCount,
							" warning",
							st.warningCount === 1 ? "" : "s"
						]
					}),
					st.warnings?.length > 0 && /* @__PURE__ */ jsxs("div", {
						className: "db-warn",
						children: ["Latest: ", st.warnings[st.warnings.length - 1]]
					})
				]
			}),
			st?.state === "failed" && /* @__PURE__ */ jsxs("div", {
				className: "db-status db-error",
				children: ["Enrichment failed: ", st.error]
			}),
			done && !running && st.warningCount > 0 && st.grammarPointCount === 0 && st.functionWordNoteCount === 0 && /* @__PURE__ */ jsxs("div", {
				className: "db-status db-error",
				children: ["The run finished but produced nothing usable. Latest warning: ", st.warnings?.[0] || "see the warnings below"]
			}),
			st?.state === "cancelled" && /* @__PURE__ */ jsx("div", {
				className: "db-status",
				children: "Cancelled. Nothing was saved from this run."
			}),
			error && /* @__PURE__ */ jsx("div", {
				className: "db-status db-error",
				children: error
			}),
			done && !running && /* @__PURE__ */ jsxs("div", {
				className: "db-inset",
				children: [
					/* @__PURE__ */ jsxs("div", {
						className: "db-row db-between",
						children: [/* @__PURE__ */ jsxs("span", { children: [
							/* @__PURE__ */ jsx("b", { children: st.grammarPointCount }),
							" grammar points · ",
							/* @__PURE__ */ jsx("b", { children: st.functionWordNoteCount }),
							" particles / auxiliaries with usage notes"
						] }), /* @__PURE__ */ jsxs("button", {
							className: "db-btn",
							onClick: () => setOpen((o) => !o),
							children: [open ? "Hide" : "Show", " results"]
						})]
					}),
					st.warningCount > 0 && /* @__PURE__ */ jsxs("div", {
						className: "db-warn",
						style: { marginTop: 6 },
						children: [
							st.warningCount,
							" item",
							st.warningCount === 1 ? "" : "s",
							" skipped (unverifiable examples or failed requests)",
							st.warnings?.length ? `: ${st.warnings[0]}` : ""
						]
					}),
					open && /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsx("div", {
						className: "db-scroll",
						style: { marginTop: 8 },
						children: /* @__PURE__ */ jsxs("table", {
							className: "db-table",
							children: [/* @__PURE__ */ jsx("thead", { children: /* @__PURE__ */ jsxs("tr", { children: [
								/* @__PURE__ */ jsx("th", { children: "Pattern" }),
								/* @__PURE__ */ jsx("th", { children: "Meaning" }),
								/* @__PURE__ */ jsx("th", { children: "Chapter" }),
								/* @__PURE__ */ jsx("th", { children: "Example" })
							] }) }), /* @__PURE__ */ jsx("tbody", { children: (st.points || []).map((g) => /* @__PURE__ */ jsxs("tr", { children: [
								/* @__PURE__ */ jsxs("td", { children: [/* @__PURE__ */ jsx("b", { children: g.pattern }), /* @__PURE__ */ jsx("div", {
									className: "db-muted",
									children: g.category
								})] }),
								/* @__PURE__ */ jsxs("td", { children: [g.meaning, /* @__PURE__ */ jsx("div", {
									className: "db-muted",
									children: g.explanation
								})] }),
								/* @__PURE__ */ jsx("td", {
									className: "db-muted",
									children: g.chapter
								}),
								/* @__PURE__ */ jsx("td", { children: g.examples?.[0] ? /* @__PURE__ */ jsx(Example, { text: g.examples[0].text.replace(g.examples[0].surface, `**${g.examples[0].surface}**`) }) : "—" })
							] }, g.id)) })]
						})
					}), /* @__PURE__ */ jsx("div", {
						className: "db-scroll",
						style: { marginTop: 8 },
						children: /* @__PURE__ */ jsxs("table", {
							className: "db-table",
							children: [/* @__PURE__ */ jsx("thead", { children: /* @__PURE__ */ jsxs("tr", { children: [/* @__PURE__ */ jsx("th", { children: "Particle / auxiliary" }), /* @__PURE__ */ jsx("th", { children: "Uses in this book" })] }) }), /* @__PURE__ */ jsx("tbody", { children: (st.functionUses || []).map((f) => /* @__PURE__ */ jsxs("tr", { children: [/* @__PURE__ */ jsx("td", { children: /* @__PURE__ */ jsx("b", { children: f.word }) }), /* @__PURE__ */ jsx("td", { children: f.uses.map((u, i) => /* @__PURE__ */ jsxs("div", { children: [
								/* @__PURE__ */ jsx("b", { children: u.function }),
								": ",
								u.note
							] }, i)) })] }, f.word)) })]
						})
					})] }),
					/* @__PURE__ */ jsxs("div", {
						className: "db-muted",
						style: { marginTop: 6 },
						children: [
							"The results are saved. “Build & attach” writes them as ",
							/* @__PURE__ */ jsx("code", { children: "deck-grammar.json" }),
							" next to the deck; the study pack then adds the grammar to the grammar master and the usage notes to the particles & function words sheet. Explanations are model output: review them before relying on them."
						]
					})
				]
			})
		]
	});
}
function DeckBuilderTab({ bookId, book }) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const { config } = usePluginConfig(PLUGIN_ID);
	const hasDeck = !!book?.hasDeckCsv;
	const [order, setOrder] = useState(config?.order || "chronological");
	const [selection, setSelection] = useState("full");
	const [selectionValue, setSelectionValue] = useState(1e3);
	const [excludeKana, setExcludeKana] = useState(!!config?.excludeKana);
	const [excludeExamples, setExcludeExamples] = useState(!!config?.excludeExampleSentences);
	const [contentOnly, setContentOnly] = useState(!!config?.contentWordsOnly);
	const [format, setFormat] = useState("csv");
	const [excludeFunction, setExcludeFunction] = useState(!!config?.excludeFunctionWords);
	const [previewTick, setPreviewTick] = useState(0);
	const [stats, setStats] = useState(null);
	const [preview, setPreview] = useState(null);
	const [busy, setBusy] = useState(false);
	const [status, setStatus] = useState(null);
	const [confirmOverwrite, setConfirmOverwrite] = useState(false);
	const analyzedFor = useRef(null);
	const valueNeeded = selection !== "full";
	const valueLabel = selection === "topN" ? "Number of words" : selection === "targetCoverage" ? "Coverage %" : "Min. occurrences";
	const params = () => ({
		bookId,
		order,
		selection,
		selectionValue: valueNeeded ? Number(selectionValue) || 0 : 0,
		excludeKana,
		excludeExampleSentences: excludeExamples,
		excludeFunctionWords: excludeFunction,
		includeGrammar: true,
		contentWordsOnly: contentOnly,
		format
	});
	useEffect(() => {
		setStats(null);
		setPreview(null);
		setStatus(null);
		setConfirmOverwrite(false);
		analyzedFor.current = null;
	}, [bookId]);
	useEffect(() => {
		if (analyzedFor.current !== null && analyzedFor.current !== contentOnly) {
			setStats(null);
			setPreview(null);
		}
	}, [contentOnly]);
	useEffect(() => {
		if (!stats) return;
		let cancelled = false;
		const t = setTimeout(async () => {
			try {
				const res = await call("previewDeck", {
					...params(),
					limit: 60
				});
				if (!cancelled) setPreview(res);
			} catch (err) {
				if (!cancelled) setStatus({
					type: "error",
					text: `Preview failed: ${errMsg(err)}`
				});
			}
		}, 250);
		return () => {
			cancelled = true;
			clearTimeout(t);
		};
	}, [
		stats,
		order,
		selection,
		selectionValue,
		excludeKana,
		excludeExamples,
		excludeFunction,
		format,
		previewTick
	]);
	const analyze = async () => {
		setBusy(true);
		setStatus({ text: "Reading the book and counting words. Long novels can take up to a minute..." });
		try {
			const res = await call("analyzeBook", {
				bookId,
				contentWordsOnly: contentOnly,
				refresh: true
			});
			setStats(res.stats);
			analyzedFor.current = contentOnly;
			setStatus(null);
		} catch (err) {
			setStatus({
				type: "error",
				text: `Analysis failed: ${errMsg(err)}`
			});
		} finally {
			setBusy(false);
		}
	};
	const build = async (overwrite) => {
		setBusy(true);
		setStatus({ text: "Writing the deck next to the book..." });
		try {
			const res = await call("exportDeck", {
				...params(),
				overwrite
			});
			if (res.exists && !res.ok) {
				setConfirmOverwrite(true);
				setStatus({
					type: "error",
					text: `${res.path} already exists.`
				});
				return;
			}
			setConfirmOverwrite(false);
			setStatus({
				type: "success",
				text: `Wrote ${res.fileName}: ${fmt(res.wordCount)} words (${Math.round((res.fileSizeBytes || 0) / 1024)} KB) to the book folder.`
			});
			await refreshLibrary(bookId);
		} catch (err) {
			setStatus({
				type: "error",
				text: `Build failed: ${errMsg(err)}`
			});
		} finally {
			setBusy(false);
		}
	};
	return /* @__PURE__ */ jsxs("div", {
		className: "db-root",
		children: [
			/* @__PURE__ */ jsxs("div", {
				className: "db-box",
				children: [
					/* @__PURE__ */ jsxs("div", {
						className: "db-row db-between",
						children: [/* @__PURE__ */ jsxs("div", {
							className: "db-row",
							children: [/* @__PURE__ */ jsx("span", {
								className: "db-title",
								children: "🧱 Build a vocabulary deck from this book"
							}), /* @__PURE__ */ jsx("span", {
								className: "db-tag",
								children: "local"
							})]
						}), /* @__PURE__ */ jsx("span", {
							className: "db-muted",
							children: hasDeck ? /* @__PURE__ */ jsxs("span", {
								className: "db-ok",
								children: [
									"✓ ",
									book.deckCsvFileName || "deck.csv",
									" attached"
								]
							}) : /* @__PURE__ */ jsx("span", {
								className: "db-warn",
								children: "⚠ no deck yet"
							})
						})]
					}),
					/* @__PURE__ */ jsxs("div", {
						className: "db-muted",
						children: [
							"Counts every word of the book (dictionary form, in order of first appearance), looks each one up in your imported dictionaries and writes ",
							/* @__PURE__ */ jsx("code", { children: "deck.csv" }),
							" next to the book. Nothing is sent over the network."
						]
					}),
					/* @__PURE__ */ jsxs("div", {
						className: "db-row db-wrap",
						style: { alignItems: "flex-end" },
						children: [
							/* @__PURE__ */ jsxs("div", {
								className: "db-field",
								children: [/* @__PURE__ */ jsx("label", { children: "Word order" }), /* @__PURE__ */ jsxs("select", {
									className: "db-select",
									value: order,
									onChange: (e) => setOrder(e.target.value),
									children: [
										/* @__PURE__ */ jsx("option", {
											value: "chronological",
											children: "Chronological (first appearance)"
										}),
										/* @__PURE__ */ jsx("option", {
											value: "deckFrequency",
											children: "Deck frequency (most used first)"
										}),
										/* @__PURE__ */ jsx("option", {
											value: "globalFrequency",
											children: "Global frequency (most common first)"
										})
									]
								})]
							}),
							/* @__PURE__ */ jsxs("div", {
								className: "db-field",
								children: [/* @__PURE__ */ jsx("label", { children: "Which words" }), /* @__PURE__ */ jsxs("select", {
									className: "db-select",
									value: selection,
									onChange: (e) => setSelection(e.target.value),
									children: [
										/* @__PURE__ */ jsx("option", {
											value: "full",
											children: "Full deck"
										}),
										/* @__PURE__ */ jsx("option", {
											value: "topN",
											children: "Top N words"
										}),
										/* @__PURE__ */ jsx("option", {
											value: "targetCoverage",
											children: "Target coverage"
										}),
										/* @__PURE__ */ jsx("option", {
											value: "minOccurrences",
											children: "Minimum occurrences"
										})
									]
								})]
							}),
							valueNeeded && /* @__PURE__ */ jsxs("div", {
								className: "db-field",
								children: [/* @__PURE__ */ jsx("label", { children: valueLabel }), /* @__PURE__ */ jsx("input", {
									className: "db-input",
									type: "number",
									min: "1",
									value: selectionValue,
									onChange: (e) => setSelectionValue(e.target.value)
								})]
							}),
							/* @__PURE__ */ jsxs("div", {
								className: "db-field",
								children: [/* @__PURE__ */ jsx("label", { children: "Format" }), /* @__PURE__ */ jsxs("select", {
									className: "db-select",
									value: format,
									onChange: (e) => setFormat(e.target.value),
									children: [
										/* @__PURE__ */ jsx("option", {
											value: "csv",
											children: "CSV (deck.csv)"
										}),
										/* @__PURE__ */ jsx("option", {
											value: "txt",
											children: "Word list (TXT)"
										}),
										/* @__PURE__ */ jsx("option", {
											value: "txtRepeated",
											children: "Word list, one line per occurrence"
										})
									]
								})]
							})
						]
					}),
					/* @__PURE__ */ jsxs("div", {
						className: "db-row db-wrap",
						style: { gap: 16 },
						children: [
							/* @__PURE__ */ jsxs("label", {
								className: "db-row",
								children: [/* @__PURE__ */ jsx("input", {
									type: "checkbox",
									checked: excludeKana,
									onChange: (e) => setExcludeKana(e.target.checked)
								}), " Exclude kana-only words"]
							}),
							/* @__PURE__ */ jsxs("label", {
								className: "db-row",
								children: [/* @__PURE__ */ jsx("input", {
									type: "checkbox",
									checked: excludeExamples,
									onChange: (e) => setExcludeExamples(e.target.checked)
								}), " No example sentences"]
							}),
							/* @__PURE__ */ jsxs("label", {
								className: "db-row",
								children: [/* @__PURE__ */ jsx("input", {
									type: "checkbox",
									checked: contentOnly,
									onChange: (e) => setContentOnly(e.target.checked)
								}), " Content words only"]
							}),
							/* @__PURE__ */ jsxs("label", {
								className: "db-row",
								title: "By default particles and auxiliaries stay in the deck, marked in the Kind column, and print in their own section",
								children: [/* @__PURE__ */ jsx("input", {
									type: "checkbox",
									checked: excludeFunction,
									onChange: (e) => setExcludeFunction(e.target.checked)
								}), " Leave particles & auxiliaries out"]
							})
						]
					}),
					/* @__PURE__ */ jsxs("div", {
						className: "db-row",
						children: [
							/* @__PURE__ */ jsxs("button", {
								className: "db-btn",
								onClick: analyze,
								disabled: busy,
								children: [
									busy && !stats ? /* @__PURE__ */ jsx(Loader2, {
										size: 13,
										className: "animate-spin"
									}) : /* @__PURE__ */ jsx(BarChart3, { size: 13 }),
									" ",
									stats ? "Re-analyze book" : "Analyze book"
								]
							}),
							/* @__PURE__ */ jsxs("button", {
								className: "db-btn db-primary",
								onClick: () => build(false),
								disabled: busy || !stats,
								children: [
									busy && stats ? /* @__PURE__ */ jsx(Loader2, {
										size: 13,
										className: "animate-spin"
									}) : /* @__PURE__ */ jsx(Hammer, { size: 13 }),
									" Build & attach ",
									format === "csv" ? "deck.csv" : "deck.txt"
								]
							}),
							confirmOverwrite && /* @__PURE__ */ jsx("button", {
								className: "db-btn",
								onClick: () => build(true),
								disabled: busy,
								children: "Replace existing file"
							})
						]
					}),
					/* @__PURE__ */ jsx(Status, { status })
				]
			}),
			/* @__PURE__ */ jsx(EnrichmentPanel, {
				bookId,
				call,
				onChanged: () => setPreviewTick((t) => t + 1)
			}),
			stats && /* @__PURE__ */ jsxs("div", {
				className: "db-box",
				children: [/* @__PURE__ */ jsxs("div", {
					className: "db-stats",
					children: [
						/* @__PURE__ */ jsx(Stat, {
							label: "Characters",
							value: fmt(stats.characterCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Words (total)",
							value: fmt(stats.wordCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Unique words",
							value: fmt(stats.uniqueWordCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Seen once",
							value: fmt(stats.uniqueWordUsedOnceCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Unique kanji",
							value: fmt(stats.uniqueKanjiCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Sentences",
							value: fmt(stats.sentenceCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Dialogue",
							value: `${stats.dialoguePercentage}%`
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Not in dictionary",
							value: fmt(stats.unresolvedWordCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Particles",
							value: fmt(stats.particleCount)
						}),
						/* @__PURE__ */ jsx(Stat, {
							label: "Auxiliaries",
							value: fmt(stats.auxiliaryCount)
						})
					]
				}), stats.unresolvedWordCount > 0 && stats.unresolvedWordCount === stats.uniqueWordCount && /* @__PURE__ */ jsx("div", {
					className: "db-status db-error",
					children: "No word matched your dictionaries, so definitions will be empty. Import a JMdict-based Yomitan dictionary in Settings first."
				})]
			}),
			preview && /* @__PURE__ */ jsxs("div", {
				className: "db-box",
				children: [
					/* @__PURE__ */ jsxs("div", {
						className: "db-row db-between",
						children: [/* @__PURE__ */ jsxs("span", {
							className: "db-title",
							children: [
								"Selected: ",
								fmt(preview.selectedCount),
								" words"
							]
						}), /* @__PURE__ */ jsxs("span", {
							className: "db-muted",
							children: [
								"covers ",
								preview.totalOccurrences ? (preview.selectedOccurrences / preview.totalOccurrences * 100).toFixed(1) : 0,
								"% of all word occurrences"
							]
						})]
					}),
					/* @__PURE__ */ jsx("div", {
						className: "db-scroll",
						children: /* @__PURE__ */ jsxs("table", {
							className: "db-table",
							children: [/* @__PURE__ */ jsx("thead", { children: /* @__PURE__ */ jsxs("tr", { children: [
								/* @__PURE__ */ jsx("th", { children: "Word" }),
								/* @__PURE__ */ jsx("th", { children: "Kind" }),
								/* @__PURE__ */ jsx("th", { children: "Reading" }),
								/* @__PURE__ */ jsx("th", { children: "×" }),
								/* @__PURE__ */ jsx("th", { children: "Rank" }),
								/* @__PURE__ */ jsx("th", { children: "Definition / usage" }),
								/* @__PURE__ */ jsx("th", { children: "Example" })
							] }) }), /* @__PURE__ */ jsx("tbody", { children: preview.words.map((w, i) => /* @__PURE__ */ jsxs("tr", { children: [
								/* @__PURE__ */ jsx("td", {
									className: w.resolved ? "" : "db-unresolved",
									children: w.word
								}),
								/* @__PURE__ */ jsx("td", { children: w.kind && w.kind !== "vocabulary" ? /* @__PURE__ */ jsx("span", {
									className: "db-kind",
									children: w.kind
								}) : /* @__PURE__ */ jsx("span", {
									className: "db-muted",
									children: "word"
								}) }),
								/* @__PURE__ */ jsx("td", { children: w.reading || "—" }),
								/* @__PURE__ */ jsx("td", { children: w.occurrences }),
								/* @__PURE__ */ jsx("td", { children: w.frequencyRank || "—" }),
								/* @__PURE__ */ jsx("td", {
									className: "db-muted",
									children: w.usageNote ? /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsx("b", { children: w.usageNote }), /* @__PURE__ */ jsx("div", { children: w.definitions })] }) : w.definitions || "—"
								}),
								/* @__PURE__ */ jsx("td", { children: /* @__PURE__ */ jsx(Example, { text: w.example }) })
							] }, i)) })]
						})
					}),
					/* @__PURE__ */ jsxs("div", {
						className: "db-muted",
						children: [
							"Showing the first ",
							preview.words.length,
							" of ",
							fmt(preview.selectedCount),
							"."
						]
					})
				]
			}),
			/* @__PURE__ */ jsxs("div", {
				className: "db-attrib",
				children: [
					"The way decks are built here is adapted from",
					" ",
					/* @__PURE__ */ jsx("a", {
						href: "https://github.com/Sirush/Jiten",
						target: "_blank",
						rel: "noreferrer",
						children: "jiten.moe (Jiten, Apache-2.0)"
					}),
					": sentence-by-sentence parsing, dictionary-form de-duplication in first-appearance order, the example-sentence selection passes, and the deck.csv column format. This plugin is not affiliated with jiten.moe. See NOTICE-Jiten.md in the plugin folder."
				]
			})
		]
	});
}
function activate(ctx) {
	ctx.registerBookInfoTab({
		id: "deck-builder",
		title: "Deck Builder",
		icon: Layers,
		component: DeckBuilderTab
	});
}
export { activate };

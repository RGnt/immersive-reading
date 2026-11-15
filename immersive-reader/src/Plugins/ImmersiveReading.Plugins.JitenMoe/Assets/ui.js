import { useEffect, useState } from "react";
import { Download, ExternalLink, Globe, Loader2, Search, Sparkles } from "lucide-react";
import { refreshLibrary, usePluginConfig, usePluginRpc } from "@ir/sdk";
import { Fragment, jsx, jsxs } from "react/jsx-runtime";
// ../../Plugins/ImmersiveReading.Plugins.JitenMoe/Assets/ui.src.jsx
const PLUGIN_ID = "jitenMoe";
const URL_FIELD = "jiten_moe_url";
const DIFF_FIELD = "jiten_difficulty";
const cfValue = (v) => v == null ? "" : typeof v === "string" ? v : v.effectiveValueText ?? v.valueText ?? "";
const deckIdFromUrl = (url) => {
	const m = url && String(url).match(/(\d+)(?!.*\d)/);
	return m ? parseInt(m[1], 10) : null;
};
const diffClass = (d) => d < 3.5 ? "jm-diff-easy" : d < 6 ? "jm-diff-med" : d < 8 ? "jm-diff-hard" : "jm-diff-master";
const errMsg = (e) => e && e.message || String(e);
function DifficultyBadge({ books, context }) {
	let diff = null;
	for (const b of books) {
		const raw = cfValue(b.customFields?.[DIFF_FIELD]);
		const n = raw ? parseFloat(raw) : NaN;
		if (!isNaN(n)) {
			diff = n;
			break;
		}
	}
	if (diff === null) return context === "row" ? /* @__PURE__ */ jsx("span", {
		className: "jm-muted",
		style: { opacity: .35 },
		children: "—"
	}) : null;
	return /* @__PURE__ */ jsxs("span", {
		className: `jm-badge jm-${context} ${diffClass(diff)}`,
		title: `jiten.moe difficulty: ${diff.toFixed(2)} / 10`,
		children: ["★ ", diff.toFixed(1)]
	});
}
function Status({ status }) {
	if (!status) return null;
	return /* @__PURE__ */ jsx("div", {
		className: `jm-status ${status.type === "success" ? "jm-success" : status.type === "error" ? "jm-error" : ""}`,
		children: status.text
	});
}
function SearchResult({ item, busy, onDownload, onApply, rpc }) {
	const [vocab, setVocab] = useState(null);
	const [vocabOpen, setVocabOpen] = useState(false);
	const [subs, setSubs] = useState(null);
	const [subsOpen, setSubsOpen] = useState(false);
	const toggleVocab = async () => {
		setVocabOpen((o) => !o);
		if (!vocab) try {
			const res = await rpc("getVocabularyPreview", {
				deckId: item.deckId,
				limit: 15
			});
			setVocab(res?.words || []);
		} catch {
			setVocab([]);
		}
	};
	const toggleSubs = async () => {
		setSubsOpen((o) => !o);
		if (!subs) try {
			const res = await rpc("getDeckDetail", { deckId: item.deckId });
			setSubs(res?.subDecks || []);
		} catch {
			setSubs([]);
		}
	};
	return /* @__PURE__ */ jsxs("div", {
		className: "jm-result",
		children: [
			/* @__PURE__ */ jsxs("div", {
				className: "jm-row-flex",
				style: { alignItems: "flex-start" },
				children: [item.coverUrl && /* @__PURE__ */ jsx("img", {
					className: "jm-cover",
					src: item.coverUrl,
					alt: "",
					onError: (e) => e.target.style.display = "none"
				}), /* @__PURE__ */ jsxs("div", {
					style: {
						flex: 1,
						minWidth: 0
					},
					children: [
						/* @__PURE__ */ jsx("div", {
							className: "jm-title",
							style: { fontSize: 12 },
							children: item.originalTitle
						}),
						/* @__PURE__ */ jsx("div", {
							className: "jm-muted",
							children: [item.romajiTitle, item.englishTitle].filter(Boolean).join(" • ")
						}),
						/* @__PURE__ */ jsxs("div", {
							className: "jm-row-flex jm-wrap jm-muted",
							style: {
								fontSize: 10,
								margin: "6px 0"
							},
							children: [
								item.difficulty != null && /* @__PURE__ */ jsxs("span", {
									className: `jm-badge ${diffClass(item.difficulty)}`,
									children: [
										"★ ",
										item.difficulty.toFixed(1),
										"/10"
									]
								}),
								/* @__PURE__ */ jsxs("span", { children: ["Words: ", (item.wordCount || 0).toLocaleString()] }),
								/* @__PURE__ */ jsxs("span", { children: ["Chars: ", (item.characterCount || 0).toLocaleString()] }),
								item.childrenDeckCount > 0 && /* @__PURE__ */ jsxs("span", {
									className: "jm-link",
									children: [item.childrenDeckCount, " Volumes"]
								})
							]
						}),
						/* @__PURE__ */ jsxs("div", {
							className: "jm-row-flex jm-wrap",
							children: [
								/* @__PURE__ */ jsxs("button", {
									className: "jm-btn jm-primary",
									disabled: busy,
									onClick: () => onDownload(item.deckId, item),
									children: [/* @__PURE__ */ jsx(Download, { size: 12 }), " Download Deck CSV"]
								}),
								/* @__PURE__ */ jsx("button", {
									className: "jm-btn",
									onClick: () => onApply(item),
									children: "Apply Metadata"
								}),
								/* @__PURE__ */ jsx("button", {
									className: `jm-btn ${vocabOpen ? "jm-active" : ""}`,
									onClick: toggleVocab,
									children: "📖 Vocab Preview"
								}),
								item.childrenDeckCount > 0 && /* @__PURE__ */ jsxs("button", {
									className: `jm-btn ${subsOpen ? "jm-active" : ""}`,
									onClick: toggleSubs,
									children: [
										"▼ Volumes (",
										item.childrenDeckCount,
										")"
									]
								}),
								item.jitenUrl && /* @__PURE__ */ jsxs("a", {
									className: "jm-link",
									href: item.jitenUrl,
									target: "_blank",
									rel: "noreferrer",
									children: ["Jiten ", /* @__PURE__ */ jsx(ExternalLink, { size: 10 })]
								})
							]
						})
					]
				})]
			}),
			vocabOpen && /* @__PURE__ */ jsx("div", {
				className: "jm-inset",
				children: !vocab ? /* @__PURE__ */ jsx(Loader2, {
					size: 14,
					className: "animate-spin"
				}) : vocab.length === 0 ? /* @__PURE__ */ jsx("span", {
					className: "jm-muted",
					children: "No vocabulary preview available for this deck."
				}) : /* @__PURE__ */ jsx("div", {
					className: "jm-scroll",
					children: /* @__PURE__ */ jsxs("table", {
						className: "jm-table",
						children: [/* @__PURE__ */ jsx("thead", { children: /* @__PURE__ */ jsxs("tr", { children: [
							/* @__PURE__ */ jsx("th", { children: "Word" }),
							/* @__PURE__ */ jsx("th", { children: "Reading" }),
							/* @__PURE__ */ jsx("th", { children: "Meaning" })
						] }) }), /* @__PURE__ */ jsx("tbody", { children: vocab.map((w, i) => /* @__PURE__ */ jsxs("tr", { children: [
							/* @__PURE__ */ jsx("td", { children: w.reading || "—" }),
							/* @__PURE__ */ jsx("td", {
								className: "jm-mono",
								children: w.readingFurigana || "—"
							}),
							/* @__PURE__ */ jsx("td", {
								className: "jm-muted",
								children: w.mainDefinition || "—"
							})
						] }, i)) })]
					})
				})
			}),
			subsOpen && /* @__PURE__ */ jsx("div", {
				className: "jm-inset",
				children: !subs ? /* @__PURE__ */ jsx(Loader2, {
					size: 14,
					className: "animate-spin"
				}) : /* @__PURE__ */ jsx("div", {
					className: "jm-grid2",
					children: subs.map((s) => /* @__PURE__ */ jsxs("div", {
						className: "jm-result jm-row-flex jm-between",
						style: {
							flexDirection: "row",
							padding: 8
						},
						children: [/* @__PURE__ */ jsxs("div", {
							style: { minWidth: 0 },
							children: [/* @__PURE__ */ jsx("div", {
								className: "jm-title",
								style: { fontSize: 11 },
								children: s.originalTitle || `Volume #${s.deckId}`
							}), /* @__PURE__ */ jsxs("div", {
								className: "jm-muted",
								style: { fontSize: 10 },
								children: [
									s.difficulty ? `★ ${s.difficulty.toFixed(1)} • ` : "",
									(s.characterCount || 0).toLocaleString(),
									" chars"
								]
							})]
						}), /* @__PURE__ */ jsx("button", {
							className: "jm-btn jm-primary",
							disabled: busy,
							onClick: () => onDownload(s.deckId, s),
							children: "Apply Vol"
						})]
					}, s.deckId))
				})
			})
		]
	});
}
function BookTab({ bookId, book }) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const { config } = usePluginConfig(PLUGIN_ID);
	const summary = book?.summary || {};
	const jitenUrl = cfValue(summary.customFields?.[URL_FIELD]);
	const hasDeck = !!book?.hasDeckCsv;
	const [mediaType, setMediaType] = useState(config?.defaultMediaType ?? 4);
	const [query, setQuery] = useState(summary.seriesName || summary.title || "");
	const [results, setResults] = useState([]);
	const [searching, setSearching] = useState(false);
	const [busy, setBusy] = useState(false);
	const [status, setStatus] = useState(null);
	useEffect(() => {
		setQuery(summary.seriesName || summary.title || "");
		setResults([]);
		setStatus(null);
	}, [bookId]);
	const search = async (e) => {
		e?.preventDefault();
		if (!query.trim()) return;
		setSearching(true);
		setStatus(null);
		try {
			const res = await call("search", {
				query: query.trim(),
				mediaType: mediaType === 0 ? void 0 : mediaType
			});
			setResults(res?.items || []);
			if (!res?.items?.length) setStatus({ text: `No direct matches found on jiten.moe for "${query.trim()}".` });
		} catch (err) {
			setStatus({
				type: "error",
				text: `Search failed: ${errMsg(err)}`
			});
		} finally {
			setSearching(false);
		}
	};
	const apply = async (deck) => {
		try {
			await call("applyMetadata", {
				bookId,
				jitenUrl: deck.jitenUrl || `https://jiten.moe/decks/media/${deck.deckId}`,
				difficulty: deck.difficulty ?? void 0,
				description: deck.description ?? void 0,
				tags: [...deck.genres || [], ...(deck.tags || []).map((t) => t.name)]
			});
			setStatus({
				type: "success",
				text: `Applied jiten.moe deck #${deck.deckId} (difficulty ${deck.difficulty ?? "—"}).`
			});
			await refreshLibrary(bookId);
		} catch (err) {
			setStatus({
				type: "error",
				text: `Apply failed: ${errMsg(err)}`
			});
		}
	};
	const download = async (deckId, deckMeta) => {
		const id = deckId ?? deckIdFromUrl(jitenUrl);
		if (!id) {
			setStatus({
				type: "error",
				text: "No valid jiten.moe deck id. Search for a deck below."
			});
			return;
		}
		setBusy(true);
		setStatus({ text: "Downloading deck CSV from jiten.moe and attaching it to the book folder..." });
		try {
			const res = await call("downloadDeckCsv", {
				bookId,
				deckId: id
			});
			if (deckMeta) await apply(deckMeta);
			setStatus({
				type: "success",
				text: `Downloaded and attached deck.csv (${Math.round((res?.fileSizeBytes || 0) / 1024)} KB).`
			});
			await refreshLibrary(bookId);
		} catch (err) {
			setStatus({
				type: "error",
				text: `Download failed: ${errMsg(err)}`
			});
		} finally {
			setBusy(false);
		}
	};
	return /* @__PURE__ */ jsx("div", {
		className: "jm-root",
		children: /* @__PURE__ */ jsxs("div", {
			className: "jm-card-box",
			children: [
				/* @__PURE__ */ jsxs("div", {
					className: "jm-row-flex jm-between",
					children: [/* @__PURE__ */ jsxs("div", {
						className: "jm-row-flex",
						children: [/* @__PURE__ */ jsx("span", {
							className: "jm-title",
							children: "🌐 jiten.moe Media Integration"
						}), /* @__PURE__ */ jsx("span", {
							className: "jm-tag",
							children: "jiten.moe"
						})]
					}), jitenUrl && /* @__PURE__ */ jsxs("a", {
						className: "jm-link",
						href: jitenUrl,
						target: "_blank",
						rel: "noreferrer",
						children: ["Open Deck ", /* @__PURE__ */ jsx(ExternalLink, { size: 11 })]
					})]
				}),
				/* @__PURE__ */ jsxs("div", {
					className: "jm-inset jm-row-flex jm-between",
					children: [/* @__PURE__ */ jsxs("div", { children: [/* @__PURE__ */ jsxs("div", {
						className: "jm-title",
						style: { fontSize: 12 },
						children: [
							"Vocabulary Deck:",
							" ",
							hasDeck ? /* @__PURE__ */ jsxs("span", {
								className: "jm-ok",
								children: [
									"✓ ",
									book.deckCsvFileName || "deck.csv",
									" attached"
								]
							}) : /* @__PURE__ */ jsx("span", {
								className: "jm-warn",
								children: "⚠ no deck.csv in book folder"
							})
						]
					}), /* @__PURE__ */ jsx("div", {
						className: "jm-muted",
						children: hasDeck ? "The enrichment pipeline and study packs use this deck for vocabulary and pitch data." : "Download the vocabulary deck to enable frequency data, definitions and pitch accent."
					})] }), deckIdFromUrl(jitenUrl) ? /* @__PURE__ */ jsxs("button", {
						className: "jm-btn jm-primary",
						disabled: busy,
						onClick: () => download(),
						children: [busy ? /* @__PURE__ */ jsx(Loader2, {
							size: 13,
							className: "animate-spin"
						}) : /* @__PURE__ */ jsx(Download, { size: 13 }), hasDeck ? "Re-download CSV deck" : "1-click download & attach deck"]
					}) : !hasDeck && /* @__PURE__ */ jsx("span", {
						className: "jm-muted",
						children: /* @__PURE__ */ jsx("i", { children: "Search below to 1-click install" })
					})]
				}),
				/* @__PURE__ */ jsxs("form", {
					className: "jm-row-flex",
					onSubmit: search,
					children: [
						/* @__PURE__ */ jsxs("select", {
							className: "jm-select",
							value: mediaType,
							onChange: (e) => setMediaType(parseInt(e.target.value, 10)),
							children: [
								/* @__PURE__ */ jsx("option", {
									value: 4,
									children: "Novels / Light Novels"
								}),
								/* @__PURE__ */ jsx("option", {
									value: 0,
									children: "All Formats"
								}),
								/* @__PURE__ */ jsx("option", {
									value: 1,
									children: "Manga"
								}),
								/* @__PURE__ */ jsx("option", {
									value: 2,
									children: "Visual Novels"
								}),
								/* @__PURE__ */ jsx("option", {
									value: 3,
									children: "Anime"
								})
							]
						}),
						/* @__PURE__ */ jsx("input", {
							className: "jm-input",
							value: query,
							onChange: (e) => setQuery(e.target.value),
							placeholder: "Search series or book title on jiten.moe..."
						}),
						/* @__PURE__ */ jsxs("button", {
							type: "submit",
							className: "jm-btn jm-primary",
							disabled: searching,
							children: [
								/* @__PURE__ */ jsx(Search, { size: 13 }),
								" ",
								searching ? "Searching..." : "Search"
							]
						})
					]
				}),
				/* @__PURE__ */ jsx(Status, { status }),
				results.length > 0 && /* @__PURE__ */ jsx("div", {
					className: "jm-results",
					children: results.map((item) => /* @__PURE__ */ jsx(SearchResult, {
						item,
						busy,
						onDownload: download,
						onApply: apply,
						rpc: call
					}, item.deckId))
				})
			]
		})
	});
}
function SeriesMatcher({ seriesName, onClose }) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const [loading, setLoading] = useState(false);
	const [match, setMatch] = useState(null);
	const [query, setQuery] = useState(seriesName);
	const [searching, setSearching] = useState(false);
	const [results, setResults] = useState([]);
	const [importTags, setImportTags] = useState(true);
	const [updateSynopsis, setUpdateSynopsis] = useState(true);
	const [working, setWorking] = useState(false);
	const [status, setStatus] = useState(null);
	const load = async (deckId) => {
		setLoading(true);
		setStatus(null);
		try {
			const res = await call("matchSeries", {
				seriesName,
				deckId
			});
			if (res?.found) setMatch(res);
			else {
				setMatch(null);
				setStatus({ text: res?.message || `No matching franchise found on jiten.moe for "${seriesName}". Try searching.` });
			}
		} catch (err) {
			setStatus({
				type: "error",
				text: `Failed to match on jiten.moe: ${errMsg(err)}`
			});
		} finally {
			setLoading(false);
		}
	};
	useEffect(() => {
		load();
	}, [seriesName]);
	const search = async () => {
		if (!query.trim()) return;
		setSearching(true);
		try {
			const res = await call("search", { query: query.trim() });
			setResults(res?.items || []);
			if (!res?.items?.length) setStatus({ text: `No results found on jiten.moe for "${query.trim()}".` });
		} catch (err) {
			setStatus({
				type: "error",
				text: `Search failed: ${errMsg(err)}`
			});
		} finally {
			setSearching(false);
		}
	};
	const mappings = match?.mappings || [];
	const parent = match?.parentDeck;
	const applyMapping = async () => {
		setWorking(true);
		try {
			const res = await call("applySeriesMapping", {
				mappings,
				tags: importTags ? match.availableTags || [] : [],
				updateSynopsis
			});
			setStatus({
				type: "success",
				text: `Updated ${res?.updatedCount ?? mappings.length} volumes with jiten.moe metadata.`
			});
			await refreshLibrary();
		} catch (err) {
			setStatus({
				type: "error",
				text: `Failed to apply metadata: ${errMsg(err)}`
			});
		} finally {
			setWorking(false);
		}
	};
	const downloadAll = async () => {
		const downloads = mappings.filter((m) => m.matchedDeckId && m.bookId).map((m) => ({
			bookId: m.bookId,
			deckId: m.matchedDeckId
		}));
		if (downloads.length === 0) {
			setStatus({
				type: "error",
				text: "No mapped sub-decks with valid deck ids to download."
			});
			return;
		}
		setWorking(true);
		setStatus({ text: `Downloading ${downloads.length} deck CSV files from jiten.moe...` });
		try {
			const res = await call("downloadSeriesDecksCsv", { downloads });
			const failed = (res?.results || []).filter((r) => r.error);
			setStatus({
				type: failed.length ? "error" : "success",
				text: `Downloaded ${res?.downloadedCount ?? 0} deck.csv files${failed.length ? `; ${failed.length} failed (${failed[0].error})` : ""}.`
			});
			await refreshLibrary();
		} catch (err) {
			setStatus({
				type: "error",
				text: `Download failed: ${errMsg(err)}`
			});
		} finally {
			setWorking(false);
		}
	};
	return /* @__PURE__ */ jsxs("div", {
		className: "jm-root",
		style: { maxWidth: "none" },
		children: [
			/* @__PURE__ */ jsxs("div", {
				className: "jm-row-flex",
				children: [/* @__PURE__ */ jsx("input", {
					className: "jm-input",
					value: query,
					onChange: (e) => setQuery(e.target.value),
					onKeyDown: (e) => e.key === "Enter" && search(),
					placeholder: "Search series or novel title on jiten.moe..."
				}), /* @__PURE__ */ jsxs("button", {
					className: "jm-btn jm-primary",
					onClick: search,
					disabled: searching,
					children: [searching ? /* @__PURE__ */ jsx(Loader2, {
						size: 13,
						className: "animate-spin"
					}) : /* @__PURE__ */ jsx(Search, { size: 13 }), " Search"]
				})]
			}),
			/* @__PURE__ */ jsx(Status, { status }),
			results.length > 0 && /* @__PURE__ */ jsxs("div", {
				className: "jm-inset",
				children: [/* @__PURE__ */ jsxs("div", {
					className: "jm-muted",
					style: { marginBottom: 6 },
					children: [
						"Search results (",
						results.length,
						"). Click one to match this series against it:"
					]
				}), /* @__PURE__ */ jsx("div", {
					className: "jm-grid2",
					children: results.map((item) => /* @__PURE__ */ jsxs("div", {
						className: "jm-result jm-row-flex",
						style: {
							flexDirection: "row",
							cursor: "pointer",
							padding: 8
						},
						onClick: () => {
							setResults([]);
							load(item.deckId);
						},
						children: [item.coverUrl && /* @__PURE__ */ jsx("img", {
							className: "jm-cover",
							style: {
								width: 32,
								height: 44
							},
							src: item.coverUrl,
							alt: ""
						}), /* @__PURE__ */ jsxs("div", {
							style: { minWidth: 0 },
							children: [/* @__PURE__ */ jsx("div", {
								className: "jm-title",
								style: { fontSize: 11 },
								children: item.originalTitle || item.romajiTitle || item.englishTitle
							}), /* @__PURE__ */ jsxs("div", {
								className: "jm-muted",
								style: { fontSize: 10 },
								children: [
									"Deck #",
									item.deckId,
									item.difficulty ? ` • ★ ${item.difficulty.toFixed(1)}` : ""
								]
							})]
						})]
					}, item.deckId))
				})]
			}),
			loading ? /* @__PURE__ */ jsxs("div", {
				className: "jm-row-flex jm-muted",
				style: {
					justifyContent: "center",
					padding: 40
				},
				children: [/* @__PURE__ */ jsx(Loader2, {
					size: 18,
					className: "animate-spin"
				}), " Querying jiten.moe franchise and sub-decks..."]
			}) : parent ? /* @__PURE__ */ jsxs(Fragment, { children: [
				/* @__PURE__ */ jsxs("div", {
					className: "jm-card-box",
					style: {
						flexDirection: "row",
						alignItems: "flex-start"
					},
					children: [parent.coverUrl && /* @__PURE__ */ jsx("img", {
						className: "jm-cover jm-cover-lg",
						src: parent.coverUrl,
						alt: ""
					}), /* @__PURE__ */ jsxs("div", {
						style: {
							flex: 1,
							minWidth: 0
						},
						children: [
							/* @__PURE__ */ jsxs("div", {
								className: "jm-row-flex jm-between",
								children: [/* @__PURE__ */ jsx("span", {
									className: "jm-title",
									children: parent.originalTitle || parent.romajiTitle || seriesName
								}), parent.jitenUrl && /* @__PURE__ */ jsxs("a", {
									className: "jm-link",
									href: parent.jitenUrl,
									target: "_blank",
									rel: "noreferrer",
									children: ["View on jiten.moe ", /* @__PURE__ */ jsx(ExternalLink, { size: 11 })]
								})]
							}),
							parent.romajiTitle && parent.romajiTitle !== parent.originalTitle && /* @__PURE__ */ jsx("div", {
								className: "jm-muted",
								children: /* @__PURE__ */ jsx("i", { children: parent.romajiTitle })
							}),
							/* @__PURE__ */ jsxs("div", {
								className: "jm-row-flex jm-wrap",
								style: { marginTop: 6 },
								children: [
									parent.difficulty != null && /* @__PURE__ */ jsxs("span", {
										className: `jm-badge ${diffClass(parent.difficulty)}`,
										children: ["★ ", parent.difficulty.toFixed(2)]
									}),
									/* @__PURE__ */ jsxs("span", {
										className: "jm-muted",
										children: [match.subDecksCount || 0, " volume sub-decks"]
									}),
									/* @__PURE__ */ jsxs("span", {
										className: "jm-muted",
										children: [
											mappings.filter((m) => m.matchedDeckId).length,
											"/",
											mappings.length,
											" local volumes matched"
										]
									})
								]
							}),
							match.availableTags?.length > 0 && /* @__PURE__ */ jsxs("div", {
								className: "jm-row-flex jm-wrap",
								style: {
									marginTop: 6,
									gap: 4
								},
								children: [match.availableTags.slice(0, 8).map((t) => /* @__PURE__ */ jsx("span", {
									className: "jm-chip",
									children: t
								}, t)), match.availableTags.length > 8 && /* @__PURE__ */ jsxs("span", {
									className: "jm-muted",
									children: [
										"+",
										match.availableTags.length - 8,
										" more"
									]
								})]
							})
						]
					})]
				}),
				/* @__PURE__ */ jsx("div", {
					style: {
						border: "1px solid var(--border-color)",
						borderRadius: 8,
						overflow: "hidden",
						maxHeight: "22rem",
						overflowY: "auto"
					},
					children: /* @__PURE__ */ jsxs("table", {
						className: "jm-table",
						children: [/* @__PURE__ */ jsx("thead", { children: /* @__PURE__ */ jsxs("tr", { children: [
							/* @__PURE__ */ jsx("th", { children: "Vol #" }),
							/* @__PURE__ */ jsx("th", { children: "Local book" }),
							/* @__PURE__ */ jsx("th", { children: "Matched jiten.moe sub-deck" }),
							/* @__PURE__ */ jsx("th", { children: "Diff" }),
							/* @__PURE__ */ jsx("th", { children: "Chars" }),
							/* @__PURE__ */ jsx("th", { children: "Status" })
						] }) }), /* @__PURE__ */ jsx("tbody", { children: mappings.map((m, i) => /* @__PURE__ */ jsxs("tr", { children: [
							/* @__PURE__ */ jsxs("td", {
								className: "jm-mono",
								children: ["#", m.seriesIndex ?? i + 1]
							}),
							/* @__PURE__ */ jsx("td", {
								title: m.title,
								children: m.title
							}),
							/* @__PURE__ */ jsx("td", {
								className: "jm-muted",
								children: m.matchedTitle || /* @__PURE__ */ jsx("i", { children: "No sub-deck match" })
							}),
							/* @__PURE__ */ jsx("td", { children: m.difficulty ? /* @__PURE__ */ jsxs("span", {
								className: "jm-warn",
								children: ["★ ", m.difficulty.toFixed(1)]
							}) : "—" }),
							/* @__PURE__ */ jsx("td", {
								className: "jm-muted",
								children: m.characterCount ? m.characterCount.toLocaleString() : "—"
							}),
							/* @__PURE__ */ jsx("td", { children: m.matchedDeckId ? /* @__PURE__ */ jsx("span", {
								className: "jm-pill-ok",
								children: "✓ Mapped"
							}) : /* @__PURE__ */ jsx("span", {
								className: "jm-pill-miss",
								children: "Unmatched"
							}) })
						] }, m.bookId || i)) })]
					})
				}),
				/* @__PURE__ */ jsxs("div", {
					className: "jm-inset jm-row-flex jm-between",
					children: [/* @__PURE__ */ jsxs("label", {
						className: "jm-row-flex",
						children: [/* @__PURE__ */ jsx("input", {
							type: "checkbox",
							checked: importTags,
							onChange: (e) => setImportTags(e.target.checked)
						}), " Import jiten.moe tags and genres as book tags"]
					}), /* @__PURE__ */ jsxs("label", {
						className: "jm-row-flex",
						children: [/* @__PURE__ */ jsx("input", {
							type: "checkbox",
							checked: updateSynopsis,
							onChange: (e) => setUpdateSynopsis(e.target.checked)
						}), " Fill missing descriptions from the jiten.moe synopsis"]
					})]
				}),
				/* @__PURE__ */ jsxs("div", {
					className: "jm-row-flex",
					style: { justifyContent: "flex-end" },
					children: [
						/* @__PURE__ */ jsx("button", {
							className: "jm-btn",
							onClick: onClose,
							children: "Close"
						}),
						/* @__PURE__ */ jsxs("button", {
							className: "jm-btn",
							disabled: working,
							onClick: downloadAll,
							children: [working ? /* @__PURE__ */ jsx(Loader2, {
								size: 13,
								className: "animate-spin"
							}) : /* @__PURE__ */ jsx(Download, { size: 13 }), " Download all decks (.csv)"]
						}),
						/* @__PURE__ */ jsxs("button", {
							className: "jm-btn jm-primary",
							disabled: working,
							onClick: applyMapping,
							children: [working ? /* @__PURE__ */ jsx(Loader2, {
								size: 13,
								className: "animate-spin"
							}) : /* @__PURE__ */ jsx(Sparkles, { size: 13 }), " Apply mapping & sync"]
						})
					]
				})
			] }) : /* @__PURE__ */ jsxs("div", {
				className: "jm-muted",
				style: {
					textAlign: "center",
					padding: 32
				},
				children: [
					"No direct jiten.moe match for “",
					seriesName,
					"”. Use the search bar to look the franchise up by romaji or English title."
				]
			})
		]
	});
}
function activate(ctx) {
	ctx.registerBookInfoTab({
		id: "jiten",
		title: "jiten.moe",
		icon: Globe,
		component: BookTab
	});
	ctx.registerBookBadge({
		id: "difficulty",
		component: DifficultyBadge
	});
	ctx.registerSeriesAction({
		id: "match-series",
		label: "Match on jiten.moe",
		title: "Match this series against jiten.moe",
		icon: Sparkles,
		onClick: ({ seriesName, openModal }) => openModal({
			id: "jiten-series-matcher",
			title: `jiten.moe series matcher: ${seriesName}`,
			size: "xl",
			component: ({ onClose }) => /* @__PURE__ */ jsx(SeriesMatcher, {
				seriesName,
				onClose
			})
		})
	});
}
export { activate };

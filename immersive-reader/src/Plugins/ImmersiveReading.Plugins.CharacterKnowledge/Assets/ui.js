import React, { useCallback, useEffect, useRef, useState } from "react";
import { AlertTriangle, CheckCircle2, Loader2, MapPin, Network, Package, Play, RefreshCw, Shield, Square, Swords, Trash2, Users } from "lucide-react";
import { PluginDrawerShell, useActiveBook, usePluginRpc } from "@ir/sdk";
import { Fragment, jsx, jsxs } from "react/jsx-runtime";
// ../../Plugins/ImmersiveReading.Plugins.CharacterKnowledge/Assets/ui.src.jsx
const PLUGIN_ID = "characterKnowledge";
const errMsg = (e) => e && e.message || String(e);
const KINDS = [
	["battle", "Battle"],
	["hunt", "Hunt"],
	["dungeon", "Dungeon"],
	["quest", "Quest"],
	["item", "Item"],
	["growth", "Growth"],
	["travel", "Travel"],
	["meeting", "Meeting"],
	["group", "Group"],
	["revelation", "Revelation"],
	["death", "Death"],
	["other", "Other"]
];
const KIND_LABEL = Object.fromEntries(KINDS);
const SLOT_LABEL = {
	weapon: "Weapon",
	shield: "Shield",
	head: "Head",
	body: "Body",
	hands: "Hands",
	legs: "Legs",
	feet: "Feet",
	cloak: "Cloak",
	accessory: "Accessory",
	other: "Other"
};
const SLOT_ORDER = Object.keys(SLOT_LABEL);
const REASON_LABEL = {
	replaced: "replaced by newer gear",
	transferred: "given to someone else",
	changed: "carried instead / worn instead",
	lost: "lost or gone"
};
/** "ch. 4", or "vol 1 ch. 4" when it is not the volume being read. Chapters are 0-based in the data. */
const where = (pos, asOf) => `${asOf && pos.volume === asOf.volume ? "" : `vol ${pos.volume} `}ch. ${pos.chapter + 1}`;
/** Polls getStatus while a build runs (every 1.5 s) and once on mount / when the key changes. */
function useKnowledgeStatus(target) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const [status, setStatus] = useState(null);
	const [error, setError] = useState(null);
	const key = JSON.stringify(target);
	const alive = useRef(true);
	const reload = useCallback(async () => {
		try {
			const s = await call("getStatus", target);
			if (alive.current) {
				setStatus(s);
				setError(null);
			}
			return s;
		} catch (e) {
			if (alive.current) setError(errMsg(e));
			return null;
		}
	}, [key]);
	useEffect(() => {
		alive.current = true;
		reload();
		return () => {
			alive.current = false;
		};
	}, [reload]);
	const running = status?.job?.state === "running";
	useEffect(() => {
		if (!running) return void 0;
		const t = setInterval(reload, 1500);
		return () => clearInterval(t);
	}, [running, reload]);
	return {
		status,
		error,
		reload,
		call
	};
}
function Progress({ job }) {
	const pct = job.total > 0 ? Math.min(100, Math.round(job.done / job.total * 100)) : 0;
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-inset",
		children: [
			/* @__PURE__ */ jsxs("div", {
				className: "ck-row ck-between",
				children: [/* @__PURE__ */ jsxs("span", {
					className: "ck-title",
					children: [
						job.state === "running" && /* @__PURE__ */ jsx(Loader2, {
							size: 13,
							className: "animate-spin"
						}),
						" ",
						job.state === "running" ? job.phase === "story" ? "Reading the book again for events and items…" : "Reading the book…" : job.state === "done" ? "Finished" : job.state === "cancelled" ? "Cancelled" : "Stopped"
					]
				}), /* @__PURE__ */ jsxs("span", {
					className: "ck-muted",
					children: [
						job.done,
						" / ",
						job.total,
						" steps",
						job.skipped ? ` (${job.skipped} already up to date)` : ""
					]
				})]
			}),
			/* @__PURE__ */ jsx("div", {
				className: "ck-bar",
				children: /* @__PURE__ */ jsx("div", {
					className: "ck-bar-fill",
					style: { width: `${pct}%` }
				})
			}),
			job.state === "running" && job.currentChapter >= 0 && /* @__PURE__ */ jsxs("div", {
				className: "ck-muted",
				children: [
					"Volume ",
					job.currentVolume,
					", chapter ",
					job.currentChapter + 1,
					job.currentTitle ? `: ${job.currentTitle}` : ""
				]
			}),
			/* @__PURE__ */ jsxs("div", {
				className: "ck-muted",
				children: [
					job.opsApplied,
					" facts stored, ",
					job.opsRejected,
					" rejected",
					job.storyOpsApplied || job.storyOpsRejected ? `; events and items: ${job.storyOpsApplied} stored, ${job.storyOpsRejected} rejected` : "",
					job.splits ? `, ${job.splits} chapters split` : "",
					job.contextTokensLearned ? `; server limit ${job.contextTokensLearned} tokens` : "",
					job.model ? `; model ${job.model}` : ""
				]
			}),
			job.error && job.state !== "done" && /* @__PURE__ */ jsx("div", {
				className: "ck-err",
				children: job.error
			}),
			job.log?.length > 0 && /* @__PURE__ */ jsxs("details", { children: [/* @__PURE__ */ jsx("summary", {
				className: "ck-muted",
				children: "Details"
			}), /* @__PURE__ */ jsx("pre", {
				className: "ck-log",
				children: job.log.join("\n")
			})] })
		]
	});
}
function TraitList({ traits }) {
	const entries = Object.entries(traits || {});
	if (entries.length === 0) return /* @__PURE__ */ jsx("div", {
		className: "ck-muted",
		children: "Nothing is known yet."
	});
	return /* @__PURE__ */ jsx("dl", {
		className: "ck-traits",
		children: entries.map(([k, values]) => /* @__PURE__ */ jsxs(React.Fragment, { children: [/* @__PURE__ */ jsx("dt", { children: k.replace(/_/g, " ") }), /* @__PURE__ */ jsx("dd", { children: Array.isArray(values) ? values.join("; ") : String(values) })] }, k))
	});
}
function ProfileView({ profile }) {
	if (!profile) return null;
	const p = profile.profile || {};
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-profile",
		children: [
			/* @__PURE__ */ jsx("div", {
				className: "ck-title",
				style: { fontSize: 15 },
				children: profile.name
			}),
			p.aliases?.length > 0 && /* @__PURE__ */ jsxs("div", {
				className: "ck-muted",
				children: ["Also known as: ", p.aliases.join("、")]
			}),
			/* @__PURE__ */ jsx(TraitList, { traits: p.traits }),
			p.relationships?.length > 0 && /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsx("div", {
				className: "ck-sub",
				children: "Relationships"
			}), /* @__PURE__ */ jsx("ul", {
				className: "ck-list",
				children: p.relationships.map((r, i) => /* @__PURE__ */ jsxs("li", { children: [
					/* @__PURE__ */ jsx("b", { children: r.other }),
					" ",
					/* @__PURE__ */ jsxs("span", {
						className: "ck-muted",
						children: [
							"(",
							r.type,
							")"
						]
					}),
					" ",
					r.summary
				] }, i))
			})] }),
			(p.equipment?.length > 0 || p.belongings?.length > 0) && /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsx("div", {
				className: "ck-sub",
				children: "Equipment"
			}), /* @__PURE__ */ jsxs("dl", {
				className: "ck-traits",
				children: [(p.equipment || []).map((e, i) => /* @__PURE__ */ jsxs(React.Fragment, { children: [/* @__PURE__ */ jsx("dt", { children: SLOT_LABEL[e.slot] || e.slot }), /* @__PURE__ */ jsxs("dd", { children: [e.item, e.replaces ? /* @__PURE__ */ jsxs("span", {
					className: "ck-tag ck-tag-new",
					children: ["replaces ", e.replaces]
				}) : null] })] }, `${e.slot}:${i}`)), p.belongings?.length > 0 && /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsx("dt", { children: "carrying" }), /* @__PURE__ */ jsx("dd", { children: p.belongings.join("、") })] })]
			})] }),
			p.former_gear?.length > 0 && /* @__PURE__ */ jsxs("div", {
				className: "ck-muted",
				children: ["Formerly: ", p.former_gear.map((g) => `${g.item} (${(SLOT_LABEL[g.slot] || g.slot).toLowerCase()}, ${g.replaced_by ? `replaced by ${g.replaced_by}, ` : ""}until ch. ${g.until_chapter + 1})`).join("; ")]
			}),
			p.timeline?.length > 0 && /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsx("div", {
				className: "ck-sub",
				children: "What has happened"
			}), /* @__PURE__ */ jsx("ul", {
				className: "ck-list",
				children: p.timeline.map((t, i) => /* @__PURE__ */ jsxs("li", { children: [
					/* @__PURE__ */ jsxs("span", {
						className: "ck-muted",
						children: ["ch. ", (t.chapter ?? 0) + 1]
					}),
					" ",
					t.kind && /* @__PURE__ */ jsx("span", {
						className: `ck-kind ck-kind-${t.kind}`,
						children: KIND_LABEL[t.kind] || t.kind
					}),
					" ",
					t.summary
				] }, i))
			})] }),
			p.speech_samples?.length > 0 && /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsx("div", {
				className: "ck-sub",
				children: "How they speak"
			}), /* @__PURE__ */ jsx("ul", {
				className: "ck-list",
				children: p.speech_samples.map((s, i) => /* @__PURE__ */ jsx("li", { children: s }, i))
			})] })
		]
	});
}
function SpoilerNote({ asOf }) {
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-note",
		children: [
			/* @__PURE__ */ jsx(Shield, { size: 12 }),
			" Only what a reader knows before chapter ",
			asOf.chapter + 1,
			asOf.volume > 1 ? ` of volume ${asOf.volume}` : "",
			". The current chapter is hidden until you have read it."
		]
	});
}
/** Character list + profile for one book as of a chapter. Used by the tab and by the reader drawer. */
function Browser({ bookId, chapterIndex, compact }) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const [state, setState] = useState({ loading: true });
	const [selected, setSelected] = useState(null);
	const [profile, setProfile] = useState(null);
	const [filter, setFilter] = useState("");
	useEffect(() => {
		let cancelled = false;
		setState({ loading: true });
		setProfile(null);
		call("listCharacters", {
			bookId,
			chapterIndex
		}).then((r) => {
			if (!cancelled) setState({
				asOf: r.asOf,
				characters: r.characters || []
			});
		}).catch((e) => {
			if (!cancelled) setState({ error: errMsg(e) });
		});
		return () => {
			cancelled = true;
		};
	}, [bookId, chapterIndex]);
	useEffect(() => {
		if (!selected) {
			setProfile(null);
			return;
		}
		let cancelled = false;
		call("getProfile", {
			bookId,
			chapterIndex,
			characterId: selected
		}).then((r) => {
			if (!cancelled) setProfile(r.profile);
		}).catch(() => {
			if (!cancelled) setProfile(null);
		});
		return () => {
			cancelled = true;
		};
	}, [
		selected,
		bookId,
		chapterIndex,
		state.asOf?.chapter
	]);
	if (state.loading) return /* @__PURE__ */ jsxs("div", {
		className: "ck-muted",
		children: [/* @__PURE__ */ jsx(Loader2, {
			size: 13,
			className: "animate-spin"
		}), " Loading…"]
	});
	if (state.error) return /* @__PURE__ */ jsx("div", {
		className: "ck-err",
		children: state.error
	});
	const q = filter.trim().toLowerCase();
	const shown = state.characters.filter((c) => !q || c.name.toLowerCase().includes(q));
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-col",
		children: [/* @__PURE__ */ jsx(SpoilerNote, { asOf: state.asOf }), state.characters.length === 0 ? /* @__PURE__ */ jsx("div", {
			className: "ck-muted",
			children: "No characters are known at this point of the story (or this part has not been built yet)."
		}) : /* @__PURE__ */ jsxs("div", {
			className: compact ? "ck-col" : "ck-split",
			children: [/* @__PURE__ */ jsxs("div", {
				className: "ck-col",
				style: { minWidth: 0 },
				children: [/* @__PURE__ */ jsx("input", {
					className: "ck-input",
					placeholder: "Filter characters…",
					value: filter,
					onChange: (e) => setFilter(e.target.value)
				}), /* @__PURE__ */ jsx("div", {
					className: "ck-chars",
					children: shown.map((c) => /* @__PURE__ */ jsx("button", {
						className: `ck-char ${selected === c.characterId ? "ck-active" : ""}`,
						onClick: () => setSelected(c.characterId === selected ? null : c.characterId),
						children: c.name
					}, c.characterId))
				})]
			}), /* @__PURE__ */ jsx("div", {
				style: { minWidth: 0 },
				children: selected ? /* @__PURE__ */ jsx(ProfileView, { profile }) : /* @__PURE__ */ jsx("div", {
					className: "ck-muted",
					children: "Select a character."
				})
			})]
		})]
	});
}
function Chip({ kind, onClick, title, children }) {
	return /* @__PURE__ */ jsx("button", {
		type: "button",
		className: `ck-chip ck-chip-${kind}`,
		onClick,
		title,
		disabled: !onClick,
		children
	});
}
/** Events newest first; children (parts of a larger happening) are listed oldest first under their parent. */
function nestEvents(events) {
	const ids = new Set(events.map((e) => e.id));
	const kids = /* @__PURE__ */ new Map();
	const roots = [];
	for (const e of events) if (e.parent && ids.has(e.parent)) {
		if (!kids.has(e.parent)) kids.set(e.parent, []);
		kids.get(e.parent).push(e);
	} else roots.push(e);
	for (const list of kids.values()) list.reverse();
	return {
		roots,
		kids
	};
}
function EventNode({ event, kids, asOf, onCharacter, onItem, nested }) {
	const children = kids.get(event.id) || [];
	return /* @__PURE__ */ jsxs("div", {
		className: nested ? "ck-event ck-nested" : "ck-event",
		children: [
			/* @__PURE__ */ jsxs("div", {
				className: "ck-row ck-wrap",
				children: [
					event.typed && /* @__PURE__ */ jsx("span", {
						className: `ck-kind ck-kind-${event.kind}`,
						children: KIND_LABEL[event.kind] || event.kind
					}),
					/* @__PURE__ */ jsx("b", { children: event.title || event.summary }),
					/* @__PURE__ */ jsx("span", {
						className: "ck-muted",
						children: where(event, asOf)
					})
				]
			}),
			event.title && event.summary && event.summary !== event.title && /* @__PURE__ */ jsx("div", { children: event.summary }),
			(event.location || event.participants.length > 0) && /* @__PURE__ */ jsxs("div", {
				className: "ck-row ck-wrap",
				children: [event.location && /* @__PURE__ */ jsxs(Chip, {
					kind: "location",
					title: "Where it happened",
					children: [
						/* @__PURE__ */ jsx(MapPin, { size: 10 }),
						" ",
						event.location.name
					]
				}), event.participants.map((p) => /* @__PURE__ */ jsxs(Chip, {
					kind: p.kind,
					title: p.kind === "character" ? "Show only their events" : p.kind === "item" ? "Show this item" : p.kind,
					onClick: p.kind === "character" ? () => onCharacter(p.id, p.name) : p.kind === "item" ? () => onItem(p.id) : void 0,
					children: [p.name, p.role ? /* @__PURE__ */ jsxs("span", {
						className: "ck-muted",
						children: [" · ", p.role]
					}) : null]
				}, `${p.id}:${p.role}`))]
			}),
			children.map((c) => /* @__PURE__ */ jsx(EventNode, {
				event: c,
				kids,
				asOf,
				onCharacter,
				onItem,
				nested: true
			}, c.id))
		]
	});
}
/** What has happened so far (as of a chapter): typed events with the characters, monsters, items and places involved. */
function EventsView({ bookId, chapterIndex, onItem }) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const [characters, setCharacters] = useState([]);
	const [who, setWho] = useState({
		id: "",
		name: ""
	});
	const [kind, setKind] = useState("");
	const [text, setText] = useState("");
	const [state, setState] = useState({ loading: true });
	useEffect(() => {
		setWho({
			id: "",
			name: ""
		});
	}, [bookId]);
	useEffect(() => {
		let cancelled = false;
		call("listCharacters", {
			bookId,
			chapterIndex
		}).then((r) => {
			if (!cancelled) setCharacters(r.characters || []);
		}).catch(() => {
			if (!cancelled) setCharacters([]);
		});
		return () => {
			cancelled = true;
		};
	}, [bookId, chapterIndex]);
	useEffect(() => {
		let cancelled = false;
		setState((s) => ({
			...s,
			loading: true
		}));
		call("listEvents", {
			bookId,
			chapterIndex,
			characterId: who.id || void 0,
			kind: kind || void 0,
			limit: 500
		}).then((r) => {
			if (!cancelled) setState({
				asOf: r.asOf,
				total: r.total,
				events: r.events || []
			});
		}).catch((e) => {
			if (!cancelled) setState({ error: errMsg(e) });
		});
		return () => {
			cancelled = true;
		};
	}, [
		bookId,
		chapterIndex,
		who.id,
		kind
	]);
	if (state.error) return /* @__PURE__ */ jsx("div", {
		className: "ck-err",
		children: state.error
	});
	if (!state.asOf) return /* @__PURE__ */ jsxs("div", {
		className: "ck-muted",
		children: [/* @__PURE__ */ jsx(Loader2, {
			size: 13,
			className: "animate-spin"
		}), " Loading…"]
	});
	const q = text.trim().toLowerCase();
	const matches = (e) => !q || [
		e.title,
		e.summary,
		e.location?.name,
		...e.participants.map((p) => p.name)
	].some((v) => v && v.toLowerCase().includes(q));
	const events = state.events.filter(matches);
	const { roots, kids } = nestEvents(events);
	const known = characters.some((c) => c.characterId === who.id);
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-col",
		children: [
			/* @__PURE__ */ jsx(SpoilerNote, { asOf: state.asOf }),
			/* @__PURE__ */ jsxs("div", {
				className: "ck-row ck-wrap",
				children: [
					/* @__PURE__ */ jsxs("select", {
						className: "ck-select",
						value: who.id,
						onChange: (e) => setWho({
							id: e.target.value,
							name: characters.find((c) => c.characterId === e.target.value)?.name || ""
						}),
						title: "Only events this character took part in",
						children: [
							/* @__PURE__ */ jsx("option", {
								value: "",
								children: "Everyone"
							}),
							who.id && !known && /* @__PURE__ */ jsx("option", {
								value: who.id,
								children: who.name
							}),
							characters.map((c) => /* @__PURE__ */ jsx("option", {
								value: c.characterId,
								children: c.name
							}, c.characterId))
						]
					}),
					/* @__PURE__ */ jsxs("select", {
						className: "ck-select",
						value: kind,
						onChange: (e) => setKind(e.target.value),
						children: [/* @__PURE__ */ jsx("option", {
							value: "",
							children: "All kinds"
						}), KINDS.map(([k, label]) => /* @__PURE__ */ jsx("option", {
							value: k,
							children: label
						}, k))]
					}),
					/* @__PURE__ */ jsx("input", {
						className: "ck-input",
						style: { flex: 1 },
						placeholder: "Search events…",
						value: text,
						onChange: (e) => setText(e.target.value)
					})
				]
			}),
			state.total === 0 ? /* @__PURE__ */ jsxs("div", {
				className: "ck-muted",
				children: [
					"No events are known at this point of the story",
					who.id || kind ? " for this filter" : "",
					" (or the events of this part have not been built yet)."
				]
			}) : /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsxs("div", {
				className: "ck-muted",
				children: [
					events.length === state.total ? `${state.total} events` : `${events.length} of ${state.total} events shown`,
					state.total > state.events.length ? ` (the newest ${state.events.length} were loaded)` : "",
					", newest first"
				]
			}), /* @__PURE__ */ jsx("div", {
				className: "ck-col",
				children: roots.map((e) => /* @__PURE__ */ jsx(EventNode, {
					event: e,
					kids,
					asOf: state.asOf,
					onCharacter: (id, name) => setWho({
						id,
						name
					}),
					onItem
				}, e.id))
			})] })
		]
	});
}
function ItemRow({ item, asOf, open, onToggle, showHolder, badge }) {
	const d = item.description || {};
	const ref = useRef(null);
	useEffect(() => {
		if (open && ref.current?.scrollIntoView) ref.current.scrollIntoView({ block: "nearest" });
	}, [open]);
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-item",
		ref,
		children: [
			/* @__PURE__ */ jsxs("button", {
				type: "button",
				className: "ck-item-head",
				onClick: onToggle,
				children: [
					badge && /* @__PURE__ */ jsx("span", {
						className: "ck-kind ck-kind-slot",
						children: badge
					}),
					/* @__PURE__ */ jsx("b", { children: item.name }),
					item.replaces && /* @__PURE__ */ jsxs("span", {
						className: "ck-tag ck-tag-new",
						title: "Newer gear: it took this slot over",
						children: ["replaces ", item.replaces.name]
					}),
					item.replaced_by && /* @__PURE__ */ jsxs("span", {
						className: "ck-tag ck-tag-old",
						title: "Older gear: it is no longer used",
						children: ["replaced by ", item.replaced_by.name]
					}),
					showHolder && /* @__PURE__ */ jsx("span", {
						className: "ck-muted",
						children: item.holder ? `${item.holder.name}${item.state === "equipped" ? ` (${(SLOT_LABEL[item.slot] || item.slot).toLowerCase()})` : ""}` : "nobody has it now"
					})
				]
			}),
			(d.appearance || d.ability || d.note || d.status) && /* @__PURE__ */ jsx("div", {
				className: "ck-muted",
				children: [
					...d.appearance || [],
					...d.ability || [],
					...d.note || [],
					...d.status || []
				].join(" · ")
			}),
			open && /* @__PURE__ */ jsxs("ul", {
				className: "ck-list",
				children: [item.history.map((h, i) => /* @__PURE__ */ jsxs("li", { children: [
					/* @__PURE__ */ jsx("b", { children: h.holder.name }),
					": ",
					h.state === "equipped" ? `worn (${(SLOT_LABEL[h.slot] || h.slot).toLowerCase()})` : "carried",
					", ",
					where(h.from, asOf),
					h.to ? ` to ${where(h.to, asOf)}${h.reason ? ` (${h.reason === "replaced" && h.replaced_by ? `replaced by ${h.replaced_by.name}` : REASON_LABEL[h.reason] || h.reason})` : ""}` : " until now",
					h.replaces ? /* @__PURE__ */ jsxs("span", {
						className: "ck-tag ck-tag-new",
						children: ["replaced ", h.replaces.name]
					}) : null
				] }, i)), item.history.length === 0 && /* @__PURE__ */ jsx("li", {
					className: "ck-muted",
					children: "Nobody is known to have had it yet."
				})]
			})
		]
	});
}
/** Items the characters have as of a chapter: grouped by who has them (worn gear by slot first), or all items. */
function ItemsView({ bookId, chapterIndex, focus }) {
	const { call } = usePluginRpc(PLUGIN_ID);
	const [state, setState] = useState({ loading: true });
	const [mode, setMode] = useState("holder");
	const [text, setText] = useState("");
	const [open, setOpen] = useState(() => /* @__PURE__ */ new Set());
	useEffect(() => {
		let cancelled = false;
		setState({ loading: true });
		call("listItems", {
			bookId,
			chapterIndex
		}).then((r) => {
			if (!cancelled) setState({
				asOf: r.asOf,
				items: r.items || []
			});
		}).catch((e) => {
			if (!cancelled) setState({ error: errMsg(e) });
		});
		return () => {
			cancelled = true;
		};
	}, [bookId, chapterIndex]);
	useEffect(() => {
		if (!focus) return;
		setMode("all");
		setText("");
		setOpen((o) => new Set(o).add(focus));
	}, [focus]);
	if (state.error) return /* @__PURE__ */ jsx("div", {
		className: "ck-err",
		children: state.error
	});
	if (!state.asOf) return /* @__PURE__ */ jsxs("div", {
		className: "ck-muted",
		children: [/* @__PURE__ */ jsx(Loader2, {
			size: 13,
			className: "animate-spin"
		}), " Loading…"]
	});
	const toggle = (id) => setOpen((o) => {
		const n = new Set(o);
		if (n.has(id)) n.delete(id);
		else n.add(id);
		return n;
	});
	const q = text.trim().toLowerCase();
	const items = state.items.filter((i) => !q || i.name.toLowerCase().includes(q) || i.holder?.name.toLowerCase().includes(q));
	const groups = /* @__PURE__ */ new Map();
	for (const item of items) {
		const key = item.holder ? item.holder.id : "";
		if (!groups.has(key)) groups.set(key, {
			name: item.holder ? item.holder.name : "Nobody has them now",
			items: []
		});
		groups.get(key).items.push(item);
	}
	const slotRank = (i) => i.state === "equipped" ? SLOT_ORDER.indexOf(i.slot) : 100;
	const ordered = [...groups.entries()].sort(([ka, a], [kb, b]) => (ka === "") - (kb === "") || b.items.length - a.items.length || a.name.localeCompare(b.name));
	for (const [, g] of ordered) g.items.sort((a, b) => slotRank(a) - slotRank(b) || a.name.localeCompare(b.name));
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-col",
		children: [
			/* @__PURE__ */ jsx(SpoilerNote, { asOf: state.asOf }),
			/* @__PURE__ */ jsxs("div", {
				className: "ck-row ck-wrap",
				children: [
					/* @__PURE__ */ jsx("button", {
						className: `ck-btn ${mode === "holder" ? "ck-active" : ""}`,
						onClick: () => setMode("holder"),
						children: "By character"
					}),
					/* @__PURE__ */ jsx("button", {
						className: `ck-btn ${mode === "all" ? "ck-active" : ""}`,
						onClick: () => setMode("all"),
						children: "All items"
					}),
					/* @__PURE__ */ jsx("input", {
						className: "ck-input",
						style: { flex: 1 },
						placeholder: "Search items or people…",
						value: text,
						onChange: (e) => setText(e.target.value)
					})
				]
			}),
			state.items.length === 0 ? /* @__PURE__ */ jsx("div", {
				className: "ck-muted",
				children: "No items are known at this point of the story (or the items of this part have not been built yet)."
			}) : mode === "all" ? /* @__PURE__ */ jsx("div", {
				className: "ck-col",
				children: items.map((i) => /* @__PURE__ */ jsx(ItemRow, {
					item: i,
					asOf: state.asOf,
					open: open.has(i.id),
					onToggle: () => toggle(i.id),
					showHolder: true
				}, i.id))
			}) : /* @__PURE__ */ jsx("div", {
				className: "ck-col",
				children: ordered.map(([key, g]) => /* @__PURE__ */ jsxs("div", {
					className: "ck-inset",
					children: [/* @__PURE__ */ jsxs("div", {
						className: "ck-title",
						children: [
							g.name,
							" ",
							/* @__PURE__ */ jsxs("span", {
								className: "ck-muted",
								children: [
									"(",
									g.items.length,
									")"
								]
							})
						]
					}), g.items.map((i) => /* @__PURE__ */ jsx(ItemRow, {
						item: i,
						asOf: state.asOf,
						open: open.has(i.id),
						onToggle: () => toggle(i.id),
						badge: i.state === "equipped" ? SLOT_LABEL[i.slot] || i.slot : i.holder ? "carrying" : ""
					}, i.id))]
				}, key || "nobody"))
			})
		]
	});
}
function BuildPanel({ target, scopeChoice, bookId }) {
	const { status, error, reload, call } = useKnowledgeStatus(target);
	const [scope, setScope] = useState(scopeChoice ? "upToVolume" : "series");
	const [estimate, setEstimate] = useState(null);
	const [endpoint, setEndpoint] = useState(null);
	const [busy, setBusy] = useState(false);
	const [message, setMessage] = useState(null);
	useEffect(() => {
		setEstimate(null);
	}, [scope, JSON.stringify(target)]);
	const withScope = {
		...target,
		scope
	};
	const run = async (fn) => {
		setBusy(true);
		setMessage(null);
		try {
			await fn();
		} catch (e) {
			setMessage({
				error: true,
				text: errMsg(e)
			});
		} finally {
			setBusy(false);
		}
	};
	const doEstimate = () => run(async () => setEstimate(await call("estimateBuild", withScope)));
	const doTest = () => run(async () => setEndpoint(await call("testEndpoint", {})));
	const doStart = (rebuild) => run(async () => {
		const r = await call("startBuild", {
			...withScope,
			rebuild
		});
		if (!r?.started && r?.error) setMessage({
			error: true,
			text: r.error
		});
		await reload();
	});
	const doCancel = () => run(async () => {
		await call("cancelBuild", target);
		await reload();
	});
	const doDelete = () => run(async () => {
		if (!window.confirm("Delete the knowledge built for this book/series? It can be rebuilt, but that takes a while.")) return;
		await call("deleteKnowledge", target);
		setEstimate(null);
		await reload();
	});
	if (error && !status) return /* @__PURE__ */ jsx("div", {
		className: "ck-err",
		children: error
	});
	if (!status) return /* @__PURE__ */ jsxs("div", {
		className: "ck-muted",
		children: [/* @__PURE__ */ jsx(Loader2, {
			size: 13,
			className: "animate-spin"
		}), " Loading…"]
	});
	const job = status.job;
	const running = job?.state === "running";
	const builtTotal = status.series.volumes.reduce((n, v) => n + v.built, 0);
	const chapterTotal = status.series.volumes.reduce((n, v) => n + v.chapterCount, 0);
	const withStory = !!status.knowledge.extractStory;
	const storyTotal = status.series.volumes.reduce((n, v) => n + (v.storyBuilt || 0), 0);
	const addStoryOnly = withStory && builtTotal > 0 && builtTotal >= chapterTotal && storyTotal < chapterTotal;
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-col",
		children: [
			!status.builder.found && /* @__PURE__ */ jsxs("div", {
				className: "ck-warnbox",
				children: [
					/* @__PURE__ */ jsx(AlertTriangle, { size: 14 }),
					" ",
					/* @__PURE__ */ jsxs("div", { children: [/* @__PURE__ */ jsx("b", { children: "The builder is not set up." }), /* @__PURE__ */ jsx("pre", {
						className: "ck-log",
						children: status.builder.problem
					})] })
				]
			}),
			status.series.orderChanged && /* @__PURE__ */ jsxs("div", {
				className: "ck-warnbox",
				children: [/* @__PURE__ */ jsx(AlertTriangle, { size: 14 }), " The volumes of this series are ordered differently than when the knowledge was built. Rebuild so the spoiler boundaries match the new order."]
			}),
			/* @__PURE__ */ jsxs("div", {
				className: "ck-inset",
				children: [/* @__PURE__ */ jsxs("div", {
					className: "ck-row ck-between ck-wrap",
					children: [/* @__PURE__ */ jsxs("div", { children: [/* @__PURE__ */ jsx("div", {
						className: "ck-title",
						children: status.series.title
					}), /* @__PURE__ */ jsxs("div", {
						className: "ck-muted",
						children: [
							builtTotal,
							" of ",
							chapterTotal,
							" chapters built across ",
							status.series.volumes.length,
							" volume",
							status.series.volumes.length === 1 ? "" : "s",
							status.knowledge.exists ? `; ${status.knowledge.counts.entities} entities, ${status.knowledge.counts.relations} relationships, ${status.knowledge.counts.events} events` : "",
							withStory ? `; events and items: ${storyTotal} of ${chapterTotal} chapters${status.knowledge.hasStory ? ` (${status.knowledge.counts.eventCards} events, ${status.knowledge.counts.itemCards} items)` : ""}` : ""
						]
					})] }), /* @__PURE__ */ jsxs("div", {
						className: "ck-muted ck-right",
						children: [
							"Model: ",
							status.llm.configured ? `${status.llm.model || "server default"} @ ${status.llm.endpoint}` : /* @__PURE__ */ jsx("span", {
								className: "ck-warn",
								children: "not configured"
							}),
							status.llm.usesSettingsAi && " (Settings → AI)"
						]
					})]
				}), status.series.volumes.length > 1 && /* @__PURE__ */ jsxs("table", {
					className: "ck-table",
					children: [/* @__PURE__ */ jsx("thead", { children: /* @__PURE__ */ jsxs("tr", { children: [
						/* @__PURE__ */ jsx("th", { children: "Vol" }),
						/* @__PURE__ */ jsx("th", { children: "Book" }),
						/* @__PURE__ */ jsx("th", { children: "Characters" }),
						withStory && /* @__PURE__ */ jsx("th", { children: "Events and items" }),
						/* @__PURE__ */ jsx("th", {})
					] }) }), /* @__PURE__ */ jsx("tbody", { children: status.series.volumes.map((v) => /* @__PURE__ */ jsxs("tr", {
						className: v.bookId === bookId ? "ck-current" : "",
						children: [
							/* @__PURE__ */ jsx("td", {
								className: "ck-mono",
								children: v.volume
							}),
							/* @__PURE__ */ jsx("td", { children: v.title }),
							/* @__PURE__ */ jsxs("td", { children: [
								v.built,
								"/",
								v.chapterCount,
								v.failed ? /* @__PURE__ */ jsxs("span", {
									className: "ck-err",
									children: [
										" (",
										v.failed,
										" failed)"
									]
								}) : null
							] }),
							withStory && /* @__PURE__ */ jsxs("td", { children: [
								v.storyBuilt,
								"/",
								v.chapterCount,
								v.storyFailed ? /* @__PURE__ */ jsxs("span", {
									className: "ck-err",
									children: [
										" (",
										v.storyFailed,
										" failed)"
									]
								}) : null
							] }),
							/* @__PURE__ */ jsx("td", { children: v.built === v.chapterCount && v.chapterCount > 0 && (!withStory || v.storyBuilt === v.chapterCount) ? /* @__PURE__ */ jsx(CheckCircle2, {
								size: 13,
								className: "ck-ok"
							}) : null })
						]
					}, v.bookId)) })]
				})]
			}),
			/* @__PURE__ */ jsxs("div", {
				className: "ck-inset ck-col",
				children: [
					/* @__PURE__ */ jsxs("div", {
						className: "ck-title",
						children: ["Build the knowledge", withStory ? " (characters, then events and items)" : " of the characters"]
					}),
					/* @__PURE__ */ jsxs("div", {
						className: "ck-muted",
						children: [
							"The book text is sent chapter by chapter to ",
							/* @__PURE__ */ jsx("b", { children: status.llm.endpoint || "the configured model" }),
							" (once per book; finished chapters are kept and only changed ones are redone).",
							withStory ? " After the characters, a second pass records what happened (battles, hunts, dungeons, drops, quests) and who has which item. It can be added to a book whose characters are already built. It can be switched off in the plugin settings." : " Events and items are switched off in the plugin settings."
						]
					}),
					scopeChoice && /* @__PURE__ */ jsxs("div", {
						className: "ck-row ck-wrap",
						children: [/* @__PURE__ */ jsxs("label", {
							className: "ck-row",
							children: [
								/* @__PURE__ */ jsx("input", {
									type: "radio",
									checked: scope === "upToVolume",
									onChange: () => setScope("upToVolume")
								}),
								" This volume",
								status.currentVolume > 1 ? " and the ones before it" : ""
							]
						}), /* @__PURE__ */ jsxs("label", {
							className: "ck-row",
							children: [/* @__PURE__ */ jsx("input", {
								type: "radio",
								checked: scope === "series",
								onChange: () => setScope("series")
							}), " The whole series"]
						})]
					}),
					/* @__PURE__ */ jsxs("div", {
						className: "ck-row ck-wrap",
						children: [
							/* @__PURE__ */ jsx("button", {
								className: "ck-btn",
								disabled: busy || running,
								onClick: doTest,
								children: "Test connection"
							}),
							/* @__PURE__ */ jsx("button", {
								className: "ck-btn",
								disabled: busy || running,
								onClick: doEstimate,
								children: "Estimate"
							}),
							running ? /* @__PURE__ */ jsxs("button", {
								className: "ck-btn ck-danger",
								disabled: busy,
								onClick: doCancel,
								children: [/* @__PURE__ */ jsx(Square, { size: 12 }), " Cancel"]
							}) : /* @__PURE__ */ jsxs(Fragment, { children: [
								/* @__PURE__ */ jsxs("button", {
									className: "ck-btn ck-primary",
									disabled: busy || !status.builder.found || !status.llm.configured,
									onClick: () => doStart(false),
									children: [
										/* @__PURE__ */ jsx(Play, { size: 12 }),
										" ",
										addStoryOnly ? "Add events and items" : builtTotal > 0 ? "Build / continue" : "Build"
									]
								}),
								builtTotal > 0 && /* @__PURE__ */ jsxs("button", {
									className: "ck-btn",
									disabled: busy || !status.builder.found || !status.llm.configured,
									onClick: () => doStart(true),
									title: "Discard what was built and start over",
									children: [/* @__PURE__ */ jsx(RefreshCw, { size: 12 }), " Rebuild"]
								}),
								status.knowledge.exists && /* @__PURE__ */ jsxs("button", {
									className: "ck-btn",
									disabled: busy,
									onClick: doDelete,
									children: [/* @__PURE__ */ jsx(Trash2, { size: 12 }), " Delete"]
								})
							] })
						]
					}),
					endpoint && (endpoint.ok ? /* @__PURE__ */ jsxs("div", {
						className: "ck-ok",
						children: [
							"Connected in ",
							endpoint.latencyMs,
							" ms",
							endpoint.model ? ` (${endpoint.model})` : "",
							".",
							endpoint.advertisedContext ? ` Advertised context: ${endpoint.advertisedContext.toLocaleString()} tokens. ${endpoint.note}` : ""
						]
					}) : /* @__PURE__ */ jsx("div", {
						className: "ck-err",
						children: endpoint.error
					})),
					estimate && /* @__PURE__ */ jsxs("div", {
						className: "ck-muted",
						children: [
							estimate.toBuild,
							" of ",
							estimate.chapters,
							" chapters to build (",
							estimate.alreadyBuilt,
							" up to date), about ",
							estimate.characters.toLocaleString(),
							" characters of text. Roughly ",
							estimate.requests,
							estimate.contextKnown ? "" : `–${estimate.requestsIfSmallContext}`,
							" model requests; local models take about 10 seconds per request.",
							estimate.extractStory && ` Events and items: ${estimate.storyToBuild} chapters (${estimate.storyAlreadyBuilt} up to date), roughly ${estimate.storyRequests} more requests.`
						]
					}),
					message && /* @__PURE__ */ jsx("div", {
						className: message.error ? "ck-err" : "ck-ok",
						children: message.text
					})
				]
			}),
			job && /* @__PURE__ */ jsx(Progress, { job }),
			status.knowledge.failedStoryChapters?.length > 0 && !running && /* @__PURE__ */ jsxs("div", {
				className: "ck-warnbox",
				children: [
					/* @__PURE__ */ jsx(AlertTriangle, { size: 14 }),
					" ",
					/* @__PURE__ */ jsxs("div", { children: [
						"The events and items of ",
						status.knowledge.failedStoryChapters.length,
						" chapter(s) failed and will be retried by Build / continue (the characters are not affected):",
						/* @__PURE__ */ jsx("ul", {
							className: "ck-list",
							children: status.knowledge.failedStoryChapters.slice(0, 5).map((c) => /* @__PURE__ */ jsxs("li", { children: [
								"vol ",
								c.volume,
								" ch. ",
								c.chapter + 1,
								": ",
								c.error
							] }, `${c.volume}:${c.chapter}`))
						})
					] })
				]
			}),
			status.knowledge.failedChapters?.length > 0 && !running && /* @__PURE__ */ jsxs("div", {
				className: "ck-warnbox",
				children: [
					/* @__PURE__ */ jsx(AlertTriangle, { size: 14 }),
					" ",
					/* @__PURE__ */ jsxs("div", { children: [
						status.knowledge.failedChapters.length,
						" chapter(s) failed and will be retried by Build / continue:",
						/* @__PURE__ */ jsx("ul", {
							className: "ck-list",
							children: status.knowledge.failedChapters.slice(0, 5).map((c) => /* @__PURE__ */ jsxs("li", { children: [
								"vol ",
								c.volume,
								" ch. ",
								c.chapter + 1,
								": ",
								c.error
							] }, `${c.volume}:${c.chapter}`))
						})
					] })
				]
			})
		]
	});
}
function BookTab({ bookId, book }) {
	const [view, setView] = useState("browse");
	const [focusItem, setFocusItem] = useState(null);
	const [chapter, setChapter] = useState(null);
	const { call } = usePluginRpc(PLUGIN_ID);
	const [reading, setReading] = useState(null);
	const chapters = (book?.chapters || []).filter((c) => !c.isExcluded);
	useEffect(() => {
		call("getStatus", { bookId }).then((s) => setReading(s.readingChapterIndex ?? 0)).catch(() => setReading(0));
		setChapter(null);
	}, [bookId]);
	const asOf = chapter ?? reading ?? 0;
	return /* @__PURE__ */ jsxs("div", {
		className: "ck-root",
		children: [/* @__PURE__ */ jsxs("div", {
			className: "ck-row ck-tabs",
			children: [
				/* @__PURE__ */ jsxs("button", {
					className: `ck-btn ${view === "browse" ? "ck-active" : ""}`,
					onClick: () => setView("browse"),
					children: [/* @__PURE__ */ jsx(Users, { size: 12 }), " Characters"]
				}),
				/* @__PURE__ */ jsxs("button", {
					className: `ck-btn ${view === "events" ? "ck-active" : ""}`,
					onClick: () => setView("events"),
					children: [/* @__PURE__ */ jsx(Swords, { size: 12 }), " Events"]
				}),
				/* @__PURE__ */ jsxs("button", {
					className: `ck-btn ${view === "items" ? "ck-active" : ""}`,
					onClick: () => setView("items"),
					children: [/* @__PURE__ */ jsx(Package, { size: 12 }), " Items"]
				}),
				/* @__PURE__ */ jsxs("button", {
					className: `ck-btn ${view === "build" ? "ck-active" : ""}`,
					onClick: () => setView("build"),
					children: [/* @__PURE__ */ jsx(Network, { size: 12 }), " Build"]
				})
			]
		}), view === "build" ? /* @__PURE__ */ jsx(BuildPanel, {
			target: { bookId },
			scopeChoice: true,
			bookId
		}) : /* @__PURE__ */ jsxs("div", {
			className: "ck-col",
			children: [/* @__PURE__ */ jsxs("div", {
				className: "ck-row ck-wrap",
				children: [
					/* @__PURE__ */ jsxs("span", {
						className: "ck-muted",
						children: [view === "events" ? "Events" : view === "items" ? "Items" : "Characters", " as of the start of"]
					}),
					/* @__PURE__ */ jsx("select", {
						className: "ck-select",
						value: asOf,
						onChange: (e) => setChapter(parseInt(e.target.value, 10)),
						children: chapters.length > 0 ? chapters.map((c) => /* @__PURE__ */ jsxs("option", {
							value: c.chapterIndex,
							children: [
								c.chapterIndex + 1,
								". ",
								c.title
							]
						}, c.chapterIndex)) : /* @__PURE__ */ jsxs("option", {
							value: asOf,
							children: ["chapter ", asOf + 1]
						})
					}),
					reading != null && chapter != null && chapter !== reading && /* @__PURE__ */ jsxs("button", {
						className: "ck-btn",
						onClick: () => setChapter(null),
						children: [
							"Back to where I am (ch. ",
							reading + 1,
							")"
						]
					})
				]
			}), view === "events" ? /* @__PURE__ */ jsx(EventsView, {
				bookId,
				chapterIndex: asOf,
				onItem: (id) => {
					setFocusItem(id);
					setView("items");
				}
			}) : view === "items" ? /* @__PURE__ */ jsx(ItemsView, {
				bookId,
				chapterIndex: asOf,
				focus: focusItem
			}) : /* @__PURE__ */ jsx(Browser, {
				bookId,
				chapterIndex: asOf
			})]
		})]
	});
}
function CharactersDrawer({ onClose }) {
	const { bookId, chapterIndex } = useActiveBook();
	const [tab, setTab] = useState("characters");
	const [focusItem, setFocusItem] = useState(null);
	return /* @__PURE__ */ jsx(PluginDrawerShell, {
		title: "Story",
		onClose,
		children: /* @__PURE__ */ jsx("div", {
			className: "ck-root",
			children: bookId ? /* @__PURE__ */ jsxs(Fragment, { children: [/* @__PURE__ */ jsxs("div", {
				className: "ck-row ck-tabs ck-wrap",
				children: [
					/* @__PURE__ */ jsxs("button", {
						className: `ck-btn ${tab === "characters" ? "ck-active" : ""}`,
						onClick: () => setTab("characters"),
						children: [/* @__PURE__ */ jsx(Users, { size: 12 }), " Characters"]
					}),
					/* @__PURE__ */ jsxs("button", {
						className: `ck-btn ${tab === "events" ? "ck-active" : ""}`,
						onClick: () => setTab("events"),
						children: [/* @__PURE__ */ jsx(Swords, { size: 12 }), " Events"]
					}),
					/* @__PURE__ */ jsxs("button", {
						className: `ck-btn ${tab === "items" ? "ck-active" : ""}`,
						onClick: () => setTab("items"),
						children: [/* @__PURE__ */ jsx(Package, { size: 12 }), " Items"]
					})
				]
			}), tab === "events" ? /* @__PURE__ */ jsx(EventsView, {
				bookId,
				chapterIndex,
				onItem: (id) => {
					setFocusItem(id);
					setTab("items");
				}
			}) : tab === "items" ? /* @__PURE__ */ jsx(ItemsView, {
				bookId,
				chapterIndex,
				focus: focusItem
			}) : /* @__PURE__ */ jsx(Browser, {
				bookId,
				chapterIndex,
				compact: true
			})] }) : /* @__PURE__ */ jsx("div", {
				className: "ck-muted",
				children: "Open a book to see its characters, events and items."
			})
		})
	});
}
function activate(ctx) {
	ctx.registerBookInfoTab({
		id: "characters",
		title: "Characters",
		icon: Users,
		component: BookTab
	});
	ctx.registerReaderDrawer({
		id: "characters-drawer",
		title: "Story",
		icon: Users,
		location: "right",
		defaultSize: 380,
		component: CharactersDrawer
	});
	ctx.registerSeriesAction({
		id: "build-character-knowledge",
		label: "Characters",
		title: "Build the character knowledge of this series",
		icon: Network,
		onClick: ({ seriesName, openModal }) => openModal({
			id: "character-knowledge-series",
			title: `Character knowledge: ${seriesName}`,
			size: "xl",
			component: () => /* @__PURE__ */ jsx("div", {
				className: "ck-root",
				style: { maxWidth: "none" },
				children: /* @__PURE__ */ jsx(BuildPanel, { target: { seriesName } })
			})
		})
	});
}
export { activate };

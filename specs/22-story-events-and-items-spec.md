# Spec 22 — Story pass: a spoiler-gated graph of events, and who has which item

Status: **implemented and tested against a scripted model; not yet run against the real model endpoint.** Python part in `knowledge-graph/` (tests: `test_story_ops`, `test_story_pass`, `test_cli_story`), C# part in the `characterKnowledge` plugin (RPC, status, estimate, progress; 8 more tests in `CharacterKnowledgeTests`), UI in `Assets/ui.src.jsx` (checked with a jsdom harness against real builder output). Builds on [spec 18](18-character-knowledge-graph-spec.md) (builder, gate, snapshots) and [spec 20](20-character-knowledge-plugin-spec.md) (plugin). Feature file: [features/18-story-events-and-items.feature](features/18-story-events-and-items.feature).

## 1. Purpose

The character graph says who people are. This adds **what happened** and **what they own**:

* a graph of **events** (battles, monster hunts, dungeon runs, quests, items found or lost, level-ups, meetings, ...) linked to the characters, monsters, items, groups and place involved, with larger happenings containing smaller ones (a dungeon run contains its fights and drops);
* the **items each character has**, over time: worn gear by slot and carried things, where **newer gear replaces older gear** and an item has one holder at a time.

Everything is gated by reading position exactly like the characters (spec 18): the reader only sees events and holdings established **before** the chapter they are about to read.

## 2. Decisions

| # | Decision | Why |
| :-- | :--- | :--- |
| S-D1 | A **second pass** over the chapters, not more operations in the character prompt | The server enforces ~8k tokens per request. A separate, focused prompt keeps both requests small, and the pass can be **added to a book whose characters are already built** (no rebuild of the profiles). A failing story chapter never blocks the character graph |
| S-D2 | The story pass runs **after** the character pass (a second loop), stops at its first failing chapter, and only over chapters the character pass built | Story state of chapter k depends on the story state before it; characters are the foundation |
| S-D3 | The story pass **may not create characters**, only items, places, groups and creatures (`creature` is a new entity kind: monster species or a named monster). It may use every character the character pass found in the **same chapter** | One source of truth for characters; the model reads that chapter anyway, so showing it the chapter's own characters leaks nothing (`ChapterContext.horizon_key` = end of the chapter) |
| S-D4 | **The story pass owns events.** When it recorded at least one event for a chapter, the character pass's untyped events of that chapter are dropped | Both passes describe the same happenings; keeping both would duplicate every timeline. A chapter where the story pass found no event keeps the untyped ones |
| S-D5 | An item has **one open holding at a time**; a holder has **one equipped item per exclusive slot** (weapon, shield, head, body, hands, legs, feet, cloak). `accessory` and `other` are not exclusive | "Newer gear overwrites older gear" is a rule of the store (`Store.set_holding`), not something the model has to emit |
| S-D5b | **Replacement is an explicit link in the graph.** When equipping into an occupied exclusive slot closes the older holding, the older holding gets `replaced_by = <new item>` and the new holding gets `replaces = <old item>` (also when the claims were listed out of order). The cards and the profile show both tags: *replaces 鉄の剣* on the new item, *replaced by 炎の剣* on the old one. Giving an item away or carrying instead of wielding is **not** a replacement | The newer gear is tagged with what it replaced, and the older gear with what replaced it, so the chain knife → iron sword → flame sword can be followed |
| S-D6 | A replaced item is **not assumed to be still carried** | The text may not say; inventing "still owned" would be a claim without evidence. If the text says the old item is kept, the model emits `owned` for it; if it is gone, `lost` |
| S-D7 | Cards are precomputed from a gated `View` and stored **once per change** (see 5) | Same principle as profiles: the app only looks up; no gating code in C# |
| S-D8 | Story pass is **on by default in the plugin** (`extractStory`), **off in the library** (`BuildConfig.story_pass`, CLI config `story_pass`) | Existing callers and tests keep their request counts; the app turns it on |

## 3. Data model (schema version 2)

A version 1 database is migrated **in place** the first time the builder opens it (additive: new columns with defaults and new tables; old rows are character-pass rows). The C# reader needs no migration: it treats missing story tables as "no story data". An older builder refuses a version 2 database with a clear message.

| Change | Notes |
| :--- | :--- |
| `events` + `kind`, `title`, `parent` | `kind` ∈ battle, hunt, dungeon, quest, item, growth, travel, meeting, group, revelation, death, other (`''` = untyped, from the character pass). `parent` is an earlier or same-answer event (part-of) |
| `event_participants` | unchanged; participants may now be characters, creatures, items, organizations (role: *obtained*, *dropped*, *enemy*, ...) |
| `holdings(item_id, holder_id, state, slot, from_key, to_key, to_src, end_reason, replaces, replaced_by, src_key, evidence_ids)` | `state` ∈ equipped, owned; `end_reason` ∈ replaced, transferred, changed, lost. Holder is a character, organization or place |
| `origin` ('c' characters / 's' story) on `entities`, `names`, `attributes`, `events`, `evidence`, `ops_log` | lets the story pass be redone without touching the character pass |
| `story_chapters(volume, chapter, text_sha, status, ...)` | build state of the story pass |
| `story_event_cards`, `story_item_cards` | derived, per event / item and **change** (5) |

Positions follow spec 18: a holding starts at the end of its latest evidence quote; a claim positioned **before** one already recorded (operations listed out of order) is valid only until that one starts, exactly like single-valued attributes.

## 4. The story pass (builder)

Operations the model may return (all with 1 to 3 verbatim evidence quotes, validated like spec 18): `add_entity` (item, location, organization, creature, concept; **not** character), `add_name`, `set_attribute` (items: appearance, ability, note, status), `add_event` (**kind and title required**, optional local `ref` and `parent`), and the new `item_state`:

```json
{"op":"item_state","item":"e0005","holder":"e0001","state":"equipped","slot":"weapon","evidence":[{"quote":"…"}]}
{"op":"item_state","item":"e0005","holder":"e0002","state":"owned","evidence":[…]}
{"op":"item_state","item":"e0005","state":"lost","evidence":[…]}
```

`lost` needs an item somebody is recorded as holding (else rejected: the model may not remove what it never gave). Giving or stealing is the new holder's `owned`/`equipped`; the old holder's claim ends by itself (`transferred`).

**Request.** `story.extract.system/user` prompts (the system prompt starts "This is the STORY PASS…"). The snapshot is built from the gate **as of the end of the chapter** and holds: characters (id, up to 2 names), other entities (places, groups, creatures), items with `held_by`/`state`/`slot`, and the last typed events (id, kind, title). It shrinks in three levels when the server says the prompt is too big, like the character snapshot; the learned context limit is shared by both passes. Long chapters are split like in the character pass.

**Loop.** `Builder.run` runs the character pass over all chapters, then `_run_story`. A chapter is current when its `story_chapters` row is built for the same text; the first chapter that is not current rolls the story pass back from there (`Store.rollback_story_from`: only `origin='s'` rows; single-valued attribute closures are recomputed from what remains) and redoes it and everything after it. A character-pass rollback (`rollback_from`) takes the story data of those chapters with it. Progress events: `story_start`, `story_done` (with `replaced_events`), `story_skipped`, `story_failed`. Exit code 4 as for characters.

**Names.** Names the story pass creates get the same "first seen in the text" index as character names, so a word the reader already met as ordinary text (a monster called ゴブリン in an earlier quest title) does not block a card.

## 5. Gating and cards

`Gate.view(P)` additionally returns the holdings **valid at P** (item and holder both visible) and the ones that **ended before P**. A holding valid at P is shown **open**: when, why and by what it ends (`replaced_by`) is a later fact; `replaces` is a past fact and is shown. (A first version leaked it: a card at chapter 3 said the sword would be sold in chapter 4. Test `test_items_follow_their_holders…`.)

`generate_snapshots` walks the chapter boundaries once and, from the same view, writes the profiles and the **story cards**:

* **event card**: id, kind, title, summary, position, parent (only if visible), participants grouped by identity group (`id`, `ids` of every member, `name`, `kind`, `role`), location;
* **item card**: names, description (appearance, ability, note, status), current holder with state and slot, the tags `replaces` (the item it took the slot from) and `replaced_by` (only once it is out of use), and the full visible **history** of holders (state, slot, from, to, reason, replaces, replaced_by).

A card is stored at the first boundary where it is visible and again whenever its rendering changes (a participant becomes known, an item changes hands, a name is revealed). A card whose text names someone the reader has not met is **not stored** (spoiler scanner, I-K4); the reader keeps the previous, older card. `verify` re-scans every stored card.

Reading: the reader at `(volume, chapter)` gets, for each event/item, the **latest card at or before** their chapter (`story.list_events` / `list_items`; the C# `KnowledgeDb` runs the same two queries). Filters (character, kind) work on the stored `ids`, which also survive identity reveals. Events are newest first, items by first appearance.

Character profiles gain `equipment` (slot, item, `replaces`), `belongings`, `former_gear` (closed equipped holdings of items no longer held, with the reason and `replaced_by`) and typed timeline entries; the rendered prompt block lists them ("equipped (weapon): … (replaces …)", "carrying: …", "formerly equipped (…): … (replaced by …)").

## 6. Plugin, RPC and UI

* Setting `extractStory` (boolean, default true) → `story_pass` in the builder config.
* RPC: `listEvents {bookId, chapterIndex?, characterId?, kind?, limit?}` → `{asOf, total, events[]}`; `listItems {bookId, chapterIndex?, holderId?}` → `{asOf, items[]}`. `getStatus` adds per volume `storyBuilt`/`storyFailed`, `knowledge.{extractStory, storyBuilt, hasStory, failedStoryChapters}` and `counts.{eventCards, itemCards}`; `estimateBuild` adds `storyToBuild`, `storyAlreadyBuilt`, `storyRequests`; the job reports `phase` (characters | story), `storyOpsApplied/Rejected`, `eventCards`, `itemCards`; `total` counts both passes.
* UI: **Events** and **Items** views next to Characters in the Book Info tab and the reader drawer (now titled "Story"). Events: filter by character / kind / text, larger happenings contain their parts, chips for people (click: show only their events), monsters, items (click: open the item) and the place. Items: "by character" (worn gear in slot order, then carried) or "all items", expandable history ("worn (weapon), ch. 2 to ch. 3 (replaced by newer gear)"). The character profile shows Equipment and formerly worn gear. The Build view shows both passes, offers **Add events and items** when the characters are complete, and lists failed story chapters.

## 7. CLI

`build` (config `story_pass`, `story_snapshot_characters|others|items|events`) · `events --db --position V:C [--character id] [--kind k] [--limit n]` · `items --db --position V:C [--holder id]` · `snapshots` (also regenerates the cards) · `verify` (now also: event kinds and titles, parents exist, holding states/slots, one open holding per item, one equipped item per slot and holder, `replaced_by` only on holdings that ended as replaced, every stored card scanned).

## 8. Invariants

I-K1..I-K4 of spec 18 hold for story data, plus:

* **S-I1** Nothing from a later chapter is in a story request (entities are limited to the end of the chapter being processed, story facts to earlier chapters and earlier parts).
* **S-I2** A holding visible at P never shows its own end (S-D5 note in section 5).
* **S-I3** The story pass never edits or deletes a character-pass row, and rolling it back never deletes one. Its only effect on one is closing a single-valued attribute value that a story value supersedes (an item's `status`); the rollback recomputes those closures.
* **S-I4** An item has at most one open holding; a holder at most one equipped item per exclusive slot (`verify` checks it).

## 9. Not done / limits

* **Not run against the real model.** The prompt is written for the 8k-token server but its answer quality (kind choice, slot choice, how well it notices "kept the old sword") is unmeasured. Expect to tune the prompt after a first chapter or two.
* `set_attribute` on an item cannot yet say "was enchanted in chapter 5, +30 after" as a history; abilities accumulate.
* Items are not merged when the model calls one thing by two names (`add_name` exists, there is no item `same_as`).
* A stat sheet, quantities (50 potions) and money are not modelled: only named items and their holder.
* Offset-level gating (inside a chapter) is still out of scope (spec 18).
* Rolling the story pass back deletes story entities created at or after the chapter; a character-pass event or relation that referenced such an entity loses that link (it cascades). Rare: it needs a character chapter built *after* the story pass of an earlier chapter and then a story redo of that earlier chapter.
* `generate_snapshots` is dominated by the (existing) profile rendering: about 45 s for 400 chapters and 4,000 events; the cards themselves are roughly 10 % of that.

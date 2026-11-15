# Spec 26: Reading quiz from the last completed chapter, the written-answer rubric, favourites and reading lists, bookmark lines

Status: **implemented** (C# `QuizPassageSelector`, `QuizQuestionValidator`, `FreeFormRubric`, `BookFlagsService`, `BookmarkLabel`; the quiz panel, the sidebar lists, card, series card, info dialog and bulk bar in the client; 60+ tests; `--verify-ui` drives the lists and the bookmark lines in the real library). Builds on [spec 25](25-editor-prompts-and-custom-css-spec.md) (the prompts are templates) and the AI companion of [spec 17](17-voice-character-companion-spec.md).

## 1. The quiz asks about the last completed chapter

* **Spoiler gate.** The question is about the **last chapter the reader completed**: the closest non-excluded chapter *before* the one being read (`IReaderContextService.GetLastCompletedChapterAsync`). Nothing of the current chapter or later is read, sent to the model or shown. With no completed chapter the quiz says so (`QuizUnavailableException`: "Finish a chapter first ..."). A test checks that text of the current chapter never appears in a request.
* **Passage.** `QuizPassageSelector`: a short chapter is used whole; a long one gives a random window of whole paragraphs (up to 2,800 characters, a longer paragraph is cut at a sentence end), avoiding the windows of the last quizzes of the same chapter while another window exists. The model sees numbered paragraphs (`[12] text`); the numbers are never part of the verified text.
* **A question must pass `QuizQuestionValidator`**, or it is asked for once more with the reason, and then the quiz refuses (`QuizGenerationException`; there is **no canned question**; the old fallback options are gone):
  * `evidenceQuote` is a sentence that really occurs in the passage (compared without spacing and punctuation); the paragraph id comes from where it is found, not from the model;
  * multiple choice: **exactly four** options, all different, none repeating the question, no "all/none of the above", `correctOptionIndex` in range, **no tell**: the correct option is not much longer or shorter than the wrong ones (1.8x / 0.4x) and does not copy the evidence sentence;
  * the **position of the correct option is random** (shuffled by the program; models favour option 1) and is never sent to the client; neither is the evidence sentence until the answer is graded.
* **Prompts** `quiz.generate.system` and `.user` (version 2): realistic wrong options built from people, places and events of the passage, similar length and grammar, understanding questions rather than trivia, the correct answer put in other words. `retry_note` carries the rejection reason. Measured against the local Gemma 4 E4B model: 8 of 8 usable on the first try on a clean passage.
* **Which model.** The AI companion falls back to an offline heuristic when its endpoint (Settings → AI) is down; that text is not a question, so the quiz refuses with "Check that the model server in Settings is running" instead of showing nonsense.

## 2. The written-answer rubric (free form)

Judged in this order, each part separately (`quiz.grade.system` version 2): **1. comprehension** (does the answer answer the question, by meaning only), **2. grammar** (particles, conjugation, structure, politeness; real mistakes only), **3. vocabulary** (word choice; words used well and words misused, in dictionary form).

* Everything the model quotes from the answer is checked against the answer: a grammar issue or word that is not in what the learner wrote is dropped (`FreeFormRubricScorer`).
* **Comprehension comes first and gates the rest.** Below 40 the answer did not answer the question: grammar and vocabulary are shown as "not scored" and nothing is nudged (a fluent answer to another question earns nothing). The overall score is 60% comprehension, 25% grammar, 15% vocabulary and is for display; **only comprehension drives the quiz stage** (multiple choice, guided, free form).
* **Nudging the vocabulary** (`IVocabularyBudgetService.RecordPractice`, setting `quizNudgesVocabulary`, on by default): practice is recorded in `vocabulary_practice`. Three correct uses in a row promote a Learning (or New) word to Known; a misuse sends a Known or New word to Learning and breaks the run; words not in the vocabulary only collect evidence; Ignored words never move; a word both right and wrong in one answer counts as wrong. The changes are listed under the answer ("Your vocabulary moved").
* The quiz panel now has a text box for written answers, shows errors instead of logging them, and shows the three parts in order.

## 3. Favourites and the reading list

* Per **book** and per **series**: a favourite flag and a status `reading | planned | completed` (or none). Stored in `books.is_favorite`, `books.reading_status` and the table `series_flags`.
* **Inheritance.** A book is a favourite when it or its series is; a book without its own status takes its series' status; its own status wins. The sidebar lists, the filters and the counts use these effective values; the book also carries its own and its series' raw flags so each can be edited separately.
* UI: the sidebar section **Lists** (Favourites, Currently reading, Planning to read, Completed, with counts); a **star** on every book card (filled = own, softer = inherited from the series) and a status chip on the cover; the info dialog has the book's star and status and the whole series' star and status; the series cards have the same two controls; the selection bar has **Favourite** and **Reading list…** for all selected books.
* RPC: `library.setBookFlags {bookIds, favorite?, readingStatus?}` (absent = unchanged, `""` clears), `library.setSeriesFlags {seriesName, favorite?, readingStatus?}`, `library.getReadingListCounts`; `library.listBooks` / `listBookIds` take `favorite` and `readingStatus` (`reading|planned|completed|any`).
* Not done: reading status does not change by itself when a book is opened or finished.

## 4. Bookmarks

The one line of a bookmark is made by the server (`BookmarkLabel`): **`Vol<X>Ch<Y>`** when nothing is selected, **`[ch<Y>] <selected text>`** when text is selected (spacing collapsed, shortened to 60 characters). X is the book's volume number in its series (1 without one), Y the chapter counted from 1. The drawer's **Bookmark here** takes the words selected in the book page; old bookmarks get the same line from what they stored.

## 5. Not done

A review mode that asks about older chapters; the spoken or guided stage differing from free form; reading status from progress; per-word spaced-repetition intervals (the vocabulary state has statuses, not intervals, so the nudge moves statuses).

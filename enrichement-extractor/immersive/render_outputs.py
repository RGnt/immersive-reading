"""Organized PDF, HTML and reusable-data exports."""
from pathlib import Path
from .enrichment import save_json, write_reader_data
from .study_data import prepare_study_data, study_reader_data
from .jobs import check_cancelled


def render_book(book, output, pdf_root, font_path=None, skip_pdf=False, kanjivg_archive=None, *, study=None, include_html=True, cancel=None, resources=None, resource_store=None):
    """Export a read-only enrichment/study model to supplementary documents."""
    if not skip_pdf:
        from .capabilities import require_print
        require_print()
        from .pdf_elements import pdf_session
        with pdf_session(font_path or (resources.font if resources else None)):
            return _render_book(book, output, pdf_root, font_path, skip_pdf, kanjivg_archive,
                                study=study, include_html=include_html, cancel=cancel, resources=resources, resource_store=resource_store)
    return _render_book(book, output, pdf_root, font_path, skip_pdf, kanjivg_archive,
                        study=study, include_html=include_html, cancel=cancel, resources=resources, resource_store=resource_store)


def _render_book(book, output, pdf_root, font_path, skip_pdf, kanjivg_archive, *, study, include_html, cancel, resources, resource_store):
    from .render import vocabulary_rows_html, card_html, page, TOOLBAR, E
    from .kanjivg import copy_svg_assets
    study = study if study is not None else prepare_study_data(book, cancel=cancel)
    book = study_reader_data(book, study)
    root = output / "html" / book["id"]
    data = output / "data" / book["id"]
    pdf = pdf_root / book["id"]
    metadata = data / "print"
    directories = [data, metadata]
    if include_html:
        directories += [root / "chapters", root / "assets"]
    for directory in directories:
        directory.mkdir(parents=True, exist_ok=True)
    if not skip_pdf:
        from .kanji_pdf import write_kanji_document
        from .master_pdf import write_master, write_chapter_vocabulary
        pdf.mkdir(parents=True, exist_ok=True)
    charts = resources.charts if resources else ()
    if include_html:
        for asset in ("style.css", "lookup.js"):
            (root / "assets" / asset).write_bytes(Path(__file__).with_name(asset).read_bytes())
    if kanjivg_archive:
        copy_svg_assets(book, kanjivg_archive, data, cancel=cancel, store=resource_store)
    sets = study.learning_sets
    chapter_numbers = {chapter["id"]: chapter["number"] for chapter in book["chapters"]}
    def set_heading(group):
        numbers = list(dict.fromkeys(chapter_numbers[card["chapter_id"]] for card in group["cards"]))
        chapters = str(numbers[0]) if len(numbers) == 1 else f'{numbers[0]}-{numbers[-1]}'
        return f'Ch {chapters} Set {group["number"]}'
    context = study.context
    save_json(data / "kanji-context.json", context)
    save_json(data / "vocabulary-sets.json", {"book_id": book["id"], "set_size": 20, "sets": book["learning_sets"]})
    write_reader_data(book, output)
    chapter_rows, grammar_sections, grammar_groups = [], [], []
    all_words, all_kanji = {}, {}
    for chapter in book["chapters"]:
        check_cancelled(cancel)
        cid = chapter["id"]
        for kind, prepared in (("kanji", study.kanji), ("vocabulary", study.vocabulary)):
            cards = prepared[cid]
            if kind == "vocabulary":
                all_words.update((c["id"], c) for c in cards)
            else:
                all_kanji.update((c["id"], c) for c in cards)
            title = f'{chapter["number"]:02d}  {chapter["title"]}'
            other = "vocabulary" if kind == "kanji" else "kanji"
            nav = f'<a href="../index.html">Book overview</a> · <a href="{cid}-{other}.html">{other.title()}</a>'
            if not skip_pdf:
                filename = f'{cid}-{kind}.pdf'
                nav += f' · <a href="../../../pdf/{book["id"]}/{filename}">Download PDF</a>'
                if kind == "kanji":
                    write_kanji_document(pdf / filename, book, chapter, metadata, context, cancel=cancel, charts=charts)
                else:
                    write_chapter_vocabulary(pdf / filename, book, chapter, cards, metadata, cancel=cancel, charts=charts)
            if include_html:
                body = '<p class="note">New items from this chapter only.</p>' + TOOLBAR
                if kind == "vocabulary":
                    for number in dict.fromkeys(c["set_number"] for c in cards):
                        body += f'<h2><a href="../vocabulary.html#set-{number:03d}">Ch {chapter["number"]} Set {number}</a></h2>' + vocabulary_rows_html([c for c in cards if c["set_number"] == number])
                else:
                    body += '<div class="grid">'+"".join(card_html(c) for c in cards)+'</div>'
                if not cards:
                    body += '<p>No new extractable entries. This section may contain only illustrations.</p>'
                (root / "chapters" / f'{cid}-{kind}.html').write_text(page(title, body, "../", kind.title(), nav), encoding="utf-8")
        gcards = study.grammar[cid]
        heading = f'{chapter["number"]:02d}  {chapter["title"]}'
        grammar_groups.append((heading, gcards))
        if include_html:
            grammar_sections.append(f'<section><h2>{E(heading)}</h2><div class="grid">'+"".join(card_html(c) for c in gcards)+('</div><p>No new patterns detected.</p>' if not gcards else '</div>')+'</section>')
            chapter_rows.append(f'<div class="chapter-row"><strong>{E(heading)}</strong><a href="chapters/{cid}-kanji.html">{len(context[cid])} new kanji</a><a href="chapters/{cid}-vocabulary.html">{len(study.vocabulary[cid])} new words</a><span>{len(gcards)} grammar candidates</span></div>')
    if not skip_pdf:
        write_kanji_document(pdf / "kanji-master.pdf", book, metadata_dir=metadata, context_by_chapter=context, cancel=cancel, charts=charts)
        write_master(pdf / "grammar-master.pdf", book, "grammar", grammar_groups, cancel=cancel, charts=charts)
        write_master(pdf / "vocabulary-master.pdf", book, "vocabulary", [(set_heading(g), g["cards"]) for g in sets], cancel=cancel, charts=charts)
    if not include_html:
        return data
    pdf_link = f'../../pdf/{book["id"]}'
    overview_nav = '<a href="index.html">Book overview</a>'
    if not skip_pdf:
        overview_nav += f' · <a href="{pdf_link}/vocabulary-master.pdf">Vocabulary master PDF</a>'
    vocab_body = '<p class="note">One master PDF, organized into sets of 20 new words. Full glosses and Japanese examples are available below.</p>'+TOOLBAR
    for group in sets:
        vocab_body += f'<section id="{group["id"]}"><h2>{set_heading(group)}</h2>'+vocabulary_rows_html(group["cards"])+ '</section>'
    (root / "vocabulary.html").write_text(page("Vocabulary master", vocab_body, nav=overview_nav), encoding="utf-8")
    (root / "grammar.html").write_text(page("Grammar master", '<p class="note">Ordered by first detected appearance, grouped by chapter. Finite rules produce reviewable candidates; coverage is not exhaustive.</p>'+TOOLBAR+"".join(grammar_sections), nav='<a href="index.html">Book overview</a>'), encoding="utf-8")
    unmatched = [v for v in study.indexes.groups.values() if not v["occurrences"]]
    review_cards = []
    for v in unmatched:
        card = {"id": v["id"], "kind": "vocabulary", "title": v["word"], "reading": v["reading"], "meaning": v["definitions"],
                "meta": "Unmatched · chapter assignment needs review", "details": "", "example": "", "plain_example": "", "source": ""}
        all_words[v["id"]] = card
        review_cards.append(card)
    for filename, title, cards in (("dictionary.html", "Word lookup", all_words.values()), ("kanji.html", "Kanji lookup", all_kanji.values()), ("review.html", "Deck alignment review", review_cards)):
        (root / filename).write_text(page(title, TOOLBAR+'<div class="grid">'+"".join(card_html(c) for c in cards)+'</div>', nav='<a href="index.html">Book overview</a>'), encoding="utf-8")
    q = book["quality"]
    body = f'<p>{q["vocabulary_matched"]} / {q["vocabulary_total"]} vocabulary entries aligned · {len(book["kanji"])} unique kanji · {len(book["grammar"])} grammar candidates.</p>'
    body += '<div class="toolbar"><a href="vocabulary.html">Vocabulary master / sets of 20</a><a href="dictionary.html">Word lookup</a><a href="kanji.html">Kanji lookup</a><a href="grammar.html">Grammar master</a>'
    if not skip_pdf:
        body += ''.join(f'<a href="{pdf_link}/{kind}-master.pdf">{kind.title()} master PDF</a>' for kind in ("kanji", "vocabulary", "grammar"))
    if book.get("dictionary_sources"):
        notices = []
        for source in book["dictionary_sources"]:
            index = source["index"]
            notices.append('<section><h2>' + E(index["title"]) + '</h2><p>Revision: ' + E(index["revision"]) + '</p><pre>' +
                           E(index.get("attribution", "Attribution not supplied in index.")) + '</pre>')
            notices.extend('<h3>' + E(name) + '</h3><pre>' + E(text) + '</pre>' for name, text in source["notices"].items())
            notices.append('</section>')
        (root / "dictionary-sources.html").write_text(page("Dictionary sources and notices", "".join(notices), nav='<a href="index.html">Book overview</a>'), encoding="utf-8")
        body += '<a href="dictionary-sources.html">Dictionary sources and notices</a>'
    body += f'<a href="../../data/{book["id"]}/reader.json">Reader JSON</a><a href="../../data/{book["id"]}/quality.json">Quality report</a><a href="review.html">Unmatched entries</a></div><div class="chapter-list">'+"".join(chapter_rows)+'</div>'
    (root / "index.html").write_text(page(book["title"], body, subtitle="Kanji / vocabulary / grammar"), encoding="utf-8")
    return root

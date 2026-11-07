"""HTML rendering and compatibility adapters for historical render imports."""
import html
from . import study_data
from .study_data import sentence_index, source_label, example_ruby, grouped_vocabulary

E = html.escape


def render_example(card):
    """Escape plain card data and add ruby/highlight markup only for HTML."""
    text = card["plain_example"]
    rubies = {r["start"]: r for r in card.get("ruby", [])}
    highlight = card.get("example_highlight")
    parts, pos = [], 0
    while pos < len(text):
        ruby = rubies.get(pos)
        stop = ruby["end"] if ruby else pos+1
        content = E(text[pos:stop])
        if ruby:
            content = f'<ruby>{content}<rt>{E(ruby["reading"])}</rt></ruby>'
        if highlight is not None and pos < highlight["end"] and stop > highlight["start"]:
            content = f"<mark>{content}</mark>"
        parts.append(content)
        pos = stop
    return "".join(parts)


def _html_card(card):
    return dict(card, example=render_example(card))


def vocab_cards(book, chapter):
    """Compatibility adapter; new data consumers use immersive.study_data."""
    return [_html_card(c) for c in study_data.vocab_cards(book, chapter)]


def kanji_cards(book, chapter):
    return [_html_card(c) for c in study_data.kanji_cards(book, chapter)]


def grammar_cards(book, chapter):
    return [_html_card(c) for c in study_data.grammar_cards(book, chapter)]


def vocabulary_learning_sets(book, size=20):
    return [dict(g, cards=[_html_card(c) for c in g["cards"]]) for g in study_data.vocabulary_learning_sets(book, size)]


def __getattr__(name):
    # Preserve historical PDF imports without loading print libraries for HTML.
    if name in {"register_font", "RubyText", "PracticeGrid", "StrokeOrder"}:
        from . import pdf_elements
        return getattr(pdf_elements, name)
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")


def rich_example(sentence, chapter, start=None, end=None):
    """Historical sentence adapter to the shared HTML example renderer."""
    highlight = {"start": start-sentence["start"], "end": end-sentence["start"]} if start is not None else None
    return render_example({"plain_example": chapter["text"][sentence["start"]:sentence["end"]],
                           "ruby": example_ruby(sentence, chapter), "example_highlight": highlight})


def vocabulary_rows_html(cards):
    rows = ['<div class="vocabulary-head"><span>No.</span><span>Word / reading</span><span>Meaning</span></div>']
    for card in cards:
        rows.append(f'''<article class="vocabulary-row" data-card id="{E(card['id'])}"><div class="meta">{card.get('study_number', '')}</div>
        <div><h2 class="word" lang="ja">{E(card['title'])}</h2><div class="reading">{E(card['reading'])}</div></div>
        <div class="meaning">{E(card['meaning'])}{dictionary_candidates_html(card)}</div><details class="vocabulary-example"><summary>Japanese example · {E(card.get('chapter_title', ''))}</summary>
        <div class="example" lang="ja">{render_example(card)}</div></details></article>''')
    return '<div class="vocabulary-list">' + "".join(rows) + '</div>'


def dictionary_candidates_html(card):
    candidates = card.get("definition_candidates", [])
    if candidates:
        blocks = []
        for candidate in candidates:
            source = candidate["source"]
            title = (source.get("dictionary_title") or source.get("file", "Dictionary"))
            blocks.append('<p><strong>' + E(title) + '</strong><br>' + E(candidate["text"]).replace('\n', '<br>') + '</p>')
        selection = card.get("sense_selection") or {}
        status = "reviewed gloss; original candidates retained" if selection.get("status") == "reviewed_override" else "sense not selected"
        return '<details><summary>Dictionary candidates (' + status + ')</summary>' + "".join(blocks) + '</details>'
    records = card.get("dictionary_entries", [])
    if not records:
        return ""
    content = []
    for record in records:
        title = record["dictionary_title"] + " · " + record.get("reading", record.get("character", ""))
        meanings = record.get("glossary_text", record.get("meanings", []))
        text = "\n".join(meanings)
        content.append('<p><strong>' + E(title) + '</strong><br>' + E(text).replace('\n', '<br>') + '</p>')
    return '<details><summary>Dictionary candidates (sense not selected)</summary>' + "".join(content) + '</details>'


def card_html(card):
    glyph = "glyph" if card["kind"] == "kanji" else "word"
    practice = '<div class="practice" aria-label="Writing practice boxes"><span></span><span></span><span></span><span></span></div>' if card["kind"] == "kanji" else ""
    example = f'<div class="example" lang="ja">{render_example(card)}</div>' if card["plain_example"] else ""
    return f'''<article class="card" data-card id="{E(card['id'])}">
    <div class="meta">{E(card['meta'])}</div><h2 class="{glyph}" lang="ja">{E(card['title'])}</h2>
    <div class="reading" lang="ja">{E(card['reading'])}</div><div class="meaning">{E(card['meaning'])}</div>
    <div>{E(card['details'])}</div>{dictionary_candidates_html(card)}{example}{practice}</article>'''


def page(title, body, prefix="", subtitle="", nav=""):
    return f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>{E(title)}</title><link rel="stylesheet" href="{prefix}assets/style.css"><script src="{prefix}assets/lookup.js" defer></script></head>
    <body><main><nav class="nav">{nav}</nav><div class="eyebrow">Immersive reading · Japanese study companion</div>
    <h1>{E(title)}</h1><p class="subtitle">{E(subtitle)}</p>{body}
    <footer>Japanese examples: your EPUB or deck. Original furigana is preserved; missing readings are generated offline by Sudachi.
    Vocabulary glosses: your Jiten.moe deck / JMdict or supplied Yomitan dictionaries (source notices in reader JSON). Primary kanji meanings: <a href="https://kanjialive.com/credits/">Kanji alive</a> (CC BY 4.0); KANJIDIC2 fallback and readings: EDRDG (CC BY-SA 4.0).
    <a href="https://www.edrdg.org/wiki/KANJIDIC_Project.html">Dictionary attribution</a> · Stroke order: <a href="https://github.com/KanjiVG/kanjivg">KanjiVG</a>, Ulrich Apel, CC BY-SA 3.0. · Grammar: finite rules, reviewable candidates. No sentence translations.</footer></main></body></html>'''


TOOLBAR = '''<div class="toolbar"><input type="search" data-search aria-label="Find a word, reading or meaning" placeholder="Find a word, reading or meaning…">
<button data-toggle="hide-ruby" aria-pressed="false">Toggle furigana</button><button data-toggle="hide-meanings" aria-pressed="false">Hide / show meanings</button></div>'''


def render_book(book, output, pdf_root, font_path=None, skip_pdf=False, kanjivg_archive=None, *, study=None):
    from .render_outputs import render_book as organized_render
    return organized_render(book, output, pdf_root, font_path, skip_pdf, kanjivg_archive, study=study)

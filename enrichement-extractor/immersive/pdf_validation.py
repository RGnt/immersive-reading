"""Strict supplementary PDF checks; imported only for PDF validation."""
import pymupdf
from PIL import Image, ImageDraw
from .validation import load_json, require
from .jobs import check_cancelled

def verify_pdfs(b, output, data_path, *, previews=True, cancel=None, preview_root=None):
    from .pdf_elements import _PDF_LOCK
    with _PDF_LOCK:
        return _verify_pdfs(b, output, data_path, previews=previews, cancel=cancel, preview_root=preview_root)


def _verify_pdfs(b, output, data_path, *, previews, cancel, preview_root):
    # Verification follows persisted output metadata, never local installation
    # charts. This also verifies packs on a machine without the print resources.
    print_resources = b.get('build_inputs', {}).get('print_resources')
    if print_resources is not None:
        chart_count = len(print_resources['charts'])
    else:
        legacy_layout = load_json(data_path.parent / 'print/kanji-master.layout.json')
        chart_count = len(legacy_layout['appendices'])
    learning_sets = b['learning_sets']
    set_words = [w['word'] for group in learning_sets for w in group['words']]
    pdfs = sorted((output / 'pdf' / b['id']).glob('*.pdf'))
    require(len(pdfs) == 2 * len(b['chapters']) + 3, 'Expected chapter kanji/vocabulary PDFs and three masters')
    expected_names = {f'{c["id"]}-{kind}.pdf' for c in b['chapters'] for kind in ('kanji', 'vocabulary')}
    expected_names.update({'kanji-master.pdf', 'vocabulary-master.pdf', 'grammar-master.pdf'})
    require({p.name for p in pdfs} == expected_names, 'Missing or unexpected PDF filenames', output / 'pdf' / b['id'])
    require(all((p.suffix == '.pdf' for p in (output / 'pdf' / b['id']).iterdir())), 'Non-PDF files in PDF directory')
    require(not any((p.stem.startswith('set-') for p in pdfs)), 'Per-set vocabulary PDFs remain')
    require(not any(('-compact' in p.stem or '-detailed' in p.stem for p in pdfs)), 'Obsolete vocabulary variants remain')
    tiles = []
    seen_vocabulary = set()
    page_count = 0
    for path in pdfs:
        check_cancelled(cancel)
        with pymupdf.open(path) as doc:
            require(len(doc) > 0, 'Check failed: len(doc) > 0')
            page_count += len(doc)
            require('COPYRIGHT / LICENSE NOTICES' in doc[0].get_text(), (path, 'missing license cover'))
            require(len(doc) >= 2, (path, 'missing blank page after cover'))
            blank = doc[1]
            require(not blank.get_text().strip() and (not blank.get_drawings()) and (not blank.get_images()), (path, 'second page must be blank'))
            layout_path = data_path.parent / 'print' / (path.stem + '.layout.json')
            if path.stem == 'kanji-master' or path.stem.endswith('-kanji'):
                require(layout_path.is_file(), 'Missing kanji layout metadata', layout_path)
            if layout_path.exists():
                layout = load_json(layout_path)
                require(layout['blank_pages'] == 1, "Check failed: layout['blank_pages'] == 1")
                require(len(layout['appendices']) == chart_count, "Check failed: len(layout['appendices']) == chart_count")
                require(len(doc) == len(layout['pages']) + 2 + chart_count, "Check failed: len(doc) == len(layout['pages']) + 2 + chart_count")
                flat = [entry['id'] for page in layout['pages'] for entry in page['entries']]
                require(len(flat) == len(set(flat)) == layout['total_entries'], "Check failed: len(flat) == len(set(flat)) == layout['total_entries']")
                for i, record in enumerate(layout['pages']):
                    require(1 <= len(record['entries']) <= 4, "Check failed: 1 <= len(record['entries']) <= 4")
                    require(record['practice_rows'] == len(record['entries']), "Check failed: record['practice_rows'] == len(record['entries'])")
                    require(record['pdf_page'] == i + 3, "Check failed: record['pdf_page'] == i + 3")
                    for entry in record['entries']:
                        x0, y0, x1, y1 = entry['bbox']
                        px0, py0, px1, py1 = entry['practice_bbox']
                        require(y0 >= 18 * 72 / 25.4 and x0 < px0 < px1 < x1 and (y0 < py0 < py1 < y1), (path, i, 'practice outside card'))
                        require(entry['outer_border'], (path, i, 'missing card border'))
                    for above, below in zip(record['entries'], record['entries'][1:]):
                        require(above['bbox'][1] - below['bbox'][3] >= 6.9 * 72 / 25.4, (path, i, 'cards too close'))
                expected = {k['id'] for k in b['kanji'] if path.stem == 'kanji-master' or k['first_chapter_id'] == path.stem.removesuffix('-kanji')}
                require(set(flat) == expected, 'Check failed: set(flat) == expected')
            if path.stem == 'vocabulary-master':
                require(len(doc) == len(learning_sets) + 2 + chart_count, 'Each vocabulary set must occupy one master-sheet page after the cover and blank page')
            elif path.stem.endswith('-vocabulary'):
                manifest_path = data_path.parent / 'print' / (path.stem + '.vocabulary.json')
                manifest = load_json(manifest_path)
                cid = path.stem.removesuffix('-vocabulary')
                expected_words = [word for group in learning_sets for word in group['words'] if word['chapter_id'] == cid]
                require(manifest['chapter_id'] == cid, "Check failed: manifest['chapter_id'] == cid")
                require(manifest['card_ids'] == [word['card_id'] for word in expected_words], "Check failed: manifest['card_ids'] == [word['card_id'] for word in expected_words]")
                require(manifest['words'] == [word['word'] for word in expected_words], "Check failed: manifest['words'] == [word['word'] for word in expected_words]")
                require(not seen_vocabulary.intersection(manifest['words']), (path, 'repeated chapter vocabulary'))
                seen_vocabulary.update(manifest['words'])
            for page in doc:
                check_cancelled(cancel)
                if page.number == 1:
                    continue
                if chart_count and page.number >= len(doc) - chart_count:
                    images = page.get_image_info()
                    require(len(images) == 1, (path, page.number, 'missing chart'))
                    image = images[0]
                    require(abs(image['transform'][0]) < 0.01 and abs(image['transform'][3]) < 0.01, (path, 'chart not rotated 90 degrees'))
                    require(page.rect.contains(pymupdf.Rect(image['bbox'])), (path, 'chart clipped'))
                    continue
                require(page.get_text().strip(), (path, page.number, 'empty page'))
                content = [line for line in page.get_text().splitlines() if line.strip() and (not line.strip().isdigit()) and (not line.startswith('EPUB examples /')) and (not line.startswith('Stroke diagrams:'))]
                require(content, (path, page.number, 'footer-only blank page'))
                require('OEBPS/' not in page.get_text() and '.xhtml' not in page.get_text(), (path, page.number, 'EPUB path in PDF'))
                for drawing in page.get_drawings():
                    require(drawing['fill'] is None or drawing['rect'].get_area() < 1000, (path, page.number, 'shaded background'))
                for block in page.get_text('dict')['blocks']:
                    if block['type'] != 0:
                        continue
                    for line in block['lines']:
                        for span in line['spans']:
                            x0, y0, x1, y1 = span['bbox']
                            require(x0 >= 20 and y0 >= 15 and (x1 <= page.rect.width - 18) and (y1 <= page.rect.height - 12), (path, page.number, span))
                for font in page.get_fonts():
                    if font[2] in {'TrueType', 'Type0'} or 'Gothic' in font[3] or 'Japanese' in font[3]:
                        require(doc.extract_font(font[0])[3], (path, 'Japanese font not embedded'))
        if previews:
            # Use a fresh document context after resource inspection so contact
            # sheets reflect the saved file without cached-resource artifacts.
            with pymupdf.open(path) as preview:
                samples = sorted({0, min(2, len(preview)-1), len(preview)-1})
                for n in samples:
                    pix = preview[n].get_pixmap(matrix=pymupdf.Matrix(.45, .45), alpha=False)
                    im = Image.frombytes('RGB', (pix.width, pix.height), pix.samples)
                    tile = Image.new('RGB', (290, 410), '#e9e9e9')
                    tile.paste(im, ((290-im.width)//2, 22))
                    ImageDraw.Draw(tile).text((8, 5), f'{path.stem} / p{n+1}', fill='black')
                    tiles.append(tile)
    require(seen_vocabulary == set(set_words), 'Missing chapter vocabulary PDF entries')
    if previews:
        preview_dir = (preview_root if preview_root is not None else output / 'previews') / b['id']
        preview_dir.mkdir(parents=True, exist_ok=True)
        for i in range(0, len(tiles), 12):
            sheet = Image.new('RGB', (290 * 4, 410 * 3), '#ddd')
            for j, im in enumerate(tiles[i:i + 12]):
                sheet.paste(im, (j % 4 * 290, j // 4 * 410))
            sheet.save(preview_dir / f"{b['id']}-contact-{i // 12 + 1:02d}.png")
    return {'pdf_files': len(pdfs), 'pdf_pages': page_count}

"""Optional supplied kana charts, placed as rotated full-page appendices."""
from reportlab.lib.units import mm
from reportlab.lib.utils import ImageReader


def chart_paths(resources=None):
    """Compatibility adapter over an explicit bundle; no directory search."""
    return list(resources.charts) if resources is not None else []


def append_charts(canvas, paths=()):
    pages = []
    for path in paths:
        image = ImageReader(str(path))
        iw, ih = image.getSize()
        page_width, page_height = 210*mm, 297*mm
        scale = min((page_width-20*mm)/ih, (page_height-20*mm)/iw)
        width, height = iw*scale, ih*scale
        canvas.saveState()
        canvas.translate(page_width/2, page_height/2)
        canvas.rotate(90)
        canvas.drawImage(image, -width/2, -height/2, width, height)
        canvas.restoreState()
        pages.append({"pdf_page": canvas.getPageNumber(), "file": path.name, "rotation": 90})
        canvas.showPage()
    return pages

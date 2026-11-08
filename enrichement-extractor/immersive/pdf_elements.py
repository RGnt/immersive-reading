"""PDF-only font registration and reusable flowables."""
from pathlib import Path
from contextlib import contextmanager
from threading import RLock
from reportlab.lib import colors
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import Flowable
from fontTools.pens.reportLabPen import ReportLabPen
from fontTools.svgLib.path import parse_path


_PDF_LOCK = RLock()


def register_font(font_path=None):
    if font_path is not None and Path(font_path).is_file():
        with _PDF_LOCK:
            pdfmetrics.registerFont(TTFont("Japanese", str(font_path), subfontIndex=0))
        return str(font_path)

    if "Japanese" in pdfmetrics.getRegisteredFontNames():
        return "Japanese"

    from .sdk.pdf import register_cjk_font
    res = register_cjk_font(font_path)
    if "Japanese" in pdfmetrics.getRegisteredFontNames():
        return res

    raise FileNotFoundError("Set --font to a Japanese TrueType font (.ttf or glyf-based .ttc).")


@contextmanager
def pdf_session(font_path):
    """ReportLab's named font registry is process-global; serialize PDF jobs."""
    with _PDF_LOCK:
        register_font(font_path)
        yield


class RubyText(Flowable):
    """Japanese example with source furigana, wrapped as ruby/base units."""
    def __init__(self, text, ruby):
        super().__init__()
        self.text, self.ruby = text, ruby
        self.spaceBefore, self.spaceAfter = 2, 4

    def wrap(self, available, height):
        self.width = available
        rubies = {r["start"]: r for r in self.ruby}
        self.lines = [[]]
        pos, used = 0, 0
        while pos < len(self.text):
            r = rubies.get(pos)
            stop = r["end"] if r else pos+1
            base, reading = self.text[pos:stop], r["reading"] if r else ""
            width = max(pdfmetrics.stringWidth(base, "Japanese", 10), pdfmetrics.stringWidth(reading, "Japanese", 5.2))
            if used + width > available and self.lines[-1]:
                self.lines.append([]); used = 0
            self.lines[-1].append((used, base, reading, width))
            used += width
            pos = stop
        self.height = len(self.lines)*20 + 2
        return self.width, self.height

    def draw(self):
        canvas = self.canv
        canvas.setFillColor(colors.HexColor("#23362f"))
        for i, line in enumerate(self.lines):
            baseline = self.height-17-i*20
            for x, base, reading, width in line:
                canvas.setFont("Japanese", 10)
                canvas.drawCentredString(x+width/2, baseline, base)
                if reading:
                    canvas.setFont("Japanese", 5.2)
                    canvas.drawCentredString(x+width/2, baseline+11, reading)


class PracticeGrid(Flowable):
    """Writing squares with clearly visible dashed horizontal/vertical centres."""
    def wrap(self, width, height):
        self.width, self.height = width, 14*mm
        return self.width, self.height

    def draw(self):
        c = self.canv
        count = max(1, int(self.width/(14*mm)))
        size = min(self.height, self.width/count)
        for i in range(count):
            x = i*size
            c.setStrokeColor(colors.HexColor("#87998d")); c.setLineWidth(.6); c.setDash()
            c.rect(x, 0, size, size)
            c.setStrokeColor(colors.HexColor("#a0afa4")); c.setLineWidth(.55); c.setDash(2, 2)
            c.line(x+size/2, 0, x+size/2, size)
            c.line(x, size/2, x+size, size/2)
        c.setDash()


class StrokeOrder(Flowable):
    """Cumulative KanjiVG paths: previous strokes dark, current stroke red."""
    def __init__(self, data):
        super().__init__()
        self.data = data

    def wrap(self, width, height):
        self.width = width
        self.cell = 13*mm
        self.columns = max(1, int(width/self.cell))
        self.height = ((len(self.data["strokes"])+self.columns-1)//self.columns)*(self.cell+10)
        return self.width, self.height

    def draw(self):
        c = self.canv
        vx, vy, vw, vh = self.data["view_box"]
        class CanvasPen(ReportLabPen):
            def _closePath(self):
                self.path.close()
        for step in range(len(self.data["strokes"])):
            x = (step % self.columns)*self.cell
            y = self.height-(step//self.columns+1)*(self.cell+10)
            side = self.cell-4
            c.setStrokeColor(colors.HexColor("#d0d8cf")); c.setLineWidth(.4)
            c.rect(x, y+9, side, side)
            c.setFont("Helvetica", 6.5); c.setFillColor(colors.HexColor("#56685b"))
            c.drawCentredString(x+side/2, y+1, str(step+1))
            c.saveState()
            inset = 2
            c.translate(x+inset, y+9+side-inset)
            scale = (side-2*inset)/max(vw, vh)
            c.scale(scale, -scale); c.translate(-vx, -vy)
            c.setLineCap(1); c.setLineJoin(1); c.setLineWidth(3.3)
            for j, stroke in enumerate(self.data["strokes"]):
                c.setStrokeColor(colors.HexColor("#b44331" if j == step else "#263b30" if j < step else "#e0e6df"))
                path = c.beginPath()
                parse_path(stroke["d"], CanvasPen(None, path))
                c.drawPath(path, stroke=1, fill=0)
            c.restoreState()

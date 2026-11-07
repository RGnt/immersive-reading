"""Explicit resource locations and application-owned, read-only shared data.

One ResourceStore may serve the library, reader and extraction workers in a
process. Worker contexts own mutable tokenizer state; stores never own books.
"""
from contextlib import contextmanager
from dataclasses import dataclass, field
import hashlib
import importlib.resources
import json
import os
from pathlib import Path
from threading import RLock
from types import MappingProxyType

from .jobs import check_cancelled

CHART_NAMES = ("hiragana-chart-by-tofugu.jpg", "katakana-chart-by-tofugu.jpg")


def freeze(value):
    if isinstance(value, dict):
        return MappingProxyType({key: freeze(v) for key, v in value.items()})
    if isinstance(value, list):
        return tuple(freeze(v) for v in value)
    if isinstance(value, tuple):
        return tuple(freeze(v) for v in value)
    return value


def thaw(value):
    if isinstance(value, (dict, MappingProxyType)):
        return {key: thaw(v) for key, v in value.items()}
    if isinstance(value, tuple):
        return [thaw(v) for v in value]
    return value


def _path(value):
    return Path(value).resolve() if value is not None else None


def _packaged_grammar():
    return Path(str(importlib.resources.files("immersive").joinpath("grammar.json"))).resolve()


@dataclass(frozen=True)
class ResourceBundle:
    """Immutable absolute paths. Empty print fields mean unavailable/disabled.

    ResourceBundle() supports standalone morphology. Use from_directory for
    reader enrichment, or name every path explicitly in an application.
    """
    root: Path | None = None
    kanjidic: Path | None = None
    kanji_alive: Path | None = None
    grammar: Path = field(default_factory=_packaged_grammar)
    sudachi_dictionary: Path | None = None
    sudachi_user_dictionaries: tuple[Path, ...] = ()
    kanjivg: Path | None = None
    font: Path | None = None
    charts: tuple[Path, ...] = ()
    yomitan: tuple[Path, ...] = ()
    yomitan_directory: Path | None = None

    def __post_init__(self):
        for name in ("root", "kanjidic", "kanji_alive", "grammar", "sudachi_dictionary", "kanjivg", "font", "yomitan_directory"):
            object.__setattr__(self, name, _path(getattr(self, name)))
        for name in ("sudachi_user_dictionaries", "charts", "yomitan"):
            object.__setattr__(self, name, tuple(dict.fromkeys(Path(p).resolve() for p in getattr(self, name))))

    @classmethod
    def from_directory(cls, root, *, kanjidic_path=None, kanji_alive=None, grammar=None,
                       kanjivg_archive=None, font=None, charts=None, yomitan_paths=None,
                       sudachi_dictionary=None, sudachi_user_dictionaries=()):
        root = Path(root).resolve()
        kd = _path(kanjidic_path) or root / "kanjidic2.xml.gz"
        from .yomitan import dictionary_paths
        return cls(root=root, kanjidic=kd,
                   # Preserve the legacy --kanjidic override convention only
                   # in this directory adapter; explicit bundles are independent.
                   kanji_alive=_path(kanji_alive) or (kd.parent if kanjidic_path is not None else root) / "kanji-alive",
                   grammar=_path(grammar) or _packaged_grammar(),
                   sudachi_dictionary=_path(sudachi_dictionary), sudachi_user_dictionaries=tuple(sudachi_user_dictionaries),
                   kanjivg=_path(kanjivg_archive) or root / "kanjivg/source.zip",
                   font=_path(font) or root / "fonts/NotoSansJP-Regular.ttf",
                   charts=tuple(charts) if charts is not None else tuple(root / "charts" / n for n in CHART_NAMES if (root / "charts" / n).is_file()),
                   yomitan=tuple(dictionary_paths(root, yomitan_paths)),
                   yomitan_directory=root / "yomitan" if yomitan_paths is None else None)


class ResourceChangedError(ValueError):
    """A store is pinned to its first observed resource bytes; create a new one."""


def _stat(path):
    s = path.stat()
    return s.st_size, s.st_mtime_ns, s.st_ctime_ns, s.st_ino


class ResourceStore:
    """Thread-shareable store with lazy immutable snapshots and explicit lifetime.

    No process-global parsed dictionaries, open ZIPs or tokenizers. Files must
    remain unchanged during a store's lifetime. Close after its workers close.
    """
    def __init__(self, bundle, *, cache=None):
        if not isinstance(bundle, ResourceBundle):
            raise TypeError("ResourceStore requires a ResourceBundle")
        self._bundle = bundle
        self.cache = cache if cache is not False else None
        self._lock = RLock()
        self._pid = os.getpid()
        self._closed = False
        self._workers = 0
        self._files = {}
        self._data = {}
        self._dictionary = None
        self._glyphs = {}

    @property
    def bundle(self):
        return self._bundle

    def _check(self):
        if os.getpid() != self._pid:
            raise RuntimeError("ResourceStore cannot cross process boundaries; construct a store in each process")
        if self._closed:
            raise RuntimeError("ResourceStore is closed")

    def pin(self, path, *, cancel=None):
        """Fingerprint once, then reject file replacement rather than reload silently."""
        self._check()
        with self._lock:
            self._check()
            check_cancelled(cancel)
            if path is None:
                raise ValueError("Required resource path is not configured")
            path = Path(path).resolve()
            stamp = _stat(path)
            if path in self._files:
                if stamp != self._files[path][0]:
                    raise ResourceChangedError(f"Resource changed; create a new ResourceStore: {path}")
            else:
                digest = hashlib.sha256()
                with path.open("rb") as stream:
                    while chunk := stream.read(1024 * 1024):
                        check_cancelled(cancel)
                        digest.update(chunk)
                if stamp != _stat(path):
                    raise ResourceChangedError(f"Resource changed while reading: {path}")
                self._files[path] = stamp, {"file": path.name, "sha256": digest.hexdigest(), "size": stamp[0]}
            return dict(self._files[path][1])

    def _load(self, key, paths, loader, cancel=None):
        self._check()
        with self._lock:
            self._check()
            for path in paths:
                self.pin(path, cancel=cancel)
            if key not in self._data:
                from .cache import CACHE_ENABLED
                persistent = self.cache if CACHE_ENABLED.get() else None
                cached = None
                if persistent is not None:
                    from .cache import digest, code_revision
                    cache_key = digest({"kind": key, "format": 1, "inputs": [self.pin(p, cancel=cancel) for p in paths],
                                        "code": code_revision(("build.py", "kanji_meanings.py", "resources.py", "validation.py", "cache.py"))})
                    cached = persistent.get("resource", cache_key, cancel=cancel)
                raw = cached["data"] if cached is not None else loader()
                value = freeze(raw)
                for path in paths:
                    self.pin(path, cancel=cancel)
                self._data[key] = value
                if persistent is not None and cached is None:
                    persistent.put("resource", cache_key, raw, validated=True, cancel=cancel)
            return self._data[key]

    def kanjidic(self, *, cancel=None):
        from .build import load_kanjidic
        return self._load("kanjidic", [self.bundle.kanjidic], lambda: load_kanjidic(self.bundle.kanjidic), cancel)

    def primary_meanings(self, *, cancel=None):
        from .kanji_meanings import load_primary_meanings
        root = self.bundle.kanji_alive
        if root is None:
            raise ValueError("Kanji alive resource directory is not configured")
        return self._load("primary_meanings", [root / "ka_data.csv", root / "source.json"], lambda: load_primary_meanings(root), cancel)

    def grammar(self, *, cancel=None):
        from .validation import load_json
        def load():
            import re
            rules = load_json(self.bundle.grammar)
            if not isinstance(rules, list):
                raise ValueError(f"{self.bundle.grammar}: expected an array of grammar rules")
            seen = set()
            for rule in rules:
                if not isinstance(rule, dict) or not all(isinstance(rule.get(k), str) and rule[k] for k in ("id", "label", "meaning", "note")):
                    raise ValueError(f"{self.bundle.grammar}: invalid grammar rule fields")
                if rule["id"] in seen:
                    raise ValueError(f"{self.bundle.grammar}: duplicate grammar rule {rule['id']}")
                seen.add(rule["id"])
                if rule.get("token") not in {"particle", "desire"}:
                    try:
                        re.compile(rule["regex"])
                    except (KeyError, TypeError, re.error) as error:
                        raise ValueError(f"{self.bundle.grammar}: invalid regex for {rule['id']}") from error
                elif rule["token"] == "particle" and not isinstance(rule.get("form"), str):
                    raise ValueError(f"{self.bundle.grammar}: particle rule needs a form")
            return rules
        return self._load("grammar", [self.bundle.grammar], load, cancel)

    def kanjivg_metadata(self, *, cancel=None):
        archive = self.bundle.kanjivg
        self.pin(archive, cancel=cancel)
        path = archive.with_name("source.json")
        return self._load("kanjivg_metadata", [path], lambda: json.loads(path.read_text(encoding="utf-8-sig")), cancel) if path.is_file() else freeze({})

    def kanjivg_glyphs(self, characters, *, cancel=None):
        self._check()
        characters = tuple(dict.fromkeys(characters))
        from .kanjivg import parse_glyph
        from zipfile import ZipFile
        with self._lock:
            self.pin(self.bundle.kanjivg, cancel=cancel)
            missing = set(characters) - self._glyphs.keys()
            if missing:
                with ZipFile(self.bundle.kanjivg) as archive:
                    if "kanjivg_index" not in self._data:
                        self._data["kanjivg_index"] = freeze({Path(n).name: n for n in archive.namelist() if n.endswith(".svg")})
                    files = self._data["kanjivg_index"]
                    for character in missing:
                        check_cancelled(cancel)
                        filename = f"{ord(character):05x}.svg"
                        name = files.get(filename)
                        if name:
                            raw = archive.read(name)
                            self._glyphs[character] = freeze({"svg": raw, "stroke_order": parse_glyph(raw, name, filename)})
                        else:
                            self._glyphs[character] = None
                self.pin(self.bundle.kanjivg, cancel=cancel)
            return MappingProxyType({c: self._glyphs[c] for c in characters})

    def morphology_paths(self):
        system = self.bundle.sudachi_dictionary
        if system is None:
            system = Path(str(importlib.resources.files("sudachidict_core").joinpath("resources/system.dic"))).resolve()
        return (system, *self.bundle.sudachi_user_dictionaries)

    def _new_tokenizers(self, cancel=None):
        # Sudachi shares immutable dictionary data internally. Only factory
        # access is serialized; each worker receives its own stateful buffers.
        self._check()
        with self._lock:
            self._check()
            paths = self.morphology_paths()
            for path in paths:
                self.pin(path, cancel=cancel)
            if self._dictionary is None:
                from sudachipy import Config, dictionary
                from sudachipy.errors import SudachiError
                try:
                    self._dictionary = dictionary.Dictionary(config=Config(system=str(paths[0]), user=[str(p) for p in paths[1:]]))
                except SudachiError as error:
                    raise ValueError(f"Cannot load configured Sudachi dictionaries: {error}") from error
            return {mode: self._dictionary.tokenizer(mode) for mode in ("A", "B", "C")}

    def worker(self):
        from .tokenization import WorkerContext
        return WorkerContext(self)

    def reader_files(self):
        b = self.bundle
        if b.kanjidic is None or b.kanji_alive is None:
            raise ValueError("Reader enrichment requires KANJIDIC2 and Kanji alive paths")
        return (b.kanjidic, b.kanji_alive / "ka_data.csv", b.kanji_alive / "source.json", b.grammar,
                *self.morphology_paths(), *b.yomitan)

    def reader_manifest(self, *, cancel=None):
        names = ("kanjidic", "kanji_alive_data", "kanji_alive_metadata", "grammar", "sudachi_system",
                 *(f"sudachi_user_{i}" for i in range(len(self.bundle.sudachi_user_dictionaries))),
                 *(f"yomitan_{i}" for i in range(len(self.bundle.yomitan))))
        return {name: self.pin(path, cancel=cancel) for name, path in zip(names, self.reader_files())}

    def print_files(self, *, pdf=True):
        b = self.bundle
        files = [b.kanjivg]
        if b.kanjivg is not None and b.kanjivg.with_name("source.json").is_file():
            files.append(b.kanjivg.with_name("source.json"))
        if pdf:
            files += [b.font, *b.charts]
        return tuple(files)

    def print_manifest(self, *, pdf=True, cancel=None):
        b = self.bundle
        result = {"kanjivg": self.pin(b.kanjivg, cancel=cancel), "charts": []}
        if b.kanjivg.with_name("source.json").is_file():
            result["kanjivg_metadata"] = self.pin(b.kanjivg.with_name("source.json"), cancel=cancel)
        if pdf:
            result["font"] = self.pin(b.font, cancel=cancel)
            result["charts"] = [self.pin(p, cancel=cancel) for p in b.charts]
        return result

    def verify_current(self, *, paths=None, cancel=None):
        """Rehash pinned resources before publication, including same-size edits."""
        self._check()
        with self._lock:
            self._check()
            selected = set(paths) if paths is not None else self._files.keys()
            for path in selected:
                stamp, expected = self._files[path]
                check_cancelled(cancel)
                if _stat(path) != stamp:
                    raise ResourceChangedError(f"Resource changed before publication: {path}")
                digest = hashlib.sha256()
                with path.open("rb") as stream:
                    while chunk := stream.read(1024 * 1024):
                        check_cancelled(cancel)
                        digest.update(chunk)
                if digest.hexdigest() != expected["sha256"] or _stat(path) != stamp:
                    raise ResourceChangedError(f"Resource changed before publication: {path}")
            folder = self.bundle.yomitan_directory
            if folder is not None and tuple(sorted(p.resolve() for p in folder.glob("*.zip"))) != self.bundle.yomitan:
                raise ResourceChangedError(f"Yomitan selection changed; create a new ResourceBundle: {folder}")

    def close(self):
        if os.getpid() != self._pid:
            raise RuntimeError("ResourceStore cannot cross process boundaries")
        with self._lock:
            if self._closed:
                return
            self._check()
            if self._workers:
                raise RuntimeError("Close worker contexts before closing their ResourceStore")
            if self._dictionary is not None:
                self._dictionary.close()
                self._dictionary = None
            self._data.clear()
            self._glyphs.clear()
            self._files.clear()
            self._closed = True

    def __enter__(self):
        self._check()
        return self

    def __exit__(self, *_):
        self.close()


@contextmanager
def resource_worker(resources=None, *, worker=None, cache=None, **overrides):
    """Borrow an application's store/worker, or own an adapter for legacy paths."""
    if cache is False:
        from .cache import CACHE_ENABLED
        token = CACHE_ENABLED.set(False)
        try:
            with resource_worker(resources, worker=worker, **overrides) as active:
                yield active
        finally:
            CACHE_ENABLED.reset(token)
        return
    supplied = {k: v for k, v in overrides.items() if v is not None}
    if worker is not None:
        worker.check()
        if supplied:
            raise ValueError("Resource overrides cannot be combined with an existing worker")
        if resources is not None and resources is not worker.store and resources != worker.store.bundle:
            raise ValueError("Resources disagree with the supplied worker")
        yield worker
        return
    if isinstance(resources, ResourceStore):
        if supplied:
            raise ValueError("Resource overrides must be set when constructing the shared bundle")
        with resources.worker() as active:
            yield active
        return
    if isinstance(resources, ResourceBundle):
        if supplied:
            raise ValueError("Resource overrides must be set when constructing the explicit bundle")
        bundle = resources
    else:
        bundle = ResourceBundle.from_directory(resources if resources is not None else Path("data"), **overrides)
    with ResourceStore(bundle, cache=cache) as store, store.worker() as active:
        yield active


def add_resource_arguments(parser):
    """Shared command adapter; Python applications can construct bundles directly."""
    parser.add_argument("--resources", type=Path, default=Path("data"))
    parser.add_argument("--kanjidic", type=Path, help="Override KANJIDIC2 (legacy: resolves kanji-alive beside it)")
    parser.add_argument("--kanji-alive", type=Path, help="Explicit Kanji alive directory")
    parser.add_argument("--grammar-rules", type=Path, help="Explicit grammar rules JSON")
    parser.add_argument("--kanjivg", type=Path, help="Explicit local KanjiVG ZIP")
    parser.add_argument("--font", type=Path)
    parser.add_argument("--yomitan", type=Path, action="append", help="Repeat local format-3 dictionary ZIPs in priority order")
    parser.add_argument("--sudachi-dictionary", type=Path, help="Shared compiled Sudachi system dictionary (default: installed core)")
    parser.add_argument("--sudachi-user-dictionary", type=Path, action="append", help="Repeat compiled Sudachi user dictionaries")
    charts = parser.add_mutually_exclusive_group()
    charts.add_argument("--charts", type=Path, help="Directory containing the two Tofugu kana charts")
    charts.add_argument("--no-charts", action="store_true")
    parser.add_argument("--cache-dir", type=Path, help="Shared local content cache (default: .cache/immersive beside output)")
    parser.add_argument("--no-cache", action="store_true", help="Bypass persistent cache reads/writes")


def bundle_from_arguments(args):
    charts = () if args.no_charts else tuple(args.charts / n for n in CHART_NAMES) if args.charts else None
    return ResourceBundle.from_directory(args.resources, kanjidic_path=args.kanjidic, kanji_alive=args.kanji_alive,
                                         grammar=args.grammar_rules, font=args.font, kanjivg_archive=args.kanjivg,
                                         charts=charts, yomitan_paths=args.yomitan,
                                         sudachi_dictionary=args.sudachi_dictionary,
                                         sudachi_user_dictionaries=args.sudachi_user_dictionary or ())

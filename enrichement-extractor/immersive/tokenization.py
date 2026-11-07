"""Worker-owned Sudachi state and bounded ruby caches over a shared dictionary."""
from collections import OrderedDict
import os
from threading import current_thread, local

from .jobs import check_cancelled


class WorkerContext:
    def __init__(self, store):
        self.store = store
        self._thread = current_thread()
        self._pid = os.getpid()
        self._closed = False
        self._tokenizers = None
        self._ruby = OrderedDict()
        store._check()
        with store._lock:
            store._check()
            store._workers += 1

    def check(self):
        if self._closed:
            raise RuntimeError("WorkerContext is closed")
        if self._pid != os.getpid() or self._thread is not current_thread():
            raise RuntimeError("WorkerContext belongs to another worker thread/process; create a worker there")
        self.store._check()

    def tokenize(self, text, mode="C", *, cancel=None):
        self.check()
        check_cancelled(cancel)
        if self._tokenizers is None:
            self._tokenizers = self.store._new_tokenizers(cancel)
        # Accept historical SplitMode values without changing a tokenizer's mode.
        mode = str(mode).rsplit(".", 1)[-1].upper()
        if mode not in self._tokenizers:
            raise ValueError("WorkerContext supports split modes A, B and C")
        return self._tokenizers[mode].tokenize(text)

    def ruby(self, text, originals):
        self.check()
        key = text, originals
        if key not in self._ruby:
            from .furigana import _furigana
            self._ruby[key] = _furigana(text, originals, self)
            if len(self._ruby) > 8192:
                self._ruby.popitem(last=False)
        self._ruby.move_to_end(key)
        return self._ruby[key]

    def close(self):
        if self._closed:
            return
        self.check()
        self._tokenizers = None
        self._ruby.clear()
        self._closed = True
        with self.store._lock:
            self.store._workers -= 1

    def __enter__(self):
        self.check()
        return self

    def __exit__(self, *_):
        self.close()


_local = local()


def default_worker():
    """Compatibility for standalone ruby helpers; never shared across threads.

    Applications should provide explicit workers to control lifetime and reuse.
    No dictionary/font/chart path is inferred from a working directory here.
    """
    active = getattr(_local, "worker", None)
    if active is None or active._pid != os.getpid() or active._closed:
        from .resources import ResourceBundle, ResourceStore
        _local.worker = active = ResourceStore(ResourceBundle()).worker()
    return active


def close_default_worker():
    active = getattr(_local, "worker", None)
    if active is not None:
        active.close()
        active.store.close()
        del _local.worker

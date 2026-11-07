"""Cooperative cancellation shared by enrichment, exporters and publication."""
from threading import Event


class CancelledError(RuntimeError):
    pass


class CancellationToken:
    def __init__(self):
        self._event = Event()

    def cancel(self):
        self._event.set()

    def is_set(self):
        return self._event.is_set()


def check_cancelled(cancel=None):
    if cancel is not None and (cancel() if callable(cancel) else cancel.is_set()):
        raise CancelledError("Job cancelled before publication")

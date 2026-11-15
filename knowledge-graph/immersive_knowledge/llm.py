"""OpenAI-compatible chat client for the knowledge builder (spec 18, FR-32).

Same lessons as the deck enrichment client (spec 16): **no fallback answers** (a knowledge base must come from the model or
not at all), real HTTP status and body in every error, retries only where retrying can help, one repair retry for invalid
JSON, a clear split between "stop everything" (bad key, wrong URL, unreachable server) and "this chapter failed" (invalid
JSON, context too long, timeouts, rate limits). Standard library only: the project has no HTTP dependency.
"""
import json
import re
import socket
import time
import urllib.error
import urllib.request
from urllib.parse import urlsplit

BACKOFF = (2, 8, 20)
_THINK = re.compile(r"<think>.*?</think>", re.S)


class LlmError(Exception):
    def __init__(self, message, fatal=False, truncated=False, raw="", context_limit=0):
        super().__init__(message)
        self.prompt_too_big = False          # the request itself exceeded the server's budget (a smaller prompt can work)
        self.context_limit = context_limit   # the server's real limit (prompt + generation, in tokens) when it said so
        self.raw = raw                  # the unusable answer (for diagnostics; stored in the chapter's error text, never on stdout)
        self.fatal = fatal
        self.truncated = truncated      # the answer hit the output limit: asking for less (a smaller part of the chapter) can help


class OpenAiClient:
    def __init__(self, endpoint, model="", api_key=None, *, temperature=0.2, max_tokens=16000, timeout=900,
                 thinking="default", sleep=time.sleep):
        if not endpoint or endpoint.startswith("offline://"):
            raise LlmError("no LLM endpoint is configured", fatal=True)
        self.endpoint = endpoint.rstrip("/")
        self.model, self.api_key = model, api_key
        self.temperature, self.max_tokens, self.timeout = temperature, max_tokens, timeout
        if thinking not in ("default", "on", "off"):
            raise LlmError(f"thinking must be default, on or off (got {thinking!r})", fatal=True)
        self.thinking, self._sleep = thinking, sleep

    # ------------------------------------------------------------------ urls
    @property
    def _base(self):
        """The address may be the bare host, the base (.../v1) or the full chat URL (same rule as the app's LlmEndpoint)."""
        base = self.endpoint[: -len("/chat/completions")] if self.endpoint.endswith("/chat/completions") else self.endpoint
        base = base.rstrip("/")
        if urlsplit(base).path == "":
            base += "/v1"
        return base

    @property
    def _chat_url(self):
        return self._base + "/chat/completions"

    def resolve_model(self):
        """When no model id is configured, use the first one the server lists (a single-model server)."""
        if self.model:
            return self.model
        body = self._request(self._base + "/models", None, method="GET")
        try:
            self.model = json.loads(body)["data"][0]["id"]
        except (ValueError, KeyError, IndexError, TypeError):
            raise LlmError("the server lists no models; set 'model' in the configuration", fatal=True) from None
        return self.model

    # ------------------------------------------------------------------ http
    def _request(self, url, payload, method="POST"):
        data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        request = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                return response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            body = error.read().decode("utf-8", "replace")
            raise _http_error(error.code, body) from None
        except (socket.timeout, TimeoutError):
            raise LlmError(f"the LLM did not answer within {self.timeout} s", fatal=False) from None
        except urllib.error.URLError as error:
            if isinstance(error.reason, (socket.timeout, TimeoutError)):
                raise LlmError(f"the LLM did not answer within {self.timeout} s", fatal=False) from None
            raise LlmError(f"cannot reach the LLM endpoint {self.endpoint}: {error.reason}", fatal=True) from None

    def _complete(self, messages, json_mode):
        payload = {"model": self.model, "messages": messages, "temperature": self.temperature, "max_tokens": self.max_tokens}
        if json_mode:
            payload["response_format"] = {"type": "json_object"}
        if self.thinking != "default":
            payload["chat_template_kwargs"] = {"enable_thinking": self.thinking == "on"}
        last = None
        for attempt in range(len(BACKOFF) + 1):
            try:
                body = self._request(self._chat_url, payload)
                break
            except _JsonModeRejected:
                raise
            except LlmError as error:
                if error.fatal or not getattr(error, "retryable", False) or attempt == len(BACKOFF):
                    raise
                last = error
                self._sleep(BACKOFF[attempt])
        else:                                               # pragma: no cover (loop always breaks or raises)
            raise last
        try:
            choice = json.loads(body)["choices"][0]
            content = choice["message"].get("content") or ""
            reasoning = bool(choice["message"].get("reasoning_content"))
            finish = choice.get("finish_reason")
        except (ValueError, KeyError, IndexError, TypeError, AttributeError):
            raise LlmError("the endpoint answered with an unexpected response shape (not an OpenAI chat completion)", fatal=True) from None
        if finish == "length":
            # Repairing or retrying cannot help: the model ran out of output budget (often spent on hidden "thinking").
            hint = ("; the model spent its output budget on reasoning before answering, set thinking to \"off\""
                    if reasoning and not content.strip() else "; lower max_ops or max_input_chars, or raise max_tokens")
            raise LlmError(f"the answer was cut off at the output limit{hint}", fatal=False, truncated=True)
        return content

    # ------------------------------------------------------------------ public
    def chat_json(self, system, user):
        """One JSON object from the model. Invalid JSON is repaired once by asking again; never guessed."""
        self.resolve_model()
        messages = [{"role": "system", "content": system}, {"role": "user", "content": user}]
        json_mode = True
        last_content = ""
        for repair in range(2):
            try:
                content = self._complete(messages, json_mode)
            except _JsonModeRejected:
                json_mode = False                           # a server without response_format: the prompt demands JSON anyway
                content = self._complete(messages, json_mode)
            parsed = extract_json(content)
            if parsed is not None:
                return parsed
            last_content = content
            if repair == 0:
                messages += [{"role": "assistant", "content": content[:4000]},
                             {"role": "user", "content": "Your previous answer was not a valid JSON object. "
                                                         "Return only the JSON object, with no other text."}]
        raise LlmError("the model did not return valid JSON (after one repair attempt)", fatal=False, raw=last_content[-1500:])


class _JsonModeRejected(Exception):
    pass


def _http_error(status, body):
    snippet = body[:400]
    if status == 400 and "response_format" in body:
        return _JsonModeRejected()
    error = LlmError(f"the LLM endpoint answered HTTP {status}: {snippet}", fatal=False)
    if status == 429 or status >= 500:
        error.retryable = True
    elif status == 400 and re.search(r"context|too long|exceed|maximum", body, re.I):
        # The request is too big for the server's real budget (often far below the advertised context length). Smaller requests can
        # work, and the message usually names the true limit: "prompt is too long: 8630 tokens > 8241 maximum (prompt + generation)".
        error.truncated = True
        error.prompt_too_big = True
        found = re.search(r"(\d+)\s*tokens?\s*>\s*(\d+)\s*maximum", body)
        if found:
            error.context_limit = int(found.group(2))
    else:
        error.fatal = True                                  # 401/403/404/other: retrying or moving on cannot help
    return error


def extract_json(content):
    """The first JSON object in the text (``<think>`` blocks and Markdown fences removed), or ``None``."""
    text = _THINK.sub("", content or "").strip()
    start, end = text.find("{"), text.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        value = json.loads(text[start:end + 1])
    except ValueError:
        return None
    return value if isinstance(value, dict) else None

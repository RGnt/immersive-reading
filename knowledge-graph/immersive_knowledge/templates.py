"""Prompt templates (spec 19, part A). The C# plugin implements the same rules; both run ``specs/fixtures/prompt-vectors.json``.

File format (``<name>.prompt.md``)::

    ---
    name: coach.improve
    version: 1
    purpose: one line
    response: json
    variables:
      - coach_language | default=English
      - utterance | required | untrusted
      - strictness | enum=low,medium,high | default=medium
      - words | type=list
    ---
    Body with {{coach_language}}, {{#if words}}...{{/if}} and {{#each words}}- {{this}}{{/each}}.

Syntax is deliberately tiny: ``{{name}}``, ``{{#if name}}..{{/if}}``, ``{{#each name}}..{{this}}..{{/each}}``. Rendering is strict:
an undeclared variable, a missing required value, a value outside its enum or of the wrong type is an error and nothing is
returned. ``untrusted`` values (book text, transcripts, user messages) are fenced as ``<<<NAME`` ... ``NAME>>>`` and any marker
inside the value is neutralised, so text cannot close the block or pose as part of the prompt.
"""
import re
from dataclasses import dataclass, field
from pathlib import Path

_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
_TOKEN = re.compile(r"\{\{\s*(#if|#each|/if|/each)?\s*([A-Za-z_][A-Za-z0-9_]*)?\s*\}\}")
TYPES = ("string", "int", "list")


class TemplateError(Exception):
    pass


@dataclass(frozen=True)
class VariableSpec:
    name: str
    required: bool = False
    untrusted: bool = False
    type: str = "string"
    default: object = None
    enum: tuple = ()


@dataclass
class Template:
    name: str
    version: int
    purpose: str
    response: str
    variables: dict
    body: list
    unused: tuple = field(default_factory=tuple)


# --------------------------------------------------------------------------- loading
def parse_template(text, source="<template>"):
    text = text.replace("\r\n", "\n")
    if not text.startswith("---\n"):
        raise TemplateError(f"{source}: a template must start with a '---' front matter block")
    end = text.find("\n---\n", 3)
    if end < 0:
        raise TemplateError(f"{source}: front matter is not closed with '---'")
    header, body_text = text[4:end], text[end + 5:]

    meta, specs, in_variables = {}, {}, False
    for line in header.split("\n"):
        if not line.strip():
            continue
        if line.startswith("variables:"):
            in_variables = True
            continue
        if in_variables and line.lstrip().startswith("- "):
            spec = _parse_variable(line.lstrip()[2:], source)
            if spec.name in specs:
                raise TemplateError(f"{source}: variable '{spec.name}' is declared twice")
            specs[spec.name] = spec
            continue
        in_variables = False
        key, sep, value = line.partition(":")
        if not sep:
            raise TemplateError(f"{source}: cannot read front matter line {line!r}")
        meta[key.strip()] = value.strip()

    name = meta.get("name") or Path(source).name.split(".prompt")[0]
    if not name:
        raise TemplateError(f"{source}: missing 'name'")
    try:
        version = int(meta.get("version", "1"))
    except ValueError:
        raise TemplateError(f"template '{name}': 'version' must be an integer") from None

    tree, used = _parse_body(body_text, name, specs)
    unused = tuple(sorted(set(specs) - used))
    return Template(name, version, meta.get("purpose", ""), meta.get("response", ""), specs, tree, unused)


def _parse_variable(line, source):
    parts = [p.strip() for p in line.split("|")]
    name = parts[0]
    if not _NAME.match(name):
        raise TemplateError(f"{source}: invalid variable name {name!r}")
    flags = {"required": False, "untrusted": False}
    kwargs = {}
    for token in parts[1:]:
        if token in flags:
            flags[token] = True
        elif token.startswith("default="):
            kwargs["default"] = token[len("default="):]
        elif token.startswith("enum="):
            kwargs["enum"] = tuple(v.strip() for v in token[len("enum="):].split(",") if v.strip())
        elif token.startswith("type="):
            kwargs["type"] = token[len("type="):]
            if kwargs["type"] not in TYPES:
                raise TemplateError(f"{source}: variable '{name}' has unknown type {kwargs['type']!r}")
        else:
            raise TemplateError(f"{source}: variable '{name}' has unknown option {token!r}")
    spec = VariableSpec(name=name, **flags, **kwargs)
    if spec.required and spec.default is not None:
        raise TemplateError(f"{source}: variable '{name}' cannot be both required and have a default")
    if spec.type == "list" and spec.default is not None:
        raise TemplateError(f"{source}: list variable '{name}' cannot have a default")
    if spec.type == "int" and spec.default is not None and not re.fullmatch(r"-?\d+", spec.default):
        raise TemplateError(f"{source}: default of '{name}' is not an integer")
    if spec.enum and spec.default is not None and spec.default not in spec.enum:
        raise TemplateError(f"{source}: default of '{name}' is not in its enum")
    return spec


def _parse_body(body, template, specs):
    """Nodes: ("text", s) | ("var", name) | ("this",) | ("if", name, nodes) | ("each", name, nodes)."""
    root, stack, used, pos = [], [], set(), 0
    current = root

    def fail(message):
        raise TemplateError(f"template '{template}': {message}")

    for match in _TOKEN.finditer(body):
        if match.start() > pos:
            current.append(("text", body[pos:match.start()]))
        pos = match.end()
        directive, name = match.group(1), match.group(2)
        if directive in ("/if", "/each"):
            if not stack or stack[-1][0] != directive[1:]:
                fail(f"unexpected {{{{{directive}}}}}")
            kind, _, parent = stack.pop()
            current = parent
            continue
        if name is None:
            fail("empty {{ }}")
        if name == "this":
            if directive or not any(s[0] == "each" for s in stack):
                fail("{{this}} can only be used inside {{#each}}")
            current.append(("this",))
            continue
        if name not in specs:
            fail(f"variable '{name}' is used but not declared")
        used.add(name)
        if directive in ("#if", "#each"):
            if directive == "#each" and specs[name].type != "list":
                fail(f"'{name}' is iterated with #each but is not declared type=list")
            node = ("if" if directive == "#if" else "each", name, [])
            current.append(node)
            stack.append((node[0], node, current))
            current = node[2]
        else:
            if specs[name].type == "list":
                fail(f"list variable '{name}' must be used with {{{{#each {name}}}}}")
            current.append(("var", name))
    if stack:
        fail(f"{{{{#{stack[-1][0]}}}}} is not closed")
    if pos < len(body):
        current.append(("text", body[pos:]))
    return root, used


def load_directory(directory):
    """Load every ``*.prompt.md`` in a folder: ``{name: Template}``. A bad file raises with its path."""
    result = {}
    for path in sorted(Path(directory).glob("*.prompt.md")):
        template = parse_template(path.read_text(encoding="utf-8"), source=str(path))
        result[template.name] = template
    return result


# --------------------------------------------------------------------------- rendering
def _neutralise(value, name):
    marker = name.upper()
    return value.replace(f"{marker}>>>", f"{marker}> > >").replace(f"<<<{marker}", f"< < <{marker}")


def _resolve(template, values):
    unknown = sorted(set(values) - set(template.variables))
    if unknown:
        raise TemplateError(f"template '{template.name}': unknown variable '{unknown[0]}'")
    resolved = {}
    for name, spec in template.variables.items():
        value = values.get(name)
        if value is None:
            if spec.default is not None:
                value = spec.default
            elif spec.required:
                raise TemplateError(f"template '{template.name}': missing required variable '{name}'")
            else:
                value = [] if spec.type == "list" else ""
        if spec.type == "list":
            if not isinstance(value, (list, tuple)) or not all(isinstance(i, (str, int)) and not isinstance(i, bool) for i in value):
                raise TemplateError(f"template '{template.name}': variable '{name}' must be a list of strings")
            value = [str(i) for i in value]
        elif spec.type == "int":
            if isinstance(value, bool) or not (isinstance(value, int) or (isinstance(value, str) and re.fullmatch(r"-?\d+", value))):
                raise TemplateError(f"template '{template.name}': variable '{name}' must be an integer")
            value = str(int(value))
        else:
            if isinstance(value, bool) or not isinstance(value, (str, int, float)):
                raise TemplateError(f"template '{template.name}': variable '{name}' must be a string")
            value = str(value)
            if spec.enum and value and value not in spec.enum:
                raise TemplateError(f"template '{template.name}': variable '{name}' must be one of {', '.join(spec.enum)} (got {value!r})")
        if spec.required and spec.type == "string" and not value.strip():
            raise TemplateError(f"template '{template.name}': required variable '{name}' is empty")
        resolved[name] = value
    return resolved


def render(template, values=None):
    resolved = _resolve(template, values or {})
    out = []

    def emit(nodes, this=None, this_untrusted_name=None):
        for node in nodes:
            kind = node[0]
            if kind == "text":
                out.append(node[1])
            elif kind == "this":
                out.append(_neutralise(this, this_untrusted_name) if this_untrusted_name else this)
            elif kind == "var":
                spec, value = template.variables[node[1]], resolved[node[1]]
                if spec.untrusted:
                    marker = node[1].upper()
                    out.append(f"<<<{marker}\n{_neutralise(value, node[1])}\n{marker}>>>")
                else:
                    out.append(value)
            elif kind == "if":
                value = resolved[node[1]]
                if (len(value) > 0) if isinstance(value, (list, str)) else True:
                    emit(node[2], this, this_untrusted_name)
            elif kind == "each":
                spec = template.variables[node[1]]
                for item in resolved[node[1]]:
                    emit(node[2], item, node[1] if spec.untrusted else None)

    emit(template.body)
    return "".join(out)

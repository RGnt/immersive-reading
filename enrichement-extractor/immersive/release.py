"""Frozen-lock release inventory and installed payload integrity checks."""
import base64
import hashlib
import importlib.metadata as metadata
from pathlib import Path
import tomllib

from packaging.markers import Marker
from packaging.tags import sys_tags
from packaging.utils import canonicalize_name, parse_wheel_filename


def locked_packages(lockfile, *, extras=("print",)):
    lock = tomllib.loads(Path(lockfile).read_text(encoding="utf-8"))
    packages = {canonicalize_name(p["name"]): p for p in lock["package"]}
    project = packages["immersive-reading"]
    pending = list(project.get("dependencies", []))
    for extra in extras:
        if extra not in project.get("optional-dependencies", {}):
            raise ValueError(f"Unknown locked extra: {extra}")
        pending += project["optional-dependencies"][extra]
    selected = {}
    while pending:
        dependency = pending.pop()
        if dependency.get("marker") and not Marker(dependency["marker"]).evaluate():
            continue
        name = canonicalize_name(dependency["name"])
        if name in selected: continue
        package = packages[name]
        selected[name] = package
        pending += package.get("dependencies", [])
        for extra in dependency.get("extra", []):
            pending += package.get("optional-dependencies", {}).get(extra, [])
    return selected


def distributions(lockfile, *, extras=("print",)):
    selected = locked_packages(lockfile, extras=extras)
    result = {}
    for name, package in selected.items():
        try: distribution = metadata.distribution(name)
        except metadata.PackageNotFoundError as error:
            raise ValueError(f"Frozen release dependency missing: {name}=={package['version']}") from error
        if distribution.version != package["version"]:
            raise ValueError(f"Frozen release requires {name}=={package['version']}; installed {distribution.version}")
        result[name] = distribution
    return result


def verify_installed_file(path, entry):
    if entry.hash is not None:
        checksum = hashlib.new(entry.hash.mode, Path(path).read_bytes()).digest()
        actual = base64.urlsafe_b64encode(checksum).rstrip(b"=").decode("ascii")
        if actual != entry.hash.value:
            raise ValueError(f"Installed dependency payload changed: {path}")


def locked_artifact(package):
    supported = list(sys_tags())
    choices = []
    for wheel in package.get("wheels", []):
        try: _, _, _, tags = parse_wheel_filename(wheel["url"].rsplit("/", 1)[-1])
        except ValueError: continue
        ranks = [i for i, tag in enumerate(supported) if tag in tags]
        if ranks: choices.append((min(ranks), wheel))
    if not choices:
        raise ValueError(f"No frozen wheel for this runtime: {package['name']}")
    wheel = min(choices, key=lambda c: c[0])[1]
    return {"url": wheel["url"], "sha256": wheel["hash"].removeprefix("sha256:")}


def tree_fingerprints(root, *, exclude=()):
    root = Path(root).resolve()
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise ValueError(f"Linked release payload: {path}")
        relative = path.relative_to(root).as_posix()
        parts = path.relative_to(root).parts
        if ".cache" in parts or (parts and parts[0] in {"books", "output"} and relative != "books/PLACE_BOOK_FOLDERS_HERE.txt"):
            continue
        if parts[:2] == ("data", "yomitan") and relative != "data/yomitan/PLACE_DICTIONARIES_HERE.txt":
            continue
        if path.is_file() and relative not in exclude and "__pycache__" not in path.parts and path.suffix != ".pyc":
            result[relative] = {"sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "size": path.stat().st_size}
    return result

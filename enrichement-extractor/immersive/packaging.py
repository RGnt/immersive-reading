"""ZIPs contain only checksum-verified artifacts from publication manifests."""
import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
from .jobs import check_cancelled
from .publication import Publication, read_manifest, relative_name, PublicationError


def write_archive(destination, members, manifest, *, cancel=None):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(destination, "w", ZIP_DEFLATED) as archive:
        for name, (source, expected) in sorted(members.items()):
            check_cancelled(cancel)
            relative_name(name)
            if name == "publication-manifest.json" or name.startswith(".publication/"):
                raise PublicationError("Reserved archive member")
            digest, size = hashlib.sha256(), 0
            with Path(source).open("rb") as stream, archive.open(name, "w", force_zip64=True) as target:
                while chunk := stream.read(1024*1024):
                    check_cancelled(cancel)
                    digest.update(chunk)
                    size += len(chunk)
                    target.write(chunk)
            if {"sha256": digest.hexdigest(), "size": size} != expected:
                raise PublicationError(f"Artifact changed during packaging: {source}")
        check_cancelled(cancel)
        archive.writestr("publication-manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))


def package(root, *, cancel=None):
    root = Path(root).resolve()
    if not root.is_dir():
        raise ValueError(f"Output directory does not exist: {root}")
    with Publication(root, base=read_manifest(root), cancel=cancel) as publication:
        manifest, _ = publication.publish(package=True)
    errors = [e for e in manifest.get("export_errors", []) if e["stage"] == "packaging"]
    if errors:
        raise PublicationError("Archive export failed: " + errors[0]["message"])
    return root / manifest["shared"]["archive"]["path"]


def main(argv=None):
    parser = argparse.ArgumentParser(description="Package manifest-owned published outputs")
    parser.add_argument("--output", type=Path, default=Path("output"))
    args = parser.parse_args(argv)
    try:
        print(package(args.output))
    except (ValueError, OSError) as error:
        parser.exit(1, f"Packaging error: {error}\n")


if __name__ == "__main__":
    main()

"""Build a relocatable Windows x64 Python distribution with vendored dependencies."""
import argparse
import hashlib
import importlib.metadata as metadata
import json
import os
import platform
import shutil
import subprocess
import sys
import sysconfig
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
from packaging.requirements import Requirement
from packaging.utils import canonicalize_name
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from immersive.release import distributions, locked_packages, locked_artifact, verify_installed_file, tree_fingerprints

ROOT = Path(__file__).resolve().parents[1]
REQUIRED = ["beautifulsoup4","lxml","sudachipy","sudachidict_core","reportlab","pymupdf","fonttools","jsonschema","packaging"]


def dependencies():
    return distributions(ROOT / "uv.lock")


def build(destination):
    if sys.platform != "win32" or platform.machine().lower() not in {"amd64","x86_64"}:
        raise ValueError("Build this Windows x64 bundle using Windows x64 Python.")
    destination = destination.resolve()
    if not destination.is_relative_to(ROOT/"dist"):
        raise ValueError("Portable builds must remain inside this project's dist folder")
    destination.mkdir(parents=True,exist_ok=True)
    runtime = destination/"python"
    runtime.mkdir(exist_ok=True)
    base = Path(sys.base_prefix)
    for name in ["python.exe","pythonw.exe","python3.dll",f"python{sys.version_info.major}{sys.version_info.minor}.dll","vcruntime140.dll","vcruntime140_1.dll","LICENSE.txt"]:
        if (base/name).exists():
            shutil.copy2(base/name,runtime/name)
    ignore = shutil.ignore_patterns("__pycache__","*.pyc","site-packages","test","tests","ensurepip","idlelib","tkinter")
    for folder in ["Lib","DLLs"]:
        shutil.copytree(base/folder,runtime/folder,dirs_exist_ok=True,ignore=ignore)
    site = runtime/"Lib/site-packages"
    if site.exists():
        if not site.resolve().is_relative_to(destination):
            raise ValueError("Refusing to replace dependencies outside the distribution")
        shutil.rmtree(site)
    site.mkdir(parents=True,exist_ok=True)
    installed_site = Path(sysconfig.get_path("purelib")).resolve()
    dists = dependencies()
    locked = locked_packages(ROOT / "uv.lock")
    inventory = []
    print(f"Vendoring {len(dists)} dependency distributions...",flush=True)
    for name, dist in sorted(dists.items()):
        for entry in dist.files or []:
            source = Path(dist.locate_file(entry)).resolve()
            if not source.is_file() or not source.is_relative_to(installed_site) or "__pycache__" in source.parts or source.suffix == ".pyc":
                continue
            if source.name in {"uv_cache.json", "direct_url.json"}:
                continue
            verify_installed_file(source, entry)
            target = site/source.relative_to(installed_site)
            target.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(source,target)
        inventory.append({"name":name,"version":dist.version,"license":dist.metadata.get("License-Expression") or dist.metadata.get("License") or "See package license files",
                          "homepage":dist.metadata.get("Home-page"), "locked_artifact": locked_artifact(locked[name])})
    pth = runtime/f"python{sys.version_info.major}{sys.version_info.minor}._pth"
    pth.write_text(".\nLib\nDLLs\nLib/site-packages\n../app\nimport site\n",encoding="utf-8")
    app = destination/"app"
    if app.exists():
        if not app.resolve().is_relative_to(destination):
            raise ValueError("Refusing to replace application modules outside the distribution")
        shutil.rmtree(app)
    app.mkdir(exist_ok=True)
    for folder in ["immersive","scripts","tests"]:
        shutil.copytree(ROOT/folder,app/folder,dirs_exist_ok=True,ignore=shutil.ignore_patterns("__pycache__","*.pyc"))
    for file in ["pyproject.toml","uv.lock"]:
        shutil.copy2(ROOT/file,app/file)
    resources = destination/"data"
    resources.mkdir(exist_ok=True)
    shutil.copy2(ROOT/"data/kanjidic2.xml.gz",resources/"kanjidic2.xml.gz")
    for folder in ["kanjivg","fonts","kanji-alive","charts"]:
        shutil.copytree(ROOT/"data"/folder,resources/folder,dirs_exist_ok=True,ignore=shutil.ignore_patterns("*variable.ttf"))
    (resources / "yomitan").mkdir(exist_ok=True)
    (resources / "yomitan/PLACE_DICTIONARIES_HERE.txt").write_text("Place your local format-3 Yomitan dictionary ZIPs here, or pass --yomitan <path> repeatedly in priority order. Dictionaries are not included in the distribution.\n", encoding="utf-8")
    for folder in ["books","output","licenses"]:
        (destination/folder).mkdir(exist_ok=True)
    (destination/"books/PLACE_BOOK_FOLDERS_HERE.txt").write_text("Create one folder per book. Put its EPUB and optional UTF-8 Jiten CSV decks inside. Without CSVs, supply Yomitan term dictionaries in data/yomitan/ or through --yomitan. No books are included in this distribution.\n",encoding="utf-8")
    (app/"run_pipeline.py").write_text('from pathlib import Path\nimport os\nfrom immersive.pipeline import main\nos.chdir(Path(__file__).resolve().parents[1])\nraise SystemExit(main())\n',encoding="utf-8")
    (destination/"Run.cmd").write_text('@echo off\r\nsetlocal\r\npushd "%~dp0"\r\n"%~dp0python\\python.exe" -I "%~dp0app\\run_pipeline.py" %*\r\nset "pipeline_exit=%ERRORLEVEL%"\r\npopd\r\nexit /b %pipeline_exit%\r\n',encoding="utf-8")
    (destination/"Check.cmd").write_text('@echo off\r\ncall "%~dp0Run.cmd" --doctor\r\n',encoding="utf-8")
    shutil.copy2(ROOT/"docs/portable-pipeline.md",destination/"README.md")
    for guide in ("yomitan-dictionaries.md", "reliable-publication.md", "shared-resources.md",
                  "profiling-caching.md", "ambiguity-handling.md", "performance-results.json"):
        shutil.copy2(ROOT / "docs" / guide, destination / guide)
    (destination/"licenses/dependencies.json").write_text(json.dumps(inventory,indent=2),encoding="utf-8")
    shutil.copy2(base/"LICENSE.txt",destination/"licenses/PYTHON-LICENSE.txt")
    shutil.copy2(ROOT/"data/fonts/OFL.txt",destination/"licenses/NOTO-OFL.txt")
    (destination/"licenses/THIRD-PARTY-NOTICES.txt").write_text(
        "Python and all bundled package license/NOTICE files are retained. Package notices are in python/Lib/site-packages/*.dist-info and package license directories.\n"
        "PyMuPDF and its bundled MuPDF components use AGPL/commercial licensing; their license files are included. Pipeline Python source is supplied in app/. Observe the included license terms when redistributing.\n"
        "KANJIDIC2: James William Breen / EDRDG, CC BY-SA 4.0. https://www.edrdg.org/edrdg/licence.html\n"
        "Kanji alive primary meanings: Harumi Hibino Lory & Arno Bosse, CC BY 4.0. https://kanjialive.com/credits/ License and pinned source metadata are in data/kanji-alive/.\n"
        "KanjiVG: Ulrich Apel, CC BY-SA 3.0. https://github.com/KanjiVG/kanjivg\n"
        "Noto Sans JP: SIL OFL 1.1; bundled regular font is a static instance of the upstream variable font.\n"
        "Input books and decks, generated study files, API keys and personal settings are not bundled.\n",encoding="utf-8")
    manifest = {"schema_version":2,"platform":"windows-x64","python":sys.version.split()[0],
                "lock_sha256":hashlib.sha256((ROOT/"uv.lock").read_bytes()).hexdigest(),
                "packages":[{"name":d["name"],"version":d["version"],"locked_artifact":d["locked_artifact"]} for d in inventory],"includes_books":False,
                "files":tree_fingerprints(destination, exclude=("distribution.json",))}
    (destination/"distribution.json").write_text(json.dumps(manifest,indent=2),encoding="utf-8")
    print("Checking the isolated bundled runtime...",flush=True)
    subprocess.run([str(runtime/"python.exe"),"-I",str(app/"run_pipeline.py"),"--doctor"],check=True,cwd=destination)
    archive = destination.with_name(destination.name + ".zip")
    print(f"Writing {archive.name}...",flush=True)
    with ZipFile(archive,"w",ZIP_DEFLATED,compresslevel=6) as z:
        for path in sorted(destination.rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc":
                relative = path.relative_to(destination)
                if ".cache" in relative.parts:
                    continue
                if relative.parts[:2] == ("data", "yomitan") and relative.as_posix() != "data/yomitan/PLACE_DICTIONARIES_HERE.txt":
                    continue
                if relative.parts[0] in {"books","output"} and relative.as_posix() != "books/PLACE_BOOK_FOLDERS_HERE.txt":
                    continue
                z.write(path,destination.name+"/"+path.relative_to(destination).as_posix())
    archive.with_suffix(".zip.sha256").write_text(hashlib.sha256(archive.read_bytes()).hexdigest()+"  "+archive.name+"\n",encoding="ascii")
    print(json.dumps({"archive":str(archive),"bytes":archive.stat().st_size,"packages":len(dists)}),flush=True)
    return archive


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--destination",type=Path,default=ROOT/"dist/ImmersivePipeline-windows-x64")
    args=parser.parse_args()
    build(args.destination)

"""Build a release ZIP from an explicit allowlist without deleting older releases."""
import argparse
import json
import os
from pathlib import Path
import re
import tempfile
import zipfile


def create_package(root: Path, version: str) -> Path:
    root = root.resolve()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        raise ValueError("Expected a numeric major.minor.patch version")
    src = root / "src" / "DynastyRetinue"
    files = [(src / name, name) for name in (
        "Info.json", "archetypes.json", "plans.json", "looks.json", "l10n_en.json")]
    files += [(src / "bin" / "Release" / "DynastyRetinue.dll", "DynastyRetinue.dll"),
              (root / "README.md", "README.md"), (root / "LICENSE", "LICENSE")]
    for source, _ in files:
        if not source.is_file():
            raise FileNotFoundError(source)
    info = json.loads((src / "Info.json").read_text(encoding="utf-8-sig"))
    if info.get("Version") != version:
        raise ValueError("Info.json version does not match the requested release")
    dist = root / "dist"
    dist.mkdir(exist_ok=True)
    result = dist / f"DynastyRetinue-{version}.zip"
    with tempfile.NamedTemporaryFile(prefix=".dynasty-release-", suffix=".tmp", dir=dist,
                                     delete=False) as temporary:
        staging = Path(temporary.name)
    try:
        with zipfile.ZipFile(staging, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for source, name in files:
                archive.write(source, "DynastyRetinue/" + name)
        with zipfile.ZipFile(staging) as archive:
            expected = ["DynastyRetinue/" + name for _, name in files]
            if archive.namelist() != expected or archive.testzip() is not None:
                raise ValueError("Release archive verification failed")
            for source, name in files:
                if archive.read("DynastyRetinue/" + name) != source.read_bytes():
                    raise ValueError("Release file changed while packaging: " + name)
        os.replace(staging, result)
    finally:
        staging.unlink(missing_ok=True)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("version")
    args = parser.parse_args()
    print(create_package(Path(__file__).resolve().parent.parent, args.version))

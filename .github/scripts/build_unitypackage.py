#!/usr/bin/env python3
"""Builds a .unitypackage from the UPM package folder without needing a Unity install.

A .unitypackage is a gzipped tar with one folder per asset, named after the asset's GUID, holding:
  asset       the file contents (omitted for folders)
  asset.meta  the asset's .meta file
  pathname    the path the asset is imported to, e.g. Assets/FastScriptReload/Scripts/Foo.cs

Usage: build_unitypackage.py <package dir> <import root> <output file>
"""

import io
import os
import re
import sys
import tarfile

GUID_PATTERN = re.compile(r"^guid:\s*([0-9a-f]{32})\s*$", re.MULTILINE)


def is_hidden_by_unity(name):
    # Unity ignores names ending in '~' or starting with '.', so they aren't part of an import either.
    return name.endswith("~") or name.startswith(".")


def add_bytes(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o644
    tar.addfile(info, io.BytesIO(data))


def main():
    package_dir, import_root, output = sys.argv[1:4]
    package_dir = os.path.normpath(package_dir)
    count = 0

    with tarfile.open(output, "w:gz", format=tarfile.GNU_FORMAT) as tar:
        for root, dirs, files in os.walk(package_dir):
            dirs[:] = sorted(d for d in dirs if not is_hidden_by_unity(d))
            entries = [os.path.join(root, d) for d in dirs]
            entries += [os.path.join(root, f) for f in sorted(files) if not f.endswith(".meta") and not is_hidden_by_unity(f)]

            for path in entries:
                meta_path = path + ".meta"
                if not os.path.exists(meta_path):
                    print(f"warning: skipping '{path}', it has no .meta file", file=sys.stderr)
                    continue

                with open(meta_path, "rb") as f:
                    meta = f.read()
                match = GUID_PATTERN.search(meta.decode("utf-8"))
                if not match:
                    sys.exit(f"error: no guid in '{meta_path}'")
                guid = match.group(1)

                relative_path = os.path.relpath(path, package_dir).replace(os.sep, "/")
                add_bytes(tar, f"{guid}/pathname", f"{import_root}/{relative_path}".encode("utf-8"))
                add_bytes(tar, f"{guid}/asset.meta", meta)
                if os.path.isfile(path):
                    with open(path, "rb") as f:
                        add_bytes(tar, f"{guid}/asset", f.read())
                count += 1

    print(f"Wrote {count} assets to {output}")


if __name__ == "__main__":
    main()

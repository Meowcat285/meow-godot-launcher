#!/usr/bin/env python3
# Regenerates the fixtures with the delta generator of the Godot fork:
#   python3 make_fixtures.py <godot>/misc/scripts/generate_delta_patches.py . <scratch folder>
# Requires the `bsdiff4` and `zstandard` packages.

import os
import random
import shutil
import sys
from pathlib import Path

script, out = sys.argv[1], Path(sys.argv[2])
src = open(script).read()
assert src.rstrip().endswith("main()")
src = src.rstrip()[:-len("main()")]
exec(compile(src, script, "exec"), sys.modules["__main__"].__dict__)

work = Path(sys.argv[3]); shutil.rmtree(work, ignore_errors=True)
old, new = work / "old", work / "new"
rnd = random.Random(42)
def write(p, data, mode=0o644):
    p.parent.mkdir(parents=True, exist_ok=True); p.write_bytes(data); os.chmod(p, mode)
# An "executable": blocks of structured data, where the new version inserts bytes and changes some values,
# shifting everything after like a recompile does.
blocks = [bytes(rnd.getrandbits(8) for _ in range(64)) for _ in range(256)]
old_exe = b"".join(blocks * 4)
new_blocks = list(blocks); new_blocks.insert(100, b"inserted code" * 20); new_blocks[30] = bytes(64)
new_exe = b"".join(new_blocks * 4)
write(old / "pkg/bin/godot.linuxbsd.editor.x86_64", old_exe, 0o755)
write(new / "pkg/bin/godot.linuxbsd.editor.x86_64", new_exe, 0o755)
write(old / "pkg/same.txt", b"unchanged\n"); write(new / "pkg/same.txt", b"unchanged\n")
write(old / "pkg/old-name.dat", b"moved file" * 100); write(new / "pkg/new-name.dat", b"moved file" * 100)
write(old / "pkg/removed.txt", b"gone\n")
write(new / "pkg/added.txt", b"brand new file\n" * 10)

# A nested archive, which is patched entry by entry.
import zipfile
def nested(path, big, extra):
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w") as zf:
        zf.writestr(zipfile.ZipInfo("sub/"), b"")
        zf.writestr("sub/big.bin", big, compress_type=zipfile.ZIP_DEFLATED)
        zf.writestr("stored.txt", b"stored as is\n" * 50, compress_type=zipfile.ZIP_STORED)
        for name, data in extra.items():
            zf.writestr(name, data, compress_type=zipfile.ZIP_DEFLATED)
nested(old / "pkg/templates.zip", old_exe, {})
nested(new / "pkg/templates.zip", new_exe, {"sub/new.txt": b"added inside the archive\n"})
# APKs are patched the same way, and so are archives inside archives, like the AARs in `android_source.zip`.
nested(old / "pkg/android.apk", old_exe[::3], {}); nested(new / "pkg/android.apk", new_exe[::3], {})
for root, exe in ((old, old_exe), (new, new_exe)):
    nested(work / "aar" / root.name / "lib.aar", exe[1::3], {})
    nested(root / "pkg/android_source.zip", b"gradle", {"libs/lib.aar": (work / "aar" / root.name / "lib.aar").read_bytes()})

def build_delta(package, old_tag, old_dir, old_files, new_tag, new_dir, new_files, work, out, jobs):
    # Finishing a delta deletes the old folder, which is still needed to make the fixtures.
    old_copy = work / f"{old_dir.name}-copy"
    shutil.copytree(old_dir, old_copy)
    state = PackageState({"size": 1 << 62}, new_dir, {"deltas": []})
    progress = Progress(new_tag, out)
    with ProcessPoolExecutor(max_workers=jobs, mp_context=multiprocessing.get_context("fork")) as pool:
        progress.add(start_delta(pool, state, package, old_tag, old_copy, old_files, new_files, work))
        progress.run()
    return out / f"{package}.from-{old_tag}.mgdelta"

def scan(root):
    files = {}
    for p in sorted(root.rglob("*")):
        if p.is_file():
            rel = p.relative_to(root).as_posix()
            files[rel] = FileInfo(rel, p.stat().st_size, sha256_file(p), p.stat().st_mode & 0o777)
    return files
out.mkdir(parents=True, exist_ok=True)
delta = build_delta("editor-linux-x86_64", "1.0", old, scan(old), "1.1", new, scan(new), work, out, 1)
shutil.make_archive(str(out / "old"), "zip", old)
shutil.make_archive(str(out / "new"), "zip", new)
print(delta, delta.stat().st_size)

# Export templates, to install per platform: Linux binaries are patched, a web zip is patched entry by entry,
# Windows templates are only in the new release.
told, tnew = work / "templates-old", work / "templates-new"
write(told / "templates/version.txt", b"4.8.beta.mono\n"); write(tnew / "templates/version.txt", b"4.8.beta.mono\n")
for name in ("linux_debug.x86_64", "linux_release.x86_64"):
    write(told / "templates" / name, old_exe, 0o755); write(tnew / "templates" / name, new_exe, 0o755)
nested(told / "templates/web_debug.zip", old_exe, {}); nested(tnew / "templates/web_debug.zip", new_exe, {"sub/new.txt": b"added\n"})
nested(told / "templates/web_release.zip", old_exe[::2], {}); nested(tnew / "templates/web_release.zip", new_exe[::2], {})
for name in ("windows_debug_x86_64.exe", "windows_release_x86_64.exe"):
    write(tnew / "templates" / name, new_exe[::-1])
delta = build_delta("export-templates", "1.0", told, scan(told), "1.1", tnew, scan(tnew), work, out, 1)
shutil.make_archive(str(out / "templates-old"), "zip", told)
shutil.make_archive(str(out / "templates-new"), "zip", tnew)
print(delta, delta.stat().st_size)

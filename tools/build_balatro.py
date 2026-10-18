"""Build a bridged copy of Balatro.

Balatro.exe is a "fused" LÖVE game: the LÖVE runtime followed by a zip of the Lua game.
This script splits the two, injects the Lua bridge from balatro-mod/ into the zip,
and writes the re-fused game plus its runtime DLLs to build/balatro/.
The original game files are only read, never modified.

By default the bridged build uses its own save profile (%APPDATA%/BalatroBridge). That
way, runs started by Slay the Spire 2 can never overwrite your real Balatro progress.
Pass --shared-saves to use your real profile instead.

Usage:
    python tools/build_balatro.py [--balatro-dir DIR] [--out DIR] [--shared-saves]
"""

import argparse
import io
import shutil
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MARKER = b"-- Injected by Balatro the Spire tools/build_balatro.py"
OLD_MARKERS = (b"-- Injected by Bolatro tools/build_balatro.py",)  # builds from before the rename
ISOLATED_IDENTITY = "BalatroBridge"


def split_fused_exe(data: bytes) -> tuple[bytes, zipfile.ZipFile]:
    """Returns (LÖVE runtime bytes, game zip). zipfile resolves the appended zip's real offset."""
    game = zipfile.ZipFile(io.BytesIO(data))
    start = min(info.header_offset for info in game.infolist())
    if data[start:start + 4] != b"PK\x03\x04":
        sys.exit(f"error: no zip local header at offset {start}; is this a fused LÖVE executable?")
    return data[:start], game


def patch_main(src: bytes) -> bytes:
    if MARKER in src or any(old in src for old in OLD_MARKERS):
        sys.exit("error: main.lua is already patched; point --balatro-dir at the original game")
    return src.rstrip() + b"\n\n" + MARKER + b"\nrequire('bridge.core').install()\n"


def patch_conf(src: bytes, identity: str | None) -> bytes:
    """Optionally isolates saves, and boots the hidden engine (bridge/engine.lua) in a tiny window."""
    needle = b"function love.conf(t)"
    end = b"end"
    if needle not in src or not src.rstrip().endswith(end):
        sys.exit("error: conf.lua no longer looks like 'function love.conf(t) ... end'; update patch_conf")
    if identity:
        src = src.replace(needle, needle + f"\n\tt.identity = '{identity}' ".encode() + MARKER, 1)
    hidden = (b"\t" + MARKER + b"\n"
              b"\tif os.getenv('BALATRO_BRIDGE_HIDDEN') == '1' then\n"
              b"\t\tt.window.width, t.window.height, t.window.borderless = 320, 180, true\n"
              b"\tend\n")
    body = src.rstrip()[:-len(end)]
    return body + hidden + end + b"\n"


def extract_assets(game: zipfile.ZipFile, out: Path, folder: str) -> int:
    """Copies Balatro's files under `folder` next to the exe, so StS2 can use them directly:
    the sprite sheets to draw cards with Balatro's art, the sounds to play Balatro's music and
    card sounds through StS2's own audio output."""
    count = 0
    for info in game.infolist():
        if info.filename.startswith(folder) and not info.is_dir():
            target = out / info.filename
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(game.read(info))
            count += 1
    return count


def main() -> None:
    parser = argparse.ArgumentParser(description="Build Balatro with the Balatro the Spire bridge injected.")
    parser.add_argument("--balatro-dir", type=Path, default=ROOT / "games" / "Balatro",
                        help="folder containing the original Balatro.exe (default: games/Balatro)")
    parser.add_argument("--mod-dir", type=Path, default=ROOT / "balatro-mod",
                        help="Lua files to add to the game (default: balatro-mod)")
    parser.add_argument("--out", type=Path, default=ROOT / "build" / "balatro",
                        help="output folder (default: build/balatro)")
    parser.add_argument("--shared-saves", action="store_true",
                        help="use your real Balatro save profile instead of an isolated one")
    args = parser.parse_args()

    exe = args.balatro_dir / "Balatro.exe"
    if not exe.is_file():
        sys.exit(f"error: {exe} not found")
    runtime, game = split_fused_exe(exe.read_bytes())
    version = game.read("version.jkr").decode(errors="replace").splitlines()[0] if "version.jkr" in game.namelist() else "?"

    mod_files = {p.relative_to(args.mod_dir).as_posix(): p.read_bytes()
                 for p in sorted(args.mod_dir.rglob("*")) if p.is_file()}
    clashes = sorted(set(mod_files) & set(game.namelist()))
    if clashes:
        sys.exit(f"error: mod files would replace game files: {clashes}")

    # Before repacking: ZipFile.writestr() rewrites the ZipInfo offsets it is given.
    args.out.mkdir(parents=True, exist_ok=True)
    textures = extract_assets(game, args.out, "resources/textures/")
    sounds = extract_assets(game, args.out, "resources/sounds/")

    patched = io.BytesIO()
    with zipfile.ZipFile(patched, "w", zipfile.ZIP_DEFLATED) as out:
        for info in game.infolist():
            content = game.read(info)
            if info.filename == "main.lua":
                content = patch_main(content)
            elif info.filename == "conf.lua":
                content = patch_conf(content, None if args.shared_saves else ISOLATED_IDENTITY)
            out.writestr(info, content)
        for name, content in mod_files.items():
            out.writestr(name, content)

    args.out.mkdir(parents=True, exist_ok=True)
    for f in args.balatro_dir.iterdir():
        if f.is_file() and f.name != "Balatro.exe":
            shutil.copy2(f, args.out / f.name)
    (args.out / "Balatro.exe").write_bytes(runtime + patched.getvalue())

    saves = "your real Balatro profile" if args.shared_saves else f"isolated profile %APPDATA%\\{ISOLATED_IDENTITY}"
    print(f"Built bridged Balatro {version} -> {args.out / 'Balatro.exe'}")
    print(f"  injected {len(mod_files)} file(s): {', '.join(mod_files)}")
    print(f"  extracted {textures} texture(s) and {sounds} sound(s) for StS2's table")
    print(f"  saves: {saves}")


if __name__ == "__main__":
    main()

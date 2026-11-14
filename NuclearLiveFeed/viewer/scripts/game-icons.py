# /// script
# requires-python = ">=3.11"
# dependencies = ["UnityPy>=1.20", "Pillow>=10"]
# ///
"""Builds the data of the "Game" icon set from a local Nuclear Option client.

Usage: uv run --script scripts/game-icons.py "<game>/NuclearOption_Data" [out.json]
Default output: src/icon-sets/generated/game-icons.json (ignored by Git: game-derived images never enter the
repository; they reach map.scramd.com only inside the built page, as the user decided on 2026-11-08).

Per unit definition (UnitDefinition and subclasses, keyed by jsonKey = the catalog's `type`):
- `map`: the map icon the game draws for aircraft (a top-down silhouette, white on black, drawn additively);
- `friendly` / `hostile`: the target icons of the HUD and target list (a rectangle or diamond frame with a kind glyph).
Each sprite becomes two white alpha masks, cropped to the shape: `fill` (the shape and everything its frame encloses)
and `line` (the drawn lines). The page tints them: fill in the side's color, lines dark, like the NATO symbols.
"""
import base64
import io
import json
import os
import struct
import sys
from pathlib import Path

import UnityPy
from PIL import Image, ImageChops

DEF_CLASSES = {"UnitDefinition", "AircraftDefinition", "BuildingDefinition", "MissileDefinition",
               "SceneryDefinition", "ShipDefinition", "VehicleDefinition"}
SIZE = 128  # longest side of the stored masks


def read_string(b, o):
    n = struct.unpack_from("<i", b, o)[0]
    if n < 0 or n > 65536:
        raise ValueError(f"bad string length {n} at {o}")
    return b[o + 4:o + 4 + n].decode("utf-8", "replace"), (o + 4 + n + 3) & ~3


def pptr(b, o):
    return struct.unpack_from("<iq", b, o), o + 12


def luminance(img: Image.Image) -> Image.Image:
    """White-on-black sprites are drawn additively in the game: black adds nothing. Alpha = brightness x alpha."""
    rgba = img.convert("RGBA")
    lum = rgba.convert("RGB").convert("L")
    return ImageChops.multiply(lum, rgba.getchannel("A"))


def enclosed(lines: Image.Image) -> Image.Image:
    """Everything not reachable from the border without crossing a line: the frame and its inside."""
    w, h = lines.size
    wall = lines.point(lambda v: 255 if v > 96 else 0)
    px = wall.load()
    outside = Image.new("L", (w, h), 0)
    out = outside.load()
    stack = [(x, y) for x in range(w) for y in (0, h - 1)] + [(x, y) for y in range(h) for x in (0, w - 1)]
    while stack:
        x, y = stack.pop()
        if x < 0 or y < 0 or x >= w or y >= h or out[x, y] or px[x, y]:
            continue
        out[x, y] = 255
        stack.extend(((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)))
    return ImageChops.invert(outside)


def mask_uri(mask: Image.Image) -> str:
    white = Image.new("RGBA", mask.size, (255, 255, 255, 0))
    white.putalpha(mask)
    buf = io.BytesIO()
    white.save(buf, "PNG", optimize=True)
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode()


def prepare(img: Image.Image, kind: str) -> dict:
    lines = luminance(img)
    fill = lines if kind == "map" else ImageChops.lighter(enclosed(lines), lines)
    box = fill.getbbox() or (0, 0, *fill.size)
    fill, lines = fill.crop(box), lines.crop(box)
    scale = SIZE / max(fill.size)
    if scale < 1:
        size = (max(1, round(fill.width * scale)), max(1, round(fill.height * scale)))
        fill, lines = fill.resize(size, Image.LANCZOS), lines.resize(size, Image.LANCZOS)
    entry = {"w": fill.width, "h": fill.height, "fill": mask_uri(fill)}
    if kind != "map":
        entry["line"] = mask_uri(lines)
    return entry


def main():
    data_dir = Path(sys.argv[1])
    target = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parents[1] / "src" / "icon-sets" / "generated" / "game-icons.json"
    names = [n for n in os.listdir(data_dir) if n.endswith(".assets") or (n.startswith("level") and "." not in n) or n == "globalgamemanagers"]
    env = UnityPy.load(*[str(data_dir / n) for n in names])
    files = {os.path.basename(k.replace("\\", "/")).lower(): f for k, f in env.files.items()}
    rev = {id(f): n for n, f in files.items()}

    def resolve(sf, ref):
        fid, pid = ref
        if pid == 0:
            return None
        if fid == 0:
            return sf.objects.get(pid)
        target_file = files.get(sf.externals[fid - 1].path.replace("\\", "/").split("/")[-1].lower())
        return target_file.objects.get(pid) if target_file else None

    scripts = {}
    for fname, sf in files.items():
        for pid, obj in sf.objects.items():
            if obj.type.name == "MonoScript":
                try:
                    scripts[(fname, pid)] = obj.read().m_ClassName
                except Exception:
                    pass

    sprites: dict[str, dict] = {}
    types: dict[str, dict] = {}
    for fname, sf in files.items():
        for pid, obj in sf.objects.items():
            if obj.type.name != "MonoBehaviour":
                continue
            raw = obj.get_raw_data()
            if len(raw) < 60:
                continue
            script = resolve(sf, pptr(raw, 16)[0])
            if script is None or scripts.get((rev.get(id(script.assets_file)), script.path_id)) not in DEF_CLASSES:
                continue
            try:
                o = 28
                _asset, o = read_string(raw, o)
                o += 20 + 16  # typeIdentity, roleIdentity
                json_key, o = read_string(raw, o)
                for _ in range(5):  # workshop name, unit name, bogey name, description, code
                    _s, o = read_string(raw, o)
                o += 12  # visible range, icon range, radar size
                friendly, o = pptr(raw, o)
                hostile, o = pptr(raw, o)
                mapicon, o = pptr(raw, o)
                map_orient = raw[o] != 0
            except Exception as e:
                print(f"skipped {fname}:{pid}: {e}", file=sys.stderr)
                continue
            entry = {"orient": map_orient}
            for label, ref, kind in (("map", mapicon, "map"), ("friendly", friendly, "hud"), ("hostile", hostile, "hud")):
                so = resolve(sf, ref)
                if so is None or so.type.name != "Sprite":
                    continue
                spr = so.read()
                name = spr.m_Name
                if name not in sprites:
                    sprites[name] = prepare(spr.image, kind)
                entry[label] = name
            types[json_key] = entry
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps({"sprites": sprites, "types": types}, separators=(",", ":")), encoding="utf-8")
    print(f"{len(types)} unit types, {len(sprites)} sprites -> {target} ({target.stat().st_size // 1024} KiB)")


main()

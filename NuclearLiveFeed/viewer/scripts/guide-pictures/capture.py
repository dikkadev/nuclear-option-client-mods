# /// script
# requires-python = ">=3.11"
# dependencies = ["playwright>=1.48", "pillow>=10"]
# ///
"""Pictures of the real map for the guide: the whole screen with the example battle, and close-ups of its parts.
Needs the viewer dev server (pnpm dev, which has the window.__nlf test hook) and feed.py running.
Writes the webp files the guide embeds into src/guide-img/ and prints where the screen picture's parts are,
for the numbered callouts in guide.html (1100 x 640 CSS pixels)."""
import io
import json
import shutil
import sys
import tempfile
from pathlib import Path

from PIL import Image
from playwright.sync_api import sync_playwright

base = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5179"
feed = sys.argv[2] if len(sys.argv) > 2 else "ws://127.0.0.1:39555"
target = Path(sys.argv[3]) if len(sys.argv) > 3 else Path(__file__).resolve().parents[2] / "src" / "guide-img"
# Written elsewhere first: a file change under src/ makes the dev server reload the map page in the middle of the run.
out = Path(tempfile.mkdtemp(prefix="guide-img-"))
W, H = 1100, 640
scenario = (Path(__file__).parent / "scenario.js").read_text(encoding="utf-8")


def webp(png: bytes, name: str, scale: float = 1.0, quality: int = 78) -> None:
    img = Image.open(io.BytesIO(png)).convert("RGB")
    if scale != 1.0:
        img = img.resize((round(img.width * scale), round(img.height * scale)), Image.LANCZOS)
    img.save(out / name, "WEBP", quality=quality, method=6)
    print(name, img.size, (out / name).stat().st_size)


with sync_playwright() as p:
    browser = p.chromium.launch(channel="msedge")
    page = browser.new_page(viewport={"width": W, "height": H}, device_scale_factor=2)
    page.add_init_script("localStorage.clear()")
    page.goto(f"{base}/?feed={feed}", wait_until="networkidle")
    page.wait_for_function("window.__nlf && window.__nlf.viewer && window.__nlf.world.session")
    page.wait_for_timeout(800)
    page.evaluate(scenario)
    # About 30 km across the shorter side, centered a little ahead of you.
    page.evaluate("""() => {
      const v = window.__nlf.viewer, s = v.state, fit = s.scale / s.zoom;
      v.setView({ centerX: -9000 + 5500, centerZ: 7000 + 1000, zoom: Math.min(s.width, s.height) / 30000 / fit });
    }""")
    page.wait_for_timeout(600)
    # A small drag stops Follow, so the Recenter button shows like it does after you move the map.
    page.mouse.move(700, 470); page.mouse.down(); page.mouse.move(712, 470, steps=4); page.mouse.up()
    page.mouse.move(420, 520)  # empty map under the pointer: grid square and bearing at the bottom right
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(1500)
    page.evaluate(scenario)  # refresh positions and timestamps right before the picture
    page.wait_for_timeout(400)
    page.mouse.move(420, 520)
    page.wait_for_timeout(400)

    strip_text = page.evaluate("document.querySelector('#strip').innerText")
    full = page.screenshot()
    webp(full, "screen.webp", 1.0, 60)  # 2200 x 1280: sharp at 1100 CSS px and in "Show the picture larger"

    def box(sel):
        b = page.locator(sel).first.bounding_box()
        return None if b is None else {k: round(v, 1) for k, v in b.items()}

    own = page.evaluate("() => { const w = window.__nlf.world, v = window.__nlf.viewer; const p = w.predict(w.tracks.get('own'), performance.now()/1000); return v.worldToScreen(p.x, p.y); }")
    viper = page.evaluate("() => { const w = window.__nlf.world, v = window.__nlf.viewer; const p = w.predict(w.tracks.get('viper'), performance.now()/1000); return v.worldToScreen(p.x, p.y); }")
    callouts = {
        "size": [W, H], "topbar": box(".top-left"), "strip": box("#strip"), "layersButton": box("#btn-layers"),
        "inspector": box("#inspector"), "recenter": box("#recenter"), "bottomRight": box(".bottom-right"), "own": own, "viper": viper,
    }

    # Close-ups cut from the same picture, so their numbers match the whole screen.
    shot = Image.open(io.BytesIO(full))

    def cut(b: dict, name: str) -> None:
        img = shot.crop((round(b["x"] * 2), round(b["y"] * 2), round((b["x"] + b["width"]) * 2), round((b["y"] + b["height"]) * 2)))
        buf = io.BytesIO(); img.save(buf, "PNG")
        webp(buf.getvalue(), name, 1.0, 80)

    cut(callouts["topbar"], "part-topbar.webp")
    cut(callouts["inspector"], "part-details.webp")
    # The strip panel is wider than its entries (they sit at its right edge); cut to the entries.
    e = page.evaluate("""() => { const r = [...document.querySelectorAll('#strip button')].map(b => b.getBoundingClientRect());
      const x = Math.min(...r.map(b => b.left)) - 4, y = Math.min(...r.map(b => b.top)) - 4;
      return { x, y, width: Math.max(...r.map(b => b.right)) + 4 - x, height: Math.max(...r.map(b => b.bottom)) + 4 - y }; }""")
    cut(e, "part-strip.webp")
    callouts["stripEntries"] = e
    callouts["stripText"] = strip_text

    # Read a contact: the selected, targeted enemy fighter alone, with its label and path.
    page.evaluate(scenario)
    page.evaluate("""() => { const w = window.__nlf.world; for (const id of [...w.tracks.keys()]) if (id !== 'viper') w.tracks.delete(id);
      w.missileTargets.clear(); w.ownship = null; w.targets = ['viper']; window.__nlf.select('viper'); }""")
    page.wait_for_timeout(600)
    viper = page.evaluate("() => { const w = window.__nlf.world, v = window.__nlf.viewer; const p = w.predict(w.tracks.get('viper'), performance.now()/1000); return v.worldToScreen(p.x, p.y); }")
    x, y = viper["x"], viper["y"]
    contact = page.screenshot(clip={"x": x - 150, "y": y - 60, "width": 360, "height": 160})
    webp(contact, "contact.webp", 1.0, 82)
    callouts["contactViper"] = [150, 60]

    page.click("#btn-layers")
    page.wait_for_timeout(300)
    webp(page.locator("#layers").screenshot(), "part-layers.webp", 1.0, 80)
    callouts["layers"] = box("#layers")
    print(json.dumps(callouts, indent=1))
    page.click("#btn-layers")
    page.mouse.move(1050, 600)
    marks = (Path(__file__).parent / "marks.js").read_text(encoding="utf-8")
    page.evaluate(marks)
    for name in page.evaluate("Object.keys(window.__marks)"):
        page.evaluate(f"window.__marks[{name!r}]()")
        page.wait_for_load_state("networkidle")
        page.wait_for_timeout(700)
        if name in ("contact-ring", "effects"):
            page.evaluate(f"window.__marks[{name!r}]()")  # restart the animation right before the picture
            page.wait_for_timeout(330 if name == "contact-ring" else 40)
        webp(page.screenshot(clip={"x": W / 2 - 88, "y": H / 2 - 58, "width": 176, "height": 116}), f"mark-{name}.webp", 1.0, 80)
    browser.close()

target.mkdir(parents=True, exist_ok=True)
for f in out.glob("*.webp"):
    shutil.move(str(f), target / f.name)
shutil.rmtree(out, ignore_errors=True)

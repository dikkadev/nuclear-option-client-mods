# Guide pictures

The guide's pictures of the map (`src/guide-img/*.webp`) are screenshots of the real map page, so they always look exactly like the map. `capture.py` opens the map in the dev server, fills it with a fixed example battle and takes the pictures: the whole screen, close-ups of the top bar, flight strip, details panel and layers panel, the "Read a contact" fighter and one tile per entry in "Lines and marks".

- `feed.py`: a stand-in feed that says hello and describes a Heartland session, so the map shows Live. It sends no units.
- `scenario.js`: the example battle for the whole screen, put straight into the map's world through the dev build's `window.__nlf` test hook.
- `marks.js`: one small scene per "Lines and marks" tile, over open water.

Retake the pictures after a change to how the map looks:

```bash
pnpm dev                                            # in viewer/, port 5173 (or pass another --port)
uv run --script scripts/guide-pictures/feed.py 39555
uv run --script scripts/guide-pictures/capture.py http://127.0.0.1:5173 ws://127.0.0.1:39555
```

It needs Microsoft Edge (Playwright's `msedge` channel) and the map data from the public site, which the dev server proxies. The script prints where the screen picture's parts are (1100 x 640 CSS pixels); move the numbered callouts in `guide.html` if they changed.

The guide embeds the pictures in its script (`import.meta.glob` with `?inline`), because the site's firewall rule serves only pages, scripts, styles and map tiles.

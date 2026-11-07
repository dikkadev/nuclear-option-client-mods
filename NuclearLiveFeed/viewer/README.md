# NuclearLiveFeed viewer

A static web page that connects to the NuclearLiveFeed mod and draws the live feed over the Nuclear Option map. Vite and TypeScript, no framework. The map itself is drawn by the separate `nuclear-option-map` library; map data is loaded from `/map-data/` and never copied into this app.

## Develop

Requirements: the map library built in the sibling `nuclear-option-map` checkout (`dist/browser/index.js`, override with `NOM_LIBRARY`).

```bash
pnpm install
pnpm dev            # http://127.0.0.1:5173
```

The dev server proxies `/map-data/` and `/maps/` to the public site (`NOM_MAP_ORIGIN`, default `https://map.scramd.com`), which serves `manifest.json`, `variants.json`, the satellite, topographic and game views. Set `NOM_MAP_DATA` to a local folder (for example the library's extracted `maps/`) to work without the site; then only the game view exists. The feed comes from the game with the mod installed.

URL parameters: `?feed=ws://host:port` (also editable by clicking the status; remembered locally) and `?data=URL` for another map-data base. Without map data, the page draws the game grid on a blank map. The page reads feed protocol `PROTOCOL` (in `src/feed.ts`); when the mod's hello names another version, it stops and shows "Update mod" or "Reload page".

## Guide page

`guide.html` (`src/guide.ts`, `src/guide.css`) is the player guide at `/guide` (the dev server serves it there too), the only place that explains the map: a quick start, a numbered picture of the whole screen, what the map shows, an annotated contact and symbol legend with the full unit list, every line and mark, the screen parts, target picking, the layers panel, keys, connection states, mod settings, exact values ("Numbers at a glance") and problems. A page finder (contents and top; `/` focuses it) searches terms, explanations and the unit list. Every number in it comes from the viewer and mod code; when one changes, change the guide too. Its pictures of the map are screenshots of this page with an example battle; `scripts/guide-pictures/` retakes them. It is written for players: no feature status or development notes. Opening the site still goes straight to the map; the map links to the guide from the waiting box (same tab) and the toolbar (new tab, so a running map keeps its state). The guide shows the connection status through its own feed connection, which reads only the text messages, and draws its symbols with the map's own `symbols.ts`, so the legend matches the map. When a map feature changes, update the guide in the same change.

## Display

- Base map: Satellite (default), Topo or Game. Effects (Dim, Muted, Night) apply to the whole map, only land or only water; the land/water mask comes from the topography tiles' alpha channel.
- Units use NATO APP-6(D) symbols drawn by `milsymbol` (MIT). Roles come from the game catalog in `src/symbology/`, taken over from the retired nomap app; this copy is now the only one. Last-known enemy tracks use the anticipated (dashed) status; damaged and destroyed units show the standard condition bar. Moving units get the standard direction-of-movement arrow.
- Labels appear for hovered and selected units and player callsigns; the Labels option shows all. Nearby ground units and ships of one side group into a frame with a count (option Group). Structures are off by default.

Keys: F fit, Esc clear selection. Click a unit for details.

## Build

```bash
pnpm build          # typecheck, then dist/
```

The build bundles the map library.

## Hosting

Public at `https://map.scramd.com` (user decisions 2026-10-29 and 2026-11-01). One Cloudflare R2 bucket, `nuclear-live-map-data`, serves everything through its custom domain; no Pages project and no Workers run per request.

- Bucket keys: the pages (`index.html`, `guide` holding the built `guide.html`, so its address has no extension, `assets/*`), the map catalogs (`manifest.json`, `variants.json`) and the tiles under `tiles/builtin/`, `terrain/` and `candidate-v8/`, mirroring the old map server's `/maps/` paths. Satellite is Satellite V3 from the private backup bucket `nuclear-option-map-backups/satellite-v3/`; the public bucket has levels 0-9, and its `variants.json` lists only those.
- Zone rules on `scramd.com` (managed in Cloudflare, not in this repository): URL rewrites (`/` to `/index.html`; `/map-data/*` and `/maps/*` to the bucket root), a WAF rule that blocks everything except GET/HEAD for the pages (`/`, `/index.html`, `/guide`), `/assets/*.js`, `/assets/*.css` and known tile shapes, a rate limit of 1,000 requests per 10 s per address, cache rules (tiles and assets for a year, page and catalogs by their stored Cache-Control, query strings ignored) and Smart Tiered Cache. No cache rule covers `/guide` yet, so Cloudflare fetches it from the bucket on every request. A new page needs its path in the WAF rule.
- Cache-Control is stored on each object: `index.html`, `guide`, `manifest.json` and `variants.json` 5 minutes, everything else a year.

Deploy a page update with `pnpm run deploy:site` (build, upload, purge the cached pages). It needs `CLOUDFLARE_API_TOKEN` or a `cf auth login` session. Without bash, or with a token limited to the bucket, run `pnpm build` and then `uv run --script scripts/deploy.py` with `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` set; it uploads through R2's S3 API, which accepts such a token. The agent's token for this is the Proton Pass item "Cloudflare - map.scramd.com deploy" (Agent vault): R2 object write on the bucket, cache purge, WAF edit and zone read on scramd.com. Old hashed assets stay in the bucket; they are small and harmless. Changing tiles or catalogs is a separate data task: keep immutable tile paths unchanged or use a new prefix, and purge the cache after replacing a catalog.

The page must keep this one HTTPS origin, because Chrome's local network permission is granted per origin, and the mod's Allowed Origins includes it.

The page always shows that it is an unofficial project not affiliated with Shockfront Studios; the full game-content disclaimer opens from that notice.

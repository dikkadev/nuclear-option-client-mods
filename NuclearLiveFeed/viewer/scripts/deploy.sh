#!/usr/bin/env bash
# Uploads the built pages (dist/) into the R2 bucket that serves https://map.scramd.com and https://mission.scramd.com,
# then drops Cloudflare's cached copy of the pages (map, guide, Mission picture) so visitors get them within minutes.
# Auth: CLOUDFLARE_API_TOKEN (R2 write and cache purge for scramd.com), or a `cf auth login` session.
set -euo pipefail
cd "$(dirname "$0")/.."
BUCKET=nuclear-live-map-data
ZONE=scramd.com
SITE=https://map.scramd.com
MISSION_SITE=https://mission.scramd.com
API=https://api.cloudflare.com/client/v4

# All three pages, or none: the map links to the Mission picture.
for page in index.html guide.html mission.html; do
  [ -f "dist/$page" ] || { echo "dist/$page is missing: run pnpm build first." >&2; exit 1; }
done
if [ -z "${CLOUDFLARE_API_TOKEN:-}" ]; then
  cf auth whoami >/dev/null  # refreshes the cf session
  CLOUDFLARE_API_TOKEN=$(jq -r .oauth_token ~/.config/cloudflare/config/default.json)
fi
auth=(-H "Authorization: Bearer $CLOUDFLARE_API_TOKEN")
account=${CLOUDFLARE_ACCOUNT_ID:-$(curl -sf "${auth[@]}" "$API/accounts" | jq -r '.result[0].id')}
zone=$(curl -sf "${auth[@]}" "$API/zones?name=$ZONE" | jq -r '.result[0].id')

put() { # key file content-type cache-control
  curl -sf -X PUT "${auth[@]}" -H "Content-Type: $3" -H "Cache-Control: $4" --data-binary "@$2" \
    "$API/accounts/$account/r2/buckets/$BUCKET/objects/$1" >/dev/null
  echo "uploaded $1"
}

# Hashed asset names never change content, so they are cached for a year; upload them before the page that names them.
for f in dist/assets/*; do
  case "$f" in *.js) type=text/javascript ;; *.css) type=text/css ;; *) type=application/octet-stream ;; esac
  put "assets/${f##*/}" "$f" "$type" "public, max-age=31536000, immutable"
done
put index.html dist/index.html "text/html; charset=utf-8" "public, max-age=300"
# The guide's key has no extension, so its address is /guide without a rewrite rule.
put guide dist/guide.html "text/html; charset=utf-8" "public, max-age=300"
# The Mission picture page; a zone rule serves this key at mission.scramd.com/.
put mission dist/mission.html "text/html; charset=utf-8" "public, max-age=300"

curl -sf -X POST "${auth[@]}" -H 'Content-Type: application/json' \
  --data "{\"files\":[\"$SITE/\",\"$SITE/index.html\",\"$SITE/guide\",\"$MISSION_SITE/\"]}" "$API/zones/$zone/purge_cache" >/dev/null
echo "purged the cached pages; $SITE and $MISSION_SITE serve the new build"

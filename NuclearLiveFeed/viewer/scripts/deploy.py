# /// script
# requires-python = ">=3.11"
# dependencies = ["boto3"]
# ///
"""Uploads the built pages (dist/) into the R2 bucket that serves https://map.scramd.com and
https://mission.scramd.com, then purges the cached pages.

Same result as deploy.sh, for machines without bash or an account-wide token. Uploads go through R2's S3 API, so a
token limited to the bucket works: its S3 access key is the token ID and the secret is the SHA-256 of the token.
Environment: CLOUDFLARE_API_TOKEN (R2 object write on the bucket, cache purge and zone read on scramd.com) and
CLOUDFLARE_ACCOUNT_ID. Run `pnpm build` first.
"""

import hashlib
import json
import os
import sys
import urllib.request
from pathlib import Path

import boto3

BUCKET = "nuclear-live-map-data"
ZONE = "scramd.com"
SITE = "https://map.scramd.com"
MISSION_SITE = "https://mission.scramd.com"
API = "https://api.cloudflare.com/client/v4"
PAGE = "public, max-age=300"
ASSET = "public, max-age=31536000, immutable"
TYPES = {".js": "text/javascript", ".css": "text/css"}


def cf(path: str, token: str, body: dict | None = None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(API + path, data=data, method="POST" if body is not None else "GET")
    request.add_header("Authorization", f"Bearer {token}")
    request.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(request) as response:
        return json.load(response)


def main() -> None:
    dist = Path(__file__).resolve().parent.parent / "dist"
    # All three pages, or none: the map links to the Mission picture.
    if not all((dist / page).is_file() for page in ("index.html", "guide.html", "mission.html")):
        sys.exit("dist/ is missing a page: run pnpm build first.")
    token, account = os.environ["CLOUDFLARE_API_TOKEN"], os.environ["CLOUDFLARE_ACCOUNT_ID"]
    token_id = cf("/user/tokens/verify", token)["result"]["id"]
    s3 = boto3.client(
        "s3",
        endpoint_url=f"https://{account}.r2.cloudflarestorage.com",
        aws_access_key_id=token_id,
        aws_secret_access_key=hashlib.sha256(token.encode()).hexdigest(),
        region_name="auto",
    )

    def put(key: str, file: Path, content_type: str, cache: str) -> None:
        s3.put_object(Bucket=BUCKET, Key=key, Body=file.read_bytes(), ContentType=content_type, CacheControl=cache)
        print("uploaded", key)

    # Hashed asset names never change content; upload them before the pages that name them.
    for file in sorted((dist / "assets").iterdir()):
        put(f"assets/{file.name}", file, TYPES.get(file.suffix, "application/octet-stream"), ASSET)
    put("index.html", dist / "index.html", "text/html; charset=utf-8", PAGE)
    # The guide's key has no extension, so its address is /guide without a rewrite rule.
    put("guide", dist / "guide.html", "text/html; charset=utf-8", PAGE)
    # The Mission picture page; a zone rule serves this key at mission.scramd.com/.
    put("mission", dist / "mission.html", "text/html; charset=utf-8", PAGE)

    zone = cf(f"/zones?name={ZONE}", token)["result"][0]["id"]
    cf(f"/zones/{zone}/purge_cache", token, {"files": [f"{SITE}/", f"{SITE}/index.html", f"{SITE}/guide", f"{MISSION_SITE}/"]})
    print(f"purged the cached pages; {SITE} and {MISSION_SITE} serve the new build")


if __name__ == "__main__":
    main()

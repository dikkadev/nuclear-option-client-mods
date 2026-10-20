#!/usr/bin/env python3
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""Explicitly refresh the pinned third-party modpack lock from NOMNOM."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import tomllib
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
MANIFEST_URL = 'https://kopterbuzz.github.io/NOMNOM/manifest/manifest.json'
VERSION_URL = 'https://kopterbuzz.github.io/NOMNOM/manifest/version.json'
MANIFEST_LIMIT = 4 * 1024 * 1024
DOWNLOAD_LIMIT = 64 * 1024 * 1024


class HTTPSRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        if urllib.parse.urlsplit(new_url).scheme != 'https':
            raise ValueError('Download redirect outside HTTPS rejected')
        return super().redirect_request(request, response, code, message, headers, new_url)


def fetch(url: str) -> bytes:
    if urllib.parse.urlsplit(url).scheme != 'https':
        raise ValueError(f'HTTPS required: {url}')
    request = urllib.request.Request(url, headers={'User-Agent': 'NuclearOptionModpackRefresh/1'})
    limit = MANIFEST_LIMIT if url in (MANIFEST_URL, VERSION_URL) else DOWNLOAD_LIMIT
    opener = urllib.request.build_opener(HTTPSRedirectHandler())
    with opener.open(request, timeout=60) as response:
        if urllib.parse.urlsplit(response.url).scheme != 'https':
            raise ValueError('Download redirected outside HTTPS')
        declared = response.headers.get('Content-Length')
        if declared is not None and int(declared) > limit:
            raise ValueError(f'Download exceeds {limit} byte limit')
        data = response.read(limit + 1)
        if len(data) > limit:
            raise ValueError(f'Download exceeds {limit} byte limit')
        return data


def version_key(value: str) -> tuple[int, ...]:
    if not isinstance(value, str) or not re.fullmatch(r'\d+(?:\.\d+)*', value):
        raise ValueError(f'Expected numeric release version: {value!r}')
    parts = tuple(int(part) for part in value.split('.'))
    return parts + (0,) * max(0, 8 - len(parts))


def refresh(config_path: Path) -> dict:
    config = tomllib.loads(config_path.read_text(encoding='utf-8-sig'))
    configured = config.get('mods', {})
    if not configured:
        raise ValueError('No configured extras')
    version_before = json.loads(fetch(VERSION_URL))
    manifest = json.loads(fetch(MANIFEST_URL))
    if not isinstance(manifest, list):
        raise ValueError('Registry manifest must be an array')
    if json.loads(fetch(VERSION_URL)) != version_before:
        raise ValueError('Registry changed during refresh; retry')
    entries = {}
    for entry in manifest:
        identifier = entry['id']
        if identifier in entries:
            raise ValueError(f'Duplicate registry ID: {identifier}')
        entries[identifier] = entry
    mods = []
    for identifier, settings in configured.items():
        entry = entries.get(identifier)
        if entry is None:
            raise ValueError(f'Missing registry ID: {identifier}')
        origin = settings['origin']
        expected = f"https://github.com/{entry.get('githubOwner')}/{entry.get('githubRepoName')}"
        if origin != expected:
            raise ValueError(f'{identifier}: registry source origin changed ({expected})')
        layout = settings['layout']
        if layout not in ('zip', '7z', 'dll'):
            raise ValueError(f'{identifier}: unsupported layout {layout!r}')
        candidates = [a for a in entry.get('artifacts', [])
                      if str(a.get('category', '')).lower() == 'release'
                      and a.get('type') == 'plugin']
        if not candidates:
            raise ValueError(f'{identifier}: no release plugin artifact')
        artifact = dict(max(candidates, key=lambda a: version_key(a['version'])))
        url = artifact.get('downloadUrl')
        alternate = artifact.pop('downloadURL', None)
        if url and alternate and url != alternate:
            raise ValueError(f'{identifier}: conflicting download URL spellings')
        url = url or alternate
        if not url:
            raise ValueError(f'{identifier}: missing downloadUrl')
        artifact['downloadUrl'] = url
        parsed = urllib.parse.urlsplit(url)
        expected_path = urllib.parse.urlsplit(origin).path + '/releases/download/'
        if (parsed.scheme != 'https' or parsed.netloc != 'github.com'
                or not parsed.path.startswith(expected_path)
                or parsed.query or parsed.fragment
                or any(part in ('.', '..') for part in urllib.parse.unquote(parsed.path).split('/'))):
            raise ValueError(f'{identifier}: artifact URL must belong to its expected origin release path')
        if not str(artifact.get('fileName', '')).lower().endswith('.' + layout):
            raise ValueError(f'{identifier}: artifact does not match configured {layout} layout')
        registry_hash = artifact.get('hash')
        if not isinstance(registry_hash, str):
            raise ValueError(f'{identifier}: mandatory registry SHA-256 missing')
        digest = registry_hash.removeprefix('sha256:').lower()
        if not re.fullmatch(r'[0-9a-f]{64}', digest):
            raise ValueError(f'{identifier}: mandatory registry SHA-256 invalid')
        actual = hashlib.sha256(fetch(url)).hexdigest()
        if actual != digest:
            raise ValueError(f'{identifier}: downloaded SHA-256 mismatch')
        artifact['sha256'] = actual
        mods.append({'id': identifier, 'origin': origin, 'artifact': artifact,
                     'layout': layout, 'stripPrefix': settings.get('strip_prefix', '')})
    resolved = {mod['id']: mod['artifact'] for mod in mods}
    for mod in mods:
        dependencies = list(mod['artifact'].get('dependencies', []))
        if mod['artifact'].get('extends') is not None:
            dependencies.append(mod['artifact']['extends'])
        for dependency in dependencies:
            target = resolved.get(dependency['id'])
            if target is None:
                raise ValueError(f"{mod['id']}: dependency {dependency['id']} not configured")
            # Match the normal producer's conservative policy. A new release
            # with a different dependency pin needs review, not a silent upgrade.
            if dependency.get('version') is not None and target['version'] != dependency['version']:
                raise ValueError(f"{mod['id']}: dependency {dependency['id']} version does not match")
    return {'schema': 1, 'registry': {'manifestUrl': MANIFEST_URL,
            'versionUrl': VERSION_URL, 'version': version_before}, 'mods': mods}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, default=ROOT / 'tools/modpack-extras.toml')
    parser.add_argument('--output', type=Path, default=ROOT / 'tools/modpack-extras.lock.json')
    args = parser.parse_args()
    try:
        lock = refresh(args.config)
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile('w', encoding='utf-8', dir=output.parent,
                                             prefix=output.name + '.', suffix='.tmp', delete=False) as stream:
                temporary = Path(stream.name)
                stream.write(json.dumps(lock, indent=2) + '\n')
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, output)
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)
        print(f'Refreshed {len(lock["mods"])} extras from registry {lock["registry"]["version"]}: {output}')
    except (ValueError, KeyError, OSError, TypeError) as error:
        parser.exit(1, f'Refresh failed; existing lock preserved: {error}\n')


if __name__ == '__main__':
    main()

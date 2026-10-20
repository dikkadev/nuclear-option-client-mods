#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["questionary==2.1.0", "py7zr==1.1.3"]
# ///
"""Publish locally built Nuclear Option mods with a complete public source snapshot."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import release_engine as engine

HELP = """
ONE REPOSITORY, LOCAL BUILDS
  All active regular mod source is exported on EVERY update, even when only one
  mod is released. No private Git/jj history, internal notes, game assemblies or
  GitHub Actions are exported. Builds and checks run locally from the exact
  frozen public source. ZIPs and the optional modpack use the same built DLLs.

TWO WAYS TO USE THIS CLI
  No arguments on a terminal opens the interactive wizard. With arguments, all
  commands are noninteractive except publish's final confirmation. Use --yes
  there for an explicitly authorized agent/automation run. No-argument use on a
  pipe prints help and exits 2, rather than waiting for hidden prompts.

VERSIONS
  Each mod owns its .csproj Version; each pack owns a separate version in
  tools/release.toml. bump is the only command that edits those versions. plan
  never guesses a bump. Discovery compares exact per-mod GitHub tag prefixes and
  existing NOMNOM IDs, including legacy release repositories. Titles and the
  mixed repository's 'latest' release are never used. Numeric versions only;
  --prerelease sets GitHub's flag instead of adding an -rc suffix.
  Packs may reuse an unselected mod's published version only when its source
  fingerprint and built DLL still match that version. Changed code needs a bump.

EXAMPLES (run from the mod source repository)
  uv run --script tools/release.py
  uv run --script tools/release.py status
  uv run --script tools/release.py bump --mod NuclearUnits --to patch
  uv run --script tools/release.py bump --pack full --to 1.0.0
  uv run --script tools/release.py plan --mod NuclearUnits --pack full \\
    --game-version 0.34.2 --output dist/releases/next
  uv run --script tools/release.py prepare --plan dist/releases/next/plan.json
  uv run --script tools/release.py publish --plan dist/releases/next/plan.json
  uv run --script tools/release.py verify --plan dist/releases/next/plan.json

AGENT / OFFLINE EXAMPLES
  uv run --script tools/release.py status --json
  uv run --script tools/release.py plan --all --game-version 0.34.2 \\
    --offline --include-untracked --output dist/releases/local-preview --json
  uv run --script tools/release.py publish --plan dist/releases/next/plan.json \\
    --yes --expect-hash FULL_PLAN_SHA256

OUTPUTS AND RECOVERY
  plan.json and source/ are the frozen reviewable update. prepare adds artifacts/,
  manifests/ and a hash-checked receipt.json. These files are portable together.
  publish does not rebuild: it verifies those files, pushes one public-only source
  commit and release tags, uploads to drafts, checks names/sizes/SHA-256 and then
  publishes. Retry the SAME publish command after a network interruption; matching
  releases are verified, incomplete draft uploads retried, and mismatches refused.
  Only unusable GitHub 'starter' or 'open' draft assets are removed before retry. There is
  no force push, published-asset overwrite, or delete-and-recreate option.

REQUIREMENTS AND FIRST USE
  Python 3.11+ through uv; git; .NET 10; authorized client/BepInEx references in
  .env or the environment. gh is needed only for publish/remote verify. Sign gh
  in with write access, such as dikkadev or the invited clankagent collaborator.
  Existing repositories can be private or public; publication keeps visibility.
  The repository remains owned by dikkadev. A VM may prepare the entire folder
  and hand it to the signed-in PC. Edit tools/release.toml to choose the repo.
  On first publication use --create-repo explicitly; no earlier command creates
  it. Supply a chosen LICENSE (or plan --license-file FILE) before publishing.
  Review bundled audio and third-party extras' redistribution terms as well.
  Local checks do not establish game-runtime acceptance.

EXIT CODES
  0 success; 1 operation/verification failure; 2 invalid command or no terminal;
  130 cancelled. --json returns one result/error object on stdout; build logs and
  progress go to stderr. Saved plans can contain local revision information and
  are private; only source/ and selected release assets are uploaded.
"""


def parser():
    result = argparse.ArgumentParser(description=__doc__, epilog=HELP,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = result.add_subparsers(dest='command')
    status = commands.add_parser('status', help='Compare local, GitHub and registry versions (read-only)')
    status.add_argument('--repo', help='Public destination OWNER/NAME under dikkadev; default tools/release.toml')
    status.add_argument('--offline', action='store_true', help='Show local versions without any network access')
    bump = commands.add_parser('bump', help='Explicitly edit selected local project or pack versions; no build/upload')
    bump.add_argument('--mod', action='append', default=[], help='Active mod name, repeatable')
    bump.add_argument('--pack', choices=['our', 'full'], help='Edit this pack version instead of mod versions')
    bump.add_argument('--to', required=True, help='patch, minor, major, or an exact numeric version')
    plan = commands.add_parser('plan', help='Freeze ALL public source and the selected release choices; no build/upload')
    selection = plan.add_mutually_exclusive_group()
    selection.add_argument('--mod', action='append', default=[], help='Release this active mod, repeatable; other source is still exported')
    selection.add_argument('--all', action='store_true', help='Release all active regular mods')
    plan.add_argument('--pack', choices=['our', 'full', 'both'], help='Also release a separately versioned pack; may be used without --mod')
    plan.add_argument('--game-version', required=True, help='Actual supported client game version, for example 0.34.2; no stale default')
    plan.add_argument('--output', type=Path, required=True, help='New or empty release directory for plan.json and source/')
    plan.add_argument('--repo', help='Override tools/release.toml destination (OWNER/NAME under dikkadev)')
    plan.add_argument('--prerelease', action='store_true', help='Mark every selected GitHub release as a prerelease; versions stay numeric')
    plan.add_argument('--notes-file', type=Path, help='UTF-8 release notes shared by selected releases; default factual name/version/game summary')
    plan.add_argument('--license-file', type=Path, help='Copy the chosen license into the public snapshot as LICENSE')
    plan.add_argument('--include-untracked', action='store_true', help='Include new nonignored allowlisted source files; default tracked worktree files only')
    plan.add_argument('--offline', action='store_true', help='Make a local preview without network discovery; replan online before publishing')
    prepare = commands.add_parser('prepare', help='Build/check the frozen snapshot locally and create hash-verified release packages')
    prepare.add_argument('--plan', type=Path, required=True, help='Saved plan.json; requires source/ beside it')
    publish = commands.add_parser('publish', help='Push the prepared public source snapshot and publish verified release assets')
    publish.add_argument('--plan', type=Path, required=True, help='Prepared plan.json; requires source/, artifacts/ and receipt.json')
    publish.add_argument('--create-repo', action='store_true', help='Explicitly create the configured public repository if missing')
    publish.add_argument('--yes', action='store_true', help='Publish without a prompt; use only after approval of this exact prepared update')
    publish.add_argument('--expect-hash', help='Require this full plan SHA-256 before proceeding')
    publish.add_argument('--dry-run', action='store_true', help='Verify local files, auth and discovery without writing to GitHub')
    verify = commands.add_parser('verify', help='Check prepared hashes; optionally check published tags and remote release files')
    verify.add_argument('--plan', type=Path, required=True, help='Prepared plan.json')
    verify.add_argument('--remote', action='store_true', help='Also verify published.json, GitHub release metadata/assets and tag targets')
    for command in (status, bump, plan, prepare, publish, verify):
        command.add_argument('--json', action='store_true', help='Emit machine-readable JSON; send progress/build logs to stderr')
    return result


def render(value):
    if isinstance(value, dict) and 'items' in value and 'repository' in value:
        print('Repository:', value['repository'])
        if value.get('offline'):
            print('Offline: published versions have not been checked.')
        for item in value['items']:
            if 'local' in item:
                print(f"  {item['name']:28} local {item['local']:12} published {item.get('published') or '-':12}"
                      + (' (pack only)' if item.get('packPublished') and not item.get('individualPublished') else '')
                      + (f" suggested {item['suggested']}" if item.get('suggested') else ''))
            else:
                print(f"  {item['tag']}  ->  {item['asset']}")
        if value.get('planHash'):
            print('Plan SHA-256:', value['planHash'])
            print('Source files:', len(value.get('sourceFiles', {})))
        if value.get('workingTreeChanged'):
            print('Includes current tracked working-tree edits. Review source/ before publishing.')
        for name in value.get('modifiedSource', []):
            print('  Modified source:', name)
        for name in value.get('omittedUntrackedSource', []):
            print('  Omitted NEW source (use --include-untracked if needed):', name)
    else:
        print(json.dumps(value, indent=2, ensure_ascii=False))


def confirm_publish(path):
    value = engine.read_plan(path)
    engine.verify_prepared(path)
    render(value)
    print('This publishes one source snapshot and the listed bundles to GitHub.')
    return input('Publish this prepared update? [y/N] ').strip().lower() in ('y', 'yes')


def wizard():
    import questionary
    def ask(question):
        answer = question.ask()
        if answer is None:
            raise KeyboardInterrupt
        return answer
    def valid_version(value):
        try:
            engine.version_key(value)
            return True
        except ValueError:
            return 'Use a numeric version, such as 1.2.3'
    print('Nuclear Option local release publisher')
    state = engine.status()
    render(state)
    choices = [questionary.Choice(f"{i['name']} (local {i['local']}, published {i.get('published') or 'none'})", value=i['name'])
               for i in state['items'] if not i['name'].startswith('pack:')]
    selected = ask(questionary.checkbox('Which mods do you want to release? (Space selects, Enter continues)', choices=choices))
    pack = ask(questionary.select('Include a modpack?', choices=['None', 'full', 'our', 'both']))
    if not selected and pack == 'None':
        print('Nothing selected.')
        return 0
    changes = []
    for name in selected:
        item = next(i for i in state['items'] if i['name'] == name)
        target = ask(questionary.text(f'Version for {name}', default=item['suggested'],
                     validate=valid_version))
        if target != item['local']:
            changes.append((name, target, None))
    packs = [] if pack == 'None' else ['our', 'full'] if pack == 'both' else [pack]
    for name in packs:
        item = next(i for i in state['items'] if i['name'] == 'pack:' + name)
        suggestion = item['local']
        if item.get('published') and engine.version_key(suggestion) <= engine.version_key(item['published']):
            suggestion = engine.next_version(item['published'], 'patch')
        target = ask(questionary.text(f'Version for {name} pack', default=suggestion, validate=valid_version))
        engine.version_key(target)
        if target != item['local']:
            changes.append((name, target, name))
    if changes:
        print('Local version edits:', ', '.join(f'{n} -> {v}' for n, v, _ in changes))
        if not ask(questionary.confirm('Apply these version edits to the local files?', default=True)):
            return 0
        for name, value, pack_name in changes:
            engine.bump(engine.ROOT, [] if pack_name else [name], value, pack=pack_name)
    game = ask(questionary.text('Supported Nuclear Option client game version (for example 0.34.2)'))
    prerelease = ask(questionary.confirm('Mark these releases as prereleases?', default=True))
    notes = ask(questionary.text('Release notes (leave blank for a name/version summary)'))
    license_path = ''
    if not (engine.ROOT / 'LICENSE').exists():
        license_path = ask(questionary.path('Chosen license file (blank prepares a local preview only)'))
    untracked = ask(questionary.confirm('Include new, untracked source files within the public allowlist?', default=False))
    default = 'dist/releases/' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')
    output = Path(ask(questionary.text('Local output directory', default=default)))
    value = engine.plan(engine.ROOT, selected, packs, game_version=game, output=output,
                        prerelease=prerelease, notes=notes, include_untracked=untracked,
                        license_file=Path(license_path) if license_path else None)
    render(value)
    if ask(questionary.confirm('Build, check and package this frozen snapshot now?', default=True)):
        engine.prepare(output / 'plan.json')
        print('Prepared:', output.resolve())
        if not (output / 'source/LICENSE').is_file():
            print('Local preview prepared. Add a chosen LICENSE and make a new plan before publication.')
        elif ask(questionary.confirm('Review is complete: publish this update to GitHub now?', default=False)):
            create = ask(questionary.confirm('Allow creating the configured repository if it does not exist?', default=False))
            render(engine.publish(output / 'plan.json', create_repo=create, expect_hash=value['planHash']))
    return 0


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    cli = parser()
    if not argv:
        if not sys.stdin.isatty() or not sys.stdout.isatty():
            cli.print_help()
            return 2
        try:
            return wizard()
        except (ValueError, OSError, KeyError, TypeError, ET.ParseError, subprocess.CalledProcessError) as error:
            print('ERROR:', error, file=sys.stderr)
            return 1
    args = cli.parse_args(argv)
    if args.command is None:
        cli.print_help()
        return 2
    output = sys.stdout
    if args.json:
        sys.stdout = sys.stderr
    try:
        if args.command == 'status':
            value = engine.status(repo=args.repo, offline=args.offline)
        elif args.command == 'bump':
            value = engine.bump(engine.ROOT, args.mod, args.to, pack=args.pack)
        elif args.command == 'plan':
            mods = engine.load_builder().load_mods(engine.ROOT)
            names = [m['name'] for m in mods] if args.all else args.mod
            packs = ['our', 'full'] if args.pack == 'both' else [args.pack] if args.pack else []
            value = engine.plan(engine.ROOT, names, packs, game_version=args.game_version,
                                output=args.output, repo=args.repo, offline=args.offline,
                                prerelease=args.prerelease, include_untracked=args.include_untracked,
                                license_file=args.license_file,
                                notes=args.notes_file.read_text('utf-8') if args.notes_file else '')
        elif args.command == 'prepare':
            value = engine.prepare(args.plan)
        elif args.command == 'publish':
            if not args.yes and not args.dry_run:
                if not sys.stdin.isatty():
                    raise ValueError('Noninteractive publish requires --yes after review of the prepared update')
                if not confirm_publish(args.plan):
                    return 130
            value = engine.publish(args.plan, create_repo=args.create_repo,
                                    dry_run=args.dry_run, expect_hash=args.expect_hash)
        else:
            value = engine.verify_published(args.plan) if args.remote else engine.verify_prepared(args.plan)
        if args.json:
            output.write(json.dumps(value, ensure_ascii=False) + '\n')
        else:
            render(value)
        return 0
    except (ValueError, OSError, KeyError, TypeError, ET.ParseError, subprocess.CalledProcessError) as error:
        if args.json:
            output.write(json.dumps({'error': str(error), 'command': args.command}) + '\n')
        else:
            print('ERROR:', error, file=sys.stderr)
        return 1
    finally:
        sys.stdout = output


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (KeyboardInterrupt, EOFError):
        print('Cancelled.', file=sys.stderr)
        raise SystemExit(130)

# /// script
# requires-python = ">=3.11"
# ///
"""Checks that every element in the viewer's pages is closed in order, so a slip in an edit cannot push the rest of a
page out of its layout. Run from viewer/: uv run --script scripts/check-pages.py"""
import sys
from html.parser import HTMLParser
from pathlib import Path

VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"}
# SVG shapes are written self-closing (<path ... />); the parser reports those through handle_startendtag.


class Balance(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack: list[tuple[str, int]] = []
        self.errors: list[str] = []

    def handle_starttag(self, tag, attrs):
        if tag not in VOID:
            self.stack.append((tag, self.getpos()[0]))

    def handle_endtag(self, tag):
        if tag in VOID:
            return
        if not self.stack or self.stack[-1][0] != tag:
            open_tag = f"<{self.stack[-1][0]}> from line {self.stack[-1][1]}" if self.stack else "nothing"
            self.errors.append(f"line {self.getpos()[0]}: </{tag}> closes {open_tag}")
            # Recover: drop back to the matching opener if there is one.
            for i in range(len(self.stack) - 1, -1, -1):
                if self.stack[i][0] == tag:
                    del self.stack[i:]
                    break
            return
        self.stack.pop()


failed = False
for name in ["index.html", "guide.html", "mission.html"]:
    p = Balance()
    p.feed((Path(__file__).resolve().parents[1] / name).read_text(encoding="utf-8"))
    p.close()
    problems = p.errors + [f"<{t}> from line {line} is never closed" for t, line in p.stack]
    for e in problems:
        print(f"{name}: {e}")
    failed |= bool(problems)
print("pages ok" if not failed else "pages have unbalanced elements")
sys.exit(1 if failed else 0)

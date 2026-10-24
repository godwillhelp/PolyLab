"""Adds, replaces or removes interface strings in src/ParticleStudio/Pfx.App/Lang/{ru,en,uk}.xaml from one side-by-side table.

Table: UTF-8, tab-separated, one string per line: key, Russian, English, Ukrainian. A line "-key" removes the key.
A literal backslash-n in a text becomes a line break (&#10;). Keys stay in ordinal order, so the three files keep the
same keys in the same places (AppTests checks the parity).

    py -3 tools/lang/merge_lang.py <table.tsv> [--replace]
"""
import re
import sys
from pathlib import Path
from xml.sax.saxutils import escape

LANGS = ("ru", "en", "uk")
ENTRY = re.compile(r'  <s:String x:Key="([^"]+)">(.*?)</s:String>\n')


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    replace = "--replace" in sys.argv[2:]
    lang_dir = Path(__file__).resolve().parents[3] / "src" / "ParticleStudio" / "Pfx.App" / "Lang"
    rows = []
    for n, line in enumerate(Path(sys.argv[1]).read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        cells = line.split("\t")
        if cells[0].startswith("-"):
            rows.append((cells[0][1:], None))
            continue
        if len(cells) != 4 or not all(c.strip() for c in cells):
            sys.exit(f"line {n}: need key, ru, en, uk")
        rows.append((cells[0], cells[1:]))
    for i, lang in enumerate(LANGS):
        path = lang_dir / f"{lang}.xaml"
        text = path.read_text(encoding="utf-8")
        head = text[: text.index("  <s:String")]
        tail = text[text.rindex("</s:String>\n") + len("</s:String>\n"):]
        entries = dict(ENTRY.findall(text))
        for key, texts in rows:
            if texts is None:
                if entries.pop(key, None) is None:
                    sys.exit(f"{lang}: no key {key} to remove")
                continue
            if key in entries and not replace:
                sys.exit(f"{lang}: key {key} exists (use --replace)")
            entries[key] = escape(texts[i]).replace("\\n", "&#10;")
        body = "".join(f'  <s:String x:Key="{k}">{entries[k]}</s:String>\n' for k in sorted(entries))
        path.write_text(head + body + tail, encoding="utf-8", newline="\n")
        print(f"{lang}: {len(entries)} keys")


if __name__ == "__main__":
    main()

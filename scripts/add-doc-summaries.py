"""One-shot: prepend a YAML frontmatter with `summary:` to every
docs/**/*.md that doesn't already have one. The summary is the first
non-heading paragraph, compressed to a single sentence ~150 chars.

DocFX picks up the summary field and renders it in the search result
preview instead of extracting plain text from the rendered HTML (which
runs bullets together and can start mid-word).
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"

def first_paragraph(lines):
    """Return the first non-empty line that isn't a heading, html tag,
    image, or code fence. Compressed to a single line."""
    for line in lines:
        s = line.strip()
        if not s:
            continue
        if s.startswith("#"):
            continue
        if s.startswith("<") or s.startswith("!["):
            continue
        if s.startswith("```"):
            continue
        if s.startswith("- ") or s.startswith("* ") or s.startswith("> "):
            continue
        return s
    return None

def strip_markdown(text):
    """Remove inline markdown decorations so the summary reads as plain
    text. Keeps sentences intact."""
    text = re.sub(r"\[([^\]]+)\]\([^\)]+\)", r"\1", text)   # [link](url) -> link
    text = re.sub(r"`([^`]+)`", r"\1", text)                # `code`     -> code
    text = re.sub(r"\*\*([^*]+)\*\*", r"\1", text)          # **bold**   -> bold
    text = re.sub(r"\*([^*]+)\*", r"\1", text)              # *italic*   -> italic
    text = re.sub(r"&mdash;", "—", text)
    text = re.sub(r"&[a-z]+;", "", text)
    return text.strip()

def compress(text, max_len=180):
    """Cut at the first sentence end. Fall back to hard cut at max_len."""
    m = re.match(r"(.+?[.!?])(?:\s|$)", text)
    if m and len(m.group(1)) <= max_len:
        return m.group(1)
    return text[:max_len].rstrip()

def has_frontmatter(lines):
    return len(lines) >= 1 and lines[0].strip() == "---"

def process(md_path):
    text = md_path.read_text(encoding="utf-8")
    lines = text.splitlines()
    if has_frontmatter(lines):
        # Already has frontmatter. Check if it has `summary:`.
        end = next((i for i in range(1, len(lines)) if lines[i].strip() == "---"), -1)
        if end < 0:
            return "skipped (bad frontmatter)"
        if any(l.strip().startswith("summary:") for l in lines[1:end]):
            return "skipped (summary exists)"
        summary = compress(strip_markdown(first_paragraph(lines[end+1:]) or md_path.stem))
        lines.insert(end, f"summary: {summary!r}")
        md_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return f"updated frontmatter: {summary[:60]}"
    para = first_paragraph(lines)
    if not para:
        return "skipped (no paragraph)"
    summary = compress(strip_markdown(para))
    frontmatter = f"---\nsummary: {summary!r}\n---\n\n"
    md_path.write_text(frontmatter + text, encoding="utf-8")
    return f"added: {summary[:60]}"

for p in sorted(DOCS.rglob("*.md")):
    rel = p.relative_to(ROOT)
    print(f"{rel}: {process(p)}")

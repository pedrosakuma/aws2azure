"""MkDocs hooks for links from portal pages back to repository source."""

from __future__ import annotations

import re
import shutil
from collections.abc import Callable
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit, urlunsplit


_MARKDOWN_LINK = re.compile(
    r"(?P<prefix>!?\[[^\]]*\]\()"
    r"(?P<destination><?[^)\s>]+>?)"
    r"(?P<suffix>(?:\s+(?:\"[^\"]*\"|'[^']*'))?\))"
)
_FENCE = re.compile(r"^\s*(?P<marker>`{3,}|~{3,})(?P<remainder>.*)$")
_HTML_MARKDOWN_HREF = re.compile(
    r"(?P<prefix>\bhref=(?P<quote>[\"']))"
    r"(?P<destination>(?![a-z]+:|/|#)[^\"']+\.md(?:[?#][^\"']*)?)"
    r"(?P=quote)",
    re.IGNORECASE,
)
_DISCOVERY_ARTIFACTS = ("llms.txt", "documentation-manifest.json")


def _rewrite_outside_code(
    markdown: str, replace: Callable[[re.Match[str]], str]
) -> str:
    rewritten: list[str] = []
    fenced_marker: str | None = None
    inline_marker: str | None = None

    for line in markdown.splitlines(keepends=True):
        fence = _FENCE.match(line)
        if fenced_marker is not None:
            rewritten.append(line)
            if (
                fence is not None
                and fence.group("marker")[0] == fenced_marker[0]
                and len(fence.group("marker")) >= len(fenced_marker)
                and not fence.group("remainder").strip()
            ):
                fenced_marker = None
            continue

        if inline_marker is None and fence is not None:
            fenced_marker = fence.group("marker")
            rewritten.append(line)
            continue

        if inline_marker is None and (line.startswith("    ") or line.startswith("\t")):
            rewritten.append(line)
            continue

        code_ranges: list[tuple[int, int]] = []
        cursor = 0
        if inline_marker is not None:
            closing = line.find(inline_marker)
            if closing < 0:
                code_ranges.append((0, len(line)))
                rewritten.append(line)
                continue
            closing += len(inline_marker)
            code_ranges.append((0, closing))
            cursor = closing
            inline_marker = None

        while cursor < len(line):
            opening = line.find("`", cursor)
            if opening < 0:
                break

            end = opening + 1
            while end < len(line) and line[end] == "`":
                end += 1
            marker = line[opening:end]
            closing = line.find(marker, end)
            if closing < 0:
                code_ranges.append((opening, len(line)))
                inline_marker = marker
                break
            closing += len(marker)
            code_ranges.append((opening, closing))
            cursor = closing

        previous = 0
        for match in _MARKDOWN_LINK.finditer(line):
            rewritten.append(line[previous : match.start()])
            if any(start <= match.start() < end for start, end in code_ranges):
                rewritten.append(match.group(0))
            else:
                rewritten.append(replace(match))
            previous = match.end()
        rewritten.append(line[previous:])

    return "".join(rewritten)


def on_page_markdown(markdown: str, *, page, config, files) -> str:
    """Keep documentation links local and send source links to GitHub."""

    del files
    docs_root = Path(config["docs_dir"]).resolve()
    repo_root = Path(config["config_file_path"]).resolve().parent
    source_path = Path(page.file.abs_src_path).resolve()
    repository_url = str(config["repo_url"]).rstrip("/")
    repository_slug = repository_url.removeprefix("https://github.com/")

    def replace(match: re.Match[str]) -> str:
        destination = match.group("destination")
        wrapped = destination.startswith("<") and destination.endswith(">")
        target = destination[1:-1] if wrapped else destination
        parsed = urlsplit(target)

        if (
            not parsed.path
            or parsed.scheme
            or parsed.netloc
            or parsed.path.startswith("/")
        ):
            return match.group(0)

        resolved = (source_path.parent / unquote(parsed.path)).resolve()
        if not resolved.exists() or not resolved.is_relative_to(repo_root):
            return match.group(0)

        if resolved.is_relative_to(docs_root) and resolved.is_dir():
            landing_page = next(
                (
                    candidate
                    for candidate in (resolved / "index.md", resolved / "README.md")
                    if candidate.exists()
                ),
                None,
            )
            if landing_page is not None:
                local_path = f"{parsed.path.rstrip('/')}/{landing_page.name}"
                rewritten = urlunsplit(
                    ("", "", local_path, parsed.query, parsed.fragment)
                )
                if wrapped:
                    rewritten = f"<{rewritten}>"
                return (
                    f"{match.group('prefix')}{rewritten}{match.group('suffix')}"
                )

        line_fragment = re.fullmatch(r"L\d+(?:-L\d+)?", parsed.fragment)
        is_documentation_page = (
            resolved.is_relative_to(docs_root)
            and resolved.suffix.lower() == ".md"
            and line_fragment is None
        )
        if is_documentation_page:
            return match.group(0)

        repository_path = quote(resolved.relative_to(repo_root).as_posix())
        if match.group("prefix").startswith("!["):
            rewritten = urlunsplit(
                (
                    "https",
                    "raw.githubusercontent.com",
                    f"/{repository_slug}/main/{repository_path}",
                    parsed.query,
                    parsed.fragment,
                )
            )
        else:
            route = "tree" if resolved.is_dir() else "blob"
            rewritten = urlunsplit(
                (
                    "https",
                    "github.com",
                    f"/{repository_slug}/{route}/main/{repository_path}",
                    parsed.query,
                    parsed.fragment,
                )
            )
        if wrapped:
            rewritten = f"<{rewritten}>"

        return f"{match.group('prefix')}{rewritten}{match.group('suffix')}"

    return _rewrite_outside_code(markdown, replace)


def on_page_content(html: str, *, page, config, files) -> str:
    """Rewrite raw-HTML Markdown hrefs to the configured published URL shape."""

    del page, files
    use_directory_urls = bool(config["use_directory_urls"])

    def replace(match: re.Match[str]) -> str:
        destination = urlsplit(match.group("destination"))
        path = destination.path
        filename = Path(path).name.casefold()
        if use_directory_urls:
            if filename in ("index.md", "readme.md"):
                published_path = f"{Path(path).parent.as_posix().rstrip('/')}/"
                if published_path == "./":
                    published_path = ""
            else:
                published_path = f"{path[:-3]}/"
        else:
            published_path = f"{path[:-3]}.html"

        rewritten = urlunsplit(
            ("", "", published_path, destination.query, destination.fragment)
        )
        quote_character = match.group("quote")
        return f"{match.group('prefix')}{rewritten}{quote_character}"

    return _HTML_MARKDOWN_HREF.sub(replace, html)


def on_post_build(*, config) -> None:
    """Publish repository-root discovery artifacts at the documentation root."""

    repo_root = Path(config["config_file_path"]).resolve().parent
    site_root = Path(config["site_dir"]).resolve()
    for name in _DISCOVERY_ARTIFACTS:
        shutil.copyfile(repo_root / name, site_root / name)

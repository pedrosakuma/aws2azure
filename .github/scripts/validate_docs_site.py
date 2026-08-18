"""Validate built documentation links and representative search coverage."""

from __future__ import annotations

import json
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


class PageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.identifiers: set[str] = set()
        self.references: list[tuple[str, str]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        identifier = values.get("id") or values.get("name")
        if identifier:
            self.identifiers.add(identifier)

        for attribute in ("href", "src"):
            value = values.get(attribute)
            if value:
                self.references.append((attribute, value))


class PersonaLinkParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.container_depth = 0
        self.references: set[str] = set()

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        classes = (values.get("class") or "").split()
        if self.container_depth:
            self.container_depth += 1
        elif "portal-path__links" in classes:
            self.container_depth = 1

        if self.container_depth and tag == "a" and values.get("href"):
            self.references.add(str(values["href"]))

    def handle_endtag(self, tag: str) -> None:
        del tag
        if self.container_depth:
            self.container_depth -= 1


def parse_pages(site_dir: Path) -> dict[Path, PageParser]:
    pages: dict[Path, PageParser] = {}
    for html_file in site_dir.rglob("*.html"):
        parser = PageParser()
        parser.feed(html_file.read_text(encoding="utf-8"))
        pages[html_file.resolve()] = parser
    return pages


def resolve_target(
    site_dir: Path, source: Path, reference: str, base_path: str
) -> tuple[Path, str]:
    parsed = urlsplit(reference)
    if parsed.path.startswith("/"):
        relative_path = parsed.path
        if base_path and relative_path.startswith(base_path):
            relative_path = relative_path[len(base_path) :]
        path = site_dir / unquote(relative_path.lstrip("/"))
    else:
        path = source.parent / unquote(parsed.path)

    if not parsed.path:
        path = source
    elif path.is_dir() or not path.suffix:
        path = path / "index.html"

    return path.resolve(), unquote(parsed.fragment)


def validate_internal_links(
    site_dir: Path, pages: dict[Path, PageParser], base_path: str
) -> list[str]:
    errors: list[str] = []
    for source, parser in pages.items():
        for attribute, reference in parser.references:
            parsed = urlsplit(reference)
            if parsed.scheme or parsed.netloc or reference.startswith("//"):
                continue

            target, fragment = resolve_target(site_dir, source, reference, base_path)
            if not target.is_relative_to(site_dir):
                errors.append(f"{source}: {attribute} escapes the site: {reference}")
                continue
            if not target.exists():
                errors.append(f"{source}: {attribute} target is missing: {reference}")
                continue
            if fragment and target.suffix.lower() == ".html":
                target_page = pages.get(target)
                if target_page is None or fragment not in target_page.identifiers:
                    errors.append(f"{source}: fragment is missing: {reference}")
    return errors


def validate_search(site_dir: Path) -> list[str]:
    search_index_path = site_dir / "search" / "search_index.json"
    if not search_index_path.exists():
        return [f"Search index is missing: {search_index_path}"]

    index = json.loads(search_index_path.read_text(encoding="utf-8"))
    documents = index.get("docs", [])
    witnesses = {
        "service operation": ("CreateBucket", "site/s3/"),
        "configuration concept": ("azureIdentities", "getting-started/"),
        "workload verdict": ("conditional", "site/workload-compatibility/"),
        "production procedure": ("rollback", "deployment/production-runbook/"),
    }

    errors: list[str] = []
    for label, (term, location_prefix) in witnesses.items():
        found = any(
            str(document.get("location", "")).startswith(location_prefix)
            and term.casefold()
            in f"{document.get('title', '')} {document.get('text', '')}".casefold()
            for document in documents
        )
        if not found:
            errors.append(
                f"Search index lacks {label} witness {term!r} under {location_prefix!r}"
            )
    return errors


def validate_persona_links(site_dir: Path, base_path: str) -> list[str]:
    index_path = site_dir / "index.html"
    parser = PersonaLinkParser()
    parser.feed(index_path.read_text(encoding="utf-8"))

    actual: set[str] = set()
    for reference in parser.references:
        path = unquote(urlsplit(reference).path)
        if base_path != "/" and path.startswith(base_path):
            path = path[len(base_path) :]
        actual.add(path.lstrip("./"))

    expected = {
        "project-maturity/",
        "site/workload-compatibility/",
        "site/coverage/",
        "getting-started/",
        "azure-authentication/",
        "workloads/",
        "deployment/sidecar/",
        "deployment/production-runbook/",
        "versioning-and-compatibility/",
    }
    if actual == expected:
        return []

    return [
        "Built persona links differ from expected directory URLs: "
        f"expected={sorted(expected)!r}, actual={sorted(actual)!r}"
    ]


def main() -> int:
    if len(sys.argv) not in (2, 3):
        print(
            "Usage: validate_docs_site.py <site-directory> [public-base-path]",
            file=sys.stderr,
        )
        return 2

    site_dir = Path(sys.argv[1]).resolve()
    base_path = sys.argv[2] if len(sys.argv) == 3 else "/"
    if not base_path.startswith("/") or not base_path.endswith("/"):
        print("Public base path must start and end with '/'.", file=sys.stderr)
        return 2

    pages = parse_pages(site_dir)
    if not pages:
        print(f"No HTML pages found under {site_dir}", file=sys.stderr)
        return 1

    errors = validate_internal_links(site_dir, pages, base_path)
    errors.extend(validate_search(site_dir))
    errors.extend(validate_persona_links(site_dir, base_path))
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(f"Validated {len(pages)} pages and representative search coverage.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

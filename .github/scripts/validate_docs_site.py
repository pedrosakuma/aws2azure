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
        self.ids: set[str] = set()
        self.duplicate_identifiers: set[str] = set()
        self.legacy_fragments: set[str] = set()
        self.references: list[tuple[str, str]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        html_id = values.get("id")
        if html_id:
            if html_id in self.ids:
                self.duplicate_identifiers.add(html_id)
            self.ids.add(html_id)
        identifier = html_id or values.get("name")
        if identifier:
            self.identifiers.add(identifier)
            if "data-legacy-fragment" in values:
                self.legacy_fragments.add(identifier)

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


def validate_unique_identifiers(pages: dict[Path, PageParser]) -> list[str]:
    return [
        f"{path}: duplicate identifier: {identifier}"
        for path, parser in pages.items()
        for identifier in sorted(parser.duplicate_identifiers)
    ]


def validate_search(site_dir: Path) -> list[str]:
    search_index_path = site_dir / "search" / "search_index.json"
    if not search_index_path.exists():
        return [f"Search index is missing: {search_index_path}"]

    index = json.loads(search_index_path.read_text(encoding="utf-8"))
    documents = index.get("docs", [])
    witnesses = {
        "service identity": ("service:s3", "site/s3/"),
        "operation identity": ("operation:s3:putobject", "site/operations/s3/putobject/"),
        "sub-feature identity": (
            "sub-feature:s3:putobject:user-metadata--x-amz-meta",
            "site/operations/s3/putobject/",
        ),
        "design-gap identity": (
            "design-gap:s3:no-iam---acl---bucket-policy-authorization-model",
            "site/design-gaps/s3/no-iam---acl---bucket-policy-authorization-model/",
        ),
        "configuration concept": ("azureIdentities", "getting-started/"),
        "operator schema": ("config.schema.json", "configuration-schema/"),
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


def validate_capability_pages(
    site_dir: Path, pages: dict[Path, PageParser]
) -> list[str]:
    witnesses = {
        "service": (
            site_dir / "site" / "s3" / "index.html",
            {
                "service-s3",
                "putobject",
                "sub-features",
                "sub-features_1",
                "behaviour-differences",
                "references",
            },
        ),
        "operation": (
            site_dir / "site" / "operations" / "s3" / "putobject" / "index.html",
            {
                "operation-s3-putobject",
                "sub-feature-user-metadata--x-amz-meta",
            },
        ),
        "design-gap index": (
            site_dir / "site" / "design-gaps" / "index.html",
            {
                "s3-no-iam---acl---bucket-policy-authorization-model",
                "transaction-scope-is-single-partition-single-table",
                "no-aws-region-account-namespace_1",
            },
        ),
        "design gap": (
            site_dir
            / "site"
            / "design-gaps"
            / "s3"
            / "no-iam---acl---bucket-policy-authorization-model"
            / "index.html",
            {"design-gap-s3-no-iam---acl---bucket-policy-authorization-model"},
        ),
    }
    errors: list[str] = []
    for label, (path, identifiers) in witnesses.items():
        page = pages.get(path.resolve())
        if page is None:
            errors.append(f"Generated {label} page is missing: {path}")
            continue
        for identifier in identifiers:
            if identifier not in page.identifiers:
                errors.append(
                    f"Generated {label} page lacks stable identifier "
                    f"{identifier!r}: {path}"
                )
    return errors


def validate_legacy_design_gap_fragments(
    site_dir: Path, pages: dict[Path, PageParser]
) -> list[str]:
    page_path = (site_dir / "site" / "design-gaps" / "index.html").resolve()
    page = pages.get(page_path)
    if page is None:
        return [f"Generated design-gap index is missing: {page_path}"]

    source_path = Path("docs/site/design-gaps.md")
    if not source_path.exists():
        return [f"Generated design-gap Markdown is missing: {source_path.resolve()}"]

    marker = '" data-legacy-fragment="true"></a>'
    expected: set[str] = set()
    for line in source_path.read_text(encoding="utf-8").splitlines():
        marker_at = line.find(marker)
        if marker_at < 0:
            continue
        id_at = line.rfind('<a id="', 0, marker_at)
        if id_at >= 0:
            expected.add(line[id_at + len('<a id="') : marker_at])

    errors: list[str] = []
    if page.legacy_fragments != expected:
        errors.append(
            "Built design-gap legacy fragments differ from generated Markdown: "
            f"missing={sorted(expected - page.legacy_fragments)!r}, "
            f"unexpected={sorted(page.legacy_fragments - expected)!r}"
        )

    duplicate_heading_witnesses = {
        "no-aws-region-account-namespace",
        "no-aws-region-account-namespace_1",
    }
    missing_witnesses = duplicate_heading_witnesses - page.identifiers
    if missing_witnesses:
        errors.append(
            "Built design-gap index lacks deterministic duplicate-heading fragments: "
            f"{sorted(missing_witnesses)!r}"
        )
    return errors


def validate_legacy_service_fragments(
    site_dir: Path, pages: dict[Path, PageParser]
) -> list[str]:
    errors: list[str] = []
    for service in ("dynamodb", "kinesis", "s3", "secretsmanager", "sns", "sqs"):
        page_path = (site_dir / "site" / service / "index.html").resolve()
        page = pages.get(page_path)
        if page is None:
            errors.append(f"Generated service index is missing: {page_path}")
            continue

        source_path = Path("docs/site") / f"{service}.md"
        marker = '" data-legacy-fragment="true"></a>'
        expected: set[str] = set()
        for line in source_path.read_text(encoding="utf-8").splitlines():
            start = 0
            while (marker_at := line.find(marker, start)) >= 0:
                id_at = line.rfind('<a id="', start, marker_at)
                if id_at >= 0:
                    expected.add(line[id_at + len('<a id="') : marker_at])
                start = marker_at + len(marker)

        if page.legacy_fragments != expected:
            errors.append(
                f"Built {service} legacy fragments differ from generated Markdown: "
                f"missing={sorted(expected - page.legacy_fragments)!r}, "
                f"unexpected={sorted(page.legacy_fragments - expected)!r}"
            )
    return errors


def validate_operator_schema_link(
    site_dir: Path, pages: dict[Path, PageParser]
) -> list[str]:
    page_path = (site_dir / "configuration-schema" / "index.html").resolve()
    page = pages.get(page_path)
    if page is None:
        return [f"Operator configuration page is missing: {page_path}"]

    expected = (
        "https://github.com/pedrosakuma/aws2azure/"
        "blob/main/config.schema.json"
    )
    if any(reference == expected for _, reference in page.references):
        return []

    return [
        "Operator configuration page lacks canonical schema link "
        f"{expected!r}"
    ]


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
    errors.extend(validate_unique_identifiers(pages))
    errors.extend(validate_search(site_dir))
    errors.extend(validate_capability_pages(site_dir, pages))
    errors.extend(validate_legacy_design_gap_fragments(site_dir, pages))
    errors.extend(validate_legacy_service_fragments(site_dir, pages))
    errors.extend(validate_operator_schema_link(site_dir, pages))
    errors.extend(validate_persona_links(site_dir, base_path))
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(f"Validated {len(pages)} pages and representative search coverage.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

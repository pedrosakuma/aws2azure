from __future__ import annotations

import unittest
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlsplit


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


class DocumentationSourceTests(unittest.TestCase):
    def test_operator_configuration_links_to_root_schema(self) -> None:
        repo_root = Path(__file__).resolve().parents[2]
        page_path = repo_root / "docs" / "configuration-schema.md"
        target = "../config.schema.json"

        self.assertIn(f"]({target})", page_path.read_text(encoding="utf-8"))
        self.assertEqual(
            repo_root / "config.schema.json",
            (page_path.parent / target).resolve(),
        )
        self.assertTrue((page_path.parent / target).is_file())

    def test_persona_links_are_github_browsable_markdown_targets(self) -> None:
        repo_root = Path(__file__).resolve().parents[2]
        index_path = repo_root / "docs" / "index.md"
        parser = PersonaLinkParser()
        parser.feed(index_path.read_text(encoding="utf-8"))

        expected = {
            "project-maturity.md",
            "adoption.md",
            "site/workload-compatibility.md",
            "site/coverage.md",
            "getting-started.md",
            "configuration-reference.md",
            "configuration-environment.md",
            "configuration-examples.md",
            "azure-authentication.md",
            "workloads/README.md",
            "deployment/sidecar.md",
            "deployment/production-runbook.md",
            "troubleshooting.md",
            "versioning-and-compatibility.md",
        }
        self.assertEqual(expected, parser.references)

        for reference in parser.references:
            parsed = urlsplit(reference)
            self.assertFalse(parsed.scheme or parsed.netloc)
            self.assertEqual(".md", Path(parsed.path).suffix)
            self.assertTrue(
                (index_path.parent / parsed.path).is_file(),
                f"Persona source link does not resolve: {reference}",
            )


if __name__ == "__main__":
    unittest.main()

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

from mkdocs_hooks import on_page_content, on_page_markdown


class MkDocsHooksTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_directory = tempfile.TemporaryDirectory()
        self.repo_root = Path(self.temp_directory.name)
        self.docs_root = self.repo_root / "docs"
        self.docs_root.mkdir()
        self.source = self.docs_root / "guide.md"
        self.source.write_text("# Guide\n", encoding="utf-8")
        (self.docs_root / "reference.md").write_text("# Reference\n", encoding="utf-8")
        (self.repo_root / "source.json").write_text("{}\n", encoding="utf-8")
        (self.repo_root / "diagram.png").write_bytes(b"not-a-real-png")

        self.page = SimpleNamespace(
            file=SimpleNamespace(abs_src_path=str(self.source))
        )
        self.config = {
            "docs_dir": str(self.docs_root),
            "config_file_path": str(self.repo_root / "mkdocs.yml"),
            "repo_url": "https://github.com/example/project",
        }

    def tearDown(self) -> None:
        self.temp_directory.cleanup()

    def rewrite(self, markdown: str) -> str:
        return on_page_markdown(
            markdown,
            page=self.page,
            config=self.config,
            files=None,
        )

    def test_rewrites_repository_files_but_keeps_documentation_local(self) -> None:
        markdown = (
            "[`source`](../source.json)\n"
            "[reference](reference.md)\n"
            "![diagram](../diagram.png)\n"
        )

        rewritten = self.rewrite(markdown)

        self.assertIn(
            "https://github.com/example/project/blob/main/source.json", rewritten
        )
        self.assertIn("[reference](reference.md)", rewritten)
        self.assertIn(
            "https://raw.githubusercontent.com/example/project/main/diagram.png",
            rewritten,
        )

    def test_does_not_rewrite_inline_or_fenced_code(self) -> None:
        markdown = (
            "`[inline](../source.json)` and [source](../source.json)\n"
            "```markdown\n"
            "[fenced](../source.json)\n"
            "```\n"
        )

        rewritten = self.rewrite(markdown)

        self.assertIn("`[inline](../source.json)`", rewritten)
        self.assertIn("[fenced](../source.json)", rewritten)
        self.assertEqual(1, rewritten.count("/blob/main/source.json"))

    def test_rewrites_raw_html_markdown_links_for_published_site(self) -> None:
        html = (
            '<a href="project-maturity.md">Maturity</a>'
            '<a href="workloads/README.md#profiles">Profiles</a>'
            '<a href="https://example.com/guide.md">External</a>'
        )
        config = {"use_directory_urls": True}

        rewritten = on_page_content(
            html,
            page=None,
            config=config,
            files=None,
        )

        self.assertIn('href="project-maturity/"', rewritten)
        self.assertIn('href="workloads/#profiles"', rewritten)
        self.assertIn('href="https://example.com/guide.md"', rewritten)


if __name__ == "__main__":
    unittest.main()

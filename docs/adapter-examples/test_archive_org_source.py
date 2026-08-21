#!/usr/bin/env python3
"""Tests for the Archive-specific half of the adapter.

The shared machinery is covered by test_adapter_base.py; these cover only what
knows about the Archive: how a page is read, which file is the game, how a
record is assembled, and the three guards that stop a crawl.

    python -m unittest discover -s docs/adapter-examples -p 'test_*.py' -v

Nothing here touches the network. The HTTP client is replaced by a fake that
serves queued pages, which is the only way to test the pagination guards --
a live endpoint will not re-serve one page on demand.
"""

from __future__ import annotations

import io
import json
import unittest
from typing import Any, Dict, List, Optional

from _adapter_base import AdapterError, HttpClient
from archive_org_source import ArchiveOrgCatalogSource, read_first_page


def page(identifiers: List[str], found: int, *, sizes: Optional[Dict[str, int]] = None) -> Dict[str, Any]:
    """Build one search response.

    Args:
        identifiers: Identifiers the page should carry.
        found: The ``numFound`` the server reports.
        sizes: Optional per-identifier item sizes.

    Returns:
        A response in the search endpoint's shape.
    """
    return {
        "response": {
            "numFound": found,
            "docs": [
                {
                    "identifier": identifier,
                    "title": identifier.replace("-", " ").title(),
                    "item_size": (sizes or {}).get(identifier, 1000),
                    "publicdate": "2026-01-17T16:44:40Z",
                }
                for identifier in identifiers
            ],
        },
    }


class FakeClient(HttpClient):
    """Serves queued documents instead of making requests."""

    def __init__(self, pages: List[Any]) -> None:
        super().__init__(delay=0, sleeper=lambda _: None)
        self.pages = list(pages)
        self.calls: List[str] = []

    def get_json(self, url: str) -> Any:  # type: ignore[override]
        self.calls.append(url)

        if not self.pages:
            raise AssertionError(f"unexpected request to {url}")

        answer = self.pages.pop(0)

        if isinstance(answer, Exception):
            raise answer

        return answer


def source(pages: List[Any], **kwargs: Any) -> ArchiveOrgCatalogSource:
    """Build a source over a fake client."""
    return ArchiveOrgCatalogSource(
        uploader=kwargs.pop("uploader", "someone@example.test"),
        client=FakeClient(pages),
        **kwargs,
    )


class ConstructionTests(unittest.TestCase):
    """What a source will and will not be built with."""

    def test_it_needs_something_to_read(self) -> None:
        with self.assertRaises(ValueError):
            ArchiveOrgCatalogSource(client=FakeClient([]))

    def test_an_uploader_alone_is_enough(self) -> None:
        with source([]) as built:
            self.assertIn('uploader:"someone@example.test"', built.query)

    def test_a_collection_alone_is_enough(self) -> None:
        built = ArchiveOrgCatalogSource(collections=["softwarelibrary_msdos_games"], client=FakeClient([]))

        self.assertIn('collection:"softwarelibrary_msdos_games"', built.query)
        built.close()

    def test_both_are_combined_with_or(self) -> None:
        built = ArchiveOrgCatalogSource(
            uploader="a@b.test", collections=["c1", "c2"], client=FakeClient([]),
        )

        self.assertIn(" OR ", built.query)
        self.assertIn("mediatype:software", built.query)
        built.close()

    def test_deep_workers_must_be_positive(self) -> None:
        with self.assertRaises(ValueError):
            ArchiveOrgCatalogSource(uploader="a@b.test", deep_workers=0, client=FakeClient([]))


class QueryTests(unittest.TestCase):
    """The fielded query and its escaping."""

    def test_a_search_term_narrows_the_query(self) -> None:
        with source([]) as built:
            self.assertIn("title:(prince of persia)", built._search_query("prince of persia"))

    def test_a_search_term_cannot_restructure_the_query(self) -> None:
        # The index speaks a Lucene-like syntax, so a quotation mark would close
        # the term and open another, widening a deliberately narrow query.
        with source([]) as built:
            produced = built._search_query('doom") OR collection:("anything')

            self.assertEqual(produced.count("collection:"), 0)
            self.assertNotIn('"', produced.split("title:(")[1])

    def test_an_empty_term_leaves_the_query_alone(self) -> None:
        with source([]) as built:
            self.assertEqual(built._search_query(""), built.query)
            self.assertEqual(built._search_query("!!!"), built.query)

    def test_the_url_carries_repeated_field_parameters(self) -> None:
        with source([]) as built:
            url = built._search_url("q", 2)

            # Repeated fl[] is the form this endpoint documents.
            self.assertEqual(url.count("fl%5B%5D="), 5)
            self.assertIn("page=2", url)
            self.assertIn("rows=100", url)
            self.assertIn("output=json", url)

            # Spaces as %20, never '+'.
            self.assertNotIn("+", url)

    def test_a_page_number_below_one_is_clamped(self) -> None:
        with source([]) as built:
            self.assertIn("page=1", built._search_url("q", 0))
            self.assertIn("page=1", built._search_url("q", -5))


class ReadPageTests(unittest.TestCase):
    """Reading either response shape."""

    def test_the_search_shape(self) -> None:
        documents, found = ArchiveOrgCatalogSource._read_page(page(["a", "b"], 2))

        self.assertEqual(len(documents), 2)
        self.assertEqual(found, 2)

    def test_the_cursor_shape_is_still_accepted(self) -> None:
        documents, found = ArchiveOrgCatalogSource._read_page(
            {"items": [{"identifier": "a"}], "total": 7},
        )

        self.assertEqual(len(documents), 1)
        self.assertEqual(found, 7)

    def test_rubbish_yields_nothing_rather_than_raising(self) -> None:
        for value in (None, [], "text", 42, {}, {"response": "no"}):
            documents, found = ArchiveOrgCatalogSource._read_page(value)

            self.assertEqual(documents, [])
            self.assertIsNone(found)

    def test_a_negative_total_is_ignored(self) -> None:
        _, found = ArchiveOrgCatalogSource._read_page({"items": [], "total": -1})

        self.assertIsNone(found)


class PaginationTests(unittest.TestCase):
    """The three guards that stop a crawl."""

    def test_pages_are_walked_to_the_reported_total(self) -> None:
        with source([
            page(["a", "b"], 4),
            page(["c", "d"], 4),
        ]) as built:
            records = list(built.crawl_library(deep=False))

        self.assertEqual([record["source_id"] for record in records], ["a", "b", "c", "d"])

    def test_the_total_stops_the_walk_even_if_pages_keep_coming(self) -> None:
        # numFound is honoured, so a server that would happily keep paging does
        # not get to decide how long the crawl runs.
        client_pages = [page(["a", "b"], 2), page(["c", "d"], 2)]

        with source(client_pages) as built:
            records = list(built.crawl_library(deep=False))

        self.assertEqual(len(records), 2)

    def test_a_repeated_page_stops_the_walk(self) -> None:
        # The cursor endpoint's failure mode: the same page for ever. Without
        # this guard a 327-item library produced 27,327 records.
        repeated = page(["a", "b"], 999)

        with source([repeated, repeated, repeated]) as built:
            records = list(built.crawl_library(deep=False))

        self.assertEqual([record["source_id"] for record in records], ["a", "b"])

    def test_an_empty_page_stops_the_walk(self) -> None:
        with source([page(["a"], 999), page([], 999)]) as built:
            records = list(built.crawl_library(deep=False))

        self.assertEqual(len(records), 1)

    def test_partial_overlap_keeps_only_what_is_new(self) -> None:
        with source([page(["a", "b"], 3), page(["b", "c"], 3)]) as built:
            records = list(built.crawl_library(deep=False))

        self.assertEqual([record["source_id"] for record in records], ["a", "b", "c"])

    def test_max_pages_caps_the_walk(self) -> None:
        with source([page(["a"], 99), page(["b"], 99), page(["c"], 99)]) as built:
            records = list(built.crawl_library(max_pages=2, deep=False))

        self.assertEqual(len(records), 2)

    def test_a_supplied_first_page_is_not_refetched(self) -> None:
        client = FakeClient([page(["b"], 2)])
        built = ArchiveOrgCatalogSource(uploader="a@b.test", client=client)

        records = list(built.crawl_library(deep=False, first_page=page(["a"], 2)))
        built.close()

        self.assertEqual([record["source_id"] for record in records], ["a", "b"])

        # One request, for page two only.
        self.assertEqual(len(client.calls), 1)

    def test_a_document_without_an_identifier_is_skipped(self) -> None:
        # numFound counts what the server matched, and only one of these is
        # usable, so the walk finishes on this page rather than asking for
        # another it does not need.
        broken = {"response": {"numFound": 1, "docs": [{"title": "no id"}, {"identifier": "a"}]}}

        with source([broken]) as built:
            records = list(built.crawl_library(deep=False))

        self.assertEqual([record["source_id"] for record in records], ["a"])

    def test_a_failing_first_page_is_reported(self) -> None:
        with source([AdapterError("unreachable")]) as built:
            with self.assertRaises(AdapterError):
                list(built.crawl_library(deep=False))


class SearchTests(unittest.TestCase):
    """Filtering on top of the endpoint's own ranking."""

    def test_strict_matching_drops_loose_hits(self) -> None:
        payload = page(["need-for-speed-the-run", "quake"], 2)

        with source([payload]) as built:
            records = built.search("need for speed", deep=False)

        self.assertEqual([record["source_id"] for record in records], ["need-for-speed-the-run"])

    def test_loose_matching_keeps_what_the_endpoint_returned(self) -> None:
        payload = page(["need-for-speed-the-run", "quake"], 2)

        with source([payload]) as built:
            records = built.search("need for speed", strict_title_match=False, deep=False)

        self.assertEqual(len(records), 2)

    def test_the_limit_is_respected(self) -> None:
        with source([page(["doom-one", "doom-two", "doom-three"], 3)]) as built:
            records = built.search("doom", limit=2, deep=False)

        self.assertEqual(len(records), 2)


class PickFileTests(unittest.TestCase):
    """Choosing the file that is the game."""

    def test_the_largest_original_game_archive_wins(self) -> None:
        chosen = ArchiveOrgCatalogSource._pick_file([
            {"name": "patch.zip", "source": "original", "size": "1000"},
            {"name": "game.zip", "source": "original", "size": "999999"},
        ])

        self.assertEqual(chosen["name"], "game.zip")

    def test_derivatives_are_ignored(self) -> None:
        chosen = ArchiveOrgCatalogSource._pick_file([
            {"name": "huge.zip", "source": "derivative", "size": "999999999"},
            {"name": "game.zip", "source": "original", "size": "10"},
        ])

        self.assertEqual(chosen["name"], "game.zip")

    def test_non_game_extensions_are_ignored(self) -> None:
        for name in ("meta.xml", "cover.jpg", "notes.txt", "files.sqlite"):
            self.assertIsNone(
                ArchiveOrgCatalogSource._pick_file(
                    [{"name": name, "source": "original", "size": "999"}],
                ),
                name,
            )

    def test_the_choice_is_deterministic_for_equal_sizes(self) -> None:
        files = [
            {"name": "b.zip", "source": "original", "size": "500"},
            {"name": "a.zip", "source": "original", "size": "500"},
        ]

        self.assertEqual(ArchiveOrgCatalogSource._pick_file(files)["name"], "a.zip")
        self.assertEqual(ArchiveOrgCatalogSource._pick_file(list(reversed(files)))["name"], "a.zip")

    def test_rubbish_yields_nothing(self) -> None:
        for value in (None, "files", 7, [], [None, 3, "x"]):
            self.assertIsNone(ArchiveOrgCatalogSource._pick_file(value))


class MetadataTests(unittest.TestCase):
    """Enriching a record from a metadata document."""

    def base(self) -> Dict[str, Any]:
        return ArchiveOrgCatalogSource._base_record(
            {"identifier": "alice", "title": "Alice", "item_size": 5000},
        )

    def test_the_address_digest_and_exact_size_are_applied(self) -> None:
        record = self.base()
        digest = "62739d2989cda3facb92304251ccb4e60735dcdd"

        with source([]) as built:
            built._apply_metadata(record, {
                "metadata": {"title": "Alice Remastered"},
                "files": [{"name": "alice.zip", "source": "original", "size": "989711238", "sha1": digest}],
            })

        self.assertEqual(record["title"], "Alice Remastered")
        self.assertEqual(record["download_url"], "https://archive.org/download/alice/alice.zip")
        self.assertEqual(record["sha1"], digest)

        # The file's own size, not the whole item's.
        self.assertEqual(record["size_bytes"], 989_711_238)

    def test_a_restricted_item_gets_no_address(self) -> None:
        record = self.base()

        with source([]) as built:
            built._apply_metadata(record, {
                "metadata": {"access-restricted-item": "true"},
                "files": [{"name": "alice.zip", "source": "original", "size": "10"}],
            })

        self.assertIsNone(record["download_url"])
        self.assertIsNone(record["sha1"])

    def test_a_nonsense_digest_is_dropped_but_the_address_is_kept(self) -> None:
        record = self.base()

        with source([]) as built:
            built._apply_metadata(record, {
                "metadata": {},
                "files": [{"name": "alice.zip", "source": "original", "size": "10", "sha1": "unknown"}],
            })

        self.assertIsNotNone(record["download_url"])
        self.assertIsNone(record["sha1"])

    def test_names_are_escaped_in_the_address(self) -> None:
        record = self.base()

        with source([]) as built:
            built._apply_metadata(record, {
                "metadata": {},
                "files": [{"name": "Alice's Game.zip", "source": "original", "size": "10"}],
            })

        self.assertIn("Alice%27s%20Game.zip", record["download_url"])

    def test_an_item_with_no_game_file_keeps_its_list_view_size(self) -> None:
        record = self.base()

        with source([]) as built:
            built._apply_metadata(record, {"metadata": {}, "files": [
                {"name": "meta.xml", "source": "original", "size": "5"},
            ]})

        self.assertIsNone(record["download_url"])
        self.assertEqual(record["size_bytes"], 5000)


class BaseRecordTests(unittest.TestCase):
    """What the list view alone produces."""

    def test_the_details_url_is_built_from_the_identifier(self) -> None:
        record = ArchiveOrgCatalogSource._base_record({"identifier": "a b/c", "title": "T"})

        self.assertEqual(record["url"], "https://archive.org/details/a%20b%2Fc")

    def test_a_title_given_as_a_list_takes_its_first_entry(self) -> None:
        # The Archive returns this as an array whenever an item has two titles.
        record = ArchiveOrgCatalogSource._base_record(
            {"identifier": "a", "title": ["Doom", "DOOM"]},
        )

        self.assertEqual(record["title"], "Doom")

    def test_a_missing_title_falls_back_to_the_identifier(self) -> None:
        record = ArchiveOrgCatalogSource._base_record({"identifier": "msdos_doom"})

        self.assertEqual(record["title"], "msdos_doom")

    def test_no_identifier_means_no_record(self) -> None:
        for document in ({}, {"identifier": ""}, {"identifier": "   "}, {"title": "x"}):
            self.assertIsNone(ArchiveOrgCatalogSource._base_record(document))

    def test_addeddate_is_used_when_publicdate_is_absent(self) -> None:
        record = ArchiveOrgCatalogSource._base_record(
            {"identifier": "a", "addeddate": "2025-01-01T00:00:00Z"},
        )

        self.assertEqual(record["pub_date"], "2025-01-01T00:00:00Z")


class ReadFirstPageTests(unittest.TestCase):
    """Reading the page the launcher pipes in."""

    def test_a_search_shaped_page_is_accepted(self) -> None:
        self.assertIsNotNone(read_first_page(io.StringIO(json.dumps(page(["a"], 1)))))

    def test_a_cursor_shaped_page_is_accepted(self) -> None:
        self.assertIsNotNone(read_first_page(io.StringIO(json.dumps({"items": [], "total": 0}))))

    def test_empty_input_is_absent_rather_than_an_error(self) -> None:
        for raw in ("", "   ", "\n"):
            self.assertIsNone(read_first_page(io.StringIO(raw)))

    def test_unusable_input_is_absent_rather_than_an_error(self) -> None:
        # A working import is worth more than a strict complaint about a pipe.
        for raw in ("not json", "[1,2,3]", '{"unrelated": true}', "null"):
            self.assertIsNone(read_first_page(io.StringIO(raw)))


if __name__ == "__main__":
    unittest.main(verbosity=2)

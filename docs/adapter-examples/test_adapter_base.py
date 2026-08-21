#!/usr/bin/env python3
"""Tests for the shared adapter machinery.

Run from the repository root:

    python -m unittest discover -s docs/adapter-examples -p 'test_*.py' -v

`unittest` rather than pytest, deliberately: these files are copied into an
adapter folder on machines that have nothing installed, and a test suite that
needed a package would be one nobody could run where it matters.

No test here touches the network. The transport is exercised through a fake
that returns whatever a case needs, which is the only way to test the retry
paths at all -- a real endpoint will not produce an empty 200 on demand.
"""

from __future__ import annotations

import io
import json
import threading
import time
import unittest
from typing import Any, List, Optional, Tuple

from _adapter_base import (
    AdapterError,
    AdapterSource,
    HttpClient,
    RECORD_FIELDS,
    RateLimiter,
    build_parser,
    emit,
    make_record,
    normalise_title,
    parse_human_size,
)


class NormaliseTitleTests(unittest.TestCase):
    """The comparison form used for matching."""

    def test_punctuation_and_case_are_ignored(self) -> None:
        self.assertEqual(normalise_title("Need for Speed: The Run"), "needforspeedtherun")
        self.assertEqual(normalise_title("NEED  FOR   SPEED!!"), "needforspeed")

    def test_a_query_is_a_substring_of_a_subtitled_title(self) -> None:
        # The property the whole matching design rests on. A word-by-word
        # comparison against a colon-separated subtitle does not give you this.
        self.assertIn(normalise_title("need for speed"), normalise_title("Need for Speed: The Run"))
        self.assertIn(normalise_title("war in the north"), normalise_title("Lord Of The Rings: War In The North"))

    def test_accents_are_folded(self) -> None:
        self.assertEqual(normalise_title("Pokémon Red"), "pokemonred")
        self.assertEqual(normalise_title("Café Crème"), "cafecreme")

    def test_one_leading_article_is_dropped(self) -> None:
        self.assertEqual(normalise_title("The Oregon Trail"), "oregontrail")
        self.assertEqual(normalise_title("A Boy and His Blob"), "boyandhisblob")

        # Only the leading one, and only once: an article inside the title is
        # part of the title.
        self.assertEqual(normalise_title("The The"), "the")

    def test_empty_and_punctuation_only_titles_survive_without_raising(self) -> None:
        for value in ("", "   ", "!!!", "***", None):
            self.assertEqual(normalise_title(value), "")  # type: ignore[arg-type]


class ParseHumanSizeTests(unittest.TestCase):
    """Sizes, in every shape these payloads carry."""

    def test_decimal_units(self) -> None:
        self.assertEqual(parse_human_size("1.8 GB"), 1_800_000_000)
        self.assertEqual(parse_human_size("700 MB"), 700_000_000)
        self.assertEqual(parse_human_size("512 KB"), 512_000)
        self.assertEqual(parse_human_size("2 TB"), 2_000_000_000_000)

    def test_binary_units(self) -> None:
        self.assertEqual(parse_human_size("1 GiB"), 1_073_741_824)
        self.assertEqual(parse_human_size("1,5 GiB"), 1_610_612_736)

    def test_a_range_yields_its_first_figure(self) -> None:
        # The smallest complete download is the one a caller can rely on being
        # able to finish.
        self.assertEqual(parse_human_size("from 14.5 GB"), 14_500_000_000)
        self.assertEqual(parse_human_size("Selective Download - from 14.5 GB"), 14_500_000_000)

    def test_plain_numbers_pass_through(self) -> None:
        self.assertEqual(parse_human_size(2_359_527), 2_359_527)
        self.assertEqual(parse_human_size("2359527"), 2_359_527)
        self.assertEqual(parse_human_size(4096.9), 4096)

    def test_unusable_values_are_none(self) -> None:
        for value in ("unknown", "", "   ", None, [], {}, "GB", "-", object()):
            self.assertIsNone(parse_human_size(value))

    def test_true_is_not_a_size(self) -> None:
        # bool subclasses int, so this would otherwise parse as one byte.
        self.assertIsNone(parse_human_size(True))
        self.assertIsNone(parse_human_size(False))

    def test_negative_numbers_are_refused(self) -> None:
        self.assertIsNone(parse_human_size(-1))
        self.assertIsNone(parse_human_size(-2.5))


class MakeRecordTests(unittest.TestCase):
    """The schema contract."""

    def test_every_contract_field_is_present(self) -> None:
        record = make_record(title="Doom", url="https://x.test/d", source_id="d")

        self.assertEqual(set(record), set(RECORD_FIELDS))

    def test_optional_fields_default_to_none(self) -> None:
        record = make_record(title="Doom", url="https://x.test/d", source_id="d")

        for field in ("download_url", "sha1", "size_bytes", "pub_date"):
            self.assertIsNone(record[field], field)

    def test_a_valid_digest_is_kept_and_lowercased(self) -> None:
        digest = "62739D2989CDA3FACB92304251CCB4E60735DCDD"
        record = make_record(title="A", url="https://x.test/a", source_id="a", sha1=digest)

        self.assertEqual(record["sha1"], digest.lower())

    def test_a_nonsense_digest_is_dropped(self) -> None:
        # A field holding "unknown" fails every transfer with a mismatch that is
        # really the source's typo, which is worse than no verification at all.
        for bad in ("unknown", "", "abc", "z" * 40, "62739d2989cda3facb92304251ccb4e60735dcd"):
            record = make_record(title="A", url="https://x.test/a", source_id="a", sha1=bad)
            self.assertIsNone(record["sha1"], bad)

    def test_sizes_are_parsed_on_the_way_in(self) -> None:
        record = make_record(
            title="A", url="https://x.test/a", source_id="a", size_bytes="1.8 GB",
        )

        self.assertEqual(record["size_bytes"], 1_800_000_000)

    def test_required_fields_are_enforced(self) -> None:
        for kwargs in (
            {"title": "", "url": "https://x.test/a", "source_id": "a"},
            {"title": "A", "url": "  ", "source_id": "a"},
            {"title": "A", "url": "https://x.test/a", "source_id": ""},
        ):
            with self.assertRaises(ValueError):
                make_record(**kwargs)  # type: ignore[arg-type]

    def test_whitespace_is_trimmed(self) -> None:
        record = make_record(
            title="  Doom  ", url="  https://x.test/d  ", source_id="  d  ", pub_date="  2026  ",
        )

        self.assertEqual(record["title"], "Doom")
        self.assertEqual(record["url"], "https://x.test/d")
        self.assertEqual(record["source_id"], "d")
        self.assertEqual(record["pub_date"], "2026")

    def test_a_blank_pub_date_becomes_none(self) -> None:
        record = make_record(title="A", url="https://x.test/a", source_id="a", pub_date="   ")

        self.assertIsNone(record["pub_date"])


class RateLimiterTests(unittest.TestCase):
    """The shared minimum interval."""

    def test_a_zero_delay_never_blocks(self) -> None:
        limiter = RateLimiter(0)
        started = time.monotonic()

        for _ in range(50):
            limiter.wait()

        self.assertLess(time.monotonic() - started, 0.2)

    def test_a_negative_delay_is_refused(self) -> None:
        with self.assertRaises(ValueError):
            RateLimiter(-1)

    def test_successive_calls_are_spaced(self) -> None:
        limiter = RateLimiter(0.05)
        limiter.wait()

        started = time.monotonic()
        limiter.wait()

        self.assertGreaterEqual(time.monotonic() - started, 0.04)

    def test_concurrent_callers_are_serialised(self) -> None:
        # The bug this guards: an unguarded read-modify-write lets every worker
        # decide at once that its turn has come, so the limit is observed by
        # each thread individually and by the site not at all.
        limiter = RateLimiter(0.02)
        stamps: List[float] = []
        lock = threading.Lock()

        def worker() -> None:
            limiter.wait()

            with lock:
                stamps.append(time.monotonic())

        threads = [threading.Thread(target=worker) for _ in range(8)]

        for thread in threads:
            thread.start()

        for thread in threads:
            thread.join()

        self.assertEqual(len(stamps), 8)

        stamps.sort()
        gaps = [b - a for a, b in zip(stamps, stamps[1:])]

        # Every gap respects the interval. The tolerance is generous because
        # time.monotonic() granularity and float error land a 0.02 wait at
        # 0.01499999999 often enough to make a tighter bound flaky; the point
        # being proved is that the waits are serialised at all, not their
        # precision.
        for gap in gaps:
            self.assertGreaterEqual(gap, 0.010)


class FakeTransport:
    """Stands in for the network.

    Each queued entry is one response: ``(body, status, retry_after)``. An entry
    that is an exception is raised instead, which is how a connection failure is
    reproduced.
    """

    def __init__(self, responses: List[Any]) -> None:
        self.responses = list(responses)
        self.calls: List[str] = []

    def __call__(self, url: str) -> Tuple[str, int, Optional[float]]:
        self.calls.append(url)

        if not self.responses:
            raise AssertionError(f"unexpected request to {url}")

        answer = self.responses.pop(0)

        if isinstance(answer, Exception):
            raise answer

        return answer


def client_with(responses: List[Any], **options: Any) -> Tuple[HttpClient, FakeTransport, List[float]]:
    """Build a client whose transport and sleeps are fakes.

    Args:
        responses: What the transport should return, in order.
        **options: Passed to :class:`HttpClient`.

    Returns:
        The client, its transport, and the list back-offs are recorded in.
    """
    slept: List[float] = []

    client = HttpClient(
        delay=0,
        retries=options.pop("retries", 3),
        sleeper=slept.append,
        **options,
    )

    transport = FakeTransport(responses)
    client.get = transport  # type: ignore[method-assign]

    return client, transport, slept


class HttpClientTests(unittest.TestCase):
    """Retries, back-off, and the shapes a busy endpoint answers with."""

    def test_a_good_response_is_returned_without_retrying(self) -> None:
        client, transport, slept = client_with([('{"ok":true}', 200, None)])

        self.assertEqual(client.get_json("https://x.test/a"), {"ok": True})
        self.assertEqual(len(transport.calls), 1)
        self.assertEqual(slept, [])

    def test_an_empty_200_is_retried(self) -> None:
        # How this endpoint sheds load: it answers, but with nothing. A rate
        # limit wearing a success code.
        client, transport, slept = client_with([
            ("", 200, None),
            ("   ", 200, None),
            ('{"ok":1}', 200, None),
        ])

        self.assertEqual(client.get_json("https://x.test/a"), {"ok": 1})
        self.assertEqual(len(transport.calls), 3)
        self.assertEqual(len(slept), 2)

    def test_a_429_is_retried_and_honours_retry_after(self) -> None:
        client, transport, slept = client_with([
            ("slow down", 429, 2.0),
            ('{"ok":1}', 200, None),
        ])

        self.assertEqual(client.get_json("https://x.test/a"), {"ok": 1})
        self.assertEqual(slept, [2.0])

    def test_a_server_error_is_retried_with_growing_back_off(self) -> None:
        client, transport, slept = client_with([
            ("boom", 500, None),
            ("boom", 503, None),
            ('{"ok":1}', 200, None),
        ])

        self.assertEqual(client.get_json("https://x.test/a"), {"ok": 1})
        self.assertEqual(len(slept), 2)
        self.assertGreater(slept[1], slept[0])

    def test_a_client_error_is_not_retried(self) -> None:
        # A 404 is an answer, not a hiccup.
        client, transport, _ = client_with([("gone", 404, None)])

        with self.assertRaises(AdapterError):
            client.get_json("https://x.test/a")

        self.assertEqual(len(transport.calls), 1)

    def test_a_connection_failure_is_retried_then_reported(self) -> None:
        client, transport, _ = client_with(
            [OSError("no route"), OSError("no route"), OSError("no route")],
        )

        with self.assertRaises(AdapterError) as caught:
            client.get_text("https://x.test/a")

        self.assertEqual(len(transport.calls), 3)
        self.assertIn("no route", str(caught.exception))

    def test_retries_are_exhausted_and_the_reason_reported(self) -> None:
        client, transport, _ = client_with([("", 200, None)] * 3)

        with self.assertRaises(AdapterError) as caught:
            client.get_text("https://x.test/a")

        self.assertEqual(len(transport.calls), 3)
        self.assertIn("shedding load", str(caught.exception))

    def test_malformed_json_is_not_retried(self) -> None:
        # Non-empty but broken will not become valid on another attempt.
        client, transport, _ = client_with([("<html>nope</html>", 200, None)])

        with self.assertRaises(AdapterError):
            client.get_json("https://x.test/a")

        self.assertEqual(len(transport.calls), 1)

    def test_back_off_is_capped(self) -> None:
        client, _, slept = client_with([("boom", 500, None)] * 3, retries=3)
        client.max_backoff = 1.0

        with self.assertRaises(AdapterError):
            client.get_text("https://x.test/a")

        for waited in slept:
            self.assertLessEqual(waited, 1.0)

    def test_an_absurd_retry_after_is_capped(self) -> None:
        client, _, slept = client_with([("slow", 429, 100_000.0), ('{"a":1}', 200, None)])

        self.assertEqual(client.get_json("https://x.test/a"), {"a": 1})
        self.assertEqual(slept, [60.0])

    def test_a_bad_tuning_argument_is_refused(self) -> None:
        for options in ({"timeout": 0}, {"timeout": -1}, {"retries": 0}):
            with self.assertRaises(ValueError):
                HttpClient(delay=0, **options)  # type: ignore[arg-type]


class RetryAfterTests(unittest.TestCase):
    """Reading the header."""

    class Headers:
        def __init__(self, value: Any) -> None:
            self.value = value

        def get(self, _name: str) -> Any:
            return self.value

    def test_seconds_are_read(self) -> None:
        self.assertEqual(HttpClient.retry_after(self.Headers("30")), 30.0)
        self.assertEqual(HttpClient.retry_after(self.Headers(" 2.5 ")), 2.5)

    def test_absent_or_unreadable_is_none(self) -> None:
        self.assertIsNone(HttpClient.retry_after(None))
        self.assertIsNone(HttpClient.retry_after(self.Headers(None)))
        self.assertIsNone(HttpClient.retry_after(self.Headers("")))
        self.assertIsNone(HttpClient.retry_after(self.Headers("Wed, 21 Oct 2026 07:28:00 GMT")))
        self.assertIsNone(HttpClient.retry_after(object()))


class AdapterSourceTests(unittest.TestCase):
    """The base's own behaviour."""

    def test_the_two_methods_must_be_implemented(self) -> None:
        source = AdapterSource(client=HttpClient(delay=0))

        with self.assertRaises(NotImplementedError):
            source.search("x")

        with self.assertRaises(NotImplementedError):
            source.crawl_library()

        source.close()

    def test_matching_ignores_punctuation(self) -> None:
        self.assertTrue(AdapterSource.matches("need for speed", "Need for Speed: The Run"))
        self.assertTrue(AdapterSource.matches("LORD OF THE RINGS!!", "The Lord Of The Rings - Conquest"))
        self.assertFalse(AdapterSource.matches("doom", "Quake"))

    def test_an_empty_query_matches_everything(self) -> None:
        self.assertTrue(AdapterSource.matches("", "Anything"))
        self.assertTrue(AdapterSource.matches("   ", "Anything"))

    def test_filtering_preserves_order(self) -> None:
        source = AdapterSource(client=HttpClient(delay=0))

        records = [
            make_record(title="Doom II", url="https://x.test/2", source_id="2"),
            make_record(title="Quake", url="https://x.test/q", source_id="q"),
            make_record(title="Doom", url="https://x.test/1", source_id="1"),
        ]

        kept = source.filter_titles("doom", records)

        self.assertEqual([record["title"] for record in kept], ["Doom II", "Doom"])
        source.close()

    def test_it_closes_as_a_context_manager(self) -> None:
        with AdapterSource(client=HttpClient(delay=0)) as source:
            self.assertIsNotNone(source.client)

        self.assertIsNone(source.client._session)


class CommandLineTests(unittest.TestCase):
    """The shared options."""

    def test_defaults(self) -> None:
        parsed = build_parser("test").parse_args([])

        self.assertIsNone(parsed.search)
        self.assertFalse(parsed.crawl)
        self.assertFalse(parsed.no_deep)
        self.assertIsNone(parsed.max_pages)
        self.assertEqual(parsed.limit, 50)
        self.assertAlmostEqual(parsed.delay, 0.34)
        self.assertEqual(parsed.retries, 3)

    def test_values_are_read(self) -> None:
        parsed = build_parser("test").parse_args(
            ["--search", "doom", "--loose", "--max-pages", "3", "--delay", "1.5", "--no-deep"],
        )

        self.assertEqual(parsed.search, "doom")
        self.assertTrue(parsed.loose)
        self.assertEqual(parsed.max_pages, 3)
        self.assertAlmostEqual(parsed.delay, 1.5)
        self.assertTrue(parsed.no_deep)

    def test_a_parser_can_be_extended(self) -> None:
        parser = build_parser("test")
        parser.add_argument("--uploader")

        self.assertEqual(parser.parse_args(["--uploader", "a@b.test"]).uploader, "a@b.test")


class EmitTests(unittest.TestCase):
    """The stdout contract."""

    def test_records_are_wrapped_in_results(self) -> None:
        buffer = io.StringIO()
        record = make_record(title="Doom", url="https://x.test/d", source_id="d")

        emit([record], buffer)

        self.assertEqual(json.loads(buffer.getvalue()), {"results": [record]})

    def test_an_empty_run_still_writes_a_document(self) -> None:
        # Writing nothing at all is reported by the launcher as a broken hook,
        # whereas "no games here" should import cleanly as zero rows.
        buffer = io.StringIO()
        emit([], buffer)

        self.assertEqual(json.loads(buffer.getvalue()), {"results": []})

    def test_non_ascii_titles_survive(self) -> None:
        buffer = io.StringIO()
        emit([make_record(title="Pokémon", url="https://x.test/p", source_id="p")], buffer)

        self.assertEqual(json.loads(buffer.getvalue())["results"][0]["title"], "Pokémon")


if __name__ == "__main__":
    unittest.main(verbosity=2)

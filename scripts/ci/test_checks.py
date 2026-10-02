import base64
import copy
import importlib.util
import io
import json
from pathlib import Path
import re
import sys
import subprocess
import tempfile
import unittest
import urllib.error
import urllib.request
from unittest.mock import patch

from PIL import Image

from check_gate import failures
from check_snapshots import check, expected_files, PAGES
from coverage_summary import dotnet_rows, swift_rows
import dictation_audio
import fake_provider
import run_dictation
import run_snapshots

ROOT = Path(__file__).resolve().parents[2]


class SnapshotChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        image = Image.new("RGB", (32, 32), "white")
        image.putpixel((4, 4), (0, 0, 0))
        for name in expected_files("en"):
            image.save(self.directory / name)

    def test_complete_render_passes(self):
        self.assertEqual(check(self.directory, "en"), [])

    def test_missing_page_fails_even_when_popover_exists(self):
        (self.directory / "settings-en-history.png").unlink()
        self.assertTrue(check(self.directory, "en"))

    def test_corrupt_and_blank_renders_fail(self):
        path = self.directory / "hud-error.png"
        for data in (b"", b"\x89PNG\r\n\x1a\n", b"not an image"):
            path.write_bytes(data)
            self.assertTrue(check(self.directory, "en"))
        for color in ((255, 255, 255, 255), (0, 0, 0, 0)):
            Image.new("RGBA", (32, 32), color).save(path)
            self.assertTrue(check(self.directory, "en"))

    def test_error_file_fails_even_with_all_images(self):
        (self.directory / "error.txt").write_text("render exception")
        self.assertTrue(check(self.directory, "en"))

    def test_wrong_language_fails(self):
        self.assertTrue(check(self.directory, "zh"))

    def test_inventory_matches_both_apps(self):
        swift = (ROOT / "macos/Sources/OpenTypeless/SettingsWindow.swift").read_text()
        cs = (ROOT / "windows/src/OpenTypeless/UI/SettingsPage.cs").read_text()
        self.assertEqual(set(re.search(r"enum SettingsPage[^\{]+\{\s*case ([^\n]+)", swift)[1].replace(" ", "").split(",")), set(PAGES))
        self.assertEqual(set(re.search(r"enum SettingsPage\s*\{([^}]+)", cs)[1].lower().replace(" ", "").split(",")), set(PAGES))
        for path, pattern in (
            ("macos/Sources/OpenTypeless/Onboarding.swift", r"enum OnboardingStep[^\{]+\{\s*case ([^\n]+)"),
            ("windows/src/OpenTypeless/UI/OnboardingWindow.cs", r"enum OnboardingStep\s*\{([^}]+)"),
        ):
            self.assertEqual(len(re.search(pattern, (ROOT / path).read_text())[1].split(",")), 4)
        for path in ("macos/Sources/OpenTypeless/UISnapshots.swift", "windows/src/OpenTypeless/UI/UISnapshots.cs"):
            states = set(re.findall(r'"(hud-[a-z]+)"', (ROOT / path).read_text()))
            self.assertEqual({state + ".png" for state in states}, {name for name in expected_files("en") if name.startswith("hud-")})


class GateChecks(unittest.TestCase):
    def setUp(self):
        self.needs = {name: {"result": "success"} for name in ("changes", "quality", "pipeline", "macos", "windows")}
        self.needs["changes"]["outputs"] = {"macos": "true", "windows": "true"}

    def test_all_required_jobs_pass(self):
        self.assertEqual(failures(self.needs), [])

    def test_required_skipped_failed_cancelled_and_missing_jobs_fail(self):
        for job in self.needs:
            for result in ("skipped", "failure", "cancelled", None):
                needs = copy.deepcopy(self.needs)
                needs[job]["result"] = result
                self.assertTrue(failures(needs), (job, result))

    def test_docs_only_and_single_platform_changes_pass(self):
        for platforms in (("macos", "windows"), ("windows",), ("macos",)):
            needs = copy.deepcopy(self.needs)
            for platform in platforms:
                needs["changes"]["outputs"][platform] = "false"
                needs[platform]["result"] = "skipped"
                if platform == "windows":
                    needs["pipeline"]["result"] = "skipped"
            self.assertEqual(failures(needs), [])

    def test_absent_filter_output_fails(self):
        self.needs["changes"]["outputs"] = {}
        self.assertTrue(failures(self.needs))


class CoverageChecks(unittest.TestCase):
    def test_cobertura_merges_classes_without_counting_the_same_line_twice(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "coverage.xml"
            path.write_text('''<coverage><packages><package name="TypelessCore"><classes>
              <class filename="src/Core.cs"><lines><line number="1" hits="0"/><line number="2" hits="1"/></lines></class>
              <class filename="src/Core.cs"><lines><line number="1" hits="2"/></lines></class>
              </classes></package><package name="Tests"><classes><class filename="test.cs"><lines>
              <line number="3" hits="1"/></lines></class></classes></package></packages></coverage>''')
            self.assertEqual(dotnet_rows(path), [("src/Core.cs", 2, 2)])

    def test_swift_excludes_test_and_generated_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "coverage.json"
            path.write_text(json.dumps({"data": [{"files": [
                {"filename": "/repo/macos/Sources/TypelessCore/A.swift", "summary": {"lines": {"covered": 4, "count": 5}}},
                {"filename": "/repo/macos/Tests/A.swift", "summary": {"lines": {"covered": 100, "count": 100}}},
            ]}]}))
            self.assertEqual(swift_rows(path), [("A.swift", 4, 5)])


class SnapshotProcessChecks(unittest.TestCase):
    def test_crash_and_timeout_fail_even_if_image_validation_succeeds(self):
        for outcome in (subprocess.CompletedProcess([], 9), subprocess.TimeoutExpired("app", 180)):
            with tempfile.TemporaryDirectory() as directory:
                executable = Path(directory) / "app with spaces"
                executable.touch()
                out = Path(directory) / "snapshots"
                with patch.object(sys, "argv", ["run_snapshots.py", str(executable), str(out)]), \
                     patch.object(run_snapshots, "check", return_value=[]), \
                     patch.object(run_snapshots.subprocess, "run", side_effect=[outcome] * 4), \
                     patch("sys.stdout", new_callable=io.StringIO), \
                     patch.dict("os.environ", {"GITHUB_STEP_SUMMARY": ""}), self.assertRaises(SystemExit) as error:
                    run_snapshots.main()
                self.assertEqual(error.exception.code, 1)
                self.assertTrue((out / "summary.md").is_file())


class EvaluationFixtureChecks(unittest.TestCase):
    def test_both_evaluation_sets_have_unique_ids_and_valid_assertions(self):
        for path in sorted((ROOT / "eval").glob("polish-*.json")):
            cases = json.loads(path.read_text())
            self.assertTrue(cases, path)
            ids = set()
            for case in cases:
                self.assertIsInstance(case["id"], str)
                self.assertNotIn(case["id"], ids)
                ids.add(case["id"])
                self.assertIsInstance(case["input"], str)
                self.assertTrue(case["input"].strip())
                for field in ("mustContain", "mustNotContain"):
                    self.assertIsInstance(case.get(field, []), list)
                    self.assertTrue(all(isinstance(s, str) and s for s in case.get(field, [])))
                self.assertIsInstance(case.get("anyOf", []), list)
                for group in case.get("anyOf", []):
                    self.assertIsInstance(group, list)
                    self.assertTrue(group)
                    self.assertTrue(all(isinstance(s, str) and s for s in group))


class PackagingChecks(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("render", ROOT / "packaging/render.py")
        self.render = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.render)
        self.release = {"published_at": "2026-10-02T00:00:00Z", "assets": [
            {"name": f"OpenTypeless-1.2.3-{suffix}", "digest": "sha256:" + "ab" * 32}
            for suffix in self.render.FILES.values()
        ]}

    def render_release(self, directory, tag="v1.2.3"):
        with patch.object(self.render, "request", return_value=io.BytesIO(json.dumps(self.release).encode())), \
             patch.object(sys, "argv", ["render.py", tag, directory]), patch("sys.stdout", new_callable=io.StringIO):
            self.render.main()

    def test_templates_render_for_all_supported_tag_prefixes(self):
        for tag in ("1.2.3", "v1.2.3", "V1.2.3"):
            with tempfile.TemporaryDirectory() as directory:
                self.render_release(directory, tag)
                files = list(Path(directory).rglob("*.*"))
                self.assertEqual(len(files), 5)
                for path in files:
                    text = path.read_text()
                    self.assertNotRegex(text, r"@[A-Z0-9_]+@")
                    self.assertIn("1.2.3", text)
                cask = (Path(directory) / "homebrew/opentypeless.rb").read_text()
                self.assertIn("ab" * 32, cask)
                installer = (Path(directory) / "winget/TylerHong.OpenTypeless.installer.yaml").read_text()
                self.assertIn("AB" * 32, installer)
                self.assertIn(f"/download/{tag}/", installer)

    def test_draft_prerelease_and_missing_asset_are_rejected(self):
        for invalid in ({"draft": True}, {"prerelease": True}, {"assets": []}):
            original = self.release.copy()
            self.release.update(invalid)
            with tempfile.TemporaryDirectory() as directory, self.assertRaises(SystemExit):
                self.render_release(directory)
            self.release = original


class DictationChecks(unittest.TestCase):
    """The fake provider and the end-to-end check must notice a lost, repeated or cut word, not just a crash."""

    def post(self, server, path, body, key=fake_provider.API_KEY):
        request = urllib.request.Request(server.base_url + path, data=json.dumps(body).encode(), method="POST",
                                         headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode()

    def wav(self, words):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "words.wav"
            dictation_audio.write(path, words)
            return base64.b64encode(path.read_bytes()).decode()

    def test_transcribes_the_words_in_the_audio_after_one_503(self):
        words = dictation_audio.script(12)
        body = {"model": "m", "input_audio": {"data": self.wav(words), "format": "wav"}}
        with fake_provider.Server() as server:
            self.assertEqual(self.post(server, "/audio/transcriptions", body)[0], 503)
            status, text = self.post(server, "/audio/transcriptions", body)
            self.assertEqual(self.post(server, "/audio/transcriptions", body, key="wrong")[0], 401)
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(text)["text"].split(), words)

    def test_a_word_cut_in_half_is_not_transcribed_as_that_word(self):
        samples = dictation_audio.samples(["typeless"])
        burst = next(i for i, s in enumerate(samples) if s)
        self.assertEqual(fake_provider.transcribe(samples[:burst + 1600]), "?")

    def test_streamed_clean_up_reassembles_split_utf8(self):
        with fake_provider.Server() as server:
            status, text = self.post(server, "/chat/completions", {"stream": True, "messages": [
                {"role": "user", "content": "<transcript>\n语音 输入\n</transcript>\n\n(reminder)"}]})
        self.assertEqual(status, 200)
        deltas = [json.loads(line[6:])["choices"][0]["delta"].get("content", "")
                  for line in text.splitlines() if line.startswith("data: {")]
        self.assertEqual("".join(deltas), fake_provider.polished("语音 输入"))

    def run_log(self, *texts, failed=True):
        log = [{"method": "POST", "path": "/api/v1/audio/transcriptions", "authorized": True, "seconds": 20.0,
                "status": 503}] if failed else []
        log += [{"method": "POST", "path": "/api/v1/audio/transcriptions", "authorized": True, "seconds": 20.0,
                 "status": 200, "text": t} for t in texts]
        return log

    def output(self, raw, cleaned=None):
        cleaned = fake_provider.polished(raw) if cleaned is None else cleaned
        return (f"\n===== Raw transcript ({len(raw)} chars) =====\n{raw}\n\n"
                f"===== Cleaned up ({len(cleaned)} chars) =====\n{cleaned}\n\n")

    def polish_log(self, raw):
        return [{"method": "POST", "path": "/api/v1/chat/completions", "authorized": True, "transcript": raw}]

    def test_a_faithful_run_passes(self):
        words = ["open", "语音", "works", "today"]
        raw = "open 语音 works today"
        log = self.run_log("open 语音", "works today") + self.polish_log(raw)
        self.assertEqual(run_dictation.check(0, self.output(raw), log, words), [])

    def test_lost_repeated_or_reordered_words_fail(self):
        words = ["open", "语音", "works", "today"]
        for raw in ("open 语音 today", "open 语音 works works today", "语音 open works today"):
            log = self.run_log("a", "b") + self.polish_log(raw)
            self.assertTrue(run_dictation.check(0, self.output(raw), log, words), raw)

    def test_no_retry_wrong_clean_up_and_crash_fail(self):
        words, raw = ["open", "works"], "open works"
        good = self.run_log("open", "works") + self.polish_log(raw)
        self.assertTrue(run_dictation.check(0, self.output(raw), self.run_log("open", "works", failed=False)
                                            + self.polish_log(raw), words))
        self.assertTrue(run_dictation.check(0, self.output(raw, "open works"), good, words))
        self.assertTrue(run_dictation.check(0, self.output(raw), self.run_log("open", "works"), words))
        self.assertTrue(run_dictation.check(2, self.output(raw), good, words))
        self.assertTrue(run_dictation.check(0, "", good, words))

    def test_macos_settings_reach_user_defaults_as_property_lists(self):
        arguments, _ = run_dictation.command(Path("/Apps/OpenTypeless"), Path("a.wav"),
                                             run_dictation.settings("http://127.0.0.1:1/api/v1"), Path("data"))
        self.assertIn("-baseURLs", arguments)
        self.assertEqual(arguments[arguments.index("-baseURLs") + 1], '{"openrouter" = "http://127.0.0.1:1/api/v1";}')
        self.assertEqual(arguments[arguments.index("-sttBackupEnabled") + 1], "NO")


if __name__ == "__main__":
    unittest.main()

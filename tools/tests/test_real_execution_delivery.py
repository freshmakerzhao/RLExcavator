"""Repository entry-point checks; never start Unity, learning, or hardware."""
from contextlib import redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
ENTRY = REPO / "tools/real_execution_v1/run_experiment.py"


def load(path, name="real_execution_delivery"):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DeliveryTests(unittest.TestCase):
    def test_help_is_available_without_ml_packages_or_process_launch(self):
        # -S hides site-packages: help/prepare/command construction need stdlib only.
        result = subprocess.run([sys.executable, "-B", "-S", str(ENTRY), "--help"],
            capture_output=True, text=True, timeout=10, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("train-command", result.stdout)
        self.assertIn("benchmark", result.stdout)

    def test_analysis_requires_explicit_existing_freeze_and_has_readonly_help(self):
        entry = ENTRY.parent / "analyze_pilot.py"
        result = subprocess.run([sys.executable, "-B", "-S", str(entry), "--help"],
            capture_output=True, text=True, timeout=10, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--freeze", result.stdout)
        analysis = load(entry, "real_execution_analysis_delivery")
        with tempfile.TemporaryDirectory() as tmp:
            missing = Path(tmp) / "missing.json"
            with self.assertRaises(FileNotFoundError):
                analysis.verify_build_snapshot({}, {}, missing)

    def test_relocated_entry_uses_current_python_and_repository_config_without_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp) / "relocated_project"
            target = repo / "tools/real_execution_v1"
            shutil.copytree(ENTRY.parent, target)
            (repo / "config").mkdir()
            shutil.copy2(REPO / "config/real_execution_v1_ppo.yaml", repo / "config")
            player = repo / "unused_player"
            player.write_bytes(b"not executable: print-only command must never run this")
            runner = load(target / ENTRY.name)
            output = io.StringIO()
            with redirect_stdout(output):
                self.assertEqual(runner.main(["train-command", "--player", str(player),
                    "--run-id", "new_test_run", "--base-port", "6180"]), 0)
            result = json.loads(output.getvalue())
            self.assertFalse(result["launch_performed"])
            self.assertEqual(Path(result["cwd"]), repo)
            self.assertEqual(result["argv"][0], sys.executable)
            self.assertEqual(Path(result["argv"][2]), repo / "config/real_execution_v1_ppo.yaml")
            self.assertNotIn(">=50000", result["pilot_pause"])
            self.assertIn("49999", result["pilot_pause"])
            self.assertFalse((repo / "results").exists())
            backend = runner.load_benchmark()
            self.assertEqual(Path(backend.__file__).parent, target)
            self.assertEqual(backend.BASE, target / "policy_loader.py")
            # A relocated sentinel proves backend loading stays inside this clone.
            backend.BASE.write_text('BEHAVIOR = "local-loader-sentinel"\n')
            self.assertEqual(backend.load_base().BEHAVIOR, "local-loader-sentinel")


if __name__ == "__main__":
    unittest.main()

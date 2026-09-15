#!/usr/bin/env python3
"""Exercise the Linux bootstrap without touching a real installation or network."""

import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class LatestInstallerTests(unittest.TestCase):
    def install(self, arch="x86_64", libc="glibc", version="2.41", bad_checksum=False):
        with tempfile.TemporaryDirectory(prefix="vibe-installer-") as directory:
            root = Path(directory)
            tools = root / "bin"
            tools.mkdir()
            package = root / "package"
            package.mkdir()
            rid = ("linux-musl" if libc == "musl" else "linux") + (
                "-x64" if arch == "x86_64" else "-arm64"
            )
            mode = "aot"
            asset = f"VibeHarder-9.8.7-{rid}-{mode}.tar.gz"
            (package / "VibeHarder").write_text("#!/bin/sh\nexit 0\n")
            (package / "runtime.txt").write_text(rid)
            (package / "Assets").mkdir()
            (package / "Assets/app.png").write_bytes(b"image fixture")
            shutil.copy(ROOT / "packaging/install-linux.sh", package / "install.sh")
            for name in ("vibe-harder.sh", "install-cli.sh"):
                shutil.copy(ROOT / "packaging" / name, package / name)
            with tarfile.open(root / asset, "w:gz") as archive:
                archive.add(package, arcname=".")
            digest = hashlib.sha256((root / asset).read_bytes()).hexdigest()
            (root / "SHA256SUMS.txt").write_text(
                f"{'0' * 64 if bad_checksum else digest}  {asset}\n"
            )
            (root / "release.json").write_text('{"tag_name": "v9.8.7"}')
            scripts = {
                "uname": f'#!/bin/sh\ncase "$1" in -m) echo {arch};; *) echo Linux;; esac\n',
                "ldd": f"#!/bin/sh\necho {libc}\n",
                "getconf": f"#!/bin/sh\necho glibc {version}\n"
                if libc == "glibc"
                else "#!/bin/sh\nexit 1\n",
                "curl": """#!/usr/bin/env python3
import os,sys,shutil
from pathlib import Path
args=sys.argv[1:]; url=args[-1]; name='release.json' if url.endswith('/latest') else url.rsplit('/',1)[-1]
shutil.copy(Path(os.environ['FIXTURE'])/name, args[args.index('--output')+1])
""",
            }
            for name, text in scripts.items():
                path = tools / name
                path.write_text(text)
                path.chmod(0o755)
            data = root / "share with spaces"
            env = dict(
                os.environ,
                PATH=f"{tools}:{os.environ['PATH']}",
                FIXTURE=str(root),
                XDG_DATA_HOME=str(data),
                HOME=str(root / "home"),
            )
            result = subprocess.run(
                ["sh", str(ROOT / "install.sh")],
                env=env,
                text=True,
                capture_output=True,
            )
            if bad_checksum or version in ("2.31", "2.36") or arch == "armv7l":
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((data / "codex-manager").exists())
            else:
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn(asset, result.stdout)
                self.assertTrue((data / "codex-manager/VibeHarder").is_file())
                self.assertTrue((data / "applications/codex-manager.desktop").is_file())
                desktop = data / "applications/codex-manager.desktop"
                self.assertIn("StartupWMClass=CodexManager", desktop.read_text())
                customized = desktop.read_text().replace("Icon=codex-manager", "Icon=/custom/my-icon.svg")
                customized += "Actions=custom;\n\n[Desktop Action custom]\nName=My action\nExec=custom-command\n"
                desktop.write_text(customized)
                reinstall = subprocess.run(["sh", str(package / "install.sh")], env=env, text=True, capture_output=True)
                self.assertEqual(0, reinstall.returncode, reinstall.stdout + reinstall.stderr)
                self.assertEqual(customized, desktop.read_text())

    def test_supported_architectures_and_libcs(self):
        for arch in ("x86_64", "aarch64"):
            for libc in ("glibc", "musl"):
                with self.subTest(arch=arch, libc=libc):
                    self.install(arch, libc)

    def test_old_glibc_rejects_instead_of_switching_to_bundled(self):
        self.install(version="2.36")

    def test_bad_checksum_installs_nothing(self):
        self.install(bad_checksum=True)

    def test_unsupported_systems_install_nothing(self):
        self.install(version="2.31")
        self.install(arch="armv7l")


if __name__ == "__main__":
    unittest.main()

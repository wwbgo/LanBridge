"""Release packaging contract tests.

These checks are dependency-free and do not publish releases or run an installer.
"""
from pathlib import Path
import hashlib
import re
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "scripts"
WORKFLOW = ROOT / ".github/workflows/release.yml"


class ReleasePackagingTests(unittest.TestCase):
    def test_tag_validation_accepts_only_stable_semver_tags_before_build(self):
        script = SCRIPTS / "Release.ps1"
        self.assertTrue(script.is_file())
        valid = subprocess.run(
            ["pwsh", "-NoProfile", "-File", str(script), "-TagName", "v1.2.3", "-ValidateOnly"],
            text=True, capture_output=True)
        self.assertEqual(valid.returncode, 0, valid.stderr)
        for tag in ("1.2.3", "v1.2", "v1.2.3-rc.1", "v1.2.3+build", "v01.2.3", "v1.02.3", "v1.2.03"):
            invalid = subprocess.run(
                ["pwsh", "-NoProfile", "-File", str(script), "-TagName", tag, "-ValidateOnly"],
                text=True, capture_output=True)
            self.assertNotEqual(invalid.returncode, 0, tag)

    def test_workflow_validates_before_build_and_never_publishes_manual_runs(self):
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("workflow_dispatch:", source)
        self.assertRegex(source, r"v\(\?<version>\\d\+\\\.\\d\+\\\.\\d\+\)")
        self.assertLess(source.index("      - name: Validate tag"), source.index("      - name: Build and package"))
        self.assertIn("if: github.event_name == 'push'", source)
        self.assertIn("permissions:\n      contents: write", source)
        self.assertNotIn("--clobber", source)

    def test_build_and_package_forward_version_to_both_products(self):
        for name in ("Build.ps1", "Package.ps1", "Package-Single.ps1"):
            source = (SCRIPTS / name).read_text(encoding="utf-8")
            self.assertRegex(source, r"\[string\]\$Version")
            self.assertIn("AssemblyVersion", source, name)
            self.assertIn("FileVersion", source, name)
        installer = (SCRIPTS / "Package-Single.ps1").read_text(encoding="utf-8")
        self.assertIn("MyAppVersion", installer)

    def test_inno_setup_is_user_scoped_and_uses_x64compatible(self):
        source = (SCRIPTS / "LanBridge.iss").read_text(encoding="utf-8")
        self.assertIn("PrivilegesRequired=lowest", source)
        self.assertIn("ArchitecturesAllowed=x64compatible", source)
        self.assertIn("ArchitecturesInstallIn64BitMode=x64compatible", source)
        self.assertNotIn("=x64\n", source)
        self.assertIn("#define MyAppVersion", source)

    def test_installer_supports_english_and_chinese_with_system_ui_default(self):
        source = (SCRIPTS / "LanBridge.iss").read_text(encoding="utf-8")
        self.assertIn("[Languages]", source)
        languages = source.split("[Languages]", 1)[1].split("[", 1)[0]
        self.assertRegex(languages, r'Name: "english"; MessagesFile: "compiler:Default.isl"')
        self.assertRegex(languages, r'Name: "chinesesimplified"; MessagesFile: "Languages\\ChineseSimplified.isl"')
        self.assertLess(languages.index('Name: "english"'), languages.index('Name: "chinesesimplified"'))
        self.assertIn("LanguageDetectionMethod=uilanguage", source)
        self.assertIn("UsePreviousLanguage=no", source)
        self.assertIn("ShowLanguageDialog=yes", source)
        self.assertIn('Description: "{cm:LaunchProgram,{#MyAppName}}"', source)
        translation = SCRIPTS / "Languages/ChineseSimplified.isl"
        self.assertTrue(translation.is_file())
        self.assertIn("LanguageID=$0804", translation.read_text(encoding="utf-8-sig"))

    def test_release_checksum_contract_is_sha256_two_space_format(self):
        source = (SCRIPTS / "Release.ps1").read_text(encoding="utf-8")
        self.assertIn("Get-FileHash", source)
        self.assertIn("{0}  {1}", source)
        with self.subTest(example=True):
            digest = hashlib.sha256(b"LanBridge").hexdigest()
            self.assertRegex(f"{digest}  LanBridge-win-x64.zip", r"^[0-9a-f]{64}  [^ ]+$")

    def test_asset_upload_uses_github_upload_host_and_verifies_before_publication(self):
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn('https://uploads.github.com/repos/$env:REPOSITORY/releases/', source)
        self.assertLess(source.index('$actual = @($verified.assets'), source.index('draft = $false'))

    def test_command_line_installer_version_is_not_redefined(self):
        source = (SCRIPTS / "LanBridge.iss").read_text(encoding="utf-8")
        self.assertRegex(source, r'#ifndef MyAppVersion\s+#define MyAppVersion "0\.1\.1"\s+#endif')

    def test_package_does_not_copy_repository_sources_into_payload(self):
        source = (SCRIPTS / "Package-Single.ps1").read_text(encoding="utf-8")
        self.assertIn("dotnet publish", source)
        self.assertIn("Copy-Item (Join-Path $root 'scripts')", source)
        self.assertNotIn("Copy-Item (Join-Path $root 'src')", source)
        self.assertNotIn("Copy-Item (Join-Path $root 'tests')", source)


if __name__ == "__main__":
    unittest.main()

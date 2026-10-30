"""LanBridge icon acceptance checks; run with python tests/test_icon_assets.py -v.

Requires Pillow and the .NET SDK. No app, host, publisher or installer is launched.
"""
from io import BytesIO
from pathlib import Path
import json
import struct
import subprocess
import unittest
import xml.etree.ElementTree as ET

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/LanBridge.App/Assets"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


class IconAssetTests(unittest.TestCase):
    def test_real_multisize_icon_and_editable_master(self):
        ico_path = ASSETS / "LanBridge.ico"
        self.assertTrue(ico_path.is_file(), "Missing generated Assets/LanBridge.ico")
        raw = ico_path.read_bytes()
        self.assertEqual(struct.unpack_from("<HHH", raw), (0, 1, len(SIZES)))
        dimensions = []
        for index in range(len(SIZES)):
            width, height, colors, reserved, planes, bits, length, offset = struct.unpack_from(
                "<BBBBHHII", raw, 6 + 16 * index)
            size = width or 256
            self.assertEqual(height or 256, size)
            self.assertEqual(reserved, 0)
            self.assertIn(planes, (0, 1))
            self.assertEqual(bits, 32)
            self.assertGreaterEqual(offset, 6 + 16 * len(SIZES))
            self.assertLessEqual(offset + length, len(raw))
            with Image.open(BytesIO(raw[offset:offset + length])) as frame:
                self.assertEqual(frame.size, (size, size), "ICO directory must match real pixels")
                self.assert_icon_pixels(frame.convert("RGBA"), size)
            dimensions.append(size)
        self.assertEqual(sorted(dimensions), list(SIZES))
        with Image.open(ico_path) as icon:
            self.assertEqual(icon.ico.sizes(), {(s, s) for s in SIZES})
        with Image.open(ASSETS / "LanBridge-256.png") as png:
            self.assertEqual(png.size, (256, 256))
            self.assertEqual(png.mode, "RGBA")
            self.assert_icon_pixels(png, 256)
            with Image.open(ico_path) as ico:
                self.assertEqual(png.tobytes(), ico.convert("RGBA").tobytes())
        svg = ET.parse(ASSETS / "LanBridge.svg").getroot()
        ns = {"s": "http://www.w3.org/2000/svg"}
        self.assertEqual(svg.attrib["viewBox"], "0 0 256 256")
        self.assertGreaterEqual(len(svg.findall(".//s:path", ns)), 1)
        self.assertGreaterEqual(len(svg.findall(".//s:circle", ns)), 2)
        self.assertEqual(svg.findall(".//s:text", ns), [], "No letter soup")

    def test_exe_embeds_icon_and_content_survives_build_publish(self):
        project = ET.parse(ROOT / "src/LanBridge.App/LanBridge.App.csproj").getroot()
        self.assertEqual(project.findtext(".//ApplicationIcon"), "Assets/LanBridge.ico")
        contents = project.findall(".//Content")
        icon = next((item for item in contents
                     if item.attrib.get("Include", item.attrib.get("Update", "")).replace("\\", "/") == "Assets/LanBridge.ico"), None)
        self.assertIsNotNone(icon, "Icon must be content for AppWindow and tray runtime file loading")
        self.assertEqual(icon.findtext("CopyToOutputDirectory"), "PreserveNewest")
        self.assertEqual(icon.findtext("CopyToPublishDirectory"), "PreserveNewest")

    def test_msbuild_indexes_runtime_icon_exactly_once(self):
        result = subprocess.run(
            ["dotnet", "msbuild", str(ROOT / "src/LanBridge.App/LanBridge.App.csproj"),
             "-getItem:Content", "-p:Configuration=Release", "-nologo"],
            cwd=ROOT, text=True, capture_output=True, check=True, encoding="utf-8")
        items = json.loads(result.stdout)["Items"]["Content"]
        icons = [item for item in items
                 if item["Identity"].replace("\\", "/") == "Assets/LanBridge.ico"]
        self.assertEqual(len(icons), 1, "Duplicate Content entries break WinUI PRI generation")
        self.assertEqual(icons[0]["CopyToOutputDirectory"], "PreserveNewest")
        self.assertEqual(icons[0]["CopyToPublishDirectory"], "PreserveNewest")

    def test_tray_owns_one_file_icon_until_disposal(self):
        tray = (ROOT / "src/LanBridge.App/TrayIcon.cs").read_text(encoding="utf-8")
        self.assertNotIn("LoadIcon(", tray, "Do not use a generic shared system icon")
        self.assertNotIn("32512", tray)
        self.assertIn('Path.Combine(AppContext.BaseDirectory, "Assets", "LanBridge.ico")', tray)
        self.assertIn("private IntPtr _hIcon;", tray)
        self.assertEqual(tray.count("_hIcon = LoadImage("), 1)
        self.assertIn("ImageIcon, GetSystemMetrics(SmCxsmicon), GetSystemMetrics(SmCysmicon), LrLoadfromfile", tray)
        self.assertIn("private const uint LrLoadfromfile = 0x00000010;", tray)
        self.assertIn("if (_hIcon == IntPtr.Zero)", tray)
        self.assertIn("hIcon = _hIcon,", tray)
        constructor = tray.split("internal TrayIcon()", 1)[1].split("internal void OpenMainWindow", 1)[0]
        self.assertIn("catch", constructor)
        self.assertIn("Dispose();", constructor, "Constructor failure must release the owned icon and HWND")
        add_icon = tray.split("private void AddIcon()", 1)[1].split("private void RemoveIcon()", 1)[0]
        self.assertNotIn("LoadImage", add_icon, "Explorer restart must reuse the existing HICON")
        dispose = tray.split("public void Dispose()", 1)[1].split("[UnmanagedFunctionPointer", 1)[0]
        self.assertIn("if (_disposed) return;", dispose)
        self.assertIn("DestroyIcon(_hIcon)", dispose)
        self.assertIn("_hIcon = IntPtr.Zero;", dispose)
        self.assertLess(dispose.index("RemoveIcon();"), dispose.index("DestroyIcon(_hIcon)"))
        self.assertEqual(tray.count("DestroyIcon(_hIcon)"), 1, "Destroy owned HICON exactly once")
        # Preserve the independent native callback/menu fixes already present.
        self.assertIn("unchecked((uint)lParam.ToInt64()) & 0xFFFF", tray)
        self.assertIn("TpmReturncmd | TpmNonotify | TpmLeftbutton", tray)
        self.assertIn("PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);", tray)

    def test_installer_and_shortcuts_share_the_product_icon(self):
        source = (ROOT / "scripts/LanBridge.iss").read_text(encoding="utf-8")
        setup = source.split("[Setup]", 1)[1].split("[Files]", 1)[0]
        self.assertIn(r"SetupIconFile={#SourceDir}\Assets\LanBridge.ico", setup)
        self.assertIn(r"UninstallDisplayIcon={app}\{#MyAppExeName}", setup)
        self.assertNotIn("IconFilename=", setup, "IconFilename is not an Inno [Setup] directive")
        shortcuts = source.split("[Icons]", 1)[1].split("[Run]", 1)[0]
        entries = [line for line in shortcuts.splitlines() if line.startswith("Name:")]
        self.assertEqual(len(entries), 2)
        for entry in entries:
            self.assertIn(r'IconFilename: "{app}\Assets\LanBridge.ico"', entry)

    def assert_icon_pixels(self, image, size):
        for point in ((0, 0), (0, size - 1), (size - 1, 0), (size - 1, size - 1)):
            self.assertEqual(image.getpixel(point)[3], 0, "Outside rounded tile must be transparent")
        pixels = [image.getpixel((x, y)) for y in range(size) for x in range(size)]
        self.assertGreater(len(set(pixels)), 30, "Reject flat/placeholder assets")
        self.assertGreater(sum(a > 240 for r, g, b, a in pixels), size * size * 0.6)
        self.assertGreater(sum(a == 0 for r, g, b, a in pixels), size * size * 0.02)
        self.assertGreater(sum(r > 180 and g > 210 and b > 210 and a > 220
                               for r, g, b, a in pixels), size * size * 0.04,
                           "Bridge must remain visible even at 16px")
        self.assertGreater(sum(g > r * 1.3 and g > 155 and a > 220
                               for r, g, b, a in pixels), size * size * 0.01,
                           "Network nodes must remain teal even at 16px")


if __name__ == "__main__":
    unittest.main()

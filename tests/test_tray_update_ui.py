"""UI composition contracts; exercised alongside real published-window smoke tests."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "LanBridge.App"


class TrayUpdateUiTests(unittest.TestCase):
    def test_batch_checkboxes_center_text_with_native_glyph(self):
        source = (APP / "MainWindow.cs").read_text(encoding="utf-8-sig")
        for field in ("_batchMode", "_selectAll"):
            with self.subTest(field=field):
                declaration = re.search(rf"CheckBox {field} = new\(\)\s*\{{(.*?)\}};", source, re.S)
                self.assertIsNotNone(declaration)
                self.assertIn("VerticalContentAlignment = VerticalAlignment.Center", declaration.group(1))
                self.assertIn("VerticalAlignment = VerticalAlignment.Center", declaration.group(1))
                self.assertIn("Padding = new Thickness(8, 0, 0, 0)", declaration.group(1))

    def test_main_page_has_no_update_command(self):
        source = (APP / "MainWindow.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("_updates", source)
        self.assertNotIn('Button("检查更新"', source)

    def test_native_tray_command_reaches_update_handler(self):
        tray = (APP / "TrayIcon.cs").read_text(encoding="utf-8-sig")
        app = (APP / "App.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("private const int IdCheckUpdates = 1003;", tray)
        self.assertIn("internal event EventHandler? CheckUpdatesRequested;", tray)
        self.assertIn('AppendMenu(menu, MfString | MfEnabled, (uint)IdCheckUpdates, "检查更新")', tray)
        self.assertIn("case IdCheckUpdates: RequestCheckUpdates(); break;", tray)
        self.assertIn("CheckUpdatesRequested?.Invoke(this, EventArgs.Empty)", tray)
        self.assertIn("_tray.CheckUpdatesRequested += async (_, _) => await window.CheckUpdatesFromTrayAsync();", app)

    def test_tray_update_restores_window_and_retains_busy_guards(self):
        source = (APP / "MainWindow.Updates.cs").read_text(encoding="utf-8-sig")
        entry = source.split("internal async Task CheckUpdatesFromTrayAsync()", 1)[-1].split("var current = CurrentVersion", 1)[0]
        self.assertIn("internal async Task CheckUpdatesFromTrayAsync()", source)
        self.assertIn("if (_busy || _dialogOpen || _closed || _exitLifecycle.IsExitRequested) return;", entry)
        self.assertIn("ShowFromTray();", entry)
        self.assertLess(entry.index("ShowFromTray();"), entry.index("_dialogOpen = true;"))


if __name__ == "__main__":
    unittest.main()

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class QuietOperationNoticeTests(unittest.TestCase):
    def test_main_notices_allow_only_warning_and_error(self):
        source = (ROOT / 'src/LanBridge.App/MainWindow.cs').read_text(encoding='utf-8')
        body = source.split('private void ShowNotice(', 1)[1].split('private async Task MutateAsync(', 1)[0]
        self.assertIn('severity is not (InfoBarSeverity.Warning or InfoBarSeverity.Error)', body)
        self.assertIn('_notice.IsOpen = false;', body)

    def test_editor_feedback_allows_only_warning_and_error(self):
        source = (ROOT / 'src/LanBridge.App/RuleEditorDialog.cs').read_text(encoding='utf-8')
        body = source.split('private void Feedback(', 1)[1].split('private ForwardRule?', 1)[0]
        self.assertIn('severity is not (InfoBarSeverity.Warning or InfoBarSeverity.Error)', body)
        self.assertIn('_feedbackFlyout.Hide();', body)

    def test_unsuccessful_probe_results_remain_warnings(self):
        for name in ('MainWindow.cs', 'RuleEditorDialog.cs'):
            source = (ROOT / 'src/LanBridge.App' / name).read_text(encoding='utf-8')
            self.assertIn('UiPresentation.ProbeSucceeded(result) ? InfoBarSeverity.Success : InfoBarSeverity.Warning', source)

    def test_loopback_copy_remains_visible_as_a_warning(self):
        source = (ROOT / 'src/LanBridge.App/MainWindow.cs').read_text(encoding='utf-8')
        copy = source.split('private async Task CopySelectedAsync()', 1)[1]
        self.assertIn('loopback ? InfoBarSeverity.Warning', copy)


if __name__ == '__main__':
    unittest.main()

# LanBridge native UI direction

Use Windows-native Fluent styling, not a dashboard. Follow the OS light/dark theme with Mica behind a single, quiet rule list. Colors come from theme resources: light canvas #F3F3F3, light surface #FFFFFF, light text #1A1A1A; dark canvas #202020, dark surface #2B2B2B, dark text #FFFFFF. Accent is the user's Windows accent, not an app-specific decorative color.

Typography: native Segoe UI Variable (Chinese falls back to Microsoft YaHei UI); title 28, section/row name 16, body 14, supporting text 12. Left-aligned layout, 28 px outer margins, 12–20 px spacing. Title / connection status / rule toolbar / list / contextual help. Rows carry actual route, runtime status and traffic, never invented metrics. A single editor owns validation, VM selection and TCP probe. Status means listening, not target reachability; those claims remain explicitly separate. No charts, sample rules, custom title bar, or automatic firewall changes.

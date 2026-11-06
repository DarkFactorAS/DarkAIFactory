# AIDashboard config

AIDashboard has its own config folder, separate from `aiteam/config`.

- `appsettings.Production.json` is tracked; deployment replaces the `DATABASE.*` placeholders.
- For local use, copy `appsettings.template.json` to `appsettings.<Environment>.json`
  (e.g. `Development`, `Acceptance`) and fill in the values. These copies are git-ignored.

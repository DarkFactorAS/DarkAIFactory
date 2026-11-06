# AIDashboard config

AIDashboard has its own config folder, separate from `aiteam/config`.

- `appsettings.Production.json` is tracked with `DATABASE.*` placeholders for
  project builds; do not use it as runtime configuration.
- For local use, copy `appsettings.template.json` to `appsettings.<Environment>.json`
  (e.g. `Development`, `Acceptance`) and fill in the values. These copies are git-ignored.
- For Compose, create `aiteam/aidashboard/runtime-config/appsettings.Production.json`
  from the template and fill in the real database settings. Compose mounts that
  separate directory at `/app/Config`; set `AIDASHBOARD_CONFIG_PATH` to use a
  different runtime-config directory. See the [dashboard setup](../../README.md#docker-and-deployment).

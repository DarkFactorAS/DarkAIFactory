# AIDashboard config

AIDashboard has its own config folder, separate from `aiteam/config`.

- `appsettings.Production.json` is tracked with `DATABASE.*` and `ACCOUNTSERVER.*` placeholders
  for project builds; do not use it as runtime configuration.
- `AccountServer` (`Endpoint`, `ClientId`, `ClientSecret`, `Scope`) is the DarkFactor account
  server used for the dashboard login, the same section as DFWeb. Without an `Endpoint` every
  login returns 503.
- For local use, copy `appsettings.template.json` to `appsettings.<Environment>.json`
  (e.g. `Development`, `Acceptance`) and fill in the values. These copies are git-ignored.
- For Compose, create `aiteam/aidashboard/runtime-config/appsettings.Production.json`
  from the template and fill in the real database settings. Compose mounts that
  separate directory at `/app/Config`; set `AIDASHBOARD_CONFIG_PATH` to use a
  different runtime-config directory. See the [dashboard setup](../../README.md#docker-and-deployment).
- `DataProtection:KeysPath` (env `DataProtection__KeysPath`) is the folder where the ASP.NET Core
  Data Protection key ring is stored. It encrypts the login cookie, so persisting it keeps users
  (including "Remember me", 7 days) logged in across restarts. The Docker image sets it to
  `/app/keys`, which Compose mounts as the named volume `aiteam_aidashboard_keys`
  (override with `AIDASHBOARD_KEYS_VOLUME_NAME`). Deleting the volume logs everyone out. When unset
  (local runs) the framework default key location is used.

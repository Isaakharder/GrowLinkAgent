# Climate (Synopta Block Summary) uploads

The **Synopta** tab sends each hourly Block Summary export to the server in
**Server URL**, using the **Upload key**. Both are settings (no rebuild):
the URL is saved in `%APPDATA%\GrowLinkAgent\config.json`, the key with
Windows DPAPI for the current user.

| Destination | Server URL |
|---|---|
| Plants (production) | `https://qaopjmaiqezvrskpnrsx.supabase.co/functions/v1/climate` |
| Plants (staging, tests only) | `https://rngfabuorwpthcokjncf.supabase.co/functions/v1/climate` |
| CropLink (legacy) | its server URL, unchanged |

The tab shows which one is configured, and asks before saving a change of destination.
Plants upload keys (`plk_…`) are created in Plants under Settings › Climate.

## What is sent

`POST <Server URL>/api/v1/climate/imports` with `Authorization: Bearer <key>` and JSON:
`filename`, `file_hash` (`sha256-…` of the file's bytes), `raw_csv_base64` (those exact
bytes), `agent_version`, and the agent's parsed `readings`. Plants stores the original
file and parses it itself; CropLink ignores the extra fields.

## What happens to a file

| Server answer | File |
|---|---|
| 2xx (created, duplicate, conflict, quarantined — all stored) | moved to `Uploaded\yyyy-MM` |
| no answer, timeout, 408, 425, 429, 5xx | stays in the folder; retried 5 s and 15 s later (longer if the server sends Retry-After), then after 1, 2, 5, 10 and 15 minutes, then every 15 minutes until accepted |
| 401 / 403 (key missing, wrong, revoked, expired) | stays in the folder and is retried the same way; the tab shows "upload key refused" |
| any other 4xx | moved to `Failed` with `<file>.error.txt` explaining why; **Retry failed files** moves it back |

A file moves to `Uploaded` only after a 2xx, which Plants sends only once the file's
original bytes and import status are committed. The folders are looked through again
every minute, so kept files recover by themselves after an outage; **Retry now** tries
them at once.

## Tests

`dotnet test GrowLinkAgent.CoreTests` runs on any OS (the services, without the form).
Tests against a real server run only when pointed at one:
`PLANTS_CLIMATE_URL`, `PLANTS_CLIMATE_KEY`, `CLIMATE_SAMPLES_DIR` (exports kept outside Git),
and optionally `FAULT_PROXY_URL` / `FAULT_PROXY_COMMAND` (see `tools/fault_proxy.py`).

Never commit real greenhouse exports: this repository is public.

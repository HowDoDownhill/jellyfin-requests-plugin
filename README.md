# Jellyfin Requests

A Jellyfin 12 plugin that adds a **Requests** page to the Jellyfin web UI, backed by Seerr, Radarr and Sonarr.

- Renders inside Jellyfin with Jellyfin's own page and button classes. The accent color is read from your active theme at runtime, so the page follows whatever theme the server uses.
- Groups requests by person (you first), with a By person / All / Mine view switch, status filters, type filter and search.
- Live updates: the page long-polls the plugin, which polls the Radarr/Sonarr download queues while someone is watching and refreshes immediately on webhooks and Jellyfin library events. Download progress, ETA and episode counts show on each card.
- Admins can approve, decline or remove requests from the page. Users can cancel their own pending requests (configurable).
- **Remove deletes the title from Radarr/Sonarr including its files** (through Seerr, so the right instance is used, 4K included), then deletes the Seerr request. If someone else still has an active request for the same title (e.g. other seasons of a show), only the request is removed and the title stays. The confirm dialog states which of the two will happen.
- Cleanup: deletes the Seerr request (and its media record, so the title can be requested again) once the content is gone from Jellyfin, Radarr or Sonarr.

Everything runs inside Jellyfin. API keys stay in the plugin config on the server and never reach browsers. No extra container and no File Transformation plugin needed: the script tag is added to `index.html` by ASP.NET middleware as it is served.

## Install

1. Build the zip (needs the .NET 10 SDK):
   ```powershell
   ./scripts/package.ps1
   ```
   This writes `dist/JellyfinRequests_1.0.0.0.zip`.
2. On the Jellyfin server, unzip it into the plugins folder so you end up with
   `<jellyfin config>/plugins/JellyfinRequests_1.0.0.0/Jellyfin.Plugin.Requests.dll` and `meta.json`.
   - Docker (linuxserver / official image): `/config/plugins/` (or `/config/data/plugins/` on the official image).
   - Debian/Ubuntu package: `/var/lib/jellyfin/plugins/`.
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\`.
3. Restart Jellyfin.
4. Dashboard → Plugins → **Jellyfin Requests**: fill in the Seerr URL and API key (and optionally Radarr/Sonarr), then **Save & test connections**.
5. Reload the web client. A **Requests** entry appears in the side menu, plus an icon in the header.

## Webhooks (recommended)

The config page lists the three URLs with the token filled in. Use an address the other services can reach (e.g. the Jellyfin container name on a shared Docker network).

| Service | Where | Settings |
| --- | --- | --- |
| Seerr | Settings → Notifications → Webhook | URL `…/JellyfinRequests/Webhook/Seerr?token=…`, enable all notification types. The default JSON payload is fine. |
| Radarr | Settings → Connect → + → Webhook | URL `…/JellyfinRequests/Webhook/Radarr?token=…`, method POST. Enable On Grab, On Import, On Movie Added, **On Movie Delete**. |
| Sonarr | Settings → Connect → + → Webhook | URL `…/JellyfinRequests/Webhook/Sonarr?token=…`, method POST. Enable On Grab, On Import, On Series Add, **On Series Delete**. |

The token can also go in the `Authorization` header or as the password of basic auth.

Without webhooks everything still works. Seerr is re-read every 60 s while the page is open, and the daily cleanup task catches deletions.

## How cleanup decides

Cleanup ships in **dry-run** mode. Use **Preview cleanup** on the config page, check the list, then turn dry run off.

A Seerr media record is deleted only when it has at least one approved/completed request and no pending request, and one of these holds:

| Trigger | Condition |
| --- | --- |
| Radarr `MovieDelete` / Sonarr `SeriesDelete` webhook | Always (the title was deleted from the *arr). |
| Movie or series leaves the Jellyfin library | After the grace period (default 15 min), it is still absent from Jellyfin and Radarr/Sonarr has no files for it. |
| Daily task / manual run | Seerr marks it deleted; or it was available and is now gone from Jellyfin with no files in Radarr/Sonarr; or it was approved over 2 hours ago and is no longer in Radarr/Sonarr at all. |

Cleanup only removes the Seerr record; it never touches Radarr/Sonarr (the content is already gone). Deleting files happens only through an admin's Remove button. Every decision is logged in the Jellyfin log and shown under "Recent cleanup activity".

Known limits:
- One Radarr and one Sonarr instance are read. 4K-only requests skip *arr checks, since they usually live on a separate instance. If you route regular requests to several Radarr/Sonarr instances, titles on the unconfigured ones look "missing". Leave Radarr/Sonarr blank in that case and rely on webhooks plus the Jellyfin checks.
- Episode-level removals do not trigger cleanup. Only whole movies and series do.

## Development

```
src/Jellyfin.Plugin.Requests/
  Api/            RequestsController (page + admin API), WebhookController
  Arr/            Radarr/Sonarr v3 client
  Seerr/          Seerr v1 client and models
  Model/          RequestMapper and CleanupPolicy (pure, unit-tested)
  Services/       RequestStore (snapshot + long-poll), LiveUpdateService, LibraryWatcher, CleanupService, CleanupTask
  Web/            client.js / client.css (injected page), ScriptInjectionStartupFilter
  Configuration/  PluginConfiguration, configPage.html
tests/            xunit tests for the mapping and cleanup rules
dev/mock_services.py  mock Seerr/Radarr/Sonarr with live-progressing downloads
```

```
dotnet test
python dev/mock_services.py --users <jellyfinUserId>:<name>,<jellyfinUserId>:<name>
```

The mock listens on 5055 / 7878 / 8989 with API keys `dev-seerr-key`, `dev-radarr-key` and `dev-sonarr-key`.

Endpoints, all under `/JellyfinRequests`:

| Method | Path | Auth |
| --- | --- | --- |
| GET | `Requests` | user |
| GET | `Changes?since=<version>` | user (long-poll, 25 s) |
| POST | `Requests/{id}/Cancel` · `Approve` · `Decline` | requester (pending) / admin |
| POST | `Requests/{id}/Remove` (deletes from Radarr/Sonarr with files unless shared) | admin |
| POST | `Admin/Test`, `Admin/Cleanup?dryRun=` | admin |
| GET | `Admin/CleanupJournal` | admin |
| POST | `Webhook/Seerr` · `Radarr` · `Sonarr` | webhook token |
| GET | `client.js`, `client.css` | anonymous |

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Trovami ("find me") is a multi-user, Docker-hosted household inventory. Things are stored all over a place (a house, a cellar, a holiday flat); Trovami records where each one is, so it can be found again. It has two clients: a Blazor web app for organizing and finding things, and an Android app for adding things fast. It follows the architecture and patterns of its siblings LaLira (D:\Programming\LaLira) and Recall (D:\Programming\LearningPortal) and reuses their user handling, backups and release setup.

- **Simplicity is key.** Adding must be fast: scan a tag, take a photo, save, done. Prefer inline, fast flows over dialogs and extra steps, in both apps.
- **Households.** All locations and items belong to a household. Any user can create households and invite others into them by email: an address with an account gets a household invite to accept, a new address gets an account invite (the admin can turn this off, so that only the admin creates accounts). A user can be in several households and switches between them. The creator is the household's **owner** (manages members, the AI key, can delete the household); members see and edit everything in it.
- **Locations.** A tree of any depth: House → Floor → Room → Closet → Drawer, House → Cellar → Rack → Box. A location has a name, a parent, a **kind** from a fixed list (house, apartment, floor, room, cellar, garage, attic, closet, cabinet, shelf, rack, drawer, box, bag, other) that only picks its icon, and optional notes, photo, color (a fixed palette) and short label (what's written on the box). There are no location types or custom attributes on purpose: an address or opening times go in the notes. Moving a location moves everything under it. A location can only be deleted once it's empty, or by moving its contents to its parent.
- **Items.** A name, a photo, optional notes and a quantity (default 1), at exactly one location, or in the household's **Inbox** when it has none. Items are moved by picking a new location inline.
- **AI naming.** An item added with a photo and no name gets its name from the AI, on the server, after the photo arrives (so photos taken offline are named on sync). The key is per household: the owner picks Claude or ChatGPT and enters the key in the household's settings. Without a key, or when the call fails, the item is "Unnamed item" and is named by hand. A name typed by a person is never overwritten. In the UI it's called "AI", with no vendor names outside the settings.
- **Tags.** A tag is a random code linked to one location; it can be re-linked. **NFC tags** are linked on the phone (scan an unknown tag → pick a location or create one under a chosen parent; this works offline). **QR labels** are printed from the web app for chosen locations (a print sheet, no PDF library) and scanned with the phone app's camera. Both carry the same `trovami://t/<code>` URI.
- **Inbox.** Items added on the phone without a tag land in the Inbox. The web app shows it as a photo grid where each item gets its location inline (several at once by selecting them).
- **Find.** One search over the household (item names, notes, location names and labels) shows each hit with its full path ("House › Cellar › Rack 2 › Blue box"). Finding things is the point of the app, so the search sits at the top of every page.
- **Responsive.** Designed mainly for PC use but must work well on tablets and phones. No PWA.

### Android app
- **Only for adding things fast.** Scanning a registered tag opens the add screen, even when the app is closed: camera → shutter → saved to that location, and the camera is ready for the next item. Name and notes are optional. Without a tag, items go to the Inbox.
- **Offline first.** The cellar has no signal. The phone keeps the household's locations and tags locally, so scanning, linking tags and adding items all work offline. Everything syncs when the server is reachable again (client-generated IDs, idempotent uploads, photos uploaded separately).
- **Sign-in.** Either by scanning a pairing QR code from the web app's Profile ("Connect a phone": server URL + a one-time code), or with server URL, email and password. Either way the phone gets a long-lived device token (only its hash is stored); Profile lists the devices and can revoke them.
- **Updates.** Distributed as a signed APK attached to each GitHub release (no Play Store). The app checks the latest release, offers the download, and shows the release notes after an update.

### Taken over from LaLira and Recall
- **Accounts and sign-in.** No open sign-up. The first user to register becomes the super admin, who invites users by email. Login with email and password, forgot password, a required display name. Admin pages (Users, Admin settings, Backups) are reached from the user menu only. Optional account requests from the sign-in page (off by default, needs email); the admin approves or declines. Users page as a sortable table (create, make admin, delete). Per-admin "Notify me when invited users join".
- **Admin settings.** SMTP server with a test email, notifications, account requests, household invites of new addresses, time and retention for automatic backups.
- **Backup and restore.** Admin-only, for the whole instance: the database, Data Protection keys and all photos in a .zip. A daily automatic backup goes to `/data/backups` (time and how many to keep, default 14), plus "Download backup now" with an optional password (AES). Before an existing database gets a newer schema, startup writes a `before-update-…` backup first; if that fails, the app doesn't migrate and stops with a critical log message. Restore: upload a .zip or pick an automatic backup; the app validates it, saves a safety backup of the current state, swaps the data in and restarts itself (Docker's restart policy brings it back). Photos are shrunk on upload (longest side 1600 px, JPEG) to keep backups manageable.
- **What's new.** After an update, each user sees a banner ("Trovami was updated to X.Y.Z") linking to the release notes page, built from `CHANGELOG.md`, until they close it. Users are never told about the version they first saw; local "dev" builds show nothing.
- **Homepage widget.** `GET /api/homepage/...` with an `X-API-Key` header (a token per user created in Profile, only its SHA-256 hash stored) returns item, location and Inbox counts for gethomepage.dev.
- **Themes.** Dark (default), Light or System per user, rendered as `<html data-theme>`. The Android app follows the phone's theme.

### Wording
- The user's own login is a "user" with a **Profile** page. "Location" is any place in the tree, "item" is a thing stored there, "tag" covers both NFC tags and QR labels.
- **US English only**, American spelling, in the UI, emails, docs and comments. No localization layer, but numbers and dates are still formatted with the user's region format (`Format`), never the server culture.

## Commands

```
dotnet build Trovami.slnx                    # build everything
dotnet run --project src/Trovami.Web         # run the app locally (port 5337, data in ./data)
dotnet test tests/Trovami.Tests              # unit tests
docker compose up --build                    # build and run the container (host port 8337)
dotnet ef migrations add <Name> --project src/Trovami.Core --startup-project src/Trovami.Web --output-dir Data/Migrations
cd android && ./gradlew assembleDebug        # build the Android app
```

## Workflow

When the user gives feedback or requests changes in a back-and-forth (bug reports, tweaks, "also do X"), don't implement each one immediately as it comes in. Acknowledge it, queue it, and keep gathering. Only start making changes once the user explicitly says "go" (or an equally unambiguous confirmation). Purely informational questions can still be answered right away.

## Git workflow

Never run `git commit` or `git push` unless the user explicitly asks for it in that turn. A commit approval does not imply a push approval, and approval from an earlier turn does not carry forward to later changes. Every change starts on its own branch off an up-to-date main; after a merge, pull main, delete merged branches (`-d`) and `git fetch --prune`. Commit as `geeForceOne <1780268+geeForceOne@users.noreply.github.com>`, never with a real name or Gmail. Write commit messages that explain why.

Nothing secret is ever committed: databases, Data Protection keys, backups, photos, `.env`, local settings, the Android keystore and its passwords. Check staged files before every commit.

## Architecture

Latest .NET (currently .NET 10, `net10.0`), Blazor Server, `.slnx` solution, plus a native Android app in `android/`.

- **`src/Trovami.Core`** (class library, no dependency on the web UI): domain models, EF Core persistence, households and membership, the location tree, items, tags, sync logic, AI naming, photo processing, backup/restore, email sending. Every query is scoped by a household the user belongs to. Long-running methods take an `IProgress<string>?` and a `CancellationToken`; no console or UI I/O in Core.
- **`src/Trovami.Web`** (Blazor Server, interactive server render mode, prerendering off): UI, Identity, hosting, the hosted service that drives the scheduler, and the phone API.
- **Phone API.** Minimal APIs under `/api/v1/` in Trovami.Web, authenticated with device tokens (`Authorization: Bearer`), versioned so older app versions keep working. Endpoints: pairing and sign-in, pull changes since a cursor (households, locations, tags), push items and tag links, upload photos, latest app version.
- **Scheduler.** A built-in hosted service (no Hangfire/Quartz) wakes every minute and runs due jobs: automatic backups, cleanup of expired invites and pairing codes. AI naming runs from a background queue as soon as a photo arrives, and retries pending items after a restart.
- **`android/`** Kotlin, Jetpack Compose (Material 3), Room, WorkManager for sync, CameraX for photos and QR scanning, Android NFC (`NDEF_DISCOVERED` for `trovami://t/…`, plus an Android Application Record on written tags so a scan opens Trovami). The phone shrinks photos before upload.

### Hosting and data
- **SQLite** through EF Core. Everything persistent (database, photos, Data Protection keys, backups) lives under one data directory, a Docker **volume** at `/data`, never in the image. Paths come from `Storage__*` environment variables.
- The container listens on 8080; `docker-compose.yml` maps host port 8337.
- A `vX.Y.Z` tag runs GitHub Actions: the image goes to `ghcr.io/geeforceone/trovami` (X.Y.Z / X.Y / latest), and a signed release APK (keystore from Actions secrets) is attached to the GitHub release.

### Authentication
- ASP.NET Core Identity with email and password, in the same SQLite database; the email is the Identity UserName. Keep it structured so external logins can be added later.

### Secrets
- The SMTP password and the households' AI keys are stored encrypted with Data Protection. A value that can no longer be decrypted counts as "not set" and the UI says it needs to be entered again. AI keys are never sent to the browser or the phone.

## Conventions

- **Verify external APIs, don't work from memory.** Check current official docs before coding against a library or service (Claude/OpenAI vision APIs, Android NFC, CameraX, image libraries). Model IDs and SDK shapes change.
- **Shared dialogs.** Reusable, awaitable `ConfirmDialog`, `PromptDialog` and `Toast` components in `Components/Shared/`. Clicking outside a dialog never closes it; Cancel (or Escape where offered) does.
- **Hand-written CSS design system** in `wwwroot/css/app.css`, no component library. Dark-first with a light theme. Responsive at desktop, tablet and phone widths. Every page uses the full page width (`--page-max`); only small controls size to their content.
- **Logo.** Animated like LaLira's and Recall's: the header logo plays a motion on hover, the sign-in logo on load, the busy indicator loops. Small sizes get thicker lines and the favicon its own simplified drawing. Nothing moves under `prefers-reduced-motion`.
- **Dates and times are entered in the region format**: use `DateField` / `TimeField`, never a bare `<input type="date">` or `type="time"`.
- **Table cells are never `display: flex`** (it breaks the row lines); put a `<div class="row-actions">` inside the cell.
- **Migrations are never regenerated or edited once they exist**, not even before the first release: the user's local `./data` database records them by ID. A schema change is always a new migration.
- **Gotcha:** numbers written into SVG attributes must use the invariant culture.
- **Gotcha:** a component callback passed as a plain `Func<...>` (not EventCallback) doesn't re-render the parent; call StateHasChanged after it changes state.
- **Gotcha:** don't name a component parameter `Assets`; it collides with `ComponentBase.Assets` in .NET 10.
- **Gotcha:** Blazor renders a bool attribute value `true` as an empty attribute and drops it for `false`; write `aria-pressed="@(x ? "true" : "false")"` (same for aria-expanded and aria-checked).

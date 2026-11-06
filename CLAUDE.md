# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Trovami ("find me") is a multi-user, Docker-hosted household inventory. Things are stored all over a place (a house, a cellar, a holiday flat); Trovami records where each one is, so it can be found again. It has two clients: a Blazor web app for organizing and finding things, and an Android app for adding things fast. Its user handling, backups and release setup came from the sibling apps LaLira and Recall; everything needed now lives in this repository, so work from its own code.

- **Simplicity is key.** Adding must be fast: scan a tag, take a photo, save, done. Prefer inline, fast flows over dialogs and extra steps, in both apps.
- **Households.** All locations and items belong to a household. Any user can create households and invite others into them by email: an address with an account gets a household invite to accept, a new address gets an account invite (the admin can turn this off, so that only the admin creates accounts). A user can be in several households and switches between them. The creator is the household's **owner** (manages members, the AI key, can delete the household); members see and edit everything in it.
- **Locations.** A tree of any depth: House → Floor → Room → Closet → Drawer, House → Cellar → Rack → Box. A location has a name, a parent, a **kind** from a fixed list (house, apartment, floor, room, cellar, garage, attic, closet, cabinet, shelf, rack, drawer, box, bag, other) that only picks its icon, and optional notes, photo, color (a fixed palette), short label (what's written on the box) and keywords. There are no location types or custom attributes on purpose: an address or opening times go in the notes. Moving a location moves everything under it. A location can only be deleted once it's empty, or by moving its contents to its parent.
- **Items.** A name, a photo, optional notes and a quantity (default 1), at exactly one location, or in the household's **Inbox** when it has none. Items are moved by picking a new location inline. The item page's photo can be cropped inline (drag a free-form rectangle, web only); the crop replaces the stored photo, so it can't be undone. Ctrl+C opens the cropper, except while typing, with text selected or in a dialog, and never applies a crop. The Left and Right arrow keys go to the previous and next item, except while typing, in a dialog or while cropping.
- **Keywords.** Words on an item that Find also matches. An item that arrives at a location (added, synced from the phone, moved there) gets a copy of the keywords of that location and all above it, on the server. The copies stay when it moves away, can be removed by hand, and adding a keyword to a location doesn't change the items already there. The AI adds its own from the photo once; after that, or after a person edits them, it leaves them alone. When it isn't sure what an item is, it adds the keyword "unsure"; the item stays where it is. In the web app keywords save as they change, without the form's Save. Every item added also gets the keyword "new", on the server, until someone takes it off or the household's days have passed (owner's setting, default 30, can be off); taking it off doesn't count as a person's edit. "new" and "unsure" don't count against the keyword limit and aren't offered to the AI. Typing a keyword suggests the household's existing ones. A keyword can have a color (the location palette) and a pin, set on its page: colors show on its chips, pinned ones show in the menu below Inbox and stay without items. A keyword's page lists its items with all their keywords, each removable right there. The AI reuses the household's keywords (the 500 most used are sent with each photo) and adds only few that help find the thing (up to 5, clothes 10; no filler, nothing already in the name). On the All keywords page the owner can have the AI find similar keywords, on demand: groups of the same word in other forms or languages, synonyms, or close relatives under a broader word (marked, since searching for the merged words stops finding those items). Each group is a card: click a word to leave it out, star one or type the name to merge into, then Merge, Dismiss or Merge all. The suggestions aren't saved.
- **AI naming.** An item added with a photo and no name gets its name from the AI, on the server, after the photo arrives (so photos taken offline are named on sync). The key is per household: the owner picks Claude or ChatGPT and enters the key in the household's settings. Without a key, or when the call fails, the item is "Unnamed item" and is named by hand. A name typed by a person is never overwritten, with one exception below. In the UI it's called "AI", with no vendor names outside the settings.
- **AI language.** The owner picks a language per household from a fixed list (`AiLanguages`), which also holds each language's words for "new" and "unsure"; picking one renames those two on all items and doesn't count as a person's edit. The AI writes names and keywords in it and reuses only keywords already in it. The app's own pages stay in US English. The AI never writes the German ß, always "ss" (told so, and enforced on its replies); what a person types stays as typed. "Translate everything" is an owner's action with a warning: item names (typed ones too, the one exception), item notes, and item and location keywords, not location names, notes or labels. Text-only batches, each keyword once so colors and pins stay; it runs in the background on the household's key, an admin can make a backup first, it can't be undone, and what fails stays as it was. Its progress is kept in memory: after a restart it's gone, and running it again leaves what's translated alone.
- **Tags.** A tag is a random code linked to one location; it can be re-linked. **NFC tags** are linked on the phone (scan an unknown tag → pick a location or create one under a chosen parent; this works offline). **QR labels** are printed from the web app for chosen locations (a print sheet, no PDF library) and scanned with the phone app's camera. Both carry the same `trovami://t/<code>` URI.
- **Inbox.** Items added on the phone without a tag land in the Inbox. The web app shows it as a photo grid where each item gets its location inline (several at once by selecting them).
- **Find.** One search over the household (item names, notes, location names and labels) shows each hit with its full path ("House › Cellar › Rack 2 › Blue box"). Finding things is the point of the app, so the search sits at the top of every page.
- **Responsive.** Designed mainly for PC use but must work well on tablets and phones. No PWA.

### Android app
- **Mainly for adding things fast.** Scanning a registered tag opens the add screen, even when the app is closed: camera → shutter → saved to that location, and the camera is ready for the next item. The camera's picture is 3:4 like the photo, between the bars with the buttons, and shrinks when the details and keyboard need the room. Name and notes are optional. Without a tag, items go to the Inbox.
- **Find mode.** A button on the camera switches to find mode and back: search items (and the phone's own locations), browse the locations from the top (each shows the locations right inside it, then all items in it and below; the path in its header goes up; the tree is local, so browsing works offline, only the items need the server), open the Inbox (below the locations, less prominent) or a location's contents (scanning a tag or label in this mode shows what's in it), and per item rename, edit keywords (saved as they change), move (scan the destination's tag or label, or pick it) and delete. It's online only: it calls the server directly and keeps no items on the phone. No location management, tag re-linking, household or admin settings. A tag that opens or brings back the app always means adding.
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
- **Phone API.** Minimal APIs under `/api/v1/` in Trovami.Web, authenticated with device tokens (`Authorization: Bearer`), versioned so older app versions keep working. Endpoints: pairing and sign-in, pull changes since a cursor (households, locations, tags), push items and tag links, upload photos, latest app version, and find mode's online calls (search, a location's items, the Inbox, keyword suggestions, an item, its photo, rename, keywords, move, delete).
- **Scheduler.** A built-in hosted service (no Hangfire/Quartz) wakes every minute and runs due jobs: automatic backups, cleanup of expired invites and pairing codes. AI naming runs from a background queue as soon as a photo arrives, and retries pending items after a restart.
- **`android/`** Kotlin, Jetpack Compose (Material 3), Room, WorkManager for sync, CameraX for photos and QR scanning, Android NFC (`NDEF_DISCOVERED` for `trovami://t/…`, plus an Android Application Record on written tags so a scan opens Trovami). The phone shrinks photos before upload.

### Hosting and data
- **SQLite** through EF Core. Everything persistent (database, photos, Data Protection keys, backups) lives under one data directory, a Docker **volume** at `/data`, never in the image. Paths come from `Storage__*` environment variables.
- The container listens on 8080; `docker-compose.yml` maps host port 8337.
- A `vX.Y.Z` tag runs GitHub Actions: `docker-publish.yml` sends the image to `ghcr.io/geeforceone/trovami` (X.Y.Z / X.Y / latest), and `android-release.yml` creates the GitHub release from that version's `CHANGELOG.md` section with the signed APK attached (keystore from Actions secrets; a manual run only builds the APK as an artifact).

### Authentication
- ASP.NET Core Identity with email and password, in the same SQLite database; the email is the Identity UserName. Keep it structured so external logins can be added later.

### Secrets
- The SMTP password and the households' AI keys are stored encrypted with Data Protection. A value that can no longer be decrypted counts as "not set" and the UI says it needs to be entered again. AI keys are never sent to the browser or the phone.

## Conventions

- **Verify external APIs, don't work from memory.** Check current official docs before coding against a library or service (Claude/OpenAI vision APIs, Android NFC, CameraX, image libraries). Model IDs and SDK shapes change.
- **Shared dialogs.** Reusable, awaitable `ConfirmDialog`, `PromptDialog` and `Toast` components in `Components/Shared/`. Clicking outside a dialog never closes it; Cancel (or Escape where offered) does.
- **Hand-written CSS design system** in `wwwroot/css/app.css`, no component library. Dark-first with a light theme. Responsive at desktop, tablet and phone widths. Every page uses the full window width, with no maximum; only small controls size to their content.
- **Logo.** Animated like LaLira's and Recall's: the header logo plays a motion on hover, the sign-in logo on load, the busy indicator loops. Small sizes get thicker lines and the favicon its own simplified drawing. Nothing moves under `prefers-reduced-motion`.
- **Dates and times are entered in the region format**: use `DateField` / `TimeField`, never a bare `<input type="date">` or `type="time"`.
- **Table cells are never `display: flex`** (it breaks the row lines); put a `<div class="row-actions">` inside the cell.
- **IDs are GUIDs, addresses use short codes.** Locations and items keep GUID ids (the phone creates them offline), but the web app links to them by their six-character `ShortCode` (`/l/K7F3QZ`, `/i/M2X9TA`), given by the server on creation. Old `/l/<guid>` and `/i/<guid>` links redirect.
- **Migrations are never regenerated or edited once they exist**, not even before the first release: the user's local `./data` database records them by ID. A schema change is always a new migration.
- **Gotcha:** numbers written into SVG attributes must use the invariant culture.
- **Gotcha:** a component callback passed as a plain `Func<...>` (not EventCallback) doesn't re-render the parent; call StateHasChanged after it changes state.
- **Gotcha:** don't name a component parameter `Assets`; it collides with `ComponentBase.Assets` in .NET 10.
- **Gotcha:** the release APK is minified by R8, so a debug build proves little about it: anything created by reflection can break in release only (ML Kit's registrars crashed the QR scanner). Test Android changes on a release build before a release.
- **Gotcha:** the app's `targetSdk` stays at 36 on purpose. At 37, Android 17 blocks the local network unless `ACCESS_LOCAL_NETWORK` is granted at runtime, which breaks pairing and sync with a server at home (the connection just times out). Don't raise it in a dependency update.
- **Gotcha:** Blazor renders a bool attribute value `true` as an empty attribute and drops it for `false`; write `aria-pressed="@(x ? "true" : "false")"` (same for aria-expanded and aria-checked).

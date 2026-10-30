# Trovami v1 plan

Decisions are in `CLAUDE.md`; this file covers the order of work and the points still open.

**Status (2026-10-30):** milestones 1–8 are built (web app and phone API), and milestone 9 (Android app in `android/`: application ID `io.github.geeforceone.trovami`, Android 10 and newer) is tested on the emulator; its NFC flows still need a check on a real phone. Milestone 10 is next. The open points below were taken with their defaults. The repository is private on GitHub; its license and visibility (point 5) are still open.

## Milestones

| # | Milestone | Contents | Needs |
|---|-----------|----------|-------|
| 1 | Skeleton | Repo, `Trovami.slnx`, Core/Web/Tests, Dockerfile, compose, tag workflow (image only), CHANGELOG + What's new + release notes page, logo | — |
| 2 | Users | Taken over from LaLira: setup, sign-in, invites, forgot password, account requests, Users page, Admin settings + SMTP, Profile, themes | 1 |
| 3 | Households | Create, switch, invite by email (existing user → accept; new → account invite), owner/member, leave, delete | 2 |
| 4 | Locations + items | Tree page (expand/collapse, inline add/rename/move), location details (kind, notes, photo, color, label), items with photo upload + resize, Inbox grid, search with paths | 3 |
| 5 | AI naming | Household AI settings (Claude/ChatGPT, encrypted key, model), background naming queue, retry, "Unnamed item" fallback | 4 |
| 6 | Tags + QR labels | Tag codes, link/unlink in the web app, printable QR label sheet | 4 |
| 7 | Backup | LaLira's backup/restore with photos, before-update backup, scheduler | 4 |
| 8 | Phone API | Device tokens, QR pairing + password sign-in, sync pull/push, photo upload, devices list in Profile | 4, 6 |
| 9 | Android app | Pairing/sign-in, sync, NFC scan → add screen (app closed too), link unknown tag, QR scan, camera loop, Inbox adds, offline queue, update check | 8, Android Studio |
| 10 | Release 1.0.0 | Signed APK in the tag workflow, homepage widget, README with demo data, test handover | all |

Milestones 1–8 need only .NET and can start right away. Milestone 9 needs Android Studio (JDK 17+, SDK, an emulator) on this PC, and an NFC phone for the final check.

## Open points (my defaults, to confirm)

1. **Item fields:** name, photo, notes, quantity. No categories, prices, purchase dates or extra photos in v1 (easy to add later).
2. **Find on the phone:** the phone app only adds in v1. A read-only search on the phone ("where is the drill?" while standing in the cellar) would be useful; I'd put it right after 1.0.
3. **Phone add screen:** after a tag scan the camera opens directly; the shutter saves, and the camera stays open for the next item at the same location. Name and notes only through an optional "Details" button.
4. **Deleting:** items are deleted with a confirmation; there's no trash/archive in v1.
5. **Repo:** github.com/geeForceOne/Trovami, MIT license, public like LaLira? (decide before the first push)
6. **Ports:** 5337 local, 8337 host (LaLira uses 5336/8336).
7. **Email:** Brevo SMTP like the others, sender trovami@gathome.net, host trovami.gathome.net.

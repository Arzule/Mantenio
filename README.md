# Mantenio

Mantenio replaces the paper ledger of a condo's maintenance fees. Residents see what they owe, upload the screenshot
of their transfer and get a PDF receipt. Admins and the gatehouse review payments and register vouchers
brought in person; admins also get the delinquency report for any date. Everything is audited.

It is built for one condo per installation: name, colors, logo, fee and receipt series come from a **condo profile**
(`condos/<slug>/condo.json`). This repository ships the `demo` profile; a real condo keeps its own profile and
deployment settings in a private copy. It is in a pilot with a 180-apartment condo.

---

## 1. What the app does

**Residents**
- Create an account with a single-use **invitation code** (6 digits, valid 24 h) and confirm the email with a code.
- See the **account statement** of their apartment: every monthly charge, its due date and state (pending, partial,
  paid, overdue), total debt and credit. Everyone in the apartment sees the same statement.
- See the condo **bank accounts** (bank, holder, account number, CCI) with copy buttons.
  A reminder asks to pay each month with its own transfer (each operation number can be registered only once).
- **Upload a payment proof**: the photo first; the app reads the amount, date and operation number from it
  (Azure AI Vision) and pre-fills the form; the resident checks it and picks the origin bank.
- Get notices (in-app bell + push on Android): payment approved/rejected/voided, fee changes, bank account changes
  (must be confirmed), monthly reminder. Receive the **receipt PDF by email**. Each approved payment shows the months it covers
  and any credit it left (one transfer for several months is applied oldest first; the rest pays the next months).
- Change their password; sign in on several devices at once.

**Admins** (up to 10, email 2FA on every new sign-in; each admin must have their apartment)
- Review proofs: **approve** (confirming the real amount) or **reject** (with a reason); **void** an approved payment.
- **Register a voucher brought in person**: photo (camera/gallery), tower and number,
  "Recibí de", confirmation step, approved at once; then **send the receipt by WhatsApp** or download it.
- Manage apartments (add, CSV import, freeze/unfreeze, special fees), users (deactivate, reactivate, move),
  invitations, bank accounts, fee schedule and due rule.
- Reports: **delinquency at any date** (screen, Excel, PDF), **monthly folio** PDF, **period audit report** (Excel for
  any range of months up to 10 years, e.g. the previous board's term: charges with what was paid and what is still
  owed, every payment in any state, rejected and voided ones apart, totals per tower), dashboard and **audit log**
  (filters by date, entity, apartment, person and source: residents, admins, the gatehouse, all people, or the
  scheduled jobs).
- **Historical payments**: load payments from before the app (paper records) with their date, amount and, when known,
  payer, bank, operation number and paper receipt number. The photo of the voucher or receipt can be attached later
  (read automatically) and replaced if it was wrong. They are kept for the history and the reports, and **do not
  change the debt yet** (how the old debt is loaded is decided with the condo's spreadsheet).

**Superadmin** (the founder account: the board president's account, on the condo's own email)
- Everything an admin does, plus the late-fee percentage, the board emails, changing their own apartment, generating a month's charges by hand and importing the
  opening debt CSV. Gets the database-down alert by email and push.

**Gatehouse / portería** (up to 20, email 2FA; never has an apartment)
- Approve and reject pending proofs, register vouchers brought in person (approved at once), void approved payments
  with a reason (also its own registrations: a guard may be alone on duty) and see the history of any payment.
- Create invitation codes for residents and gatehouse staff. No reports, users, settings or audit log.

**Rules for everyone who reviews**
- Nobody approves or rejects a proof they sent; an admin never reviews, registers or voids payments of their own
  apartment (another admin or the gatehouse does), and an admin without an apartment cannot act on payments (the app
  says so instead of opening the dialog).
- An admin's apartment is changed by another admin; only the superadmin changes their own. A reason is always required.

**Automatic (scheduled jobs, Lima time)**
- Day 1, 00:05: monthly charges for every apartment (skips frozen ones, applies credit).
- Daily 00:30: late fees on overdue maintenance charges (only if the % is above 0).
- Day 15, 09:00: reminder to apartments with overdue debt.
- Daily 03:30: cleanup of expired refresh tokens and invitation codes, and of files past the retention period
  (proof photos and receipt PDFs older than 10 years). Daily 03:00: database backup to R2.

---

## 2. Architecture

```
 Android app (.NET MAUI Blazor Hybrid)      Web app (Blazor WebAssembly, Cloudflare Pages)
            \                                   /
             \---- Mantenio.UI (shared Razor pages, MudBlazor) ----/
                              |
                     Mantenio.Client (typed HTTP clients, JWT + refresh)
                              | HTTPS / JSON
                              v
                 Mantenio.Api (ASP.NET Core Minimal APIs, Azure Container Apps)
        |            |              |               |              |             |
   PostgreSQL     Cloudflare R2   Azure Comm.    Firebase FCM   Azure AI      App Insights
   (Aiven)        (images, PDFs,  Services       (push)         Vision        (telemetry)
                   backups)       (email)                       (voucher OCR)
                              ^
        Container Apps Jobs (same API image: `Mantenio.Api job <name>`) + backup image
```

**Projects**
- `Mantenio.Domain` – pure business rules (no EF, no HTTP): entities, `Period`, FIFO allocator, fee resolver,
  debt calculator, late-fee policy, receipt numbers and amounts in words, voucher text reader. Unit-tested.
- `Mantenio.Shared` – DTOs, error codes, claim names and the **condo profile** (`condos/<slug>/condo.json`,
  embedded at build time, selected with `-p:Condo=<slug>`; `demo` by default).
- `Mantenio.Api` – feature folders (`Features/<Feature>/…Endpoints.cs`) and `Infrastructure/` (auth, email, push,
  storage, persistence, rate limiting, vision). The same binary runs setup commands and scheduled jobs.
- `Mantenio.Client` – `PublicApi`/`MantenioApi` typed clients, `AuthSession` (access token in memory, refresh token
  in secure storage), `BearerTokenHandler` (renews on 401).
- `Mantenio.UI` – every page and component, shared by both hosts; platform needs go through interfaces
  (`IImagePicker`, `IFileOpener`, `IPushRegistration`, `ITokenStore`).
- `Mantenio.Web` / `Mantenio.Mobile` – the two hosts with their platform implementations.
- `Mantenio.AppHost` / `Mantenio.ServiceDefaults` – .NET Aspire for local development and shared telemetry.

**Where everything runs** (accounts and what they host)
| Service | Hosts |
|---|---|
| Aiven (Free plan) | PostgreSQL 18, one database per condo |
| Azure | API (Container App), scheduled jobs (Container Apps Jobs), email (Communication Services), Application Insights, AI Vision (F0, voucher reading) |
| Cloudflare | Pages (web), R2 (proof images, receipt PDFs, founder push tokens; a second bucket for nightly dumps, 30 days) |
| Firebase / Google Cloud | Push notifications (FCM HTTP v1); the Android API key is restricted to the app |
| GitHub | Code, CI (build, tests, gitleaks), API and backup images (GHCR) |

**Key design decisions**
- **One instance per condo**: no multi-tenant code; branding and defaults come from the condo profile.
- **Ledger, not just receipts**: charges, payment proofs and allocations (which part of a payment paid which
  charge). Debt = charges − active allocations; credit = approved money not allocated. Nothing is hard-deleted:
  void sets `Status`/`ReversedAt`.
- **Every ledger or configuration change writes an `AuditEvent`** in the same transaction (actor, source,
  old/new JSON, reason, apartment, charge).
- **Times** are stored in UTC and shown in America/Lima; **money** is `numeric(10,2)` PEN; months are `int yyyymm`.
- **Push outbox**: notices are saved with the change; `PushDispatcher` sends them after commit and every 5 min.
- **Files**: only proof images, receipt PDFs and backups are stored; reports are generated on demand. Photos and
  receipt PDFs are kept **10 years** (by payment date, `fileRetentionMonths`), which covers two 5-year board terms
  and stays within the R2 free tier (about 6 GB for 180 apartments); ledger rows are kept forever.

---

## 3. Security and validations

**Authentication and sessions**
- Passwords: at least 8 characters with a digit (ASP.NET Identity). 5 wrong attempts lock the account 15 min.
- Admins and the gatehouse: a 6-digit code by email on every sign-in (2FA). New accounts confirm their email with a code.
- Access token: JWT, 15 minutes, carries the session id (`sid`). Refresh token: random 256-bit, stored hashed,
  **rotated on every use**; replaying an old one revokes the whole session (theft detection).
- Session length: 12 h, or 30 days (admins, gatehouse) / 90 days (residents) with "keep me signed in".
- Changing or resetting the password, or deactivating a user, revokes every other session **immediately**
  (`SessionGuard` rejects access tokens of revoked sessions; the app checks every minute).
- Invitation codes: 6 digits, single use, 24 h, stored as HMAC; at most 10 active admins and 20 gatehouse accounts (advisory lock).

**Authorization**
- **Deny by default**: every endpoint needs a signed-in user unless explicitly anonymous; a test lists the allowed
  anonymous routes and requires the admin policy on all of `/api/admin`.
- Policies: signed in (and no pending temporary password), `StaffOnly` (admins and gatehouse: the payment queue,
  in-person registration, void, payment history, invitation codes), `AdminOnly`, `FounderOnly`.
- **IDOR protection**: residents only reach their own apartment; anything else answers **404** (not 403).
- Admins cannot review, register or void payments of **their own apartment**, and need an apartment to act on
  payments; nobody approves or rejects a proof **they sent**.

**Input and business validation**
- Payment proof: amount 0.01–99,999; date not in the future and at most a year old; bank and operation number
  required. The operation number is normalized (letters and digits, upper case) and **unique per bank** among
  proofs that are not rejected (409 `duplicate_operation_number`).
- Images: max 10 MB; the server decodes it (must be a real image), fixes the orientation, strips metadata
  (EXIF/GPS) and re-encodes JPEG ≤ 1600 px. Max ~12 MB per request; 30 uploads per hour per resident and 300 per
  admin (historical photos).
- Historical payments: amount 0.01–99,999, date not in the future and within the 10-year retention, optional texts
  within their lengths; an operation number, when given, is still unique per bank. Only admins, never for their own
  apartment; every change is audited with the old and new values.
- Concurrency: proofs carry their `RowVersion` (PostgreSQL `xmin`); a stale approve/reject answers 409.
- Receipts: gap-free numbers `SERIES-NNNNNN` taken with `UPDATE … RETURNING` inside the approval transaction.
- Opening debt CSV and apartments CSV: all rows or none, with line-numbered errors and a preview.
- Bank accounts: CCI exactly 20 digits; account number digits and dashes; every change notifies all users with a
  notice they must confirm (protection against "the account changed" fraud). Never by email.
- Rate limits (per IP): sign-in 10/min, email codes 5 per 15 min, invitation checks 5/min, refresh 30/min.

**Platform**
- Security headers on the API (`nosniff`, `X-Frame-Options: DENY`, CSP `default-src 'none'`, HSTS) and a CSP on
  the web app (`_headers`, generated on deploy, with the import map hash).
- No secrets in the repository: `secrets.json` (git-ignored) feeds the deploy scripts, which pass them to Container
  Apps as secrets; gitleaks scans the whole history in CI.
- Database down: the API answers 503 `database_unavailable` and alerts the founder (email + push, at most hourly).
  The founder's push tokens are copied to R2 because the database is the thing that is down.

**Tamper protection**
- The ledger is append-only in the database too: triggers reject deleting ledger rows and editing or deleting audit
  events. The app runs with a login that does not own the tables (it cannot drop them or disable the triggers); only the
  migrations use the owner login.
- On day 1 the board members chosen by the founder get the previous month's folio and period report by email, with the
  **audit fingerprint** (SHA-256 of every event up to the close; the period report shows it too, so copies can be
  compared). Backups should live in an R2 bucket with object lock, ideally in an account another board member controls.

**Error handling**: the API returns ProblemDetails with a `code` (`Mantenio.Shared.Errors.ErrorCodes`); the UI
shows the Spanish text `Error_<code>` from `UiStrings.resx` (a test fails if any code has no text).

---

## 4. Problems found during development and how they were fixed

| Problem | Cause | Fix |
|---|---|---|
| Container Apps Jobs could not be created | The API environment is "Express", which has no jobs | A separate Workload Profiles environment for the jobs |
| Jobs failed to start | They referenced an image tag that did not exist | Jobs use the image of the running API |
| Backup image source missing from git | The Visual Studio `.gitignore` template has `Backup*/`, case-insensitive on Windows | `!deploy/backup/` exception; files committed |
| Tests failing in a random order | Test classes shared a database; moving the fake clock broke 2FA codes of other tests | Tests that move the clock or touch every apartment got their own API instance; assertions filter by the test's own data |
| A commit with a failing test was pushed | Commit ran in parallel with the tests | Rule: run the tests, then commit |
| 6-digit code not found in test emails | Python on Windows wrote CRLF into `.resx` | Always write with `newline='\n'` |
| `Condo.props` not matched by `build_android.py` | A `\b` in a Python string became a backspace character | Fixed the bytes; scripts use raw strings |
| Accents broken in the keystore certificate | Windows mangled "José" | ASCII certificate name |
| Page bottoms hidden under the phone bottom bar | Scoped CSS `::deep` does not reach a MudBlazor root element | The layout is wrapped in a plain `<div class="layout-root">` |
| MudBlazor 9 build errors | `PanelClass` renamed | `TabPanelsClass` |
| App bar under the Android status bar | Android 15 draws edge to edge | `SafeAreaEdges="Container"` and the bars painted with the theme color |
| Camera not available in the Android web view | The HTML file input cannot open the camera there | `IImagePicker` with MAUI `MediaPicker` |
| Sessions kept working after a password change | Revoking refresh tokens left the 15-minute access tokens valid | Session id in the token + `SessionGuard` check + periodic client check |
| Apartment picker only showed tower A | The search endpoint returned the first 20 apartments | Two comboboxes (tower, number) loaded from all apartments |
| Firebase key could not be created from the CLI | Blocked by the environment's permission rules | The service account key was downloaded by hand (kept outside git) |
| Cognitive Services resource creation failed | Provider not registered in the subscription | `az provider register --namespace Microsoft.CognitiveServices` |
| The file retention setting did nothing | `fileRetentionMonths` existed but no job used it | The cleanup job deletes expired photos and receipt PDFs (10 years) |
| Several historical payments without operation number clashed | The duplicate index treated empty operation numbers as equal | The unique index ignores empty operation numbers |

---

## 5. Working with the code

```
dotnet run --project src/Mantenio.AppHost       # local stack: PostgreSQL 18 + Mailpit + API (:5013) + web (:5024)
                                                # dev founder: founder@mantenio.local / Mantenio2026 (codes in Mailpit)
dotnet build Mantenio.Ci.slnf                   # everything except Android
dotnet test --solution Mantenio.Ci.slnf         # all tests (Docker needed for the API tests)
dotnet ef migrations add <Name> -p src/Mantenio.Api -o Infrastructure/Persistence/Migrations
dotnet build src/Mantenio.Mobile -f net10.0-android -t:Install   # install on a USB phone (Debug calls localhost:5013;
                                                                 # run "adb reverse tcp:5013 tcp:5013" first)
```

**Deploy** (from a private copy that adds the condo profile, `Condo.props` and `deploy/deployment.json`, with
`secrets.json` filled in and `az` signed in):
`python deploy/deploy_api.py` (image + Container App; migrations run at startup), `python deploy/deploy_web.py`,
`python deploy/deploy_jobs.py`, `python deploy/build_android.py` (signed AAB/APK; keystore outside the repo),
`python deploy/restore_backup.py` (restore drill).

**Conventions**: code, identifiers, routes and comments in English; user-facing text in Spanish only in `.resx`.
Business rules go to `Mantenio.Domain` with unit tests. Multi-step writes use `db.InTransactionAsync`. New
anonymous endpoints must be added to `AuthorizationCoverageTests`.

**Planned**: keep the secrets in Azure Key Vault (read by reference with a managed identity) instead of passing
them on each deploy.

---

## 6. Files

Generated folders (`bin/`, `obj/`), EF Core migrations (`src/Mantenio.Api/Infrastructure/Persistence/Migrations/`,
generated by `dotnet ef`), images and fonts are not listed.

**Repository root**

| File | What it does |
|---|---|
| `.editorconfig` | Code style and analyzer settings (the few analyzer exceptions live here). |
| `.gitattributes` | Line-ending rules for git. |
| `.gitignore` | Ignored files: build output, secrets.json, Firebase keys, *.md, private condo profiles and their reference documents. |
| `.gitleaks.toml` | gitleaks configuration for the CI secret scan (default rules plus a narrow exception). |
| `Directory.Build.props` | Settings shared by every project: .NET 10, nullable, warnings as errors, analyzers, condo profile selection. |
| `Directory.Packages.props` | Central package versions (with transitive pinning). |
| `Mantenio.Ci.slnf` | Solution filter used by CI: everything except the Android app (faster, no Android SDK needed). |
| `Mantenio.slnx` | Solution with every project. |
| `dotnet-tools.json` | Local .NET tools (dotnet-ef for migrations). |
| `global.json` | .NET SDK version and the Microsoft.Testing.Platform test runner. |

**`.github/workflows/`**

| File | What it does |
|---|---|
| `ci.yml` | GitHub Actions: build, all tests (PostgreSQL via Testcontainers) and the gitleaks secret scan on every push. |

**`condos/demo/`**

| File | What it does |
|---|---|
| `apartments.csv` | Demo condo apartments (tower,number): 9 towers, 180 apartments. |
| `condo.json` | Demo condo profile: name, colors, fee, receipt series, Android id, file retention. |

**`deploy/`**

| File | What it does |
|---|---|
| `aiven_app_user.py` | Creates (or rotates the password of) the dedicated PostgreSQL login the API uses on Aiven and makes it the owner of the condo database, so EF Core migrations can create tables. Runs psql from the postgres:18 image. |
| `aiven_runtime_user.py` | Creates (or rotates the password of) the login the API and its jobs run with: it can read and write the app data, but it does not own the tables, so it cannot drop or alter them, disable triggers, delete ledger rows or edit the audit log. Migrations keep using |
| `android_keystore.py` | Creates the release signing keystore of a condo's Android app, once, OUTSIDE the repo: %USERPROFILE%/.mantenio/-release.keystore The random password goes to secrets.json (AndroidKeystorePassword) and the path to AndroidKeystorePath. |
| `build_android.py` | Builds the signed release of the condo's Android app: an AAB (Google Play) and an APK (direct install, Firebase App Distribution). Output: artifacts/android/. |
| `common.py` | Shared by the deploy scripts: configuration, secrets and the settings the API and its jobs run with. |
| `deploy_api.py` | Builds the API container image, pushes it to GHCR and creates/updates the Azure Container App. |
| `deploy_jobs.py` | Creates/updates the scheduled jobs (Azure Container Apps Jobs, cron in UTC; Lima = UTC-5, no DST): |
| `deploy_web.py` | Publishes the Blazor WebAssembly app and deploys it to Cloudflare Pages (free, global CDN). |
| `deployment.example.json` | Template of deployment.json (Azure, Cloudflare and storage resource names; no secrets). |
| `restore_backup.py` | Restore drill (or real restore): downloads the newest nightly backup from the R2 backups bucket and restores it into a throwaway local PostgreSQL 18 container, then prints row counts. The container is left running when --keep is given (connect with: docker exe |
| `setup_database.py` | One-time setup of a condo database: migrations, apartments from the profile, founder admin. |

**`deploy/backup/`**

| File | What it does |
|---|---|
| `Dockerfile` | Image of the nightly backup job: PostgreSQL 18 client tools plus the AWS CLI (for R2). |
| `backup.sh` | Runs inside the backup job: pg_dump of the database, uploaded to the R2 backups bucket. |

**`src/Mantenio.Api/Features/Account/`**

| File | What it does |
|---|---|
| `AccountEndpoints.cs` | The signed-in user's own account: profile and password change. |

**`src/Mantenio.Api/Features/Apartments/`**

| File | What it does |
|---|---|
| `ApartmentAdminEndpoints.cs` | Admin endpoints for towers, apartments, the CSV import and apartment freezes. |
| `ApartmentEndpoints.cs` | Apartment endpoints for any signed-in user (and the public options list). |
| `ApartmentImport.cs` | Adds towers and apartments from a list (CSV tower,number). Existing apartments are skipped, never changed, so importing the same file twice is harmless. Used by the admin import and by the seeding command. |

**`src/Mantenio.Api/Features/Auth/`**

| File | What it does |
|---|---|
| `AuthEndpoints.cs` | Anonymous sign-in endpoints: login, 2FA, email confirmation, refresh, logout and password reset. |
| `SignInFlow.cs` | What happens once the password (or invitation) step succeeded: confirm the email first if needed, then the admin second factor, then the session. |

**`src/Mantenio.Api/Features/BankAccounts/`**

| File | What it does |
|---|---|
| `BankAccountEndpoints.cs` | Condo bank accounts. Any change notifies every active user with a notice they must acknowledge (popup at app start; push in Phase 6), because "the account changed" is the classic fraud: never by email. |

**`src/Mantenio.Api/Features/Devices/`**

| File | What it does |
|---|---|
| `DeviceEndpoints.cs` | Phones register their Firebase token after sign-in and remove it on sign-out. |

**`src/Mantenio.Api/Features/Invitations/`**

| File | What it does |
|---|---|
| `AdminSeats.cs` | Seat limits for staff accounts: admins (MaxAdmins) and the gatehouse (MaxGuards). |
| `InvitationEndpoints.cs` | Invitation codes (admins and the gatehouse) and registration with a code (anonymous). |

**`src/Mantenio.Api/Features/Ledger/`**

| File | What it does |
|---|---|
| `LedgerEndpoints.cs` | Payment proofs, their review and the account statement (docs/context.md §4). Approving applies the money FIFO to the apartment's charges and issues a gap-free receipt; voiding reverses it. Every change writes an audit event. |
| `LedgerService.cs` | Ledger operations on top of the pure rules (LedgerAllocator). Callers own the transaction. |
| `OpeningDebtCsv.cs` | Opening debt from the paper ledger: one line per apartment and month, apartmentCode,period,amount (e.g. A-101,202609,180.00; period also as 2026-09; optional header; ; also accepted). All or nothing: any error and nothing is imported. |
| `ReceiptEmails.cs` | Sends the receipt PDF to whoever uploaded the proof or, when an admin registered it for a resident, to the apartment's active users with an email (the admin can also send it by WhatsApp). Failures are logged, never thrown. |
| `ReceiptPdf.cs` | Draws the receipt PDF (QuestPDF) like the condo's paper receipt, with a VOIDED stamp when voided. |

**`src/Mantenio.Api/Features/Notifications/`**

| File | What it does |
|---|---|
| `NotificationEndpoints.cs` | The caller's own notices. A notice of another user answers 404. |

**`src/Mantenio.Api/Features/Reports/`**

| File | What it does |
|---|---|
| `AuditFingerprint.cs` | How many audit events existed at a moment and a SHA-256 over all of them. |
| `DelinquencyService.cs` | Overdue debt on a date, rebuilt from the ledger as it was at the end of that day (Lima): later payments, voids and charges do not change a past report. Frozen apartments are listed (marked) because their debt is still owed. |
| `PeriodReportService.cs` | Builds the period audit report: charges of a range of months with what was paid by the close, every payment and the debt at the close. |
| `ReportEndpoints.cs` | Admin reports (generated on demand, never stored), the admin dashboard and the audit viewer. |
| `ReportFiles.cs` | Builds the report files: delinquency Excel and PDF, monthly folio PDF and the period audit Excel. |

**`src/Mantenio.Api/Features/Settings/`**

| File | What it does |
|---|---|
| `CondoSettingsStore.cs` | The single settings row; created from the condo profile ("billing") the first time it is needed. |
| `SettingsEndpoints.cs` | Fee, due-rule and late-fee settings. |

**`src/Mantenio.Api/Features/Setup/`**

| File | What it does |
|---|---|
| `Seeding.cs` | One-time setup: the condo structure and the founder admin. Both are idempotent. |
| `SetupCommands.cs` | Command-line entry points (`Mantenio.Api <command> --option value`), run once per condo instance: migrate: applies pending migrations. seed-apartments: loads the towers and apartments from the condo profile's apartments.csv. seed-founder --email x --first-name |

**`src/Mantenio.Api/Features/SystemInfo/`**

| File | What it does |
|---|---|
| `SystemInfoEndpoints.cs` | Public system information. |

**`src/Mantenio.Api/Features/Users/`**

| File | What it does |
|---|---|
| `UserAdminEndpoints.cs` | Admin management of accounts: list, deactivate, reactivate and move between apartments. |

**`src/Mantenio.Api/Infrastructure/Auth/`**

| File | What it does |
|---|---|
| `AuthSetup.cs` | Identity, JWT and authorization policies of the API. |
| `InvitationCodeHasher.cs` | Keyed hash (HMAC-SHA256) of invitation codes. A plain hash of a 6-digit code could be reversed by trying all one million values, so the key (derived from the JWT signing key) never leaves the server. |
| `JwtOptions.cs` | Bound from the "Jwt" section. The signing key is a secret (Aspire parameter locally, Container Apps secret in prod). |
| `Roles.cs` | Maps the stored role to the one in the contracts and the audit log. |
| `SessionGuard.cs` | Rejects access tokens whose session was revoked (logout, password change, deactivation) without waiting for the token to expire. A session is alive while its token family has an unrevoked refresh token. Live sessions are cached briefly so most requests skip th |
| `SessionService.cs` | Issues, rotates and revokes refresh-token sessions. |
| `TokenService.cs` | Creates and validates the JWTs: 15-minute access tokens and the challenge tokens of the email-code steps; hashes refresh tokens. |

**`src/Mantenio.Api/Infrastructure/Email/`**

| File | What it does |
|---|---|
| `AccountEmails.cs` | Account emails (all carry a 6-digit code). Texts live in Resources/EmailTexts.resx. |
| `EmailLayout.cs` | HTML version of every email, built from its plain text: a header in the condo's colors with its name, the text as paragraphs (a line with only a 6-digit code is shown large) and a footer. No images, so mail clients that block them still show the branding. |
| `EmailSending.cs` | Email sending: Azure Communication Services in production, SMTP (Mailpit) locally, or log only. |

**`src/Mantenio.Api/Infrastructure/Errors/`**

| File | What it does |
|---|---|
| `ApiProblems.cs` | ProblemDetails responses carrying an explicit ErrorCodes value. |
| `DatabaseUnavailableHandler.cs` | Database down: answers 503 database_unavailable and alerts the founder (email + push) at most once an hour. |
| `ProblemDetailsSetup.cs` | Error responses as ProblemDetails with an error code. |

**`src/Mantenio.Api/Infrastructure/Identity/`**

| File | What it does |
|---|---|
| `AppUser.cs` | An account (resident, admin or gatehouse). The email is also the user name. |
| `RefreshToken.cs` | One refresh token of a session. Every refresh rotates it: the old one is revoked and points to its replacement. All tokens of one sign-in share a FamilyId; reusing a rotated token revokes the family. Only the SHA-256 hash of the token is stored. |

**`src/Mantenio.Api/Infrastructure/Notifications/`**

| File | What it does |
|---|---|
| `NotificationService.cs` | Creates in-app notices (the caller saves them in its own transaction, next to the change and its audit event). Each notice also goes out as a push (see PushDispatcher, after the commit); bank account changes are deliberately never sent by email. |

**`src/Mantenio.Api/Infrastructure/Persistence/`**

| File | What it does |
|---|---|
| `AppDbContext.cs` | Identity user tables (no roles: the role is a column on the user) plus the condo data. |
| `AuditLog.cs` | Audit helpers (entity names, JSON snapshots, source) and InTransactionAsync for multi-step writes. |
| `DesignTimeDbContextFactory.cs` | Used only by `dotnet ef` (migrations). It never connects to a real database. |
| `MigrationConnection.cs` | In production the app runs with a login that cannot drop tables, disable triggers or delete ledger rows; only the migrations use the owner login ("Database:MigrationConnectionString"). Without it (development, tests) the normal connection is used. |

**`src/Mantenio.Api/Infrastructure/Persistence/Configurations/`**

| File | What it does |
|---|---|
| `AuditConfigurations.cs` | EF Core mapping of the audit log table. |
| `CondoConfigurations.cs` | EF Core mapping of settings, towers, apartments, freezes, fee changes, bank accounts, notices and devices. |
| `IdentityConfigurations.cs` | EF Core mapping of users, refresh tokens and invitation codes. |
| `LedgerConfigurations.cs` | EF Core mapping of charges, proofs, allocations and receipts (unique indexes, xmin concurrency). |

**`src/Mantenio.Api/Infrastructure/Push/`**

| File | What it does |
|---|---|
| `FounderDevices.cs` | A copy of the founder's push tokens in file storage (R2), outside the database: when the database is down, the outage alert still reaches the founder's phone. Rewritten whenever the founder registers or removes a phone. |
| `PushDispatcher.cs` | Sends the push copy of in-app notices after their transaction commits (outbox: PushedAt is null until sent). NotificationService signals new notices; a sweep every few minutes catches the rest. Jobs call DispatchPendingAsync directly before they exit. |
| `PushSending.cs` | Push sending through Firebase Cloud Messaging HTTP v1 (or disabled), and its registration. |

**`src/Mantenio.Api/Infrastructure/RateLimiting/`**

| File | What it does |
|---|---|
| `RateLimitingSetup.cs` | Per-IP limits for the endpoints an attacker would hammer. Limits are configurable ("RateLimiting"). |

**`src/Mantenio.Api/Infrastructure/Security/`**

| File | What it does |
|---|---|
| `SecurityHeaders.cs` | Security headers of every API response and the upload limits of the image endpoints. |

**`src/Mantenio.Api/Infrastructure/Storage/`**

| File | What it does |
|---|---|
| `FileStorage.cs` | Private object storage for payment-proof images and receipt PDFs (Cloudflare R2 in production, a local folder in development and tests). Files are only read through short-lived URLs (~5 min). |
| `ProofImage.cs` | Validates and re-encodes an uploaded transfer screenshot: it must really be an image, it is turned upright (EXIF orientation), shrunk to at most MaxSide px and saved as JPEG, which also drops all metadata (EXIF, GPS). Anything that does not decode as an image  |

**`src/Mantenio.Api/Infrastructure/Time/`**

| File | What it does |
|---|---|
| `CondoClock.cs` | "Today" and "this month" in the condo's time zone (America/Lima), not in UTC. |

**`src/Mantenio.Api/Infrastructure/Vision/`**

| File | What it does |
|---|---|
| `ProofScanning.cs` | Reads the text of a payment voucher image (OCR). The lines go to ProofTextReader. |

**`src/Mantenio.Api/Jobs/`**

| File | What it does |
|---|---|
| `BoardSummaryJob.cs` | Day 1: emails last month's summary to the board members the founder listed: totals, the folio PDF, the period report (Excel) and the audit fingerprint. Several people keep a copy outside the app, so a later deletion or edit of the records can be noticed. Sent  |
| `JobCommands.cs` | Mantenio.Api job <name>: runs one scheduled job and exits (Container Apps Jobs; cron in UTC): generate-charges: day 1, 00:05 Lima (5 5 1 * *). late-fees: daily, 00:30 Lima (30 5 * * *). reminders: day 15, 09:00 Lima (0 14 15 * *). cleanup: daily, 03:30 Lima (3 |
| `LedgerJobs.cs` | Scheduled work (Container Apps Jobs, cron in UTC, see docs §8). Every change is audited with Source = System and no actor, and every job can run twice without doing anything twice. |

**`src/Mantenio.Api/`**

| File | What it does |
|---|---|
| `Mantenio.Api.csproj` | The API project (also runs setup commands and jobs). |
| `Mantenio.Api.http` | Sample HTTP requests for trying the API from the IDE. |
| `Program.cs` | API entry point: services, middleware, endpoints; also runs setup commands and jobs from the command line. |
| `appsettings.Development.json` | Development overrides: local storage, dev seed, Mailpit. |
| `appsettings.json` | API defaults: logging, JWT issuer, rate limits, storage, database pool size. |

**`src/Mantenio.Api/Properties/`**

| File | What it does |
|---|---|
| `launchSettings.json` | Local run profile of the API (port 5013). |

**`src/Mantenio.Api/Resources/`**

| File | What it does |
|---|---|
| `EmailTexts.resx` | Spanish texts of every email. |
| `PushTexts.resx` | Spanish texts of the push notifications. |
| `ReceiptTexts.resx` | Spanish texts of the receipt PDF and its email. |
| `ReportTexts.resx` | Spanish texts of the reports (delinquency, folio, period report). |

**`src/Mantenio.AppHost/`**

| File | What it does |
|---|---|
| `AppHost.cs` | Local stack: PostgreSQL 18, Mailpit, the API and the web app. |
| `Mantenio.AppHost.csproj` | Aspire host for local development. |
| `appsettings.Development.json` | Aspire host development settings. |
| `appsettings.json` | Aspire host settings. |
| `aspire.config.json` | Aspire tooling configuration. |

**`src/Mantenio.AppHost/Properties/`**

| File | What it does |
|---|---|
| `launchSettings.json` | Local run profile of the Aspire host. |

**`src/Mantenio.Client/`**

| File | What it does |
|---|---|
| `ApiException.cs` | An API call that failed. Code is an ErrorCodes value the UI turns into Spanish. |
| `ClientPreferences.cs` | Per-host defaults. Android keeps people signed in by default; the web asks every time. |
| `Mantenio.Client.csproj` | Typed HTTP client library shared by the web and Android hosts. |
| `MantenioApi.cs` | Typed API clients: PublicApi (no token) and MantenioApi (signed in), one method per endpoint. |
| `ServiceCollectionExtensions.cs` | Registers the API clients, the session and the token handler in a web or Android app. |

**`src/Mantenio.Client/Auth/`**

| File | What it does |
|---|---|
| `AuthSession.cs` | The signed-in user of this app instance: keeps the access token in memory, renews it with the stored refresh token (one refresh at a time) and raises Changed when the user signs in or out. |
| `BearerTokenHandler.cs` | Adds the access token to every call; on a 401 it renews the token once and retries. |
| `ITokenStore.cs` | What is persisted between app starts: the refresh token and the "keep me signed in" choice. |
| `MantenioAuthenticationStateProvider.cs` | Bridges AuthSession to Blazor's <AuthorizeView> and [Authorize]. |

**`src/Mantenio.Domain/Audit/`**

| File | What it does |
|---|---|
| `AuditEvent.cs` | Append-only history entry. Every ledger or configuration change writes one in the same transaction. Old and new values are JSON snapshots of the fields that changed. |
| `AuditSource.cs` | Who caused an audited change: a resident, an admin, the gatehouse or the system (scheduled jobs, seeding). |

**`src/Mantenio.Domain/Banking/`**

| File | What it does |
|---|---|
| `BankAccount.cs` | A condo bank account residents transfer to. Only bank, holder, account number and CCI (Peruvian interbank code, 20 digits) are stored: never passwords or keys. Accounts are deactivated, not deleted, to keep the history. |

**`src/Mantenio.Domain/Common/`**

| File | What it does |
|---|---|
| `Period.cs` | A billing month. Stored as an int in yyyymm form (e.g. 202610). |

**`src/Mantenio.Domain/Condo/`**

| File | What it does |
|---|---|
| `Apartment.cs` | An apartment of the condo (tower + number, e.g. H-102). Charges, payments and residents belong to it. |
| `ApartmentFreeze.cs` | One period during which an apartment was frozen, with who started and ended it and why. |
| `ApartmentList.cs` | Parses the apartments CSV (tower,number). |
| `CondoSettings.cs` | Operational settings managed by admins (single row). Branding lives in the condo profile, not here. The global fee history lives in FeeChange; MonthlyFee is the fee in force from TrackingStartPeriod until the first change. |
| `Tower.cs` | A building of the condo ("Edificio"/"Torre" in the UI, depending on the condo profile). |

**`src/Mantenio.Domain/Fees/`**

| File | What it does |
|---|---|
| `FeeChange.cs` | Fee changes (global or per apartment, from a month on) and FeeResolver, which picks the fee of an apartment in a month. |

**`src/Mantenio.Domain/Identity/`**

| File | What it does |
|---|---|
| `InvitationCode.cs` | Single-use 6-digit code an admin or the gatehouse hands out so someone can create an account. Only a keyed hash of the code is stored; the plain code is shown once to the admin. |
| `SessionPolicy.cs` | How long sessions last and which accounts need a second factor. |
| `UserRole.cs` | The account roles, stored as text. The founder (superadmin) is an admin with IsFounder set. |

**`src/Mantenio.Domain/Ledger/`**

| File | What it does |
|---|---|
| `Charge.cs` | A charge of an apartment: monthly fee, opening debt or late fee, with its due date. |
| `DebtCalculator.cs` | Rebuilds charge balances at any past moment (delinquency at a date, period report). |
| `LateFeePolicy.cs` | Late fees (recargo por mora): one per overdue monthly fee, once, as a % of that fee (no compound interest). The percentage is 0 by default and only the founder changes it. Opening debt and late fees themselves get none. |
| `LedgerAllocator.cs` | Applies payments to charges FIFO (oldest first); the remainder is credit. |
| `PaymentProof.cs` | A payment proof (approve, reject, void, historical payments) and PaymentAllocation (which part paid which charge). |
| `ProofTextReader.cs` | Finds amount, date and operation number in the OCR text of a voucher. |
| `Receipts.cs` | Receipts (number, void, PDF) and the amount in Spanish words for the PDF. |

**`src/Mantenio.Domain/`**

| File | What it does |
|---|---|
| `Mantenio.Domain.csproj` | Pure business rules (no EF, no HTTP). |

**`src/Mantenio.Domain/Notifications/`**

| File | What it does |
|---|---|
| `UserDevice.cs` | A phone that receives push notifications for a user (Firebase Cloud Messaging registration token). |
| `UserNotification.cs` | An in-app notice to one user (read, acknowledged, pushed). |

**`src/Mantenio.Mobile/`**

| File | What it does |
|---|---|
| `App.xaml` | MAUI application resources (colors, styles). |
| `App.xaml.cs` | The MAUI application: one window with the Blazor page. |
| `LauncherFileOpener.cs` | Android: opens links and files with the system apps (browser, PDF viewer, WhatsApp). |
| `MainPage.xaml` | The page that hosts the BlazorWebView. |
| `MainPage.xaml.cs` | The only page: a BlazorWebView that hosts the shared UI, with the system bars painted in the condo color. |
| `Mantenio.Mobile.csproj` | .NET MAUI Blazor Hybrid host (the Android app); version and package id come from the condo profile. |
| `MauiProgram.cs` | Composition root of the Android app. |
| `SecureTokenStore.cs` | Android Keystore-backed storage for the refresh token. |

**`src/Mantenio.Mobile/Components/`**

| File | What it does |
|---|---|
| `_Imports.razor` | Razor usings of the Android host. |

**`src/Mantenio.Mobile/Platforms/Android/`**

| File | What it does |
|---|---|
| `AndroidManifest.xml` | Android permissions (internet, notifications, camera) and the FileProvider for sharing receipts. |
| `MainActivity.cs` | The Android activity that hosts the app (MAUI default, edge to edge). |
| `MainApplication.cs` | The Android application class: starts Firebase before the app. |
| `MediaImagePicker.cs` | System camera and gallery; photos are shrunk on the phone (the server re-encodes them anyway). |
| `PushSupport.cs` | Firebase Cloud Messaging: started from the condo profile (there is no google-services.json). |
| `WhatsAppShare.cs` | Opens WhatsApp (or WhatsApp Business) with a file attached; the admin then picks the resident. |

**`src/Mantenio.Mobile/Platforms/Android/Resources/values/`**

| File | What it does |
|---|---|
| `colors.xml` | Android system colors (kept in sync with the condo theme). |

**`src/Mantenio.Mobile/Properties/`**

| File | What it does |
|---|---|
| `launchSettings.json` | Local run profile of the Android app. |

**`src/Mantenio.Mobile/wwwroot/`**

| File | What it does |
|---|---|
| `app.css` | Base styles of the Android web view. |
| `index.html` | Host page of the Android web view. |

**`src/Mantenio.ServiceDefaults/`**

| File | What it does |
|---|---|
| `Extensions.cs` | Adds telemetry, health checks, service discovery and resilient HTTP clients to a project. |
| `Mantenio.ServiceDefaults.csproj` | Shared telemetry and health-check setup (Aspire service defaults). |

**`src/Mantenio.Shared/Apartments/`**

| File | What it does |
|---|---|
| `ApartmentAdminContracts.cs` | DTOs of the admin apartment and tower endpoints. |
| `ApartmentContracts.cs` | DTOs of the apartment picker and apartment details. |

**`src/Mantenio.Shared/Auth/`**

| File | What it does |
|---|---|
| `AuthContracts.cs` | DTOs of sign-in, registration, password and profile endpoints, plus password rules. |
| `MantenioClaims.cs` | Claim names inside the access token. The API writes them and the clients read them. |

**`src/Mantenio.Shared/BankAccounts/`**

| File | What it does |
|---|---|
| `BankAccountContracts.cs` | DTOs of the bank account endpoints. |

**`src/Mantenio.Shared/Condos/`**

| File | What it does |
|---|---|
| `CondoProfile.cs` | Per-condo configuration that lives in code (condos/<slug>/condo.json), selected at build time. Operational settings that admins change (fees, late-fee %, bank accounts) live in the database instead. |

**`src/Mantenio.Shared/Errors/`**

| File | What it does |
|---|---|
| `ErrorCodes.cs` | Machine-readable error codes returned by the API (ProblemDetails "code" extension). The UI maps each code to a Spanish message; never show these strings to users. |

**`src/Mantenio.Shared/Invitations/`**

| File | What it does |
|---|---|
| `InvitationContracts.cs` | DTOs of invitation codes and registration. |

**`src/Mantenio.Shared/Ledger/`**

| File | What it does |
|---|---|
| `LedgerContracts.cs` | DTOs of proofs, statements, charges, reviews, generation, opening debt and voucher reading. |

**`src/Mantenio.Shared/`**

| File | What it does |
|---|---|
| `Mantenio.Shared.csproj` | DTOs, error codes and the condo profile (embedded condo.json), shared by API and clients. |

**`src/Mantenio.Shared/Notifications/`**

| File | What it does |
|---|---|
| `NotificationContracts.cs` | DTOs of notices and push devices. |

**`src/Mantenio.Shared/Reports/`**

| File | What it does |
|---|---|
| `ReportContracts.cs` | DTOs of the delinquency report, dashboard and audit log (with its source filter). |

**`src/Mantenio.Shared/Settings/`**

| File | What it does |
|---|---|
| `SettingsContracts.cs` | DTOs of fee and settings endpoints, and yyyymm helpers. |

**`src/Mantenio.Shared/SystemInfo/`**

| File | What it does |
|---|---|
| `SystemInfoResponse.cs` | Public info: which condo this API serves and its version. |

**`src/Mantenio.Shared/Users/`**

| File | What it does |
|---|---|
| `UserContracts.cs` | DTOs of the admin user endpoints and paged results. |

**`src/Mantenio.UI/Components/`**

| File | What it does |
|---|---|
| `AdminDashboardCards.razor` | Admin dashboard cards: proofs to review, overdue debt by tower, this month's collection. |
| `ApartmentPicker.razor` | Tower and number comboboxes that pick an apartment. |
| `BusyComponentBase.cs` | Pages and components that call the API: one call at a time, errors shown in Spanish. |
| `ChargeTimelineDialog.razor` | Dialog with the history of one charge. |
| `CondoTime.cs` | Times are stored in UTC and shown in the condo time zone (America/Lima for the pilot). |
| `Forbidden.razor` | Shown when a signed-in user opens a page their role cannot see. |
| `Formats.cs` | How money, months and dates are written in the UI (condo culture, es-PE: "S/ 180.00", "noviembre 2026"). |
| `LedgerText.cs` | Spanish labels and chip colors for ledger states (keys "ProofStatus_*", "DebtStatus_*", "ChargeType_*"). |
| `LoadingIndicator.razor` | Spinner with "Loading..." while a page waits for the API. |
| `MyFeeCard.razor` | The user's monthly fee, its due date and the next scheduled change. |
| `NoticeText.cs` | The Spanish title and body of a notice, built from its kind and data (the API stores no text). |
| `OriginBankField.razor` | Origin bank select (condo profile list plus "Other bank"). |
| `PendingNoticeDialog.razor` | Popup of a notice that must be confirmed (bank account changes) before using the app. |
| `PendingNoticesGate.razor` | After sign-in, shows the notices that must be confirmed (bank account changes) one by one. |
| `ProofCard.razor` | One payment proof with its state, review, image, receipt (system or paper) and WhatsApp button. |
| `ProofImageInput.razor` | The voucher photo: camera or gallery on Android, a file input on the web. |
| `ProofScanFill.cs` | Pre-fills the payment form with what was read from the voucher, never overwriting what the person typed. |
| `RedirectToLogin.razor` | Used by the router for signed-out users: sends them to the login page. |
| `RoleLabels.cs` | Spanish name of each role ("Superadmin" for the founder). |
| `SignInChallenge.razor` | The 6-digit emailed code step (admin 2FA, email confirmation). |
| `UserActionDialog.razor` | Confirmation dialog that asks for a reason (and an apartment when needed). |
| `UserActionResult.cs` | What a confirmation dialog returns: the reason and, when asked, the apartment. |
| `VoucherImageField.razor` | Voucher photo field that sends the image to be read and reports the values found. |

**`src/Mantenio.UI/Errors/`**

| File | What it does |
|---|---|
| `ErrorMessages.cs` | Translates API error codes (ErrorCodes) into Spanish UI messages. |

**`src/Mantenio.UI/Layout/`**

| File | What it does |
|---|---|
| `AuthLayout.razor` | Sign-in layout: centered card with the condo logo. |
| `AuthLayout.razor.css` | Styles of the sign-in layout. |
| `MainLayout.razor` | Layout of every signed-in page: app bar with the bell and account menu, side drawer (desktop), bottom bar (phone), push registration and the session check. |
| `MainLayout.razor.css` | Styles of the main layout (bottom bar, page padding). |
| `NavigationItems.cs` | Who sees a menu entry. |
| `ThemeProviders.razor` | MudBlazor providers, rendered once by whichever layout is active. |

**`src/Mantenio.UI/`**

| File | What it does |
|---|---|
| `Mantenio.UI.csproj` | Razor class library with every page and component, shared by web and Android. |
| `Routes.razor` | Router of the shared UI: pages need a signed-in user unless marked anonymous; signed-out users go to login. |
| `_Imports.razor` | Razor usings shared by every page and component. |

**`src/Mantenio.UI/Pages/Account/`**

| File | What it does |
|---|---|
| `ChangePassword.razor` | Change password page (also forced after a temporary password). |

**`src/Mantenio.UI/Pages/Admin/`**

| File | What it does |
|---|---|
| `AddApartmentDialog.razor` | Adds one apartment, or a new tower first. |
| `AdminHub.razor` | Admin home: dashboard cards and links to every administration page. |
| `Apartments.razor` | Admin: apartments list with search, frozen filter, special fees, freeze/unfreeze, add, import and delete. |
| `Apartments.razor.css` | Styles of the apartments page. |
| `Audit.razor` | Admin: the audit log (who changed what and why), filtered by date, source, entity, apartment and person. |
| `BankAccountDialog.razor` | Add or edit a bank account (bank, holder, number, CCI) with a reason. |
| `BankAccountsAdmin.razor` | Admin: the condo bank accounts; adding, editing or hiding one notifies every resident. |
| `Charges.razor` | Founder: generate a month's charges by hand and import the opening debt CSV. |
| `HistoricalPaymentDialog.razor` | Admin: loads a payment from the paper records, or completes one (photo read automatically and replaceable). |
| `ImportApartmentsDialog.razor` | CSV import of apartments with a mandatory preview. |
| `Invitations.razor` | Admin: create single-use invitation codes for residents or admins. |
| `Invitations.razor.css` | Styles of the invitations page (big code). |
| `PaymentReview.razor` | Admin: pending proofs to approve or reject, the reviewed history (void), historical payments, and registering a resident's voucher. |
| `RegisterProofDialog.razor` | Admin/guard: registers a voucher brought in person (form, confirmation, receipt with WhatsApp). |
| `Reports.razor` | Admin: delinquency report at a date (screen, Excel, PDF), the monthly folio PDF and the period audit Excel. |
| `ReviewDialog.razor` | Approve (amount) or reject/void (reason) dialog. |
| `Settings.razor` | Admin: global fee schedule, due rule and (founder) late-fee percentage, with the fee history. |
| `SpecialFeeDialog.razor` | Sets or removes the special fee of one apartment from a month on. |
| `Users.razor` | Admin: accounts list with search; deactivate, reactivate and change apartment. |
| `Users.razor.css` | Styles of the users page. |

**`src/Mantenio.UI/Pages/Auth/`**

| File | What it does |
|---|---|
| `AuthForms.cs` | Login form fields and validation. |
| `ForgotPassword.razor` | Password reset in two steps: email, then the emailed code and the new password. |
| `Login.razor` | Login page: email and password, then the emailed code for admins. |
| `Register.razor` | Account creation: invitation code, personal data and apartment, then the email confirmation code. |

**`src/Mantenio.UI/Pages/`**

| File | What it does |
|---|---|
| `BankAccounts.razor` | Resident: the condo bank accounts to pay to, with copy buttons. |
| `Help.razor` | Help page: resident topics for everyone, admin topics for admins. |
| `Home.razor` | Home page: greeting, the apartment's fee and shortcuts (admins also see the dashboard). |
| `Home.razor.css` | Styles of the home page. |
| `NotFound.razor` | Page shown for an unknown address. |
| `Notifications.razor` | The user's notices (bell); opening the page marks them as read. |
| `Payments.razor` | Payments: the apartment's account statement and its proofs, and the button to upload a new proof. |
| `UploadProofDialog.razor` | Resident: upload a payment proof (photo first, read automatically, then the fields). |

**`src/Mantenio.UI/Platform/`**

| File | What it does |
|---|---|
| `IFileOpener.cs` | Opens private files: stored ones from a short-lived link, generated ones (reports) from their bytes. |
| `IImagePicker.cs` | An image chosen by the user: bytes, type and file name. |
| `IPushRegistration.cs` | Push notifications for this device. Android registers its Firebase token with the API after sign-in and removes it on sign-out; the web has no push (notices stay in the app). |

**`src/Mantenio.UI/Resources/`**

| File | What it does |
|---|---|
| `UiStrings.resx` | Every Spanish (es-PE) text of the UI, including Error_<code> messages and the Help topics. |

**`src/Mantenio.UI/Theme/`**

| File | What it does |
|---|---|
| `CondoThemeFactory.cs` | Builds the MudBlazor theme from the condo profile colors. Layouts never change; only tokens do. |

**`src/Mantenio.UI/wwwroot/`**

| File | What it does |
|---|---|
| `mantenio.css` | Global styles of the shared UI. |
| `mantenio.js` | Browser helpers: download a generated file and share it to WhatsApp. |

**`src/Mantenio.Web/`**

| File | What it does |
|---|---|
| `App.razor` | Root component of the web app: the shared UI routes. |
| `BrowserFileOpener.cs` | Web: opens links in a new tab and downloads or shares files through mantenio.js. |
| `BrowserTokenStore.cs` | "Keep me signed in" → localStorage (survives closing the browser); otherwise sessionStorage (this tab only). Accepted pilot risk: an XSS could read it, so the API keeps refresh tokens short-lived and rotating. |
| `Mantenio.Web.csproj` | Blazor WebAssembly host (the web app on Cloudflare Pages). |
| `Program.cs` | Web app entry point: culture, MudBlazor, API clients and the browser implementations; the API address comes from the condo profile. |
| `_Imports.razor` | Razor usings of the web host. |

**`src/Mantenio.Web/Properties/`**

| File | What it does |
|---|---|
| `launchSettings.json` | Local run profile of the web app (port 5024). |

**`src/Mantenio.Web/wwwroot/`**

| File | What it does |
|---|---|
| `appsettings.Development.json` | Local API address for the web app. |
| `index.html` | Host page of the web app (loads Blazor WebAssembly). |

**`src/Mantenio.Web/wwwroot/css/`**

| File | What it does |
|---|---|
| `app.css` | Base styles of the web host page. |

**`tests/Mantenio.Api.Tests/Apartments/`**

| File | What it does |
|---|---|
| `ApartmentAdminTests.cs` | Towers, apartments, CSV import, freezes and deleting apartments through the admin API. |

**`tests/Mantenio.Api.Tests/Auth/`**

| File | What it does |
|---|---|
| `LoginTests.cs` | Login, admin 2FA, lockout and password reset. |
| `SessionTests.cs` | Refresh token rotation, replay detection, logout and session lifetimes. |

**`tests/Mantenio.Api.Tests/BankAccounts/`**

| File | What it does |
|---|---|
| `BankAccountTests.cs` | Bank accounts: validation, audit and the notices residents must confirm. |

**`tests/Mantenio.Api.Tests/Infrastructure/`**

| File | What it does |
|---|---|
| `ApiFactory.cs` | Boots the real API against PostgreSQL 18 (same major version as Aiven). One container serves the whole test run; every factory gets its own empty database, seeded with the condo apartments and a founder. Requires Docker. |
| `TestFlows.cs` | The common end-to-end flows (sign in, invite, register) written once for every test class. |

**`tests/Mantenio.Api.Tests/Invitations/`**

| File | What it does |
|---|---|
| `InvitationExpiryTests.cs` | Invitation codes stop working after 24 hours. |
| `RegistrationTests.cs` | Registration with invitation codes: roles, apartments, admin seats and email confirmation. |

**`tests/Mantenio.Api.Tests/Jobs/`**

| File | What it does |
|---|---|
| `JobTests.cs` | Scheduled jobs and push (docs/context.md Phase 6, §10 "Notificaciones y jobs" and scenario 13). |

**`tests/Mantenio.Api.Tests/Ledger/`**

| File | What it does |
|---|---|
| `AdminRegistrationTests.cs` | Staff (an admin or the gatehouse) registers the voucher a resident brought in person; it is approved at once. |
| `ChargeGenerationTests.cs` | Monthly charge generation and the opening debt import. Generation touches every apartment, so the tests run in order on their own database and each uses its own month. |
| `HistoricalPaymentTests.cs` | Historical payments, the 10-year file retention, the period report and the audit people filter. |
| `LedgerFlows.cs` | Ledger steps shared by the ledger test classes. |
| `LedgerTests.cs` | The ledger scenarios of docs/context.md §10. Each test works on its own apartment. |
| `ProofScanTests.cs` | Reading a voucher image to pre-fill the form. Changes the shared fake scanner, so it has its own API instance. |

**`tests/Mantenio.Api.Tests/`**

| File | What it does |
|---|---|
| `Mantenio.Api.Tests.csproj` | API integration tests (real API + PostgreSQL in Docker). |

**`tests/Mantenio.Api.Tests/Reports/`**

| File | What it does |
|---|---|
| `ReportTests.cs` | Delinquency report, monthly folio, dashboard and audit viewer (docs/context.md Phase 5, §10 scenario 14). |

**`tests/Mantenio.Api.Tests/Security/`**

| File | What it does |
|---|---|
| `AuthorizationCoverageTests.cs` | Guards against an endpoint accidentally left open: everything requires sign-in unless it is in the reviewed anonymous list below, and every /api/admin route requires an admin policy. |
| `DatabaseUnavailableTests.cs` | The database is off (e.g. Aiven Free powered it down): clear 503, and one alert (email + push to the founder). |
| `LedgerProtectionTests.cs` | Protections against tampering: the database refuses to delete ledger rows or edit the audit log, the board gets a monthly summary with the audit fingerprint, and the fingerprint changes when the log changes. |
| `RuntimeLoginTests.cs` | Like production: the app runs with a login that does not own the tables, and only the migrations use the owner. |

**`tests/Mantenio.Api.Tests/Settings/`**

| File | What it does |
|---|---|
| `SettingsTests.cs` | Fee changes, due rule, late-fee percentage and who may change them. |

**`tests/Mantenio.Api.Tests/SystemInfo/`**

| File | What it does |
|---|---|
| `SystemInfoTests.cs` | The public system info endpoint. |

**`tests/Mantenio.Api.Tests/Users/`**

| File | What it does |
|---|---|
| `AuthorizationTests.cs` | Who can see what: admin routes, other apartments (404) and the apartment picker. |
| `SessionRevocationTests.cs` | A revoked session stops working at once, not when its 15-minute access token expires. |
| `StaffRoleTests.cs` | The gatehouse (portería) and the apartment rules for staff: admins need an apartment to act on payments, nobody reviews what they sent, and the gatehouse never has an apartment. |
| `UserAdminTests.cs` | Admin user management: list, deactivate, reactivate and change apartment. |

**`tests/Mantenio.Domain.Tests/Audit/`**

| File | What it does |
|---|---|
| `AuditEventTests.cs` | Audit event construction rules. |

**`tests/Mantenio.Domain.Tests/Banking/`**

| File | What it does |
|---|---|
| `BankAccountTests.cs` | Bank account validation and changes. |

**`tests/Mantenio.Domain.Tests/Common/`**

| File | What it does |
|---|---|
| `PeriodTests.cs` | The yyyymm month value object. |

**`tests/Mantenio.Domain.Tests/Condo/`**

| File | What it does |
|---|---|
| `ApartmentFreezeTests.cs` | Freezing and unfreezing apartments. |
| `ApartmentListTests.cs` | Parsing the apartments CSV (including the real lists in condos/). |
| `ApartmentTests.cs` | Apartment codes. |
| `CondoSettingsTests.cs` | Due dates and settings validation. |

**`tests/Mantenio.Domain.Tests/Fees/`**

| File | What it does |
|---|---|
| `FeeResolverTests.cs` | Which fee applies to an apartment in a month (global and special fees). |

**`tests/Mantenio.Domain.Tests/Identity/`**

| File | What it does |
|---|---|
| `InvitationCodeTests.cs` | Invitation code lifetime, single use and format. |
| `SessionPolicyTests.cs` | Session and token lifetimes. |

**`tests/Mantenio.Domain.Tests/Ledger/`**

| File | What it does |
|---|---|
| `DebtCalculatorTests.cs` | Rebuilding charge balances at a past moment. |
| `HistoricalPaymentTests.cs` | Payments loaded from the paper records: optional data, editable, photo replaceable. |
| `LateFeePolicyTests.cs` | Late fee amounts and which charges get them. |
| `LedgerAllocatorTests.cs` | FIFO allocation of payments to charges and credit. |
| `ProofTextReaderTests.cs` | Reading amount, date and operation number from voucher text. |
| `ReceiptTests.cs` | Receipt numbers and amounts in words. |

**`tests/Mantenio.Domain.Tests/`**

| File | What it does |
|---|---|
| `Mantenio.Domain.Tests.csproj` | Unit tests of the business rules. |

**`tests/Mantenio.UI.Tests/`**

| File | What it does |
|---|---|
| `AccessibilityTests.cs` | Basic accessibility rules checked on the Razor sources: screen readers need a name for every control. |
| `AuthPageTests.cs` | Login, registration and password reset pages. |
| `FakeApi.cs` | Answers API calls with canned responses keyed by path, and records what was sent. |
| `FakeFileOpener.cs` | Records what the UI asked to open or save. |
| `Mantenio.UI.Tests.csproj` | Component tests of the UI (bUnit). |
| `Phase3UiTests.cs` | Bank accounts, notices and settings pages. |
| `Phase4UiTests.cs` | Payments, proofs, review and receipts pages. |
| `Phase5UiTests.cs` | Reports, audit log and dashboard pages. |
| `ProofScanFillTests.cs` | Pre-filling the payment form from the voucher reading. |
| `UiTests.cs` | Layout, navigation, theme and error message coverage. |

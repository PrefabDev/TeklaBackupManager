# Tekla Backup Manager — Community Edition

[Français](README.md) · [Download 0.1.0-alpha.2](https://github.com/PrefabDev/TeklaBackupManager/releases/tag/v0.1.0-alpha.2) · [MIT](LICENSE)

Open-source Windows backup manager for Tekla Structures models. Automated DB1 backups, scheduled full backups, configurable retention, multi-user management and French/English interface.

**Experimental community prerelease 0.1.0-alpha.2 — not stable or certified for production.** Keep independent backups and test recovery before real use. No commercial support or response-time commitment is provided.

## Install

Download `TeklaBackupManager-0.1.0-alpha.2-win-x64.zip` from the prerelease and compare its SHA-256 with `SHA256SUMS.txt`. Extract it to a stable user folder, keep the included LICENSE and README files, and run `TeklaBackupManager.exe`. The Windows x64 executable is self-contained: no separate .NET installation is required. It is unsigned; do not disable Windows protections to run it. Start with fictitious files and a **new dedicated backup root**.

Startup was tested in Windows Sandbox without a detected shared .NET installation. This does not validate all Windows editions or every backup/network scenario.

## Configure

The current Windows account needs read access to sources and write access to destinations. No passwords are requested or stored. Initial user/workstation values come from the local session: review them.

Fictitious examples: `\\WORKSTATION-01\TeklaStructuresModels`, `\\BACKUP-SERVER\TeklaBackups`. A source folder can contain multiple projects. Add users, select active entries, set their `HH:mm` times, apply schedules to the selected user or edit the grid, and save.

Settings are local at `%AppData%\TeklaBackupManager\settings.json`; no actual settings are distributed. Older installations share this location, so back it up before upgrading. Closing the window hides it in the tray; the tray Quit action exits. Optional Windows startup has not yet been qualified after signing in again. Some messages and the tray menu remain French.

## Modes and schedules

- **DB1:** copies `.db1` files Monday–Friday, default 12:15, with configurable retention (default 30 days). An empty DB1 selection refuses both backup and purge. Repeated runs on one day update that day's directory.
- **Full:** Friday mirror, default 12:30, including all files. **Deletes destination files missing from the source.** No full history is retained. A completely empty source is refused.
- Manual actions, progress, cancellation and staggering between users within one batch.

```text
[Destination]/BACKUPDB1/[USER]/yyyy-dd-MM
[Destination]/TEKLASTRUCTUREMODEL/[USER]
```

The historical format is year-day-month. Retention only removes sufficiently old dated directories also older by creation/modification times. Metadata is outside model folders in `.tekla-backup-manifests`, so a source `manifest.txt` is preserved. Manifests are not subject to DB1 retention; archive them separately.

Schedules are checked every 30 seconds while the application/session run. Missed times may run later the same day, not on missed days. In-memory run keys mean restarting can rerun a job; failed automatic jobs are not retried in the same process. DB1 has priority over full mirroring.

## Concurrency and upgrading

Only one application instance may use a profile. Each backup holds an exclusive destination lease; a second writer is refused. The filesystem must enforce verified exclusive sharing; otherwise writes are refused/stopped. Handles are released normally or by Windows after process termination. Lock files deliberately persist: **never delete them to force access**.

Reserved/invalid Windows names, case-insensitive collisions and identical destinations are rejected for the whole configuration, including disabled users and partial scheduled batches. Directory identities are resolved before comparison. Ambiguous/device paths, reparse points and nested backup roots are rejected.

`.tekla-backup-owners.json` associates user destinations with sources. A different source cannot silently reuse a user name. Do not delete that registry. Older nonempty backups without a verified owner are not automatically adopted: **use a new backup root** and preserve the old backups independently. No implicit rename, migration or merge occurs.

## Integrity and recovery

New/changed files are staged and replaced by a same-directory rename after lease validation. Cancellation during staging preserves the previous file. SHA-256 content comparison catches same-size/same-time changes. This is not a whole-model transaction: interruption may leave mixed file versions, an orphaned temporary file or partly completed mirror deletions.

Stop automatic jobs, close the Tekla model, preserve its current state, then copy the backed-up model into a **new working location**. Open it in a compatible Tekla version and validate expected business data before replacing anything. DB1 alone is not a complete model backup; have your Tekla administrator validate how to combine it with other required files.

**No authorized representative Tekla models were available to validate real recovery.** There is no restoration wizard, VSS snapshot or coordination with Tekla. Models open for editing may change during backup; prefer closed models and independently test recovery.

## Qualification and limitations

54 fictitious-data tests include actual separate processes, exclusion without changing existing files, forced termination, retention, DB1, mirroring, naming collisions, interrupted staging and hash-verified copy-back. Windows Sandbox startup was tested. These checks do not establish real Tekla restorability.

Real network disconnections, SMB/NAS equipment, multiple remote hosts, load and multi-day schedules remain unqualified. Exclusive handles use compatible Windows/SMB semantics and failures stop the operation; do not use a provider without established sharing guarantees. No encryption, free-space check, hostile-administrator protection or whole-model atomic snapshot. Third-party scripts may ignore the application's lease protocol.

Settings, source registry, manifests and logs can contain local identifiers/paths: redact before reporting issues. No telemetry or upload mechanism was identified in application code; UNC paths use Windows networking.

## Build and contribute

Windows and a maintained .NET 10 SDK; no Tekla SDK required.

```powershell
dotnet build TeklaBackupManager.csproj -c Release
dotnet run --project tests/FunctionalTests.csproj -c Release
.\publish.ps1
```

Self-contained output: `artifacts/win-x64/TeklaBackupManager.exe`. See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

MIT — Copyright (c) 2026 Benoît Gosselin. Independent community tool, not published, affiliated with or endorsed by Trimble or Tekla. Tekla Structures and Trimble are trademarks of their respective owners. No third-party logos are distributed.

Two additional sandbox checks validated an independent substituted-drive destination and rejection of overlap between that drive and its actual path.

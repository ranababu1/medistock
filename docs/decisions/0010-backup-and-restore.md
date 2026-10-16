# 0010 — Backup and restore, and what a backup is not

- **Status:** Accepted — **the deployment target for the database engine is still unconfirmed** (see ADR 0004)
- **Date:** 2026-10-16
- **Deciders:** MediStock maintainers
- Code: [`IBackupService`](../../src/MediStock.Application/Operations/IBackupService.cs), [`SqlServerBackupService`](../../src/MediStock.Infrastructure/Operations/SqlServerBackupService.cs), [`MediStockOptions`](../../src/MediStock.Application/Configuration/MediStockOptions.cs), [`RestoreTests`](../../tests/MediStock.IntegrationTests/Operations/RestoreTests.cs), [`BackupTests`](../../tests/MediStock.IntegrationTests/Operations/BackupTests.cs), [`release-process.md`](../release-process.md), [ADR 0004](0004-localdb-for-development.md), [ADR 0006](0006-context-lifetime-and-execution-strategy.md), [ADR 0009](0009-users-roles-and-the-audit-trail.md)

## Context

The plan asks for a "backup and restore workflow", for a backup to be restorable "successfully in a test
environment", for backup and restore operations to be audited, and for the application not to "bundle
database credentials with elevated privileges". Milestone 6 adds that an installer must "clearly document
any database engine prerequisite", and Milestone 5 that "errors are actionable and do not expose
credentials".

The awkward part is not running `BACKUP DATABASE`. It is that the two directions have opposite needs, and
the plan's own wording — "a backup can be restored successfully in a test environment" — hides four
questions:

- **Can a restore run while the application is using the database?** A `RESTORE` needs exclusive access. A
  WPF application is full of objects holding a connection pool and a screen holding loaded rows.
- **What does a backup take with it?** Everything, including the audit row that would record the restore.
  A trail can therefore go *backwards*.
- **Where do files live?** Writing them into the installation folder means a reinstall can delete the
  pharmacy's only copy. Writing them onto the same disk means a failed disk loses both.
- **How does a single-PC pharmacy find out that its backup is a backup** before it needs it?

Two facts constrain the answers:

- **One machine, one database, one copy of the application.** The pharmacy is a single Windows PC on
  LocalDB or a local SQL Server instance, and the operator is the person who runs the backup. There is no
  DBA to call and no second machine to restore onto.
- **The audit trail is the record of truth for accountability** (ADR 0009). A restore that leaves no trace is
  indistinguishable from data that was never entered, and the difference matters enormously the next
  morning.

## Decision

### 1. One service, four operations, and every one of them guarded

`IBackupService` has `Folder`, `ListAsync`, `CreateAsync`, `VerifyAsync(path)` and `RestoreAsync(path,
reason)`. All four require `Permission.BackupDatabase`, including `ListAsync`: knowing which files exist and
when they were taken is administrative information, and a role that may not restore the database does not
need to know that October's file is missing. This is the one place where a per-method check is used rather
than a decorator over a contract (ADR 0009 §2) — the four operations are one contract with one permission,
so a decorator would add a wrapper without adding a rule.

### 2. Files are written outside the installation folder, with a timestamped name

`CreateAsync` writes `MediStock-yyyyMMdd-HHmmss.bak` into `MediStock:BackupFolder`, which defaults to
`MediStock\Backups` under the user's documents folder and is resolved the same way receipts and reports are
(ADR 0004, ADR 0006). Three things follow from the name:

- **Sorting the folder by name sorts it by age**, which is what a list column needs and what an operator
  reads when looking for "the one from before the stock correction".
- **The name carries the moment the operator pressed the button**, taken from `IClock` and written in the
  machine's local time — which on a single-PC install is the configured `MediStock:TimeZoneId` (ADR 0009
  §11) — so a backup taken at 11pm reads as the evening the pharmacy was open rather than as the next
  morning in UTC. Two backups in the same second share a name and the second replaces the first; at a human
  button-press that is not reachable, and if it ever were, the file that loses is a duplicate taken seconds
  after the winner.
- **The installation folder is not the backup folder.** Reinstalling, repairing or upgrading the application
  must never be able to delete the pharmacy's data.

SQL Server's own catalog is told the same story: the backup media carries `NAME = "MediStock {database}"`
and a description naming the operator, so a file found on a USB stick in a year's time can be identified
without MediStock.

`Folder` is created if it does not exist. A relative path in configuration is resolved against the user's
documents folder, not against the working directory, so a shortcut's "Start in" cannot send backups
somewhere surprising.

### 3. A backup is taken `COPY_ONLY` and `CHECKSUM`

`BACKUP DATABASE … WITH INIT, CHECKSUM, COPY_ONLY`. Each word is a decision:

- **`INIT`** — the file name is unique, so this overwrites nothing; stating it stops SQL Server from
  appending to a file that happens to exist.
- **`CHECKSUM`** — every page is checksummed as it is written, so a damaged page is discovered while taking
  the backup rather than while restoring it, at the moment when there is still a working database to copy
  from. `VerifyAsync` uses `RESTORE VERIFYONLY … WITH CHECKSUM`, which reads the whole file without touching
  the live database.
- **`COPY_ONLY`** — the application's backup does not disturb the differential-backup chain of a SQL Server
  instance that is being backed up by something else as well. A MediStock backup is a point-in-time copy for
  this application, not a contribution to a server-wide strategy, and it must not silently break one.

### 4. Restoring is refused before it starts, not half way through

`RestoreAsync` verifies the file first, then requires a reason, then restores. The order is deliberate: a
file that is not a usable backup is refused as a file, before the database has been touched at all, and the
reason is demanded before `SINGLE_USER` is entered rather than after the point of no return. Restoring a
missing or damaged file leaves the database exactly as it was — `BackupTests` asserts that a missing file is
refused by both `VerifyAsync` and `RestoreAsync` without throwing and without touching anything.

The refusal wording tells the operator what to do rather than what went wrong: *"There is no file at
{path}."* and *"SQL Server could not read {name} as a backup. The file is damaged or is not a MediStock
backup."*

### 5. A restore carries a reason, and the reason is the point

Restoring replaces everything that has happened since the file was written. The row it leaves is what tells
the next person why October's takings vanished:

> A reason is required. The trail records why a restore was done, so that the next person can tell a
> deliberate rollback from a mistake.

A blank reason is refused before anything is executed.

### 6. The restore itself: clear the pools, take the database out of service, replace, put it back

`RestoreAsync` runs on a connection to `master` with a ten-minute command timeout:

1. `SqlConnection.ClearAllPools()` — the application's own pooled connections are holding the database open
   and would each be a blocker for the exclusive lock that follows.
2. `ALTER DATABASE [MediStock] SET SINGLE_USER WITH ROLLBACK IMMEDIATE` — any connection that arrives or
   reconnects in the meantime is closed, and the rollback is immediate rather than waiting for a screen to
   finish a query it started before the operator asked for the restore.
3. `RESTORE DATABASE [MediStock] FROM DISK = … WITH REPLACE, RECOVERY` — `REPLACE` is required because the
   database exists and is being overwritten rather than created, and `RECOVERY` brings it back online in one
   step.
4. `ALTER DATABASE [MediStock] SET MULTI_USER`.
5. The `RestoreCompleted` audit row.

### 7. The audit row is written after the restore, and a failure to write it is logged rather than raised

The row must be written after, because everything written before it would be replaced by the file. The
summary therefore says what happened in the past tense and names the file:

> The database was restored from MediStock-20261016-105904.bak by Restore Administrator.

with the reason, the file name as `EntityId` and the full path as `Detail`. If writing the row fails — the
connection is new, the pool was just cleared, and this is the least reliable moment in the whole workflow —
the failure is **logged and not raised**. The restore has already happened; reporting it as a failed
operation would be a lie about the state of the database, and the operator's next move (restart, and check
the data) is the same either way.

### 8. Restoring is two steps: restore, then restart

The service reports the restore's outcome and says that the application should be restarted. It does not
pretend to hot-swap a database that open screens are holding. Concretely:

- The pool is cleared and EF Core will happily reconnect, so the *next* read is correct. The scratch-database
  round trip in `RestoreTests` proves exactly that: the same provider reads the restored medicines and the
  audit row, with no restart.
- What is **not** rebuilt is everything the shell has already loaded: a dashboard's figures, a list of
  invoices, a half-filled cart, the account list on the settings screen. Those are stale in a way that is
  harder to explain than a restart, and some of them — a cart holding a batch that no longer exists — are
  dangerous.

The restart requirement is in the settings screen's wording and in `release-process.md`.

### 9. What a backup is not

Stated here so that the omission is a decision rather than an oversight:

- **It is not a disaster-recovery plan.** The file lands on the same disk as the database by default. If
  that disk fails, both are gone. The install guide tells the operator to copy backups to a USB stick or
  another machine; the application does not do it for them, and does not claim to.
- **It is not a SQL Server maintenance plan.** No scheduling, no retention policy, no log backups. A
  pharmacy that wants point-in-time recovery, or an instance shared with other databases, needs a DBA and a
  server-wide strategy; this is a per-application copy.
- **It is not encrypted at rest, and it is not access-controlled beyond the file system.** The backup
  contains every patient-adjacent and financial record the pharmacy has. Where it is written and who can
  read the folder is a real decision that belongs to the pharmacy, and the settings screen shows the path so
  that it can be made deliberately.
- **It does not protect against the mistakes it is most likely to be needed for.** A restore takes the whole
  database back, not one sale. Correcting a mistake should be done through a reversal or an adjustment
  (ADR 0006, ADR 0008); a restore is for losing the database, not for losing an argument.

### 10. Recovery from a crash or an interruption

The plan asks Milestone 5 to "test recovery from application crash and database interruption". What the
design provides:

- **A crash between a change and its commit** leaves neither, because the change and its audit row are in one
  transaction (ADR 0006, ADR 0009 §3). A half-completed checkout is rolled back by SQL Server, not repaired
  by the application.
- **A crash while the application is idle** costs nothing. The next start reconnects and applies pending
  migrations if `ApplyMigrationsOnStartup` is set.
- **An interrupted restore** leaves the database in `SINGLE_USER` and possibly `RESTORING`. This is the one
  state the application cannot repair itself, and the reason a failed restore is logged with the SQL error:
  the operator needs to know that the database is offline and that the fix is a `RESTORE … WITH RECOVERY` in
  SSMS, not another click in MediStock.
- **A crash caused by the UI** is caught by the three global handlers in `App.xaml.cs` (dispatcher,
  app-domain, unobserved task), which log it and show a message rather than losing the process silently.

### 11. The round trip is covered by a test, against a database of its own

`RestoreTests` creates `MediStock_Test_Restore` from the same migrations, sets it up through the real
bootstrap, records one medicine, takes a backup, adds a second medicine, restores, and asserts that:

- the medicine from the backup is still there and the one added afterwards is gone;
- **the audit row for the medicine added afterwards is gone too**, because the file replaced the whole
  database — the trail went back in time, which is the fact most worth knowing before someone restores
  "just to fix one sale";
- the `RestoreCompleted` row is the newest row, names the administrator and carries the reason, the file
  name and the path;
- the administrator account survives, so the operator can still sign in afterwards — the property that makes
  the two-step workflow safe.

The database and every backup file the run produces are deleted afterwards.

## Consequences

**What this buys.**

- The most likely real-world use of a backup — discovering on Monday that the till's history is wrong — works,
  and it works through the application rather than through SSMS, with a reason on the record.
- A damaged or foreign file is refused before the database is touched, so the failure mode of "restore"
  cannot be a corrupt database.
- A backup taken from MediStock cannot break a server-wide backup strategy, and every backup and restore is
  on the trail.

**What it costs, and what it does not do.**

- **The restore is a blunt instrument.** Whole database, not one sale. The settings screen says so, and the
  refusal wording for a missing reason says so, but an operator who wants to undo one transaction will be
  disappointed by the right answer.
- **A restore can be followed by a stale screen.** The application says "restart"; nothing enforces it. It
  would be possible to force a sign-out and a full reload after a restore, and that is the obvious next
  improvement to this workflow.
- **No retention policy.** Backups accumulate in the folder at roughly the size of the database each. There
  is no pruning, no "keep the last 30", and no warning when the disk fills.
- **No scheduled backup.** Somebody has to press the button. The dashboard footer and the settings screen
  show when the last one was; nothing nags.
- **Not tested against a real SQL Server instance, only LocalDB.** ADR 0004 still applies: the deployment
  target is unconfirmed, and `BACKUP`/`RESTORE` permission is exactly the kind of thing a least-privilege
  deployment will get wrong first. The service needs the operator's login to hold `db_backupoperator` plus
  `ALTER DATABASE` on the MediStock database — not `sysadmin`, which is what a developer's LocalDB login
  has.
- **The audit row after a restore can be lost** (see §7). A restore that is not on the trail is
  indistinguishable from a restore that never happened, and the log line is the only evidence.
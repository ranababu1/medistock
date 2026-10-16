# 0009 — Users, roles, the audit trail, and where a refusal is decided

- **Status:** Accepted
- **Date:** 2026-10-16
- **Deciders:** MediStock maintainers
- Code: [`Role`](../../src/MediStock.Domain/Identity/Role.cs), [`Permission`](../../src/MediStock.Domain/Identity/Permission.cs), [`RoleCatalog`](../../src/MediStock.Domain/Identity/RoleCatalog.cs), [`PermissionSet`](../../src/MediStock.Domain/Identity/PermissionSet.cs), [`User`](../../src/MediStock.Domain/Identity/User.cs), [`PasswordHash`](../../src/MediStock.Domain/Identity/PasswordHash.cs), [`PasswordPolicy`](../../src/MediStock.Domain/Identity/PasswordPolicy.cs), [`AuditEntry`](../../src/MediStock.Domain/Identity/AuditEntry.cs), [`AuditJournal`](../../src/MediStock.Infrastructure/Identity/AuditJournal.cs), [`EfAuthenticationService`](../../src/MediStock.Infrastructure/Identity/EfAuthenticationService.cs), [`EfUserDirectory`](../../src/MediStock.Infrastructure/Identity/EfUserDirectory.cs), [`EfAuditTrail`](../../src/MediStock.Infrastructure/Identity/EfAuditTrail.cs), [`CatalogAuthorization`](../../src/MediStock.Application/Identity/Authorization/CatalogAuthorization.cs), [`AuthorizationTests`](../../tests/MediStock.IntegrationTests/Identity/AuthorizationTests.cs), [`FirstAdministratorTests`](../../tests/MediStock.IntegrationTests/Identity/FirstAdministratorTests.cs), [`database-design.md` §3.14–3.16](../database-design.md), [ADR 0004](0004-localdb-for-development.md), [ADR 0006](0006-context-lifetime-and-execution-strategy.md)

## Context

The plan asks for authentication and role-based permissions, for sensitive actions to be "audited", for
unauthorized actions to be "blocked in the application layer and enforced where applicable at the data
boundary", for audits to "store the actor, timestamp, action, relevant record IDs, and reason where
applicable", and for errors that "do not expose credentials or sensitive information". It names four roles
— Administrator, Pharmacist, Cashier, Inventory operator — and says the first local prototype may run in
"single operator mode" but that named users and permissions are needed "before any real operational use".

Six questions in that brief have more than one defensible answer, and each of them is a decision that is
expensive to reverse once a pharmacy has a year of history:

- **Where the check lives.** A check inside a screen is bypassed by the next screen; a check inside
  `SaveChangesAsync` cannot say *why* it refused; a check inside the EF catalogs puts a policy rule in the
  data layer that a report or an import tool would then have to repeat.
- **Whether a refusal is a message, an exception, or a hidden screen.** The three behave differently when
  somebody is under pressure at a counter with a queue.
- **What the audit trail is for.** A trail written inside the business transaction and a trail written
  afterwards are both called "auditing" and mean different things when something fails.
- **Whether the actor on a row can name somebody who is not signed in.** Sign-in refusals are the awkward
  case: the person who acted is exactly the person the application has decided not to identify.
- **What to say when credentials do not match.** Naming the wrong half helps a legitimate operator and helps
  somebody guessing account names just as much.
- **Whether an administrator may administer themselves,** and whether the last administrator may step down.

Two facts constrain the answers:

- **A single PC, one session.** The application has one signed-in operator at a time (`UserSession`), so
  permissions are read once at sign-in and are not re-read per operation. Nothing in the design should
  pretend the local installation has to defend against a determined attacker with a debugger; it has to
  defend the *records* against accidents, and it has to leave a trail that a tax or drug-sale inspection
  can follow.
- **The database is the record of truth** (ADR 0003, ADR 0006). An audit row that a later failure can
  discard is not a record of anything.

## Decision

### 1. Permissions are named after business activities, and roles are a table

Ten `Permission` values (`Sell`, `ProcessReturns`, `ManageMedicines`, `ChangePrices`, `ReceivePurchases`,
`ManageSuppliers`, `RunReports`, `ManageUsers`, `BackupDatabase`, `ViewAuditTrail`) are grouped into four
`Role`s by `RoleCatalog.PermissionsFor`. `ChangePrices` is deliberately separate from `ManageMedicines`:
a store-room operator who may correct a spelling must not thereby reprice the shelf.

A `User` is a `UserDto` plus a password hash: `UserName`, `DisplayName`, `Role`, `IsActive`,
`CreatedAtUtc`, `LastSignedInAtUtc`. `UserDto.Permissions` is *derived* from the role rather than stored, so
there is no second copy of the policy to fall out of date. Permissions are coarse and named after what the
pharmacy does ("can take a return"), not after the screens that happen to exist.

### 2. The check lives in the application layer, as a decorator over each catalog

Each catalog contract that carries a permission is wrapped in a guard by
`CatalogAuthorization.AddGuardedCatalog<TCatalog, TImplementation>`, driven by a single `Guards` table. The
concrete catalog stays in `MediStock.Infrastructure`, where the database is; the guard lives in
`MediStock.Application`, where the policy is. Consequences that made this worth the indirection:

- **Registering is the only way in.** `AddGuardedCatalog` throws when a contract has no entry in `Guards`,
  and `EfPurchaseCatalog`-style internals are registered as the *implementation*, not as the contract, so
  resolving the contract always gives the guard. A screen cannot reach an unguarded catalog by resolving it
  a different way.
- **`GuardedContracts` and `UnguardedContracts` are data, not prose.** `AuthorizationTests` walks both lists:
  every guarded contract resolves from the container, every unguarded one resolves, and a test asserts that
  every data contract in the application layer appears in one list or the other. A contract added later
  cannot be left out of both by omission.
- **Refusal is one sentence, worded in one place.** `PermissionSet.Require(permission, what)` throws
  `PermissionDeniedException` with *"You do not have permission to {action}. An administrator can grant it
  (permission: {Permission})."* The optional `what` lets a caller say "take this return" rather than naming
  the permission twice. Every refusal in the application therefore reads the same way, and a screenshot of
  one is enough to explain any of them.

The four contracts left unguarded are listed with a reason each in `CatalogAuthorization`:
`IStockQueries`, `IDashboardQueries`, `ISalesQueries` and `IReceiptExporter`. In short: shelf quantities,
today's takings, and an invoice the operator is already holding are things the person at the counter is
entitled to see; the *management* view of the same data is `IReportQueries`, which requires `RunReports`.
`IBackupService` is the exception that proves the rule: it is guarded per method rather than by a decorator,
because `ListAsync` and `VerifyAsync` are reads that must be refused on the same grounds as `RestoreAsync`.

### 3. The audit trail has two writers and one shape

`AuditJournal` is the only thing that adds an `AuditEntry` to a context, and it is entered two ways:

- **`AuditJournal.Stage` adds the row to a context already inside somebody else's transaction** — a
  checkout, a receipt, a return, a purchase correction — and does not save it. The caller's existing
  `SaveChangesAsync` commits it, which keeps the row in the same *transaction*, not merely the same request.
  Every catalogue that changes a record stages its row this way.
- **`IAuditTrail.RecordAsync` writes immediately in its own context,** for work that is not part of a larger
  transaction: a sign-in, a sign-out, a password change, an account edit, a backup, a restore.

Audit writes are never given a retry of their own. A row staged onto a transaction that is being re-run is
discarded with the rest of that attempt's change tracker, so a retry cannot double-record (ADR 0006).

### 4. Every row names an actor, and the only exceptions are the ones with nobody to name

`AuditJournal.ActorOf(session)` returns the signed-in account's name and identifier, or `system` and null
when nobody is signed in. Callers pass the actor explicitly only where the session is *not* the person who
acted. The deliberate cases are:

- A **refused sign-in**: `Actor = "system"`, because naming the attempted account would confirm that it
  exists. It is also the only row where the summary distinguishes an unknown account from a wrong password
  (see §5).
- The **development seeder**, which writes `actor: Operator` and a reason naming it as seed data.

A full audit of every path that writes a row — the seventeen calls to `AuditJournal.Stage`, the private
`Stage` helpers that wrap them, and `EfAuditTrail.RecordAsync` — found three that passed no actor and were
silently recording `system`: `EfAuthenticationService.ChangeOwnPasswordAsync`, and
`EfPurchaseCatalog.CreateAsync` and `UpdateAsync`. All three are fixed, and `AuditTrailTests` includes a
regression guard that a catalogue write names the operator rather than `system`.

The seeder is the case that reads oddly on purpose: it records `actor: Operator` with a reason naming the
rows as sample data, because the alternative is a trail whose first entries look like a real administrator
created three accounts nobody remembers. The till has a related nuance — "sold by" is a free-text box, so
`EfSaleCatalog.OperatorOf` records the name that was typed and links an account identifier only when the
typed name really is the signed-in one.

### 5. A refused sign-in says one thing to the operator and another to the trail

The dialog always shows the same sentence — *"That account name and password do not match an active
account. Check both and try again."* — whether the account is unknown, the password is wrong, or the account
has been deactivated. A refusal path that matches no account still hashes the supplied password against a
fixed decoy hash, so the time taken does not reveal whether the account exists.

The **audit row** distinguishes the three cases, because the only person who can read it is an administrator
who has already signed in. This is a deliberate asymmetry: the interactive wording protects account names
from somebody probing at the counter, and the trail protects the pharmacist from a support call that cannot
be answered.

### 6. No administrator may administer their own account

`IUserDirectory` refuses three operations when the target is the signed-in account:

- **`SetRoleAsync`** — because permissions are read once at sign-in, so a role an administrator gave
  themselves would be recorded as applied and would only take effect at the next sign-in.
- **`SetActiveAsync(false)`** — because deactivating the account you are using is a lock-out with no
  remaining way to undo it.
- **`ResetPasswordAsync`** — because setting your own password from this screen would bypass
  `IAuthenticationService.ChangeOwnPasswordAsync`, whose whole point is that it requires the current
  password first.

### 7. The last-administrator rule is enforced twice, and the reachable guard is the second one

`User.ChangeRole` and `User.Deactivate` take a count of the *other* active administrators and refuse a change
that would leave none. `EfUserDirectory` counts those other administrators **inside the transaction and
under an update lock** (`SELECT COUNT(*) … WITH (UPDLOCK, HOLDLOCK)`) before it asks the aggregate, because
two transactions that each count one other administrator under `READ COMMITTED` would both be told the
installation has a spare and would both demote theirs.

**The rule is not reachable through the public API, and this record says so on purpose.** The caller must
hold `ManageUsers`, which means being an active administrator, and §6 stops them being their own target — so
for any target the count is at least one, i.e. the caller. The aggregate rule is proven by unit tests and
the locked count is exercised by every role and deactivation integration test; what the integration tests
prove is that both guards *hold*, which is the property the pharmacy depends on. Adding a test that inserts
two administrators directly with SQL to reach the aggregate would test a state the application cannot
produce.

### 8. Navigation is not permission-gated

Every screen appears in the sidebar for every signed-in operator. A screen a role may not use does not
disappear: it explains what it needs and who can grant it, using the same sentence as every other refusal.

Concealing a destination is worse than showing it here. On a single-PC install the operator is normally
standing next to the person who can grant the permission, and "I cannot take a return and I do not know
why" is a support call; "Returns needs permission: ProcessReturns, ask Ravi" is a sentence. It also keeps
the permission question in one place: what is *hidden* is nothing, and what is *refused* is decided by
`PermissionSet.Require` on the operation, not by the shell's view of the world.

### 9. Free text is scrubbed and truncated before it is stored

`AuditEntry.Record` runs every free-text field through `SensitiveText.Scrub` (which recognises
`password=…`, connection-string shapes, and bearer tokens) and then truncates it to the column width
(Actor 120, Summary 400, Reason 400, EntityType 60, EntityId 60, Detail 2000). Truncating *before* the
length guard is deliberate and matters most on a refused sign-in: the account name in that summary was typed
by somebody who has not signed in, so a name longer than the column would otherwise throw on the way to
recording the refusal, and the operator would see a different message from every other refusal.

The `Truncate`-then-`Guard` order and the quoted-key case in `SensitiveText` (`"password": "…"` in a JSON
settings file) are both covered by tests. The application log is scanned for the development passwords as
part of the Milestone 5 verification, and neither the row nor the log contains them.

### 10. Passwords are PBKDF2 hashes with a per-installation iteration count

`PasswordHash` stores `iterationCount.salt.hash`, PBKDF2-HMAC-SHA256. The iteration count lives in the stored
value, so raising `MediStock:PasswordIterations` affects new passwords without invalidating old ones.
`PasswordPolicy` requires 10 to 128 characters, four different characters, not in a small list of common
passwords, and not containing the account name. The recommended count is used in production and lowered in
automated tests, where hashing is not what is being tested and 210,000 rounds per account would dominate the
run.

### 11. Timestamps

An audit row's `OccurredAtUtc` comes from `DateTimeOffset.UtcNow` (or an injected instant in a test) rather
than from `IClock`, because the trail records wall-clock reality and a test that moved the clock should not
be able to rewrite it. The *business date*, which decides which day's takings a sale belongs to, does come
from `IClock` with the configured `MediStock:TimeZoneId` (ADR 0002, ADR 0007). The two agree on a
single-PC install in one time zone; the settings screen renders UTC instants in the machine's zone for the
same reason.

## Consequences

**What this buys.**

- One place decides a refusal, one sentence words it, and one table decides which contracts are guarded —
  and the table is a test input rather than a comment.
- A change and the row that records it commit together. A failed checkout leaves neither.
- The trail answers "who, when, what, about which record, and why" for every action the plan lists as
  sensitive, and it is written by the same code path whichever screen was used.
- An installation cannot be locked out: the bootstrap can only run once, no administrator can turn
  themselves off, and the demotion of the last administrator is refused at two levels.

**What it costs, and what it does not do.**

- **There is no re-authentication for a sensitive action.** A signed-in administrator is trusted for the
  life of the session; there is no "confirm your password to restore a backup". Sensible next step before
  real use, and cheap to add, since every guarded operation already receives `IUserSession`.
- **No password expiry, lockout-after-failures or inactivity sign-out.** Refused sign-ins are recorded, so a
  pattern is visible in the trail, but nothing acts on it automatically.
- **The trail is not tamper-evident.** An administrator with database access can edit or delete rows. Every
  row is written through one code path and never updated by the application, which is why the tests
  assert on it, but nothing cryptographically chains the rows.
- **A refusal is an exception, not a result.** `PermissionDeniedException` carries a message already worded
  for an operator, and screens catch it. This is deliberate — an unguarded call must not silently return an
  empty list — but it means a new screen that forgets to catch it shows the message through the global
  handler rather than in place.
- **Permissions are read once at sign-in.** A role change takes effect when that person next signs in. §6
  turns the resulting confusion into a refusal rather than a silent no-op.
- **`ManageUsers` also governs reading the account list.** Knowing who works in the pharmacy is
  administrative information, so `SearchAsync` and `FindAsync` require it; the account list is not a
  directory. `IBackupService.ListAsync` is guarded for the same reason.
- **The last-administrator rule cannot be proven end to end** through the application (see §7). It is
  proven at the aggregate and exercised at the locked count, and the composition is argued rather than
  tested.

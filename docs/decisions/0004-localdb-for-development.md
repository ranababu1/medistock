# 0004 — LocalDB as the development and test database

- **Status:** Accepted, **pending confirmation of the deployment target**
- **Date:** 2026-10-15
- **Deciders:** MediStock maintainers

## Context

The plan specifies "Microsoft SQL Server backed" and "SQL Server Developer during development", and
warns explicitly that "SSMS is a management client, not the SQL Server database engine. Verify that
a SQL Server instance is installed and accessible before starting database work."

An audit of the development machine found:

| Component | Present? |
|---|---|
| `sqlcmd`, SSMS client tooling | Yes |
| SQL Server Developer / Express **engine** (Windows service) | **No** |
| Docker (for a SQL Server container) | No |
| **SQL Server 2025 Express LocalDB 17.0.4025.3**, instance `MSSQLLocalDB` | **Yes** |

Milestone 0 cannot be accepted without a working database, and every subsequent milestone builds on
it. The plan's own "database deployment note" makes the choice of edition a deployment concern, not
a modelling concern. Nothing about the schema, the EF Core provider or the query code differs
between LocalDB and a full instance.

## Decision

**Use SQL Server Express LocalDB (`MSSQLLocalDB`) as the development and test database**, reached
through the same `Microsoft.EntityFrameworkCore.SqlServer` provider that a full instance would use.

Specifics:

- Development database: `MediStock` on `(localdb)\MSSQLLocalDB`, `Trusted_Connection=True`.
- Test database: `MediStock_Test` on the same instance, created and dropped by
  `TestDatabaseFixture` so tests never touch development or real data.
- LocalDB must be started explicitly before use — `sqllocaldb start MSSQLLocalDB` — and this is a
  documented prerequisite in [README.md](../../README.md), because a stopped instance is reported by
  the application as a connection failure and that is the first thing a new contributor will hit.
- The connection string is resolved in one place (`DependencyInjection.ResolveConnectionString`)
  with `MEDISTOCK_CONNECTION` taking priority over `ConnectionStrings:MediStock`, so switching to a
  full instance or a shared developer server is a configuration change, not a code change.
- The database is created and upgraded **only** by EF Core migrations, so "the schema is what the
  migrations say it is" holds identically on LocalDB and on a real instance.

LocalDB was chosen over installing a full engine because it needs no service, no administrator
rights and no licensing decision to get started, and because it removes an installation step from
the "clean checkout builds" acceptance criterion. It was chosen over Docker because Docker is not
installed and would add a second large prerequisite to a project whose first goal is learning C#.

## Consequences

**Easier**

- Milestone 0 could be completed and verified on the machine that exists today.
- New contributors need one command (`sqllocaldb start MSSQLLocalDB`) rather than a SQL Server
  installation and a licensing decision.
- `EnsureDeleted` / `Migrate` cycles for integration tests are fast, which keeps the integration suite
  runnable on every save rather than every release.
- No credentials exist anywhere, because LocalDB is integrated-security only. There is nothing to
  leak and nothing to accidentally commit.

**Harder**

- LocalDB does **not** support remote connections or concurrent multi-user access. Milestone 5's
  "multi-user operation over a local network" is impossible on LocalDB and will require a real
  instance. That is a future problem, but it is a real one.
- LocalDB is a **development** product. It is not licensed for production and must not be the
  pharmacy's database. The plan already says this about SQL Server Developer; it applies here too.
- Behaviour is not byte-identical to a full instance: LocalDB runs as the user, with default
  collation `SQL_Latin1_General_CP1_CI_AS` and a single-user limit. Anything relying on a specific
  server collation, SQL Agent, or a service-level feature must be tested on the real target.
- The acceptance criterion "configuration and connection strings are not committed with secrets" is
  satisfied trivially here, which means it is **not yet proven** for a credentialled instance. The
  layering that makes it true (`appsettings.Local.json` git-ignored, environment-variable override)
  is implemented, but has not been exercised against real credentials.

**Expected of future code**

- Never emit SQL or EF configuration that assumes LocalDB. No `(localdb)`, no
  `AttachDbFilename`, no user-instance syntax outside `appsettings.json` and documentation.
- Anything that depends on server configuration — collation-sensitive sorts, case sensitivity, `SEQUENCE`
  objects, Agent jobs — must be checked against the eventual production instance before it is relied on.
- Milestone 6 must decide, and document, how the database engine and the database itself are
  provisioned on a new pharmacy PC. That decision belongs to that milestone; this record only fixes
  the development story.

## Open item

**The deployment target was never confirmed with the user.** They were unavailable when asked, and
instructed that work should continue without them. LocalDB is therefore an assumption made for
pragmatism, clearly labelled as such.

Before Milestone 6, and certainly before any real deployment, the project owner must choose between:

1. A full SQL Server Express/Standard instance installed on the pharmacy PC.
2. A shared SQL Server instance on the pharmacy's network.
3. Some other deliberate local database strategy.

The change is expected to be configuration plus a migration run, but it should be validated on the
real instance rather than assumed to be free.

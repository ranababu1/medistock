# Release process

How a MediStock version gets from a green working tree to a downloadable artefact on the
[Releases page](https://github.com/ranababu1/medistock/releases).

For the *version numbers*, see [`plan.md`](plan.md) §8. For *what changed*, see
[`CHANGELOG.md`](CHANGELOG.md).

---

## 1. Versioning

`0.MINOR.PATCH`, where `MINOR` tracks the milestone.

| Version | Meaning |
|---|---|
| `0.5.0` | Milestone 5 — users, roles, audit, backup |
| `0.6.0` | Milestone 6 — packaging |
| `1.0.0` | first release intended for a real pharmacy, after validation |

The version lives in exactly one place:

```xml
<!-- Directory.Build.props -->
<Version>0.5.0</Version>
```

It is stamped into every assembly, into the product version shown on the Support tab, and into the
artefact file name. Never edit a version anywhere else.

---

## 2. Cutting a release

### Before you start

- [ ] The working tree is clean or contains exactly the changes being released.
- [ ] `plan.md` and `currentstate.md` describe the state you are about to ship.
- [ ] `CHANGELOG.md` has an `## [Unreleased]` section with everything in it, or the version heading
      has already been renamed.
- [ ] `dotnet build MediStock.slnx` and `dotnet test MediStock.slnx` are green.
- [ ] The development database has been rebuilt from migrations and the reconciliation script is OK.
- [ ] The application has been launched and the screens that changed have been looked at.

### The steps

```powershell
Set-Location C:\Users\Imran\source\repos\MediStock

# 1. Bump the version
#    edit <Version> in Directory.Build.props, then rename the CHANGELOG section
#    from "## [Unreleased]" to "## [0.5.0] - 2026-10-16"

# 2. Prove it builds and passes
dotnet build MediStock.slnx
dotnet test  MediStock.slnx

# 3. Rebuild the database from migrations alone and re-seed
.\scripts\dev\reset-database.ps1
.\scripts\dev\check-reconciliation.ps1

# 4. Launch it and look
dotnet run --project src\MediStock.Desktop

# 5. Commit and tag
git add -A
git commit -m "Release 0.5.0: users, roles, audit and backup"
git tag -a v0.5.0 -m "MediStock 0.5.0"

# 6. Publishes the self-contained build, zips it, and creates the GitHub release
.\scripts\release\publish-release.ps1 -Version 0.5.0

# 7. Push the commit and the tag
git push origin main
git push origin v0.5.0
```

`publish-release.ps1` does steps 6 in full: it publishes, sanity-checks the output, writes a
`SHA256SUMS.txt`, zips it into `artifacts\MediStock-<version>-win-x64.zip`, and — if the GitHub CLI
is authenticated — creates the release and uploads the zip. If `gh` is not authenticated it stops
after producing the artefact and tells you the exact command to run.

---

## 3. Continuous integration

[`.github/workflows/build.yml`](../.github/workflows/build.yml) runs on every push to `main`, on
every pull request, and on demand (`workflow_dispatch`):

| Trigger | What runs |
|---|---|
| push to `main`, pull request | **Build and test** — `dotnet build` + the full suite on a `windows-latest` runner against its SQL Server LocalDB |
| push of a `v*` tag | the same **Build and test** job, then **Publish release artefact** |

The release job is deliberately not a reimplementation: it calls
`.\scripts\release\publish-release.ps1 -SkipTests -SkipRelease` and then `gh release create` with
the runner's `GITHUB_TOKEN`, so **CI and a local release can never drift apart**. `-SkipTests`
exists because the test job has already run the suite.

Consequences for the manual process in §2:

- Step 7 (`git push origin main` + the tag) is what triggers the CI build — push the tag and the
  release appears without any local `gh` authentication.
- The push to `main` itself must already be green, because CI runs the suite again and the red
  ✗ is public on the repository page.
- A `v*` tag is a release. Don't push one for experimentation.

---

## 4. What the artefact is

`MediStock-<version>-win-x64.zip` contains a **self-contained, single-file, framework-dependent-free**
build: the .NET runtime is inside, so the target machine needs no .NET installation. It still needs
a **SQL Server instance** — LocalDB for a single-PC installation, or any reachable SQL Server.

```
MediStock.exe
appsettings.json
appsettings.Development.json
LICENSE / README (as applicable)
```

Configuration files sit **next to** the executable, never inside it, so a pharmacy can point the
application at its own database by editing `appsettings.json` without a rebuild.

The application applies its own migrations on first launch, so a fresh database is created and
brought to the current schema automatically. An empty database triggers first-run administrator
setup; a provisioned one goes straight to the sign-in window.

---

## 5. Validating the artefact on a clean machine

Do this at least once per minor version. It is the only way to find a packaging bug before a
pharmacy does.

- [ ] Copy the zip to a Windows machine that has **never** had MediStock on it and does not have the
      .NET SDK installed.
- [ ] Install SQL Server Express LocalDB (or point `appsettings.json` at an existing instance).
- [ ] Unzip and run `MediStock.exe`. It must open the first-run administrator form.
- [ ] Create the administrator, sign in, and confirm the dashboard renders.
- [ ] Create a medicine, receive a purchase, ring up a sale, print the receipt, return a line.
- [ ] Take a backup, verify it, then restore it and confirm the totals are unchanged.
- [ ] Close the application. Confirm `%LOCALAPPDATA%\MediStock\logs` has a log with no `ERROR`.
- [ ] **Upgrade test:** unzip the next version over the same folder, run it, sign in, and confirm the
      data is intact and any new migration applied.
- [ ] **Uninstall test:** delete the folder, and document what is left behind (the database, the
      logs, the documents folder) so the procedure is honest about it.

---

## 6. Release notes

Write for a pharmacist, not a developer. Say what they can now do, what they should watch out for,
and what the prerequisite is.

A template:

```markdown
## MediStock 0.5.0 — users, roles and backup

Pharmacy staff can now sign in with their own account, and every change to stock, prices, purchases,
returns and accounts is written to an audit trail that names who made it.

### New
- Named accounts, four roles, and first-run setup of the first administrator.
- An audit trail you can filter by person, action and date.
- Back up the database, verify the backup, and restore it — all from inside the application.
- A support tab showing the database state and the recent activity.

### Before you upgrade
- Existing databases are migrated automatically on first launch. Take a backup first anyway.

### Prerequisites
- Windows 10 or later, 64-bit.
- SQL Server Express LocalDB, or any reachable SQL Server instance.

### Not in this release
Manual stock adjustments, expiry write-offs, supplier returns, and PDF/Excel export.
```

---

## 7. Hotfixes

A fix released between milestones bumps `PATCH` — `0.5.0` → `0.5.1` — and gets its own changelog
section and tag. The rule is the same: never ship a fix that is not in `CHANGELOG.md` with the commit
that made it.

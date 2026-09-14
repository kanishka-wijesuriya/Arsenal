# Arsenal Windows repository rules for AI agents

This repository owns only the Windows application. The sibling workspace keeps release
artifacts in `../releases/`; Android, website and G-Helper are separate repositories.

## Git and versions

- After requested changes pass their checks, create a local commit. Do not push unless
  the user explicitly asks.
- Ordinary commit subjects describe the change and contain no version number. Name a
  version only in the commit that creates an explicitly requested stable release.
- `Directory.Build.props` is the single source version for all Windows projects.
- Test builds keep the current stable numeric version. Do not bump the file for tests;
  the sibling test folder is named `<version>t-<slug>`.
- Only an explicitly requested stable release advances the version. Build it fresh; do
  not rename or promote a test executable.

Use `../releases/build-windows-test.ps1` for normal test artifacts. The complete channel,
metadata and website-publication workflow is documented in `../AGENTS.md` and
`../releases/README.md` when this repository is inside the Arsenal workspace.

## Required checks

Run serially for release work:

```powershell
dotnet test Arsenal.Tests/Arsenal.Tests.csproj -c Release -p:Platform=x64 -p:GITHUB_ACTIONS=true -m:1 -nr:false
dotnet build Arsenal.slnx -c Release -p:Platform=x64 -p:GITHUB_ACTIONS=true -m:1 -nr:false
```

Use the relevant `tools/*-smoke` harness for UI changes. Do not delete tests, suppress
CS8602/CS8604, or claim that compilation proves runtime, hardware or multi-monitor
behavior.

## Licence and security

The Windows app is GPL-3.0-or-later because `Arsenal.Core` derives from G-Helper.
`LICENSE` and `NOTICE.md` must remain. Do not weaken signed-feed, package-size or SHA-256
checks. Authenticode signing is separate from update-feed signing, and signing material
must never enter the repository.

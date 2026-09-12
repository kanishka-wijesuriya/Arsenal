<!--
  One concern per pull request. See docs/CONTRIBUTING.md.
  By opening this you are contributing your work under GPL-3.0-or-later.
-->

## What this changes

<!-- What changed and why. Not which files you touched; the diff says that. -->

## Verification

<!--
  State plainly what you did and did not test. A pull request that admits a gap is
  cheaper to review than one that claims more than it checked.
-->

- [ ] `dotnet build Arsenal.slnx -c Release` reports **0 warnings**
- [ ] `dotnet test Arsenal.Tests/Arsenal.Tests.csproj` passes
- [ ] Ran on real hardware, model: <!-- e.g. ROG Zephyrus G16 GU605MI -->
- [ ] Rendered through a `tools/*-smoke` harness (for visual changes)
- [ ] Not verified on hardware, because: <!-- ... -->

## Checklist

- [ ] No test was deleted or weakened to make the build pass
- [ ] `CS8602` / `CS8604` were not added back to `NoWarn`
- [ ] No new dependency, or the new dependency's licence is compatible with GPL-3.0
- [ ] Version bumped in `Directory.Build.props` if this produces a build

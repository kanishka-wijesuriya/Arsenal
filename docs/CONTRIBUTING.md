# Contributing to Arsenal

Thanks for looking. This file is the working agreement, and most of it exists because
getting one of these wrong has already cost somebody a day.

## Licence: read this first

Arsenal is **GPL-3.0-or-later**, because `Arsenal.Core` is a derivative of
[G-Helper](https://github.com/seerge/g-helper). That is an obligation, not a preference:
of the 110 source files the two projects share, 96.7% of the upstream lines are present
here verbatim.

- Every released binary must have its complete corresponding source available to whoever
  receives it. That is what this repository is for.
- [`LICENSE`](../LICENSE) and [`NOTICE.md`](../NOTICE.md) travel with the code, at the
  root of the repository. Do not remove or move either.
- Do not describe the relationship to G-Helper as inspiration, or as a few borrowed
  routines. [`NOTICE.md`](../NOTICE.md) states what came from where; keep it accurate.
- Anything you add is GPL-3.0 too, because it ships as one program with the code above.
  **Do not add a dependency whose licence cannot live with that.** No proprietary SDKs,
  and no source-available-but-not-free libraries.

By opening a pull request you are contributing your work under GPL-3.0-or-later.

## Getting set up

You need Windows 11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build Arsenal.slnx -c Release
dotnet test Arsenal.Tests/Arsenal.Tests.csproj
```

Most of Arsenal talks to ASUS hardware, so a lot of it cannot be exercised on a machine
that does not have it. Say so in your pull request when that is the case. See
[Verification](#verification) below.

## Versions

- The version lives in **one** file: [`Directory.Build.props`](../Directory.Build.props).
  All four projects inherit it, so there is nothing to keep in sync and no project that
  can be forgotten.
- `AssemblyVersion` and `FileVersion` stay numeric (`1.0.0.0`). A pre-release label lives
  only in `InformationalVersion`, which is what the About page reads and what
  `ReleaseVersion.CurrentString` parses.
- Bump on **every** build, not only on release. Five different binaries were once all
  stamped `1.0.1`, which made it impossible to say which file was which.
- A folder name is not evidence of the version inside it. Read the stamped version with
  `(Get-Item Arsenal.exe).VersionInfo.ProductVersion`, then go one past it.
- Keep the project version, the release JSON, the package filename and the executable's
  `ProductVersion` identical. **Never rename an older binary as a newer release.**

## Builds

```powershell
dotnet publish Arsenal.UI/Arsenal.UI.csproj -c Release -p:Platform=x64 -r win-x64 `
  --self-contained false -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

`tools/sign-release.ps1` Authenticode-signs a built executable. It exits 2 with an
explanation rather than pretending when no certificate is configured.

`tools/build-store-msix.ps1` produces the Microsoft Store package. Store builds define
`ARSENAL_STORE`, which compiles out the self-update path. The Store does the updating,
and an app that replaces its own binary underneath a Store install is broken. If you add
anything that downloads or installs an Arsenal build, put it behind that symbol too.

## The update system

The signed `get-arsenal.com` feed, the package size check and the SHA-256 check are the
whole of the update system's security. **Leave them intact.**

`ReleaseFeedClient` trusts a **set** of signing keys, selected by the envelope's
`key_id`. That exists so the key can be rotated: ship a build that trusts both keys, wait
for it to reach the installed base, then switch the server, and only drop the old key in
a later build. Removing a key before the server has stopped using it strands every copy
still running an older version.

## The companion bridge

Pairing is a window the person opens by looking at the pairing screen, not an endpoint
that is always listening. `/v1/pair` must **never** issue a code, because an incoming
request that can mint the credential it is guessing at defeats a six-digit code. Five
wrong codes close the window. Leaving the pairing screen closes it.

`PairingWindow` lives in `Arsenal.Application` rather than beside the bridge so the
pairing rules can be tested without a socket, a certificate or a phone. Keep it that way.

## Tests

`Arsenal.Tests` runs with `dotnet test`. It is deliberately narrow, covering version
comparison, the signed update envelope, range clamping and the pairing rules, because
those are the places a silently wrong answer is expensive and no hardware is needed to
find it.

**Do not delete a test to make a build pass.**

## The keyboard preview's chassis table

`LaptopKeyboardCatalog` maps a chassis family to the keyboard it has. Nothing on the
machine reports the physical arrangement. The Aura probe gives the backlight zoning, the
region code and the light bar, and `HKLM\SOFTWARE\ASUS\RLSinfo` gives the SKU and the
recorded zoning, but none of them says whether there is a number pad. So the table is
hand-written, entries describe a family rather than a single SKU, and the switches on the
Lighting page override it.

When adding a chassis, add a case to `AssertCatalog` in `tools/keyboard-preview-smoke` in
the same commit, and say in the pull request which entries you verified against a real
machine and which you authored from family knowledge.

## Hardware limits

Limits are per-chassis, and the writers silently drop out-of-range values. Bind a control
to the `ControlRange` the machine reports, not to a number you typed.

## Verification

State plainly what you did and did not verify. A pull request that claims more than it
tested costs more to review than one that admits the gap.

- A successful build is not a runtime or hardware test.
- `dotnet build` reporting `0 Warning(s)` is only meaningful because `CS8602` and `CS8604`
  are no longer suppressed. **Do not put them back in `NoWarn`** to quiet a build.
- `tools/*-smoke` projects lay out real views and render them to PNG without hardware.
  Use one rather than claiming a visual change works. Add a state to an existing harness
  when it fits, and a new harness when it does not.

## Pull requests

- One concern per pull request.
- Write the change into the commit message: what changed and why, not what file you
  touched. `git log` is the project's only record of reasoning.
- Match the surrounding code: its naming, its comment density, its idiom. Arsenal's
  comments explain why something is the way it is; they are not narration.
- If you touched hardware behaviour, name the machine you tested on.

## Reporting bugs

Open an issue with your exact model (for example *ROG Zephyrus G16 GU605MI*), your
Windows build, the Arsenal version from the About page, and what you expected instead.

A control that is missing is usually a control Arsenal decided your machine does not
expose. Mention what Armoury Crate shows for the same setting, if you have it installed.

**Security issues do not go in the issue tracker.** See [`SECURITY.md`](SECURITY.md).

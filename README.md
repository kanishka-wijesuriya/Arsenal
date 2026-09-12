<div align="center">

<img src="Arsenal.UI/Assets/arsenal-mark.png" alt="Arsenal" width="120">

# Arsenal

**A native Windows control centre for ASUS ROG and TUF laptops.**

Performance modes, fan curves, GPU switching, display, battery, lighting and drivers
in one lightweight app, with no account and no background service stack.

[![Licence: GPL-3.0-or-later](https://img.shields.io/badge/licence-GPL--3.0--or--later-blue.svg)](LICENSE)
[![Build](https://github.com/kanishka-wijesuriya/Arsenal/actions/workflows/build.yml/badge.svg)](https://github.com/kanishka-wijesuriya/Arsenal/actions/workflows/build.yml)
[![Platform: Windows 11 x64](https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4.svg)](#requirements)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)

[Download](https://get-arsenal.com/download) ·
[Licence &amp; attribution](https://get-arsenal.com/license) ·
[Requirements](https://get-arsenal.com/requirements)

</div>

---

## Built on G-Helper

**Arsenal's hardware layer is [G-Helper](https://github.com/seerge/g-helper) by
[Serge (@seerge)](https://github.com/seerge).** That is not a footnote. `Arsenal.Core` is a
direct derivative: of the 110 source files the two projects share, **96.7% of the upstream
lines are present here verbatim**. Every ACPI call, every fan curve, every register write
that makes this software work came from that project.

Arsenal is GPL-3.0-or-later as a result. That is an obligation of the licence, not a
preference.
What Arsenal adds on top is a WPF shell, a service and view-model layer, and an update
client. Those are GPL-3.0 too, because they ship as one program with the code above.

If Arsenal is useful to you, **[support G-Helper](https://github.com/seerge/g-helper)**.
It came first and it did the hardest part. [`NOTICE.md`](NOTICE.md) records what came
from where, in detail.

---

## What it does

| Area | Controls |
| --- | --- |
| **Performance** | Silent / Balanced / Turbo and custom profiles, SPL / SPPT / FPPT power limits, CPU undervolt, Windows boost policy, per-mode fan curves, fan sensor calibration |
| **Graphics** | GPU Eco / Standard / Ultimate / Optimized, NVIDIA clock offsets and TGP, XG Mobile, discrete-GPU recovery |
| **Display** | Refresh rate and automatic switching, panel overdrive, Mini-LED modes, HDR, GameVisual and gamut, white point, OLED software dimming |
| **Battery** | Charge limit, full-charge override, Windows charging indicator integration |
| **Lighting** | Aura keyboard modes and colour, per-chassis keyboard preview, light bar, AniMe Matrix and Slash, power-state rules |
| **Devices** | ASUS mice and keyboards: onboard DPI, polling rate and sleep timers written directly |
| **Drivers** | Detects installed ASUS drivers and compares them against ASUS support |
| **Handheld** | ROG Ally AutoTDP and FPS limiter |
| **Automation** | Startup actions, clamshell behaviour, scheduled battery and backlight rules |
| **Companion** | Local HTTPS bridge for the Android companion app |

Plus a tray menu, a quick panel on a hotkey, a searchable command palette, a hardware
overlay, and **22 interface languages**.

Controls are **capability-gated**: Arsenal probes what your specific machine exposes and
hides what it cannot reach, rather than offering a switch that silently does nothing.

## Requirements

- **Windows 11**, 64-bit. There is no 32-bit or ARM build, and no Linux or macOS version.
- An **ASUS ROG, TUF, Zephyrus, Flow, Strix, Scar** or **ROG Ally** machine. Other ASUS
  models often work in part; non-ASUS hardware does not, because there is no ASUS ACPI
  interface to talk to.
- [**.NET 10 Desktop Runtime**](https://dotnet.microsoft.com/download/dotnet/10.0) (x64)
  for the lightweight build.
- **No administrator rights** for everyday use. Power-limit writes, undervolting and ASUS
  service control need elevation, and say so where they appear.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```powershell
dotnet build Arsenal.slnx -c Release
dotnet test Arsenal.Tests/Arsenal.Tests.csproj
```

The build is expected to report **0 warnings**. `CS8602` and `CS8604` are deliberately not
suppressed, so a nullability warning is a real finding.

### A single portable executable

```powershell
dotnet publish Arsenal.UI/Arsenal.UI.csproj -c Release -p:Platform=x64 -r win-x64 `
  --self-contained false -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

The result is `artifacts/win-x64/Arsenal.exe`.

### An MSIX package for the Microsoft Store

```powershell
./tools/build-store-msix.ps1
```

Store builds define the `ARSENAL_STORE` symbol, which compiles out the self-update path
entirely. Store packages are updated by the Store, and an app that tries to update
itself underneath it would be wrong. The script writes its output to a `releases/`
folder **beside** the repository, not inside it.

## How it is laid out

```
Arsenal.Core         ASUS hardware: ACPI/WMI, HID, fans, GPU, display, battery,
                     lighting, AniMe Matrix, peripherals, input, the update client.
                     This is the layer derived from G-Helper.
Arsenal.Application  Typed services and models the UI binds to. Hardware access is
                     behind interfaces so it can be reasoned about without a machine.
Arsenal.UI           WPF shell: pages, tray, quick panel, command palette, overlays,
                     and the local companion bridge.
Arsenal.Tests        xUnit. Deliberately narrow: version comparison, the signed update
                     envelope, range clamping and the companion pairing rules.
tools/               Build and release scripts, plus *-smoke harnesses that lay out real
                     views and render them to PNG without any hardware attached.
StorePackage/        Manifest template and icons for the Microsoft Store MSIX.
docs/                Contributing guide, security policy and code of conduct.
```

The version lives in exactly one place, [`Directory.Build.props`](Directory.Build.props),
and all four projects inherit it.

## Updates and the release feed

Arsenal checks a signed feed on `get-arsenal.com`. The envelope is RSA-SHA256 signed, the
client trusts a **set** of keys selected by the envelope's `key_id` so a key can be
rotated without stranding installed copies, and a downloaded package is checked against
both its declared size and its SHA-256 before anything is launched. An unrecognised key
id is refused outright rather than falling back to trying every key.

The signed feed, the size check and the SHA-256 check are the whole of the update
system's security. They prove package integrity and release authority; they are not a
substitute for Windows Authenticode signing, which needs a certificate this project does
not yet hold. [`tools/sign-release.ps1`](tools/sign-release.ps1) is ready for one and
exits with an explanation rather than pretending when none is configured.

The feed client is implemented in
[`Arsenal.Core/AutoUpdate/ReleaseFeedClient.cs`](Arsenal.Core/AutoUpdate/ReleaseFeedClient.cs)
and it is short enough to read in one sitting.

## The Android companion

Arsenal hosts a local HTTPS bridge on TCP port **51117**. Open **Settings → Mobile
companion** for the private-network address and a rotating pairing code.

The bridge uses a persistent TLS identity with certificate pinning, a five-minute
one-time pairing code, and a 256-bit revocable bearer token. Pairing is a window a person
opens by looking at the pairing screen. `/v1/pair` never issues a code, five wrong
attempts close the window, and leaving the screen closes it. Mobile commands run through
the same application services and capability checks as the on-screen controls.

The companion app itself is a separate program in its own repository and is not covered
by this licence; the protocol it speaks is implemented here in
`Arsenal.UI/Services/Remote`.

## Command-line options

| Argument | Effect |
| --- | --- |
| `--startup`, `--minimized`, `-m` | Start in the tray |
| `--quick` | Open only the quick panel |
| `charge` | Scheduled battery-limit / backlight startup action |
| `cpu`, `gpu`, `uv` | Open the relevant performance controls |
| `services` | Open advanced ASUS service controls |
| `colors` | Install the model-specific colour profile and open Display |
| `autoupdate` | Run the update path |

`--quick-test` and `--tray-test` are diagnostic variants that expose the quick panel and
tray menu for automated UI inspection.

## Contributing

See [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md). The short version: the licence obligations in
[`NOTICE.md`](NOTICE.md) are not negotiable, the version gets bumped on every build, a
clean build means zero warnings, and no test gets deleted to make a build pass.

Security issues go to [`docs/SECURITY.md`](docs/SECURITY.md), not the public issue
tracker. Behaviour in project spaces is covered by the
[code of conduct](docs/CODE_OF_CONDUCT.md).

## Licence

**GNU General Public License, version 3 or later.** Full text in [`LICENSE`](LICENSE);
copyright and third-party notices in [`NOTICE.md`](NOTICE.md).

This repository is the complete corresponding source for released `Arsenal.exe` builds.
Every release is tagged. If you hold an Arsenal binary and cannot get the source for that
exact version, that is a bug. Please report it.

> ASUS, ROG, TUF, Armoury Crate, Aura and AniMe Matrix are trademarks of ASUSTeK Computer
> Inc. Arsenal is an independent project, and is not affiliated with, endorsed by or
> supported by ASUSTeK Computer Inc.

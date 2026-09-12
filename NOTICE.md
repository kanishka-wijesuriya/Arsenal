# Arsenal: copyright and third-party notices

Arsenal is free software, licensed under the **GNU General Public License,
version 3 or later**. The full licence text is in [`LICENSE`](LICENSE).

    Arsenal, control software for ASUS ROG and TUF laptops
    Copyright (C) 2026 Kanishka Wijesuriya
    Copyright (C) 2023-2026 Serge (@seerge) and the G-Helper contributors

    This program is free software: you can redistribute it and/or modify it
    under the terms of the GNU General Public License as published by the Free
    Software Foundation, either version 3 of the License, or (at your option)
    any later version.

    This program is distributed in the hope that it will be useful, but
    WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General
    Public License for more details.

    You should have received a copy of the GNU General Public License along
    with this program. If not, see <https://www.gnu.org/licenses/>.

---

## G-Helper

**Arsenal's hardware layer is G-Helper.** This is not a case of shared ideas or a
handful of borrowed routines: `Arsenal.Core` is a direct derivative of
[G-Helper](https://github.com/seerge/g-helper) by Serge (@seerge), carried across
with the namespace renamed from `GHelper` to `Arsenal`. A line-level comparison of
the 110 files common to both projects finds 26,493 of 27,399 upstream lines present
verbatim, or **96.7%**.

Everything Arsenal knows about ASUS hardware came from that work:

| Area | Files |
| --- | --- |
| ACPI / WMI hardware interface | `Arsenal.Core/AsusACPI.cs`, `HardwareControl.cs`, `AppConfig.cs` |
| Fan curves and sensors | `Fan/`, `Mode/` |
| GPU control (NVIDIA, AMD, XG Mobile) | `Gpu/`, `USB/XGM.cs` |
| Display, panel and colour | `Display/` |
| Battery | `Battery/` |
| Aura and keyboard lighting | `USB/Aura.cs`, `USB/AsusHid.cs`, `USB/AsusLampArray.cs` |
| AniMe Matrix and Slash | `AnimeMatrix/` |
| Peripherals (mice and keyboards) | `Peripherals/` |
| CPU interfaces | `Pawn/` |
| Input, hotkeys, ROG key | `Input/` |
| ROG Ally | `Ally/` |

G-Helper is licensed under GPL-3.0. Arsenal is therefore GPL-3.0 as well. This is
an obligation of the licence, not a choice Arsenal made freely, and it means the
complete corresponding source for every released Arsenal binary must be available to
everyone who receives that binary.

What Arsenal adds on top is the WPF shell (`Arsenal.UI`), the service and view-model
layer (`Arsenal.Application`), and the update client. Those are original work, and they
are GPL-3.0 too, because they are distributed as one program with the code above.

This repository is the complete corresponding source for the released `Arsenal.exe`.

The Android companion and the get-arsenal.com website are separate programs, developed
alongside Arsenal but not part of this work, not distributed with it, and **not covered
by this licence**. Neither shares any code with G-Helper: the companion talks to Arsenal
over a network protocol, and the website only hosts the download. Neither is a
derivative work, so no copyleft obligation reaches them. They are not published.

## Starlight

The AniMe Matrix implementation in `Arsenal.Core/AnimeMatrix/` derives
from [Starlight](https://github.com/vddCore/Starlight) by vddCore, reaching Arsenal
by way of G-Helper. The attribution comments in `AnimeMatrixDevice.cs`,
`Communication/Device.cs` and `Communication/Packet.cs` are original and are
preserved deliberately.

## AMD Display Library

`Arsenal.Core/Gpu/AMD/AmdAdl2.cs` follows the interop definitions
published in AMD's [display-library](https://github.com/GPUOpen-LibrariesAndSDKs/display-library)
sample, which AMD distributes under the MIT licence.

## Bundled packages

The Windows application links these at build time. Each remains under its own
licence; none is modified.

| Package | Licence |
| --- | --- |
| WPF-UI | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Extensions.DependencyInjection | MIT |
| QRCoder | MIT |
| FftSharp | MIT |
| HidSharpCore | Apache-2.0 |
| NAudio.Wasapi | MIT |
| NvAPIWrapper.Net | MIT |
| System.Management | MIT |
| TaskScheduler | MIT |

## Icons

Interface icons in `Arsenal.Core/Resources/` come from
[Icons8](https://icons8.com) and are used under the terms of an Icons8 licence.

## Trademarks

ASUS, ROG, TUF, Armoury Crate, Aura and AniMe Matrix are trademarks of ASUSTeK
Computer Inc. Arsenal is not affiliated with, endorsed by, or supported by ASUSTeK
Computer Inc. Those names are used only to describe what this software is
compatible with.

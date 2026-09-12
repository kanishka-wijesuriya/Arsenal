# Security policy

Arsenal writes to ACPI, controls firmware switches, installs drivers, replaces its own
binary and listens on a local network port. A bug in any of those is worth reporting
carefully, so please do not open a public issue for one.

## Reporting a vulnerability

Use GitHub's private reporting:
**[Report a vulnerability](https://github.com/kanishka-wijesuriya/Arsenal/security/advisories/new)**
on the Security tab of this repository. That opens a private thread with the maintainer,
and nothing is disclosed until an advisory is published.

If you would rather use email, write to **legal@get-arsenal.com** with `SECURITY` in the
subject line.

Please include:

- The Arsenal version from the About page, and your laptop model.
- What an attacker gets, and what they need in order to get it: local access, the same
  private network, a malicious update feed, or a paired phone.
- The smallest set of steps that reproduces it.

You should get an acknowledgement within a few days. There is no bounty; this is a
one-person project, and credit in the advisory is what is on offer.

## Scope

Most relevant is anything touching:

- **The update client** (`Arsenal.Core/AutoUpdate/`): feed signature verification, the
  trusted key set, the size and SHA-256 checks, and what gets launched after a download.
- **The companion bridge** (`Arsenal.UI/Services/Remote/`): TLS identity and pinning,
  the pairing window, bearer tokens, and any command reachable without a valid token.
- **Elevation**: anywhere Arsenal asks for administrator rights, or runs something that
  has them.
- **Driver downloads**: the host allowlist, the staging path, and anything that could
  make Arsenal fetch or execute a file from somewhere it should not.

## Not vulnerabilities

- **Arsenal binaries are not Authenticode-signed yet.** The project does not hold a
  code-signing certificate, so Windows SmartScreen warns on download. This is a known
  gap, documented in `tools/sign-release.ps1`, not a finding.
- **Hardware writes that ASUS also allows.** Fan curves, power limits and undervolting
  can make a machine unstable or hot. That is the nature of the controls; Arsenal gates
  them to the ranges the firmware reports.
- **Requiring local administrator rights.** If an attack needs an administrator on the
  machine already, they have won by other means.
- Findings from an automated scanner with no demonstrated impact.

## Supported versions

The latest release is the supported one. Fixes ship in a new version rather than as
patches to older ones.

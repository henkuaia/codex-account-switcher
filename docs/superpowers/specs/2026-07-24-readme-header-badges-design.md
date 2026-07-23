# README Header Badges Design

## Goal

Update the GitHub README header to use the compact, two-part badge style shown
in the CC Switch reference.

## Layout

- Center the project title and existing one-line product description.
- Place one centered badge row directly below the description.
- Keep the rest of the README content unchanged.

## Badges

Show exactly these three badges:

1. `platform | Windows x64`
2. `built with | .NET 9 · WPF`
3. `helper | codex-auth 0.2.10`

Use Shields.io `flat` badges with a dark label segment and distinct colored
value segments. Do not add version, downloads, license, build, test, or other
badges.

## Links

- `.NET 9 · WPF` links to the Microsoft WPF documentation.
- `codex-auth 0.2.10` links to the bundled helper release.
- The platform badge is informational and does not require a link.

## Verification

- Confirm the three badge image URLs render successfully.
- Confirm the README contains exactly the requested three header badges.
- Confirm no existing README sections are modified.

# Changelog

## Unreleased

- Uplift is released under the MIT License (`LICENSE`), which the downloads now include.

## 1.0.0

The first release of Uplift, a GS1 to GS2 converter for Graal servers.

**Downloads.** Self-contained builds that need no .NET on the machine:
- `uplift-1.0.0-win-x64.zip` for Windows;
- `uplift-1.0.0-linux-x64.tar.gz` for Linux.

Each holds `Uplift` (the window), `uplift-cli` (the command line) and an empty `plugins` folder. `SHA256SUMS.txt` has the
checksums.

**What it does**
- Converts database NPCs, weapons, classes, level NPCs and ganis, on the server and the client side.
- Reads GS1 and GS2 with its own parser: GS1's commands with bare arguments, message codes, text arguments, the comparing `=`,
  and GS1's operator precedence.
- Writes GS1's main code as one GS2 event function per event, keeping every comment with its statement.
- Proves every result: the printed GS2 must parse back to the same tree and pass a strict GS2 check, or the part is left as
  it was, with the reason in the report.
- Rules a server can switch on or off:
  - `callnpc-self`, `gs1-level-names`, `self-message-codes`, `command-typos`, `gs1-flags`, `misspelt-events`;
  - its own regex text fixes, GS1 commands and plugins.
- Changes nothing outside the converted scripts: files keep their bytes and line endings.
- A window with light and dark themes and a side-by-side preview, and a command line (`scan`, `convert`, `check`, `part`,
  `rules`, `events`, `init`).

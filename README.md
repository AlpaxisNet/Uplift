<p align="center"><img src="src/Uplift.Gui/Assets/uplift.png" width="96" alt="Uplift" /></p>

<h1 align="center">Uplift</h1>

<p align="center">Converts a Graal server's GS1 scripts to GS2: database NPCs, weapons, classes, level NPCs and ganis.</p>

<p align="center"><a href="https://github.com/AlpaxisNet/Uplift/actions/workflows/ci.yml"><img src="https://github.com/AlpaxisNet/Uplift/actions/workflows/ci.yml/badge.svg" alt="CI" /></a></p>

![Uplift in dark mode](docs/uplift-dark.png)

Uplift opens a server folder, finds every script in it, and shows which ones are GS1, GS2 or broken. You tick what to convert,
preview every file before and after, side by side, and then convert. It comes as a window (light and dark themes) and as a
command line for scripts and automation. It is written in C# for .NET 10, has no other dependencies, and runs on Windows,
Linux and macOS.

- **It reads GS1 and GS2 itself.** A hand-written parser reads both languages, including GS1's old forms: commands with bare
  arguments, `#` message codes, text arguments, and the comparing `=`.
- **It proves every result.** A conversion is only written when its output parses back to exactly the tree it was printed
  from, and passes a strict GS2 check.
- **It changes nothing it doesn't have to.** Comments are kept. Everything in a file outside the converted scripts stays
  byte for byte, line endings included.
- **It explains itself.** Every run writes a report: each part, what it was, what was done to it, what it is now, and why.
- **You can extend it** with rules, regex fixes, GS1 commands, file formats and plugins.
- **It's fast.** A server with about 13,700 script files is scanned in a second or two and converted in about four seconds.

## Download

Get the latest build from [Releases](https://github.com/AlpaxisNet/Uplift/releases): `uplift-<version>-win-x64.zip` for
Windows or `uplift-<version>-linux-x64.tar.gz` for Linux. Unpack it and run `Uplift` (the window) or `uplift-cli` (the
command line). The builds are self-contained: no .NET is needed on the machine. `SHA256SUMS.txt` has the checksums. What
changed in each version is in [CHANGELOG.md](CHANGELOG.md).

## Contents

- [Download](#download)
- [Using the window](#using-the-window)
- [Using the command line](#using-the-command-line)
- [How a script is converted](#how-a-script-is-converted)
- [Settings for a server (`uplift.json`)](#settings-for-a-server-upliftjson)
- [Rules, text fixes and plugins](#rules-text-fixes-and-plugins)
- [What to check after converting](#what-to-check-after-converting)
- [Building](#building)
- [Project layout](#project-layout)

## Using the window

1. Start `Uplift` and choose the server's folder: the one with `weapons/`, `scripts/`, `npcs/` and `levels/`. Uplift scans
   it and lists every script file, with how many of its parts are GS1, GS2 or broken.
2. On the left, choose what to convert:
   - the kinds of script;
   - server-side code, client-side code (after `//#CLIENTSIDE`), or both;
   - the rules and text fixes to apply (hover over one to see what it does);
   - where the result goes: a new folder (optionally with the unchanged files copied too), or in place with a backup.
3. Click a file to see it before and after conversion. The preview opens at the first change and lists each part's result
   and notes. Nothing is written yet.
4. Untick any files you want to leave alone, then click **Convert**. A banner shows the result, with buttons to open the
   output and the report.

The menu at the top right switches between the system theme, light and dark.

![Uplift in light mode](docs/uplift-light.png)

## Using the command line

```bash
uplift-cli scan /srv/myserver --list
uplift-cli convert /srv/myserver --out /srv/myserver-gs2 --copy-unchanged
uplift-cli convert /srv/myserver --in-place --only weapons,classes
uplift-cli convert /srv/myserver --out /tmp/trial --dry-run
uplift-cli check /srv/myserver-gs2 --quiet
uplift-cli part snippet.txt --kind weapon --side client
uplift-cli rules /srv/myserver
uplift-cli events /srv/myserver
uplift-cli init /srv/myserver
```

| Command | What it does |
| --- | --- |
| `scan` | Counts GS1, GS2 and broken files and parts per kind (`--list` lists every file that needs work). |
| `convert` | Converts and writes the files, with a report. |
| `check` | Runs the strict GS2 check on every part; the exit code is 1 when any part fails. |
| `part` | Converts one snippet file and prints the result. |
| `rules` | Lists the rules and text fixes, and whether each is on. |
| `events` | Lists the custom events the server's scripts send by name. |
| `init` | Writes a starter `uplift.json` into the server's folder. |

`convert` takes these options:

| Option | Meaning |
| --- | --- |
| `--out <folder>` / `--in-place` | Where the result goes. In place, every changed file is first copied to `.uplift/<time>/backup`. |
| `--only <kinds>` | `dbnpcs`, `weapons`, `classes`, `levels`, `ganis` (default: all). |
| `--sides <sides>` | `server`, `client` (default: both). |
| `--enable <ids>` / `--disable <ids>` | Switch rules or text fixes on or off. |
| `--files @list.txt` | Convert only the files listed (paths relative to the server). |
| `--copy-unchanged` | With `--out`: also copy the files that need no change, so the output is a complete copy. |
| `--no-repair` | Leave files the server cannot read as they are (a level's missing `NPCEND` or `GLEVNW01`, a gani's missing `SCRIPTEND`). |
| `--no-fix-gs2` | Run the text fixes on converted parts only, not on parts that were GS2 already. |
| `--report <folder>` | Where the report goes (default: `<out>.report`, or `.uplift/<time>/report` in place). |
| `--dry-run` | Convert and report, but write no script file. |
| `--jobs <n>` | Worker threads (default: half the processors, at most 8). |
| `--plugins <folder>` | Another folder of plugins (the `plugins` folder beside Uplift is always read). |

The report has four files:
- `parts.tsv`: every part, before and after, with notes and reasons;
- `files.tsv`: every file, whether it changed, and its problems and repairs;
- `warnings.txt`;
- `summary.json`.

## How a script is converted

**Why not just regular expressions?** Converting GS1 is a change of structure, not of spelling:
- **Events.** A GS1 script's main code runs once for *every* event, with that event's flag (`playerenters`, `timeout`,
  `actionfoo`, ...) set to true. GS2 has one function per event, so the main code has to be specialised for each event. The
  flags are folded to true or false, the dead branches dropped, and no call with side effects may be lost. If the script
  already has a function for an event, it runs after the main code, so the two are merged.
- **Comparison with `=`.** In a GS1 `if` or `while` condition, the first `=` compares, and it binds after `&&` and `||`:
  `a && b = c` is `(a && b) == c`. Its operands are values, where a later `=` assigns: `a = b = c` is `a == (b = c)`. In an
  assignment's value and in a `for` condition, `=` assigns.
- **Names.** A bare name the script never assigns and GS1 does not define is the player's flag: `set gotsword;` and
  `if (gotsword)` are the same variable, written `player.gotsword` in GS2.
- **Old syntax.** Commands take bare text arguments (`setimg door.png;`). The last text argument runs to the `;`, commas
  included, and quotes and `//` inside it are text. Message codes (`#c`, `#p(0)`, `#v(...)`, `#3(-1)`) have to be read.
  `with` blocks on players and `players[0]` mean something different in GS2.
- **Strings and comments** must not change, and every comment has to stay with its statement.

So Uplift parses every part into a syntax tree. It uses regular expressions only where they are safe: *text fixes* on the
converted GS2 code, which never touch strings or comments.

**The steps:**

1. **Read the file.** Each file is read with the server's rules. For a level, the NPC blocks; for a weapon or database NPC,
   the script between its start and end lines; for a class, the whole file; for a gani, its `SCRIPT` block. Each script is
   split at its first real `//#CLIENTSIDE` marker. A marker inside a string or a comment doesn't count.
2. **Decide the language of each part:**
   - **The marker:** the first line comment that starts with `//#GS1` or `//#GS2` decides.
   - **GS2:** the part parses as GS2 and has none of GS1's signs. It is left alone. The signs are:
     - an event flag (`created`, `playerenters`, `timeout`, `actionX` ...) tested outside functions, or `timeout = x;` there;
     - an old command alone outside functions (`dontblock;`; not `hide;`, `show;`, `destroy;` or `enableweapons;`, which
       GS2 scripts call too).

     A part with an event function (`function onCreated()`) is GS2 unless it tests an event outside functions.
   - **GS2 with GS1 main code:** the part's functions are GS2 (they use syntax GS1 doesn't have: `@`, `SPC`, method calls,
     parameters ..., or they are event functions). The code outside functions is read with GS1's rules, and so is any
     single statement inside a function that isn't GS2.
   - **GS1:** anything else that parses with GS1's rules.
   - **Broken:** a part that parses in none of these ways is reported and left as it is.
3. **Convert.** The tree is normalised, then rewritten by the enabled rules. Its main code is specialised for each event into
   `function onX()` handlers, and it is printed as GS2 with its comments.
4. **Prove.** The printed text must parse as GS2 back to the same tree, and pass the strict GS2 check. Otherwise the part is
   left as it was and the report says why.
5. **Fix.** The enabled text fixes run. A fix that would break the strict check is left out.
6. **Write.** Only the replaced scripts change. Every other byte of the file stays as it was: 8-bit text is kept as it is,
   and each line keeps its `\r\n` or `\n`.

Client-side code is converted as well. A client that only runs GS2 needs its client code served as GS2 by the server.

## Settings for a server (`uplift.json`)

This file is optional and lives in the server's folder. `uplift-cli init` writes a starter one.

```json
{
  "folders": { "dbnpcs": "npcs", "weapons": "weapons", "classes": "scripts", "levels": "levels" },
  "customEvents": ["openshop"],
  "scanCustomEvents": true,
  "rules": { "command-typos": false },
  "gs1Commands": { "givegralats": "N", "setmyflag": "VT" },
  "gs1Functions": { "myfunc": "TN" },
  "textRules": [
    {
      "id": "rename-helper",
      "title": "oldhelper( → newhelper(",
      "pattern": "\\boldhelper\\s*\\(",
      "replacement": "newhelper(",
      "side": "server",
      "kinds": ["weapons", "classes"]
    }
  ],
  "events": { "playerjoinsguild": "onPlayerJoinsGuild" },
  "plugins": ["tools/MyServer.Uplift.dll"],
  "overrides": ".uplift/overrides"
}
```

| Setting | Meaning |
| --- | --- |
| `folders` | Where each kind of script lives, if not in the standard folders. |
| `customEvents`, `scanCustomEvents` | GS1 scripts send events by name (`callnpc`, `callweapon`, `trigger`, `scheduleevent`), and the receiving script tests the name as a flag. Uplift finds the names the scripts send, so `if (openshop)` becomes `function onOpenShop()` and is not read as a variable. Add any it cannot find. |
| `rules` | Rules and text fixes switched on (`true`) or off (`false`) by id. |
| `gs1Commands`, `gs1Functions` | GS1 commands (or functions with text arguments) that the standard set lacks, with the kind of each argument: `T` text, `N` number, `V` a variable's name, `D` a direction (up, left, down, right), `B` a script block (`{...}`). A command's last `T` argument takes the rest of the statement. |
| `textRules` | Your own regular-expression fixes of GS2 code. |
| `events` | More GS1 event flags and the GS2 function each becomes. |
| `plugins` | Plugin assemblies (relative to the server's folder, or absolute). |
| `overrides` | A folder of hand conversions: `<hash>.gs2` replaces the part with that hash (the `hash` column of `parts.tsv`), and `<hash>.keep` (with the reason on its first line) leaves it as it is. Later runs use them. |

## Rules, text fixes and plugins

**Rules** rewrite the GS1 syntax tree before the events are split. What GS1 means is always converted; a rule is a choice,
such as a bug fix or a rewrite for engines that read something differently. The built-in rules are all on by default:

| Rule | What it does |
| --- | --- |
| `callnpc-self` | `callnpc(-1, "ev", ...)` (the current NPC) → `this.trigger("ev", ...)` |
| `gs1-level-names` | `players`, `npcs`, `playerscount`, `npcscount`, `setani`, `setbow` in level NPCs' server code → `level.players`, `level.npcs`, `level.players.size()`, `level.npcs.size()`, `player.setani()`, `player.bowimg =` |
| `self-message-codes` | `players[-1]` (the NPC itself) → `this` |
| `command-typos` | `drawoveplayer`, `dontblok` ... (bare words that did nothing) and commands joined to their number (`sleep1;`, which the original compiler dropped) → the command; off, they are kept as comments |
| `gs1-flags` | a bare name the script never assigns, that no script on the server assigns or makes as an object (database NPCs, `new T("Name")`, `Name.f()`) and that GS1 does not define → the player's flag (`if (gotsword)` → `if (player.gotsword)`, `quest.step` → `player.quest.step`). In GS2 syntax with GS1 main code, only names the server uses as flags (`set x;`, `player.x`) |
| `misspelt-events` | `playerenterslevel`, `playertouchesme`, `playerhurted`, `wasshooted`, `PlayerEnters` ... (never true in GS1) → the event they meant |

**Text fixes** are regular expressions run on GS2 code, never inside strings or comments:

| Text fix | What it does |
| --- | --- |
| `drawoverplayer-typo` | `drawoveplayer(` → `drawoverplayer(` |
| `callnpc-self-text` | `callnpc(-1, ...)` → `this.trigger(...)` in parts that were GS2 already |
| `gs1-counts-text` | `playerscount` / `npcscount` → `players.size()` / `npcs.size()` in GS2 server code |

Your own text fixes go in `uplift.json` (`textRules`).

**Plugins** are .NET assemblies with a public class that implements `Uplift.Plugins.IUpliftPlugin`. Put them in the
`plugins` folder beside Uplift, or name them in `uplift.json`. A plugin can add:
- rules (`IConversionRule`, with hooks on the expressions and statements of the syntax tree in `Uplift.Syntax`);
- text fixes (`ITextFix`, or a ready-made `RegexTextFix`);
- GS1 commands and functions (`AddGs1Command`, `AddGs1Function`);
- event flags;
- file formats (`IScriptFormat`).

[`samples/Uplift.Plugins.Sample`](samples/Uplift.Plugins.Sample/SamplePlugin.cs) has one of each.

## What to check after converting

- **Classes converted from GS1.** GS1 ran only the joining script's own main code: a joined class gave it functions,
  and its event tests made the script's code run, but the class's own code outside functions never ran. Converted, that
  code becomes the class's event functions, which GS2 does run. Check that it should. `warnings.txt` also lists every
  script that joins such a class and defines the same event function on the same side: GS2 runs the script's own.
- **Level NPCs without `//#CLIENTSIDE`.** In old worlds, before NPC servers, they ran on the player's client. Converted
  as server code, commands such as `play`, `showimg` or `setimgpart` no longer reach the player.
- **The notes in `parts.tsv`.** They say what changed in form or meaning: merged event functions, bug fixes, `with`
  blocks written with GS1's meaning, player flags, `a && b = c` (GS1 reads it as `(a && b) == c`) and `a = b = c` in a
  value (GS1 sets both).
- **Parts that were not converted.** Each has its reason. Convert them by hand and save the result as an override, so the
  next run keeps it. A command the reason calls unknown can be added in `uplift.json` (`gs1Commands`).

## Building

You need the .NET 10 SDK. Nothing else.

```bash
dotnet build Uplift.slnx -c Release
dotnet test Uplift.slnx -c Release
scripts/publish.sh linux-x64 win-x64 osx-arm64
```

`publish.sh` writes self-contained, single-file builds (no .NET needed on the machine) to `dist/uplift-<runtime>/`:
`Uplift` (the window) and `uplift-cli` (the command line), with an empty `plugins` folder.

GitHub Actions builds and tests every push and pull request on Linux and Windows (`.github/workflows/ci.yml`). Pushing a tag
`v<version>` that matches `<Version>` in `Directory.Build.props` builds the linux-x64 and win-x64 downloads and publishes them
as a release, with that version's section of `CHANGELOG.md` as its notes (`.github/workflows/release.yml`).

The tests include a small sample server (`tests/Uplift.Tests/Fixtures/SampleServer`) and render the window in both themes
without a screen. Set `UPLIFT_SCREENSHOTS=<folder>` to save the renders.

## Project layout

| Path | What it is |
| --- | --- |
| `src/Uplift.Core/Syntax` | The GS1 and GS2 lexer, parser and syntax tree, the GS1 command catalog, and the language decision. |
| `src/Uplift.Core` | Also: formats, the converter, rules and text fixes, the strict check, plugins, scans and conversions. No user interface. |
| `src/Uplift.Cli` | `uplift-cli`, the command line. |
| `src/Uplift.Gui` | `Uplift`, the window (Avalonia, AvaloniaEdit). |
| `samples/Uplift.Plugins.Sample` | An example plugin. |
| `tests/Uplift.Tests` | Parser, conversions, formats (unchanged files render byte for byte), a sample server, and the window. |
| `scripts/publish.sh` | Self-contained builds for download. |
| `docs/` | The screenshots. |

The GS1 command and message-code catalog, and the rules for GS1's operators, events and names, follow GS1's and GS2's
documentation, chiefly the Graal Bible pages *Old GScript*, *Starting Guide: Old script compatibility* and *GS1 To GS2*. The
built-in names a bare name is checked against are the *Global* sections of the client's and the NPC server's script help. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
for the code Uplift includes from other projects.

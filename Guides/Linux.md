# Linux

Compile Pal is a Windows application. On Linux it runs under [Wine](https://www.winehq.org/), the
same way Hammer and the Source compile tools already do, and compiles maps there with the game's own
Windows tools.

This is a supported way to run it, but not a native port: anything Compile Pal does through Windows
itself goes through Wine's version of it.

## What works

Tested with Wine 9.0 and with GE-Proton 11-7 (Ubuntu 24.04), with Garry's Mod and Team Fortress 2
installed as their Windows versions:

| | |
|---|---|
| The window, settings, presets and game setup | Yes |
| VBSP, VVIS, VRAD (stock and Hammer++ tools) | Yes |
| COPY, PACK | Yes. With Hammer++'s bspzip, PACK needs CompilePal#16, which fixes it on Windows too |
| Plugins | Meshwright: yes, from the build after Meshwright#6. Shipwright: its commands and Workshop window work; publishing itself was not tested |
| Finding games through Steam for Linux | Yes, once Hammer has been run for the game - see below |
| GAME, CUBEMAPS | Compile Pal starts the game; whether it runs depends on your Wine setup |
| Map preview, formatted error pages | No - they need Microsoft Edge WebView2, which does not run under Wine. Errors fall back to plain text |
| SHUTDOWN | No - Wine cannot power off Linux. The step says so and skips |

## Setting up

1. **Install Wine** 9.0 or newer, 64-bit. Your distribution's package is fine.

2. **Make a prefix for Compile Pal** - or use the one Hammer already lives in:

   ```bash
   export WINEPREFIX=~/.wine-compilepal
   wineboot -i
   ```

   No extra fonts or runtimes are needed. Compile Pal carries the .NET runtime and its own fallback
   fonts. `winetricks corefonts` is optional and only changes which fonts it draws with.

   Do **not** override `mscoree` (for example with `WINEDLLOVERRIDES="mscoree="` to skip the Wine Mono
   prompt). .NET applications need it to load, and Compile Pal fails at startup without it.

3. **Unpack Compile Pal** anywhere and start it:

   ```bash
   cd ~/CompilePal
   wine CompilePalX.exe
   ```

### With Proton instead of Wine

Proton works the same way. Outside Steam, give it a prefix and a Steam folder (any folder will do)
and use `proton run`:

```bash
export STEAM_COMPAT_DATA_PATH=~/.compilepal-proton
export STEAM_COMPAT_CLIENT_INSTALL_PATH=~/.steam/steam
mkdir -p "$STEAM_COMPAT_DATA_PATH"
/path/to/GE-Proton11-7-x86_64/proton run ~/CompilePal/CompilePalX.exe
```

Proton's prefix has its own placeholder Steam folder at `C:\Program Files (x86)\Steam`, so give game
paths through `Z:` there.

## Games

Install the game's **Windows** version - in Steam, *Properties → Compatibility → Force the use of
a specific Steam Play compatibility tool*. The Linux versions of Source games do not ship the compile
tools.

Compile Pal finds games itself when it can. Under Wine it looks for Steam for Linux in
`~/.steam/steam`, `~/.local/share/Steam`, the Flatpak and the Snap, and reads its library list.
A game appears once it has a `GameConfig.txt`, which Hammer writes the first time it is opened for
that game, the same as on Windows.

To add a game by hand, use **+** in the game selector and give every path through drive `Z:`, which is
how Wine shows the Linux filesystem:

```
Z:\home\you\.local\share\Steam\steamapps\common\GarrysMod\garrysmod
Z:\home\you\.local\share\Steam\steamapps\common\GarrysMod\bin\win64\vbspplusplus.exe
```

## Known limits

- **GAME and CUBEMAPS** start the game's Windows executable directly, with the right arguments. Whether
  the game then runs is down to your Wine setup - it needs working graphics in the prefix, and most
  Source games also want Steam running. If it does not start, open the map from the game console in
  Steam instead. CUBEMAPS waits for the game to close, as it does on Windows.

If something here is wrong for your setup, [report it](Issues.md) with your distribution, Wine version
and the compile log.

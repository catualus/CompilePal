# Plugins (Beta)
Compile Pal's plugin based architecture allows developers to create their own compile steps.

Plugins are currently in a beta state, so the format and structure are subject to change.

![image](https://user-images.githubusercontent.com/15372675/218288001-2154a3fa-201c-4f18-ad0f-36959aed9108.png)

## Installation
Plugins can be installed by copying the plugin folder into the Compile Pal/Plugins folder.

***USE PLUGINS AT YOUR OWN RISK, DO NOT INSTALL PLUGINS FROM UNTRUSTED SOURCES***

## Structure
Plugins consist of a folder that contains a `meta.json` and `parameters.json` file, and optionally other files that the plugin may require to run such as an executable.
```
My Plugin/
  meta.json
  parameters.json
  MyPlugin.exe
```

### Meta.json Structure
`meta.json` is a JSON file that defines the metadata about the compile step
```json
{
  "Name": "string",
  "Description": "string",
  "Warning": "string",
  "Path": "string",
  "Arguments": "string",
  "BasisString": "string",
  "Order": "float",
  "DoRun": "bool",
  "ReadOutput": "bool",
  "SupportsBSP": "bool",
  "CheckExitCode": "bool",
  "CompatibleGames": "int[]",
  "IncompatibleGames": "int[]",
  "WorkingDirectory": "string"
}
```
| Field | Description |
| ----- | ----------- |
| Name    | Plugin Name. Must match the folder name.
| Description | Description shown in the process adder dialog.
| Warning | Warning shown in the process adder dialog.
| Path    | Path to a program, relative to the working directory (by default the Compile Pal folder). Can be templated, see [Variable Substitution](#Variable-Substitution). (For versions <=v27.28, this is relative to the Compile Pal/CompileLogs folder)
| Arguments | The first arguments passed to the program. Can be templated, see [Variable Substitution](#Variable-Substitution). (>=v27.28)
| BasisString | The last arguments passed to the program. Can be templated, see [Variable Substitution](#Variable-Substitution). Order of arguments is `Arguments` → `Arguments selected by user` → `BasisString`.
| Order   | Determines when your step should run. For example, an Order of 1.5 would run between VBSP and VVIS. For the complete ordering, look at the existing compile steps in the `Parameters` folder.
| DoRun		| Controls whether step is enabled by default. Set to `true` to enable it.
| ReadOutput | Controls whether program output is shown in the compile log.
| SupportsBSP | Indicates that this step can be used for BSP files. Steps that don't support BSPs are automatically disabled if a user selects a BSP file. Defaults to `false`. (>=v27.27)
| CheckExitCode | Checks for process exit code and raises a warning when it is not 0. Defaults to `true`. (>=v27.31)
| CompatibleGames | Whitelist of Steam App IDs for games that this plugin is compatible with. Will override IncompatibleGames if both are set. (>=v27.29)
| IncompatibleGames | Blacklist of Steam App IDs for games that this plugin is not compatible with. (>=v27.29)
| WorkingDirectory | Working Directory of the plugin. Defaults to the Compile Pal folder. Can be templated, see [Variable Substitution](#Variable-Substitution). (>=v28.4)
| Configure | A program to run when the user presses the step's settings button, for a plugin that needs to be told something a list of flags cannot express. Templated the same way `Path` is, and given the selected map. Compile Pal runs it, waits for it to close, and re-reads the step's rows; it does not read its output and does not know what it does. Omit it and the step has no button.
| ConfigureLabel | What that button says. Defaults to `Configure`.
| MapStatus | A program run for every queued map, before any compile starts, that prints one line of JSON saying what this step makes of that map. Compile Pal shows it on the map's card and can refuse to start the run. See [Map Status](#map-status).

### Variable Substitution
| Variable | Description |
| -------- | ----------- |
| `$vmfFile$` | Path to the vmf file
| `$map$` | Path to the vmf file without extension
| `$bsp$` | Path to the bsp file
| `$mapCopyLocation$` | Path to the bsp file after copying to the map folder
| `$gameName$` | Name of the current Game Configuration
| `$game$` | Path to the folder of the current Game Configuration
| `$gameEXE$` | Path to the game of the current Game Configuration
| `$mapFolder$` | Path to the map folder of the current Game Configuration
| `$sdkFolder$` | Path to the SDK map folder of the current Game Configuration
| `$binFolder$` | Path to the bin folder of the current Game Configuration
| `$vbsp$` | Path to VBSP for the current Game Configuration
| `$vvis$` | Path to VVIS for the current Game Configuration
| `$vrad$` | Path to VRAD for the current Game Configuration
| `$bspzip$` | Path to BSPZip for the current Game Configuration
| `$vbspInfo$` | Path to VBSPInfo for the current Game Configuration

### Parameters.json Structure
`parameters.json` is a JSON file that defines the parameters for a compile step
```json
[
	{
		"Name": "string",
		"Description": "string",
		"Warning": "string",
		"Parameter": "string",
		"CanBeUsedMoreThanOnce": "bool",
		"CanHaveValue": "bool",
		"Value": "string",
		"ValueIsFile": "bool",
		"ValueIsFolder": "bool",
		"CompatibleGames": "int[]",
		"IncompatibleGames": "int[]"
	},
	...
]
```
| Field | Description |
| ----- | ----------- |
| Name    | Parameter name.
| Description | Description shown in parameter adder dialog.
| Warning | Warning shown in parameter adder dialog.
| Parameter | Parameter passed to the plugin. Should have a space in front of the parameter, Ex. " --foo".
| CanBeUsedMoreThanOnce | Allows the parameter to be used multiple times. Defaults to `false`.
| CanHaveValue | Allows users to pass a value to the parameter.
| Value | Default value for the parameter.
| ValueIsFile | Indicates that value is a file. Adds a button that opens a File Picker dialog. Defaults to `false`.
| ValueIsFolder | Indicates that value is a folder. Adds a button that opens a Folder Picker dialog. Defaults to `false`.
| CompatibleGames | Whitelist of Steam App IDs for games that this plugin parameter is compatible with. Will override IncompatibleGames if both are set. (>=v27.29)
| IncompatibleGames | Blacklist of Steam App IDs for games that this plugin parameter is not compatible with. (>=v27.29)

## Settings Windows

A plugin that needs more than flags - which Workshop item to publish to, which account to use, which
of something to pick from a list - can bring its own window and declare it in `meta.json`:

```json
{
	"Configure": "Plugins\\My Plugin\\my-plugin-ui.exe -vmf $vmfFile$ -bin $binFolder$",
	"ConfigureLabel": "Workshop"
}
```

Compile Pal runs it and waits. What the window writes, and where, is the plugin's business - the same
files it reads at compile time.

**Pass it the map.** A parameter belongs to the preset, and a preset applies to every map in the
queue, so anything that differs per map cannot live in one. `$vmfFile$` here is the map selected in
the queue, and a window that stores its answer per map behaves correctly when several are queued.

The window's process is started with one extra environment variable:

| Variable | Description |
| ------ | ---- |
| COMPILE_PAL_THEME | `dark` or `light`, so a window can match the application it was opened from. |

## Map Status

A step can say something about a queued map before anything is compiled:

```json
{
	"MapStatus": "Plugins\My Plugin\my-plugin.exe status \"$vmfFile$\""
}
```

It is run once per queued map whenever the queue, a map's preset, or which steps are ticked changes,
and again for every map when Compile is pressed. It must print **one line of JSON and nothing else**:

```json
{ "label": "Atlas RP | Downtown", "detail": "Replaces it for everyone subscribed.", "severity": "warn", "confirm": true }
```

| Field | Description |
| ----- | ----------- |
| label | Short text for the chip on the map's card. Required - without one there is no chip. Trimmed to 60 characters. |
| detail | A sentence, shown as the chip's tooltip and in the confirmation. Trimmed to 400 characters. |
| severity | `ok`, `info`, `warn` or `blocking`. Colours the chip; `blocking` also stops the compile from starting. Anything unrecognised is `ok`. |
| confirm | `true` to list this map in a confirmation shown before the run starts. |

Keep it fast and offline: it runs in front of someone who is about to press Compile, and a step that
does not answer within eight seconds is simply not shown. Anything it prints that cannot be read is
no chip at all - never an error dialog, and never a reason someone cannot compile.

The step's own process is given two extra environment variables, because "what will happen to this
map" usually depends on how the step is configured for it:

| Variable | Description |
| ------ | ---- |
| COMPILE_PAL_STEP_ARGS | The arguments this step would be given for this map, under that map's preset. |
| COMPILE_PAL_STEP_ENABLED | `true` or `false` - whether the step is ticked. |

**Only for maps it would actually run on.** A step that is not in a map's preset is never asked about
that map, and an unticked map in the queue is not asked about before a compile.

## What A Step Is Told About The Compile

Every external step's process is started with these set, so a step can see something about the run it
is part of rather than only its own arguments:

| Variable | Description |
| ------ | ---- |
| COMPILE_PAL_ERRORS | Errors logged so far for the map being compiled. A step that does something irreversible - publishing, uploading, deploying - should refuse when this is not `0`. |
| COMPILE_PAL_WARNINGS | Warnings logged so far for the map being compiled. |
| COMPILE_PAL_VERSION | The version of Compile Pal running the step. |

These matter because a failing step does not necessarily stop a compile: only a non-zero exit code
does. A leak, a failed pack or a missing texture all reach later steps with the compile still in
progress, and without these a plugin has no way to know.

## Modifying The Current Game Configuration (>=v27.30)
You can modify the current game configuration by sending `COMPILE_PAL_SET {variable} {value}` through stdout. These changes will persist until the next map is compiled.

| Variable | Description |
| ------ | ---- |
| file | VMF filepath |
| bspdir | BSP directory |
| bindir | Bin directory|
| sdkbindir | SDK bin directory |
| gamedir | Game directory |
| vbsp_exe | Path to VBSP |
| vvis_exe | Path to VVIS |
| vrad_exe | Path to VRAD |
| game_exe | Path to the game |
| bspzip_exe | Path to BSPZip |
| vpk_exe | Path to VPK.exe |
| vbspinfo_exe | Path to VBSPInfo |

For example, sending `COMPILE_PAL_SET file 'new/file/path.vmf'` will update the configuration to point to the vmf at `new/file/path.vmf` instead of what was originally selected.

## Best Practices
For examples, download [PLUGIN DEMO.zip](https://github.com/ruarai/CompilePal/files/9440548/PLUGIN.DEMO.zip) or look at the existing compile steps in the `Parameters` folder.


### Packaging An Application
It is recomended to package your application inside the plugin folder to make it easier to point to. For example, `Path` can be set to `Plugins\\My Plugin\\plugin.exe`.

### Python Plugins
Setting the `Path` to `python` or `python3` is not portable. Use the [Python Launcher](https://docs.python.org/3/using/windows.html#python-launcher-for-windows) `py` (requires Python >= 3.3), passing the python version in the `Arguments`, Ex.
```json
{
	"Path": "py",
	"Arguments": "-3 my_plugin.py",
}
```

## Debugging Plugins
You can view the program path and arguments in the `debug.log` found in the Compile Pal folder.

## Game Plugin Autodiscovery
Source Engine games that ship with additional compilers can also distribute Compile Pal plugin definitions, which can be automatically picked up. 
All thats needed is a `CompilePal` section in the `GameConfig.txt` with a `Plugins` key/value pointing to a folder containing Compile Pal plugins.

ex.
GameConfig.txt
```json
"Configs"
{
	"Games"
	{
		"Team Fortress 2"
		{
			"CompilePal"
			{
				"Plugins"		"C:\Program Files (x86)\Steam\steamapps\common\Team Fortress 2\bin\Plugins"
			}
			...
		}
	}
}

```



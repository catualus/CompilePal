# Native Linux port: plan

Compile Pal is a WPF application. It runs on Linux today under Wine or Proton, which is the
supported route until a native build reaches parity. This document is the plan for getting from
there to a native build, and the record of what has been done so far.

## The shape of it

Split the application in two:

- **CompilePalX.Core** - a plain `net10.0` library with no WPF and no Windows-only APIs: compile
  steps, the compile loop, the logger, error recognition, presets and parameters, game
  configuration, PACK, and the rest of what runs a compile.
- **A UI on top of it.** The WPF app becomes one UI, referencing Core. A cross-platform UI -
  Avalonia is the obvious choice, being XAML with most of WPF's concepts - becomes the other.

The tests move to target Core, so they can run on Linux in CI.

One thing the port does not remove: the compile tools themselves (vbsp, vvis, vrad, bspzip and so
on) are Windows executables for nearly every Source game. A native Compile Pal still runs those
through Wine or Proton. What the port gains is a native window, native file dialogs and paths,
and no .NET-under-Wine layer - not freedom from Wine altogether.

## Ground rules

- Every slice is its own pull request and leaves the app behaving exactly as it did.
- The WPF app keeps working at every step. Nothing is deleted until its replacement is in use.
- Tests pass at every step.

## Done

### Slice 1: no WPF types in the logger or the compile loop

- `CompilePalLogger` takes a severity (0-5, the error catalogue's scale, or
  `CompilePalLogger.Success`) and an optional font weight, and hands back an opaque handle. It used
  to take a WPF `Brush` and return a WPF `Run`, so every compile step had to choose a WPF brush for
  its own text. The window now maps severity to colour in `Theming.SeverityBrushes`.
- `Error.GetSeverityBrush` and `Error.ErrorColor` are gone for the same reason.
- `UiThread` replaces `MainWindow.ActiveDispatcher` and `Application.Current.Dispatcher` in the
  compile loop, plugin status, the updater and the particle conflict prompt. It holds a plain
  `SynchronizationContext`, captured in `App.OnStartup`.
- The preset autosave debounce is a `System.Threading.Timer` that posts the write to the UI thread,
  instead of a WPF `DispatcherTimer`.
- Unused `System.Windows` imports removed from the compile steps.

## Still in the way

What keeps the non-UI code tied to WPF or Windows, by where it has to go.

### UI reached from compile code

These need the compile code to raise an event or call an interface, and the window to act on it.

| Where | What |
| --- | --- |
| `Compiling/OrderManager.cs` | `MainWindow.Instance.UpdateOrderGridSource` / `SetOrder`; `BindingOperations` |
| `Compiling/ProgressManager.cs` | Drives the taskbar through `TaskbarItemInfo` |
| `Compilers/Utility/ParticleUtils.cs` | Opens `ConflictWindow` from the compile thread to ask about particle conflicts |
| `TaskbarFlash.cs` | `user32` FlashWindowEx |

### UI that only looks like core

These are already UI; they move to the UI project as they are.

- `Compiling/LoggedIssue.cs` (holds a `Hyperlink` and a `Brush`), `Compiling/OutputSearch.cs`
  (searches the `FlowDocument`).
- Value converters: `Configuration/StatusSeverityConverters.cs`,
  `Configuration/IsCompatiblePropertyGroup.cs`, `GameConfiguration/ToolsPlusPlusHintConverter.cs`.
- `RowDragHelper.cs`, `Theming/`, every `*.xaml` and `*.xaml.cs`.

### Windows APIs

These want a small platform interface with a Windows implementation and a Linux one.

| Where | What | On Linux |
| --- | --- | --- |
| `RegistryManager.cs` | Compile Pal's own settings in the registry | A file under `$XDG_CONFIG_HOME` |
| `GameConfiguration/GameConfigurationManager.cs` | Hammer's settings from the registry | Read the Wine prefix's `user.reg`, or skip |
| `Compilers/BSPPack/Pack.cs` | Steam's path from the registry | The Linux Steam folders `Platform/Wine.cs` already knows |
| `Compilers/CustomProcess.cs` | `shell32` FindExecutable for a file's associated program | `xdg-mime query default` |
| `Compiling/CompilingManager.cs` | `kernel32` SetThreadExecutionState to stop sleep mid-compile | `systemd-inhibit`, or the freedesktop inhibit portal |
| `Crash/CrashReporter.cs` | `user32` message box | The UI's own dialog |
| `Compiling/ErrorWindow.xaml.cs`, `Preview/MapPreviewView.xaml.cs` | WebView2 | Avalonia's web view, or render without a browser |
| `CompilePalX.csproj` | `Microsoft.Windows.Compatibility` | Drop once the above are behind the interface |

### Running Windows tools from a Linux process

- Launching a step's executable has to go through Wine or Proton, with the right prefix and
  environment, behind one process launcher.
- Paths cross the boundary both ways: game configuration and the map are Linux paths to Compile
  Pal and `Z:\...` paths to the tools. `Platform/Wine.cs` (from the Wine support work) already
  does this translation in one direction.
- Case sensitivity. PACK and the content scanners find files by the case written in materials and
  models, which Windows forgives and Linux does not. Lookups need a case-insensitive fallback.
- Folders such as `./Presets` and `./Parameters` are relative to the working directory, which a
  Linux install will not keep beside the executable. They should resolve from a known data folder.

## Next slices, in order

1. **Break the UI reach-backs.** `OrderManager`, `ProgressManager` and `ParticleUtils` raise events
   or call an interface the window implements, instead of naming `MainWindow` or WPF types.
2. **Platform interface.** One `IPlatform` (settings store, keep-awake, associated program, flash)
   with the Windows implementation moved behind it unchanged. `Platform/Wine.cs` folds in here.
3. **CompilePalX.Core project.** Move the code that no longer references WPF into a `net10.0`
   library; the WPF app references it. Point the tests at Core and run them on Linux in CI.
4. **Process launcher and paths.** Wine/Proton launching, path translation and case-insensitive
   lookup, in Core, with a Linux implementation.
5. **Avalonia shell.** Main window first - queue, steps, parameters, output - then settings, game
   setup, the error window and map preview.
6. **Packaging.** A tarball or AppImage, and native Steam and game discovery replacing the
   registry route.

For a sense of scale: the UI is 17 XAML views, of which the main window alone is about 1,200
lines of XAML and 3,000 of code-behind. There are about 80 other source files, most of which
should move to Core with little or no change once slices 1 to 3 are done.

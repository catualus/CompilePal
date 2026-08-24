<p align="center">
	<img
		alt="Compile Pal"
		src="http://i.imgur.com/jPEig83.png"
		width="400"
	/>
</p>

<p align="center">An easy to use wrapper for the Source Engine map compiling tools.</p>

<p align="center">
	<em>A fork of <a href="https://github.com/ruarai/CompilePal">ruarai/CompilePal</a>, with a rebuilt
	interface, faster setup, and better failure reporting.</em>
</p>

![Compile Pal's main window](docs/images/main-window.png)

## Downloads

Windows only. Self-contained — no .NET install required.

#### Latest release

[**Download the latest release**](https://github.com/catualus/CompilePal/releases/latest)

#### Pre-release builds

New features before they are settled. Expect rough edges.

[Latest pre-release](https://github.com/catualus/CompilePal/releases)

Looking for the original? It lives at [ruarai/CompilePal](https://github.com/ruarai/CompilePal).
Version numbers here are unrelated to its own — see [Versioning](#versioning).

## What this fork changes

Everything below is new since the fork point. The compile tooling itself is unchanged — this is
the same VBSP/VVIS/VRAD wrapper, with the parts around it rebuilt.

### Interface

* **Rebuilt main window**, organised around the compile itself: a map queue, the steps in run
  order with their arguments visible, and separate tabs for order, history and output.
* **Follows your Windows light/dark setting**, or can be pinned to either in Settings.
* **Every step says what it does** on its own row, instead of requiring you to know already.
* **Searchable step and preset pickers**, rather than a long unordered list.
* **The output font is yours to choose** from the monospace families you have installed.

### Setup

* **Finds your Source games automatically** by scanning your Steam libraries, instead of asking
  you to point at each `gameinfo.txt` by hand.
* **Detects Hammer++ compile tools** next to the stock ones and prefers them, so `vbsp++` and
  friends are used without reconfiguring anything. Can be forced on or off.
* **Says which game configuration key is missing** when one cannot be read, instead of dropping
  the game silently.

### While compiling

* **A progress bar that means something.** Steps are weighted by how long they have actually
  taken on previous runs, so it moves at a roughly constant rate instead of stalling through
  VVIS and then leaping.
* **An estimate of time remaining**, once there is history to base one on.
* **An issues list** collecting every recognised error and warning, each one clickable to jump
  to where it happened in the output.
* **Jump between compile steps** in the output rather than scrolling for the next banner.
* **Search the output**, with matches highlighted.
* **Per-map result chips** — succeeded, failed or cancelled, with duration and error counts.

### After compiling

* **Compile history**, with the full log of previous runs kept and viewable.

### Errors

* **Error descriptions render in a modern browser engine** (WebView2) rather than the
  Internet-Explorer-era control, and are **readable in dark mode** — they previously came out as
  dark text on a dark background.
* **Error recognition works offline.** The catalogue ships with the app, so a fresh install still
  explains compile errors when the upstream error site is unreachable — which it frequently is.
* **Extra error definitions** for messages the original catalogue predates.

### Automatic fixes

* **VMFFIX**, an optional compile step that repairs common map problems before compiling: light
  falloff values the wrong way round, props that need `$staticprop`, and missing material
  references. Supports a dry run, and backs the map up first.

### Presets

* **More presets out of the box** — Draft, Fast, Good, Best, Best (tools++), and Publish and Full
  variants for LDR, HDR and both.
* **Compare two presets side by side** to see exactly which parameters differ.

### Reliability

* Compiles no longer abort part-way through on a queue change.
* A cancelled compile no longer leaves its last partial line at the top of the next run's output.
* The error catalogue is no longer re-downloaded on every shutdown, which used to exhaust the
  source's rate limit within a few launches and stop error descriptions loading at all.
* Asset paths from a map can no longer escape your content folders when packing.

### Privacy and updates

* **Usage reporting is opt-in and off by default.** The original reported from every install with
  no way to refuse. See [Privacy](#privacy).
* **The update check points here**, not at the original — so it no longer tells you this build is
  outdated whenever the original publishes a release.

## What Compile Pal does

* Packing
* Error checking
* Not freezing your computer while compiling
* Cubemaps
* Manifest generation
* Nav file generation
* Plugins and custom compile steps
* Batch compiling

## Versioning

This is a fork of [ruarai/CompilePal](https://github.com/ruarai/CompilePal), and it numbers itself
independently, starting at **1.0.0**. Upstream's scheme (`029`, `029.1`) is not continued.

That is deliberate rather than cosmetic. Sharing a number line with upstream would leave "which
build of Compile Pal 030 are you running?" with no answer. Upstream's scheme also has no minor
version — the minor slot holds a prerelease counter — and orders prereleases *above* the release
they precede, so `029.1` outranks `029` and a release-candidate user is never offered the finished
build.

Releases here follow [Semantic Versioning 2.0.0](https://semver.org): `1.2.0` for a release,
`1.2.0-rc.1` for a candidate, which correctly sorts below it. Version numbers here therefore say
nothing about upstream's — a fork at 1.4.0 is not "behind" upstream at 029.

## Privacy

Compile Pal sends **nothing** unless you turn usage reporting on in Settings. It is off by
default and there is no first-run prompt that quietly opts you in.

If you do turn it on, one summary is sent as the app closes:

* how many times you launched it, compiled, and how many compiles finished clean, failed or
  were cancelled
* how many compile errors were recognised
* the Compile Pal version and Windows build number
* which of the games Compile Pal ships a configuration for you used — anything else is
  reported as `other`, so a renamed configuration never leaves your machine

There is **no identifier of any kind** in it: no account, no machine ID, no hardware
fingerprint, nothing that distinguishes your install from anyone else's. Map names, file
paths, presets and compile output are never sent. Nothing is written to disk waiting to be
sent, and a failed send is dropped rather than retried.

**Settings → Privacy → Show what is sent** prints the exact message, generated from the same
values the send uses, so you can check rather than take our word for it. The totals everyone
contributes to are public at
[/api/telemetry/v1/stats](https://telemetry.catualus.dev/api/telemetry/v1/stats).

Those public figures are deliberately coarse. Compile Pal has a small user base, and "aggregate"
stops meaning "anonymous" when a sum describes one person — a daily figure reading *1 install,
version 029.1, Garry's Mod, 3 compiles* is somebody's working day. So the endpoint reports weeks
rather than days, withholds any figure describing fewer than 5 installs, publishes nothing at all
until a window covers 25 install-days, never reports your OS version in any form, and never
combines those dimensions. Where rows are withheld it says how many, so you can see that
something was omitted rather than being handed a quietly partial answer.

The destination is compiled into official builds and is not a setting — a reporting endpoint
read from a file on your disk would be an obvious thing for malware to repoint. It also means
**builds that are not ours report nowhere at all**: if you build from source, or run a fork, the
endpoint is absent and nothing is sent no matter what the toggle says.

Upstream Compile Pal reports usage from every install with no way to refuse. This fork does
not, and none of its data goes to upstream.

## Guides
* [Quick Start](Guides/QuickStart.md)
* [Reporting An Issue](Guides/Issues.md)
* [Plugin Development (Beta)](Guides/Plugins.md)
* [Custom Compile Steps](Guides/Custom.md)
* [Custom Compile Step Collection](Guides/CustomCollection.md)
* [Command Line Arguments](Guides/CMDArgs.md)
* [Registry Values](Guides/Registry.md)
* [VScript Packing Hints](Guides/VScript.md)

## Building

Compile Pal links [NavPal](../../../NavPal), the offline navigation mesh generator behind the NavPal
compile step, which lives in its own repository. Clone with submodules so it comes along:

```bash
git clone --recurse-submodules <this repo>
```

If you already cloned without them:

```bash
git submodule update --init --recursive
```

A checkout of NavPal sitting beside this repository is also picked up automatically, which is
convenient when working on both at once. If neither is present the build stops with a message saying
so rather than a wall of unresolved-type errors.

## Contributing

New features and bugfixes are welcome — open a pull request, or
[report an issue](https://github.com/catualus/CompilePal/issues) here.

Please report bugs against **this fork** rather than upstream unless you can reproduce them in
[the original](https://github.com/ruarai/CompilePal) too. Most of what has changed here is not
theirs to fix.

### This fork
- [catualus](https://github.com/catualus)

### Original Compile Pal
- [ruarai](https://github.com/ruarai)
- [maxdup](https://github.com/maxdup)
- [Exactol](https://github.com/Exactol)
- iMilo


### Bug Testing
- wareya
- Gangleider 
- Matt2468rv 
- Sevin7 

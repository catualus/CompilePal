<p align="center">
	<img
		alt="Compile Pal"
		src="http://i.imgur.com/jPEig83.png"
		width="400"
	/>
</p>

<p align="center">Compile Pal is an easy to use wrapper for the Source Engine map compiling tools.</p>

![image](https://user-images.githubusercontent.com/15372675/219901251-38a9dc7a-ab95-42c4-9794-e811521a5e89.png)



## Downloads

#### Latest Release

[Compile Pal V29](https://github.com/ruarai/CompilePal/releases/latest)

#### Experimental Releases
Get the latest features without having to wait. Experimental releases may be unstable, use at your own risk.

[Compile Pal V29.1](https://github.com/ruarai/CompilePal/releases/tag/v029.1)


## Features
* Packing
* Error Checking
* Not freezing your computer while compiling
* Cubemaps
* Manifest Generation
* Nav File Generation
* Plugins and Custom Compile Steps
* Batch Compiling
* Much More!

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

New features or bugfixes are always welcome. Feel free to create a pull request. We also make good use of any issues [reported to us](https://github.com/ruarai/CompilePal/issues).

### Developers
- [ruarai](https://github.com/ruarai)
- [maxdup](https://github.com/maxdup)
- [Exactol](https://github.com/Exactol)
- iMilo


### Bug Testing
- wareya
- Gangleider 
- Matt2468rv 
- Sevin7 

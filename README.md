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

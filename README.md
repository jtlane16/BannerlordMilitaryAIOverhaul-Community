# Bannerlord Military AI Overhaul

A single-player mod for Mount & Blade II: Bannerlord. It gives each kingdom a commander that coordinates army formation, sieges, and relief across the campaign map. It leaves the player in control of their own party and armies and uses Bannerlord's native actions to carry out orders.

In play, commanders can reuse available armies, muster new ones when needed, and shift forces from an offensive siege to an urgent defense. The campaign log on your Desktop explains what the commanders chose and what happened next.

This is a source preview for contributors. To try it in-game, build and install it using the steps below.

## Build from source

You need Windows, a .NET SDK with C# 10 support, a local Bannerlord installation, and the `Bannerlord.Harmony` module installed in that game. The mod targets .NET Framework 4.7.2. The project uses the game and Harmony assemblies from your installation; it doesn't bundle them. The build and headless smoke tests were verified with Bannerlord 1.5.7.

Clone this repository or your fork. From the repository root in PowerShell, replace the example with your installation folder (the one containing `bin` and `Modules`). In Steam, you can find it under **Manage → Browse local files**.

```powershell
$env:BANNERLORD_DIR = 'C:\path\to\Mount & Blade II Bannerlord'
dotnet build -c Release
```

You can also pass `"-p:BannerlordDir=$installPath"` to `dotnet build` to override the environment setting. A normal build writes to `bin/Release/net472/` and does not install the mod.

To build and install locally, close the game and its launcher first:

```powershell
dotnet build -c Release -p:DeployToGame=true
```

This copies the module files to `Modules/BannerlordMilitaryAIOverhaul` under your game installation. Enable **Bannerlord Military AI Overhaul** and **Harmony** in the launcher. Use a disposable copy of a save when testing development builds.

## Test and contribute

Run the headless smoke tests from the repository root in **64-bit Windows PowerShell 5.1**:

```powershell
.\tests\RunSmoke.ps1
```

The script uses the same `BANNERLORD_DIR` setting (or `-BannerlordDir`), prints the location of its temporary artifacts, and doesn't deploy to the game. Use a fresh PowerShell process for each run so the assemblies loaded for one run don't affect the next. The live campaign test notes are in [`tests/`](tests/).

All changes to `main`, including maintainer changes, go through pull requests. See [CONTRIBUTING.md](CONTRIBUTING.md) for the workflow, [SPEC.md](SPEC.md) for intended behavior, and [ARCHITECTURE.md](ARCHITECTURE.md) for the code layout.

The mod's original code is licensed under the [MIT License](LICENSE). Bannerlord and Harmony are separate dependencies under their own terms.

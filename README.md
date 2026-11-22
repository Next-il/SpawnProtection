![GitHub tag (with filter)](https://img.shields.io/github/v/tag/audiomaster99/SpawnProtection?style=for-the-badge&label=Version)
![GitHub Repo stars](https://img.shields.io/github/stars/audiomaster99/SpawnProtection?style=for-the-badge)
![GitHub all releases](https://img.shields.io/github/downloads/audiomaster99/SpawnProtection/total?style=for-the-badge)

## Features

- Configurable spawn protection
- Customisable display message
- Render player models transparent
- Timer with progress bar
- CT Only protection (can be enabled in config)

![Spawn Protection Plugin](https://files.cfg-filetransfer.eu/CS2/site/spawn-protection.gif "SpawnProtection")

## HUD (Next-il fork)

The center HTML timer is replaced by a small Panorama pill 60px under the crosshair: "Spawn protected", the
seconds left and a bar that sits full through freeze time and drains once the protection timer starts. When
protection runs out, or stops because the player fired or moved, it turns into a red "Protection ended" line
for 1.5s (`spawn-prot-end-announce: false` makes it just leave). Nothing is drawn per tick: the bar is one CSS
transition and the seconds change once a second.

```json
"Hud": { "Enabled": true }
```

`Enabled: false` brings the old center text back unchanged. `display-timer: false` still shows nothing at all.

The layout ships in the workshop addon: `hud/panorama/layout/custom_game/spawnprotect_hud.xml`,
`hud/panorama/styles/custom_game/spawnprotect_hud.css` and the shared `hud_shared.css`. The plugin needs
CounterStrikeSharp 1.0.374 or newer (.NET 10) and loads `PanoramaManager.dll` from its own folder. Without the
layout on the client the pill does not draw; set `Hud.Enabled` to false until the addon is out.

## Dependencies

- [Metamod:Source](https://www.sourcemm.net/downloads.php/?branch=master "Metamod:Source")
- [CounterStrike Sharp](https://github.com/roflmuffin/CounterStrikeSharp "CounterStrike Sharp")

## Installation

- Download latest [Release](https://github.com/audiomaster99/SpawnProtection/releases) and place its contents inside **csgo/addons/counterstrikesharp/plugins/SpawnProtection**

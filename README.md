# TrueColorLayer

A client-side mod for [Vintage Story](https://www.vintagestory.at/) that adds a "TrueColorLayer" map tab with true block colors, 3D height-based shading, and automatic seasonal snow-skipping. Shows your world in satellite-like detail - spot biomes, ore patches, and base layouts easily.

## Features
*   **True Block Colors:** Renders map using actual block colors (like color-accurate mode)
*   **3D Shading:** Height-based shading creates depth perception (brighten slopes up, darken slopes down)
*   **Snow-Skipping:** Optional feature replaces seasonal snow with the ground below (preserves permafrost/glacier)
*   **Performance Optimized:** Reuses buffers (ThreadStatic), caches snow lookups, preserves cache between map opens
*   **Client-Side Only:** Works on multiplayer servers without needing the mod installed on the server

## Configuration
Edit `VintagestoryData/ModConfig/truecolorlayer.json`:
```json
{
  "ReplaceDefaultMap": false,     // Set true to replace the default "Paper" map tab
  "DisableSnowInWinter": true    // Skip seasonal snow (default: true, preserves permafrost/glacier)
}
```

## Changelog

### v1.2.0 (Current)
- **Snow-Skipping:** Seasonal snow replaced with ground below (configurable via `DisableSnowInWinter`)
- **Performance:** ThreadStatic buffers, snow lookup cache (`snowCache`)
- **3D Shading:** Height-based shading like original map (brighten/darken slopes)
- **Config:** `DisableSnowInWinter: true` by default, preserves permafrost/glacier ice
- **Cache:** Preserves rendered chunks between map opens (no unnecessary redraws)

### v1.1.1
- Initial release with true block colors
- Based on gi-map by Marat Zaripov

## Installation
1.  Download the latest release.
2.  Place the `.zip` file into your `VintagestoryData/Mods` folder.
3.  Open your map in-game (`M` by default) and select the **"TrueColorLayer"** tab.

## Compatibility
Tested and optimized for Vintage Story **1.21.6+**.

## Credits
This mod utilizes core logic patterns inspired by the [gi-map](https://github.com/m-zaripov/gi-map) project by **Marat Zaripov**.

## License
Distributed under the MIT License. See `LICENSE` for more information.

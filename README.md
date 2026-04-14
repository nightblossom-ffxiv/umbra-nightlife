# Umbra Nightlife

An [Umbra](https://github.com/una-xiv/umbra) toolbar widget that shows which FFXIV
venues are open right now and lets you teleport to any of them with one click.

Data courtesy of [ffxivvenues.com](https://ffxivvenues.com/).
Teleport courtesy of [Lifestream](https://github.com/NightmareXIV/Lifestream).

## Install

In-game:

1. `/umbra` → **Plugins**
2. Click **Add plugin**, enter:
   - Repository owner: `nightblossom-ffxiv`
   - Repository name: `umbra-nightlife`
3. Click **Install** and restart Umbra.
4. On the toolbar, click **Add widget → Tonight in Eorzea**.

## Features

- Two sections: **Open now** (live venues on your data centre) and **Opening soon**.
- Per-venue one-click teleport via Lifestream.
- Filters: data centre, SFW only, open-only, max items.
- Disk cache of the FFXIVVenues catalogue — survives network blips.

## Build from source

Requires Dalamud + Umbra installed via XIVLauncher on Windows.

```
dotnet build
```

The output DLL lands in `out/Debug/UmbraNightlife/UmbraNightlife.dll`.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE).

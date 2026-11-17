# Freeze Together

**Co-op multiplayer for The Long Dark.** Share one world, see and hear each other, trade, revive each other and, if the host allows it, fight.

![Game](https://img.shields.io/badge/The%20Long%20Dark-2.60-4b6584)
![Loader](https://img.shields.io/badge/MelonLoader-0.7.x-6c5ce7)
![License](https://img.shields.io/badge/license-MIT-2ecc71)
![Status](https://img.shields.io/badge/status-early%20build-e67e22)

> **Status:** early build. The mod loads and its patches apply; most multiplayer features have had little testing with several players. Each section below says what to expect. If something breaks, [open an issue](../../issues) and attach `MelonLoader\Latest.log`.

---

## Table of contents
- [About](#about)
- [Features](#features)
- [Installation](#installation)
- [Compatibility](#compatibility)
- [Development](#development)
- [Acknowledgments](#acknowledgments)
- [License](#license)

---

## About

Freeze Together is a standalone multiplayer mod for **The Long Dark 2.60**, built on [MelonLoader](https://github.com/LavaGang/MelonLoader). It grew out of legacy multiplayer code (see [Acknowledgments](#acknowledgments)), and a large part of that code has since been rewritten, reworked or replaced: the player model, world and item synchronization, the lobby and server settings, the downed/revive and respawn rules, and support for the current game version.

The project is developed with AI as a development tool. Changes are built and reviewed by the maintainers, and the code is the source of truth for how the mod behaves.

The mod is unofficial and is not affiliated with Hinterland Studio.

---

## Features

### Multiplayer core
- **Hosting:** through a **Steam P2P lobby** (no port forwarding) or by **IP** (UDP port `26950` by default). The lobby has a region and experience vote and a spawn style choice.
- **Reliable ordered UDP channel** for important packets, and a server-side lock so two players cannot loot one container at once.
- **Shared world:** weather, time, animals and carcasses, doors, containers, fires, dropped and picked-up items.
- **Communication:** text and voice chat, emotes, short-wave radio channels.
- **Expeditions** with an in-game editor.
- **Localization:** English and Russian, following the game language (`Language` in `UserData/MelonPreferences.cfg`: `Auto`, `English`, `Russian`).

### Host settings
Set in **Multiplayer → Host Server**; the last used values are remembered.

| Setting | Options |
|---------|---------|
| Items duplication / Containers duplication | Off: whatever one player takes disappears for the others. On: every player can take it. |
| Spawn style | Same as host, client's choice, random, fixed place |
| Players | Maximum number of players |
| Steam P2P / Port | Steam lobby, or an IP server on a chosen port |
| Availability (Steam) | Invites only, friends only, public (listed in the server browser) |
| Fire sync | Disabled, or shared fires and shared fuel |
| Cheats | Disabled, host only, everyone |

Host tools in game: **kick** a player from the player list in the Esc menu, **close the lobby** (Steam P2P) from the lobby menu, and the `kick` / `ban` server commands.

### Shared world items
All players see the same items in the same places. Nothing is rolled separately for each player.

| What | How it is shared |
|------|------------------|
| Sticks, stones, limbs (radial spawners) | One player per scene (the host when present) runs the game's own spawning and respawn timers and sends each spawner's saved state; the others load it. An item taken by anyone disappears for everyone. |
| Loose items on the floor and inside buildings | The first player to have an area ready records which items lie where. The host keeps the first record it receives and every other player applies it once: missing items are created, extra ones are removed. |
| Containers | Unopened containers roll their loot from a seed made from the container's GUID, so it is the same for everyone. After the first close, the host stores the contents and sends them to whoever opens the container next. |
| Campfires, stoves, fireplaces | One state per fire (the game's own save snapshot), identified by the fire's GUID and kept by the host. Fires are reapplied when a player enters a building. |

Notes and story items are left out of the shared records, so each player keeps their own story progress. Items dropped or moved by another player can be moved with the game's right-click action; the item exists only once, either in the host's list or in the hands of the player moving it.

### Players
- **Humanoid player model** with clothing that uses the game's own materials, including wear.
- **Held items** (firearms, bow, torches, flares, lanterns, knives, tools, stim and any other game gear) are copies of the game's own gear meshes, attached to the hand with per-item placements.
- **Worn accessories** (scarves, earmuffs, hats, helmets, the moose-hide satchel) appear when worn and disappear when taken off.
- **Animation:** walking, crouching, aiming, harvesting, igniting, sleeping, knocked down. The spine follows the camera pitch and the mouth moves with voice chat.

### Sleep and waiting
**Wait for everyone** is available for both sleeping and *Pass time*. Everyone rests for the shortest remaining timer among the players; the rest of a longer timer is kept for the next rest. Players in different scenes are counted together.

### Downed state, revive and respawn
- At 0 HP a player is **downed** instead of dying. They can wait for a teammate or give up.
- A teammate revives them with an **emergency stim** in a timed action that cannot be skipped. The stim is used up and the revived player returns with 25 % of their maximum health.
- **Giving up** respawns the player immediately where they last slept indoors, including rooms of buildings from [SeamlessInteriors](#seamlessinteriors), which are entered through the building's doors. Their items stay where they gave up. The respawn costs the game's own temporary penalties, **Cheat Death** (480 hours) and **Diminished State** (60 hours). Online, the penalty never becomes permanent and the image filter you chose is kept. The "You are down" window warns about the penalty.

### PvP
PvP is on: players can damage each other with firearms, melee and thrown items, indoors as well as outdoors.

---

## Installation

1. Install **MelonLoader 0.7.x** into The Long Dark 2.60 and start the game once.
2. Put **ModComponent** (`ModComponent.dll`) into `Mods`.
3. Delete any older copies of this mod's DLL, `.modcomponent` and `.unity3d` files from `Mods`.
4. Download the latest zip from the [Releases](../../releases) page and copy its contents into the game folder:
   ```
   Mods\FreezeTogether.dll
   Mods\FreezeTogether.modcomponent
   Mods\multiplayerstuff.unity3d
   Mods\freezetogetherplayer.unity3d
   UserLibs\Steamworks.NET.dll
   ```
5. Every player must use the **same build**.

Reinstalling or updating MelonLoader can wipe `UserLibs`; check that `Steamworks.NET.dll` is still there. Without ModComponent the mod's own items (`GEAR_SC*`) are missing.

**Playing**
- **Steam:** the host creates a server, friends join through an invitation, *Join Game* in Steam, or the server browser.
- **Epic Games Store, or Steam together with Epic:** use an IP connection. The host opens UDP port `26950`, or players use a VPN such as Radmin VPN or Hamachi.
- Quitting the game during a network session closes the whole game. This is by design.

**Data.** Host-side world state is stored next to the saves in `<PersistentDataPath>/FreezeTogether/<save>/`. Data written by earlier builds is moved there on first start.

---

## Compatibility

### Requirements
| Component | Version |
|-----------|---------|
| The Long Dark | **2.60** |
| [MelonLoader](https://github.com/LavaGang/MelonLoader) | **0.7.x** (tested on 0.7.3) |
| [ModComponent](https://github.com/ds5678/ModComponent) | **7.x** |
| Steamworks.NET | Shipped in the release, bound to the game's own `steam_api64.dll` |
| SeamlessInteriors | Optional. If one player uses it, all must. |

### Other mods
The server compares the full list of mods (SHA-256 of every Melon and `.modcomponent` file). **All players must have exactly the same mods and versions**, or the connection is refused. Client-side mods (interface, tweaks) generally work, provided every player has the same set.

### SeamlessInteriors
SeamlessInteriors (`SeamlessInteriors.dll`, API version 1) is a separate mod and is not included. Freeze Together supports it without changing it and without a build-time reference: at start it looks for the `SeamlessInteriorsApi` type by reflection and uses only its public API. Without SeamlessInteriors the mod behaves as usual; with a different API version it logs a warning and does not use it.

With SeamlessInteriors installed by every player:
- Buildings are part of the region scene, so players inside and outside see and hear each other through doors and windows, and PvP works across the threshold.
- Items inside buildings are shared **per building**: the first player inside a ready building records its items, the host stores the record and every other player applies it once on entering. The region's own record leaves building items out, so the two never conflict.
- Respawning into a building goes through its doors, and fires and the host's weather are reapplied on entering a building.

### Known limitations
- Multiplayer with two or more players is lightly tested. New in this build and not yet checked in game: *Wait for everyone* for *Pass time*, respawn through SeamlessInteriors doors and its penalties, kicking from the Esc list and closing a Steam lobby.
- Cooking is not synchronized: only the player who placed a pot sees it.
- The wood stove door (open or closed) may differ between players.
- Fire timers are not kept equal when someone skips time (waiting or sleeping); when another player adds fuel, the timer of the player who skipped time rises to that level.
- Body temperature and freezing are computed on each client, so players can get different warmth from the same fire.
- If two players record the same never-visited area at once, the host keeps the first record; an item the second player picked up in between can come back once. The same can happen to an item picked up within the first second inside a never-visited building.
- Saves where an earlier build ran together with SeamlessInteriors may hold building items in the region's record; the first player to enter such a region can get them twice. Start a new save to avoid it.
- The short-wave radio is not shown in first person.
- Vehicles are not synchronized.

---

## Development

### Building
```
dotnet build -c Release FreezeTogether.csproj
```
Requirements: .NET 6 SDK and a game install with MelonLoader that has generated `MelonLoader\Il2CppAssemblies`. The default game path is set in `FreezeTogether.csproj`; override it with `/p:TLDPath=<game folder>`. Output goes to `bin/Release`, release files are collected in `dist/`.

Build configurations `Dedicated` and `Dedicated Linux` produce a server without the game UI. `Mods\serversideonly.json` and `modswhitelist.json` list files that are left out of the mod check.

### Project structure
```
.
├─ MyModMain.cs          Mod entry (MelonMod): init, scene events, player loop, UI, revive, actions
├─ Patches.cs            Harmony patches on game classes
├─ Comps.cs              Il2Cpp components: player objects, animals, containers, special objects
├─ PlayerModel.cs        Humanoid model: animation, clothing, held items, accessories
├─ SceneItemsSync.cs     Shared spawners, floor records (region and buildings), fires
├─ SeamlessCompat.cs     Optional bridge to the SeamlessInteriors public API
├─ Client*.cs, Server*.cs, Packet.cs, Reliable*.cs   Networking (client, host, packets, reliable UDP)
├─ SteamConnect.cs, SteamCallbacks.cs, IpLobby.cs    Steam lobby and IP lobby
├─ MenuHook.cs, HostMenuSkin.cs                      Multiplayer menus and the server settings screen
├─ MPSaveManager.cs      Host-side world state stored next to the save
├─ Expedition*.cs        Expeditions and their editor
├─ Shared.cs, DataStr.cs Shared logic and data structures (also used by the dedicated server)
├─ Compat.cs, Loc.cs     Game API compatibility layer, English/Russian text
├─ TinyJSON/             Bundled JSON library
└─ Tools/BundleRepack/   Unity editor script that rebuilds the ModComponent item bundle
```

### Contributing
Bug reports and pull requests are welcome. For a bug, use the issue template: the mod and game versions, host or client, and the `MelonLoader\Latest.log` of **both** sides, since many problems only show in the other player's log. Keep changes focused, build the project before submitting, and describe what the code does and why in commit messages and comments.

---

## Acknowledgments

- **SkyCoop** by Filigrani and its contributors, whose legacy multiplayer code ([`BeforeTFTFT` branch](https://github.com/Filigrani/SkyCoop/tree/BeforeTFTFT)) was the starting point of this project.
- [MelonLoader](https://github.com/LavaGang/MelonLoader), [HarmonyX](https://github.com/BepInEx/HarmonyX) and [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) for mod loading, patching and game access.
- [ModComponent](https://github.com/ds5678/ModComponent) for custom items.
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) for Steam networking.
- [TinyJSON](TinyJSON/LICENSE.md), bundled for serialization.
- The authors of SeamlessInteriors for the interior API this mod builds on.
- Hinterland Studio for The Long Dark.

---

## License

Released under the [MIT License](LICENSE). The license keeps the copyright notice of the legacy code this project started from; third-party components keep their own licenses.

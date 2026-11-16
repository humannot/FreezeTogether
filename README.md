# SkyCoop — The Long Dark 2.60 port

Unofficial port of [Filigrani's SkyCoop](https://github.com/Filigrani/SkyCoop) multiplayer mod (LTS 0.13.2) to **The Long Dark 2.60**. This build reports version **0.13.6**. The original author did not release this build and does not support it.

SkyCoop adds cooperative and competitive multiplayer to The Long Dark: players share one world, see each other, trade items, revive each other and fight each other if the host allows PvP.

> **Status:** early, lightly tested build. Every item in [Features](#features) says how far it was checked. If something breaks, open an issue and attach `MelonLoader\Latest.log` (not the whole `Logs` folder).

---

## Table of contents
- [Features](#features)
- [Compatibility with other mods](#compatibility-with-other-mods)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [Multiplayer and synchronization](#multiplayer-and-synchronization)
- [Custom models and assets](#custom-models-and-assets)
- [Project structure](#project-structure)
- [Building from source](#building-from-source)
- [Known limitations](#known-limitations)
- [Credits](#credits)
- [License](#license)

---

## Features

### Core multiplayer
- Hosting through a **Steam lobby** or by **IP** (UDP port `26950`), with a lobby, region/experience vote and spawn style choice.
- Reliable ordered UDP channel for important packets; server-side container lock so two players cannot loot one container at once.
- Shared weather (by seed), time, animals, animal corpses, doors, containers, fires, dropped and picked-up items.
- Voice and text chat, emotes, short-wave radio channels, expeditions (from the original mod).
- English / Russian text that follows the game language (`Language` in `UserData/MelonPreferences.cfg`: Auto / English / Russian).

### Shared world items
All players see the same items in the same places. Nothing is rolled separately for each player:

| What | How it is shared |
|------|------------------|
| Sticks, stones, limbs (radial spawners) | One player per scene (the host when it is there) runs the game's own spawning and respawn timers and sends each spawner's saved state; the others load it. An item taken by anyone disappears for everyone. |
| Loose items on the floor and inside buildings | One rule for every area: the region and, with SeamlessInteriors, each building (see [SeamlessInteriors](#seamlessinteriors)). The first player to have an area ready records which items lie where. The host keeps the first record it gets and every other player applies it once: missing items are created, extra ones are removed. |
| Containers | Unopened containers roll their loot with a seed made from the container's GUID, so it is the same for everyone. After the first close, the host stores the contents and sends them to whoever opens the container next. |
| Campfires, stoves, fireplaces | One state per fire (the game's own save snapshot), identified by the fire's GUID and kept by the host. |

Notes and story items (`NarrativeCollectibleItem`) are left out of the floor records, so each player keeps their own story progress.

### Players
- **Humanoid player model** for other players (`skycoopplayer.unity3d`). Clothing uses the game's own gear materials, including wear.
- **Held items** (rifles, revolvers, flare gun, bow, torches, flares, lanterns, matches, knives, hatchets, hammer, prybar, camera, stim, stones, noisemakers and any other game gear) are mesh copies of the game's own gear prefabs. They are attached to the model's right hand bone with per-item placements taken from SkyCoop Reborn. Items without their own placement use the knife grip. Lanterns hang upright from the hand. A rifle that is not being aimed is held lowered in both hands: the aiming pose with both arms turned down.
- **Worn accessories** are copies of game gear on the model's bones: scarves, earmuffs, aviator hat, miner's helmet, cougar wrap, wolf and deer skin hats on the head, and the **moose-hide satchel** on the back (left shoulder bone, Reborn's placement). Accessories appear when worn and disappear when taken off.
- Animations: walking, crouching, aiming, harvesting, igniting, sleeping, knocked down; the spine follows the camera pitch; the mouth moves with voice chat.

### Downed state and revive
- At 0 HP a player is **downed** instead of dying. They can wait for a teammate or give up and respawn where they last slept indoors.
- A teammate revives them with an **emergency stim**. The stim must be in the reviver's inventory, and the revive is a timed action with a progress bar that the reviver must not interrupt. There is no instant revive.
- The stim is used up. The revived player gets the game's own emergency-stim effects (pulse, screen effect, fatigue relief and stimulation, applied with the stim prefab's values) and comes back with **25 % of their maximum health**.

### PvP
- The host enables or disables PvP. Buildings are **not** safe zones: shooting, melee and thrown damage work indoors as outdoors.
- Optional **safe-zone circles** around settlements and camps (host setting "Safe zones"). Inside a circle, nobody can deal or take player damage. With SeamlessInteriors, a building that stands inside a circle is covered by it, because it is part of the region.

### Moving items in the world
- Items dropped or moved by another player are shown as SkyCoop stand-ins, which the game's own right-click **move** does not handle. A right click on one takes it from the host's drop list, removes it for every player and starts the game's normal placement. When placed, it is dropped again and appears in its new spot for everyone. The item exists only once: either on the host's list or in the hands of the player moving it.

---

## Compatibility with other mods

The server compares the full list of mods (SHA-256 of every Melon and `.modcomponent`). **All players must have exactly the same mods and versions**, or the connection is refused.

### SeamlessInteriors
SkyCoop supports SeamlessInteriors (`SeamlessInteriors.dll`, API version 1, a separate mod that is not included) without changing it. SkyCoop does not reference its assembly at build time. At start it looks for `SeamlessInteriors.SeamlessInteriorsApi` by reflection and uses only that public API (`GetInteriorIdOf`, `GetInteriorRoot`, `GetPlayerInteriorId`, `IsInteriorOpen`, `AreInteriorsReady`). Without SeamlessInteriors, SkyCoop behaves as before. With a different API version, SkyCoop logs a warning and does not use the API.

With SeamlessInteriors installed (by **every** player, which the mod check enforces):
- Buildings are part of the region scene, so players inside and outside a building share one scene. They see and hear each other through doors and windows, and PvP works across the threshold.
- SeamlessInteriors creates a building's content when a player comes near and rolls its random loot on each player's game. SkyCoop therefore keeps **one floor record per building**. The first player to be inside a building once its content is ready (checked every second) records its items, the host stores the record, and every other player applies it once on entering. Items created from a record are placed under the building's root, so SeamlessInteriors saves them with the building.
- The region's own floor record leaves building items out, so the two records never fight over the same item.

No extra dependencies are needed for this.

### Other mods
- **Working Vehicles:** runs alongside; vehicles are not synchronized.
- **ResumePreviewer:** its save-name suffix is hidden on the HOST SERVER / NEW GAME buttons.
- Other client-side mods (UI, tweaks) generally work, as long as every player has the same set.

---

## Requirements
| Component | Version |
|-----------|---------|
| The Long Dark | **2.60** |
| [MelonLoader](https://github.com/LavaGang/MelonLoader) | **0.7.x** (tested on 0.7.3) |
| [ModComponent](https://github.com/ds5678/ModComponent) | **7.x** (`ModComponent.dll` in `Mods`). Without it, the mod's own items (`GEAR_SC*`) are missing. |
| Steamworks.NET | Shipped in the release (`UserLibs\Steamworks.NET.dll`), bound to the game's own `steam_api64.dll` |
| SeamlessInteriors | Optional. If one player uses it, all must. |

---

## Installation
1. Install MelonLoader 0.7.x into The Long Dark 2.60 and start the game once.
2. Put `ModComponent.dll` into `Mods`.
3. Remove older SkyCoop files, including `Mods\mpgear.unity3d` and `Mods\SkyCoopGearOffsets.txt`, which this version no longer uses.
4. Download the release zip and copy its contents into the game folder:
   ```
   Mods\SkyCoop.dll
   Mods\SkyCoop.modcomponent
   Mods\multiplayerstuff.unity3d
   Mods\skycoopplayer.unity3d
   UserLibs\Steamworks.NET.dll
   ```
5. Every player must use the same build.

Reinstalling or updating MelonLoader can wipe `UserLibs`; check that `Steamworks.NET.dll` is still there.

---

## Configuration
- **Host settings** (Multiplayer → Host Server): PvP, safe-zone circles, fire sync, spawn style and the other options of the original mod. The last used settings are remembered.
- **Language:** `Language` in `UserData/MelonPreferences.cfg` (`Auto`, `English`, `Russian`).
- **Dedicated server:** build configurations `Dedicated` / `Dedicated Linux`. `Mods\serversideonly.json` and `modswhitelist.json` list files left out of the mod check.

---

## Multiplayer and synchronization
- **Steam:** host with "Host Server"; friends join through the Steam lobby (Steam *Join Game* works).
- **Epic / no Steam:** IP connection only; the host opens UDP port **26950** (or uses a VPN such as Radmin VPN / Hamachi).
- The host is the authority for stored world state: containers, floor records, spawner states, fires, picked items and safe-zone status.
- A player's scene is identified by the loaded scene's build index and save name. With SeamlessInteriors, the scene stays the region while inside a building.
- Player appearance (clothing, wear, accessories, held item, animation state, tilt) is sent by each player and applied on everyone else's model.
- Quitting the game during a network session closes the whole game (by design).
- Version 0.13.6 changes the floor-record messages: it does not work together with 0.13.5 or older.

---

## Custom models and assets

| File | Contents | Used by | Can the game's own models replace it? |
|------|----------|---------|---------------------------------------|
| `Mods\multiplayerstuff.unity3d` | Original SkyCoop bundle: classic player rig (`multiplayerPlayer`) with colliders, voice and light objects; UI prefabs (`MP_*` menus, lobby, chat, server browser, customization, emote wheel, expedition editor); radio models and sounds (`MyRadio`, `RadioBeep`, `RadioOver`); first-person animations (`FPH_Anims`); `Bolt`, `CanadiumTemplate`, `Golosovanie`, `NewGamePreview`; skill textures | `MyModMain.MakeModObject` and `LoadedBundle.LoadAsset` across the mod | **No** — UI and networking objects have no game equivalent. Kept. |
| `Mods\skycoopplayer.unity3d` | Humanoid `SkyCoopPlayer` model from the SkyCoop Reborn Unity project (Unity 6000.0.60f1): rigged body, hair, beard, brows and clothing meshes named after game gear | `PlayerModel.cs` | **No** — the game has no third-person survivor model. Kept. Its clothing meshes use the game's own materials. |
| `Mods\SkyCoop.modcomponent` | ModComponent package with 24 mod items (`GEAR_SC*`: keys, lockpick, metal blank, heat packs, emergency ration, notes, photos, map pieces, books, presents, decoy, wood forms, liquid lead), their bundle and sounds (`SkyCoop.bnk`), a forge blueprint and loot tables | Item logic in `MyModMain.cs`, `Patches.cs`, `MPSaveManager.cs` | **No** — they are mod-specific items with no game counterpart. Kept. |

**Removed in 0.13.6**
- `Mods\mpgear.unity3d`: 24 held-item models for the classic rig, with the `GearFit` hand-tuning tool, its `SkyCoopGearOffsets.txt` file and the `!gearfit` chat command. The humanoid model shows the game's own gear meshes for the same items, and the classic rig already copied game meshes for items without a model. The bundle was loaded and then hidden every half second.

**Unused but kept**
- `GEAR_SCCacheNote`, `GEAR_SCDoorKeyTemp` and `GEAR_SCMetalBlankSmall` in `SkyCoop.modcomponent`: no code, blueprint, loot table or expedition creates them. Their prefabs live in the package's shared item bundle, and ModComponent requires a definition for every prefab in it. They can only be removed by rebuilding that bundle in Unity (`Tools/BundleRepack`).

---

## Project structure
```
SkyCoop-2.60/
├─ MyModMain.cs          Mod entry (MelonMod): init, scene events, player loop, UI, revive, actions
├─ Patches.cs            Harmony patches on game classes
├─ Comps.cs              Il2Cpp components: classic player object, animals, containers, special objects
├─ PlayerModel.cs        Humanoid model for other players: animation, clothing, held items, accessories
├─ SceneItemsSync.cs     Shared spawners, floor records (region and buildings), fires
├─ SeamlessCompat.cs     Optional bridge to the SeamlessInteriors public API
├─ SafeZoneManager.cs    Safe-zone circles
├─ Client*.cs / Server*.cs / Packet.cs / Reliable*.cs   Networking (client, host, packets, reliable UDP)
├─ SteamConnect.cs / SteamCallbacks.cs / IpLobby.cs     Steam lobby and IP lobby
├─ MPSaveManager.cs      Host-side world state stored next to the save (<PersistentDataPath>/SkyCoop/<slot>/)
├─ Expedition*.cs        Expeditions (from the original mod)
├─ Shared.cs / DataStr.cs  Shared logic and data structures (also used by the dedicated server)
├─ Compat.cs / Loc.cs    Game API compatibility layer, English/Russian text
├─ TinyJSON/             Bundled JSON library
├─ Tools/BundleRepack/   Unity editor script that rebuilds the ModComponent item bundle
└─ SkyCoop.csproj        .NET 6 project (MelonLoader 0.7, Il2CppInterop)
```

---

## Building from source
```
dotnet build -c Release SkyCoop.csproj
```
The project references the game's generated `MelonLoader\Il2CppAssemblies`. The default game path is set in `SkyCoop.csproj`; override it with `/p:TLDPath=<game folder>`. Release files are collected in `dist/`.

---

## Known limitations
- **Testing:** the build loads and its patches apply. Multiplayer with two or more players, and the features new in 0.13.6, have **not** been tested in game yet: per-building floor records, right-click moving of other players' items, the revive stim effects, the satchel and the new held-item placements.
- Cooking is not synchronized (only the player who placed the pot sees it).
- The wood stove door (open/closed) may differ between players.
- Fire timers are not kept equal. If a player skipped time (waited or slept), their fire timer goes down. When another player adds fuel, the timer of the player who skipped time goes up to the level of that player. This is an exploit of the time skip, not an intended mechanic.
- Body temperature and freezing are computed on each client; clients have been seen getting less warmth from a fire than the host.
- If two players record the same never-visited area at once, the host keeps the first record and sends it to the second player, who applies it. An item that player picked up in between can come back once.
- An item picked up within the first second inside a never-visited building, before its record is applied, can come back once.
- Saves where SkyCoop 0.13.5 or older ran with SeamlessInteriors may have building items in the region's floor record. Players who enter such a region for the first time can get those items twice. Start a new save to avoid this.
- Natural-item pickup sync falls back to name + distance matching, so rare mismatches are possible.
- The short-wave radio is not shown in first person.
- Vehicles (Working Vehicles) are not synchronized.
- Epic / non-Steam: IP connection only.
- Containers opened on much older port versions may be empty; start a new save.
- Not compatible with the original mod, older ports or SkyCoop Reborn.

---

## Credits
- **Filigrani** and contributors: original [SkyCoop](https://github.com/Filigrani/SkyCoop) and SkyCoop Reborn (code, player model, held-item and satchel placements). Discord: https://discord.gg/ydmufU2HHj · Patreon: https://www.patreon.com/Filigrani
- **NativeCodeMaker**: player character models and clothing of the original mod (https://www.patreon.com/NativeCodeMaker).
- **Hinterland Studio Inc.**: The Long Dark. We don't own the game; this is a free modification.
- **Lava Gang**: [MelonLoader](https://github.com/LavaGang/MelonLoader).
- **WulfMarius**, **ds5678**: ModComponent and AssetLoader.
- **DigitalzombieTLD**: JumpMod and FoxCompanion code references; help with custom models and animations.
- Assets of the original mod: free models from [Sketchfab](https://sketchfab.com/), [Survival Stylized Characters](https://assetstore.unity.com/packages/3d/characters/survival-stylized-characters-5-weapons-115559) rig, [Quick Outline](https://assetstore.unity.com/packages/tools/particles-effects/quick-outline-115488).
- Libraries: [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET), [HarmonyX](https://github.com/BepInEx/HarmonyX), [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop), TinyJSON.
- Debugging tools used by the original author: UnityExplorer, TLD Developer Console, Coordinates-Grabber, PlacingAnywhere, KeyboardUtilities.
- The Long Dark Modding Discord community.

## License
MIT. See [LICENSE](LICENSE). Copyright (c) 2021 Filigrani.

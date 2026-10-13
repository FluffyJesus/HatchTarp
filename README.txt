# HatchTarp

Have you ever been caught in a storm? Of course you have — any captain worth his salt has. And if you've ever sailed a big ship with grated hatches on deck, you've probably wondered: *Shiver my timbers! Why can't I batten down those damn hatches? One more wave like that and we'll be feeding the fishes!*

Well, now you can!

## Requirements

- Sailwind 0.39.2
- BepInEx 5.4.x

## Installation

- **Mod manager:** install HatchTarp from Thunderstore.
- **Manually:** copy the `HatchTarp` folder into `Sailwind/BepInEx/plugins/`.

## How it works

- Buy **oiled canvas** from any trader who sells oakum. It is on sale next to the counter.
- Close the hatch, then click it while holding the canvas. The canvas is nailed over the grating and keeps the sea out of your hold.
- A covered hatch can't be opened. To take the canvas off, click the hatch with an empty hand.
- Look at a covered hatch to see the condition of the canvas.
- Some deck openings are made of two hatches next to each other. Cover both of them, otherwise water still gets in through the open one.

## Wear and repair

The canvas won't last forever. It wears a little with every wave it stops and a bit more each day it stays fitted. Below 50% it starts to leak, and the more worn it is, the more water gets through.

Keep some oakum aboard: hold a barrel and click the covered hatch to patch the canvas up. A full repair from 0% to 100% uses a quarter of a barrel.

If condition of canvas reaches 0%, it's basically just a rag with holes, and you can't use it or repair it. Buy a new one and don't forget to fix it or store it away when not used.

## Open companionways

Some ships have an open companionway into the hold (by default: the large dhow and the Chronian). On these ships, battening down the hatches only helps so much — the canvas keeps out about half of the water.

## Supported ships

Out of the box, the canvas works on ships whose water overflow points sit right at the hatches:

- Large dhow (vanilla)
- Gloriana (mod)
- Chronian (mod)

On some ships (e.g. Caelanor, Santa Maria) the overflow points are on the sides of the hull rather than at the hatches. You can make the canvas cover them by increasing `ZoneRadius`. Keep in mind that this water may actually be coming over the side, so it's a bit of a cheat. Take the canvas off and fit it again after changing the radius.

## Configuration

Settings can be changed with [BepInEx ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager/releases) (F1 in game) or in `BepInEx/config/HatchTarp.cfg`.

| Setting | Default | Description |
|---|---|---|
| `ZoneRadius` | 2.5 | Max distance (m) between a hatch and an overflow point for the canvas to cover it |
| `OpenCompanionwayBoats` | dhow large, chronian | Ships with an open companionway (part of the ship name, comma-separated) |
| `CompanionwayLeak` | 0.45 | Share of water (0..1) that still gets in on such ships |
| `Battens` | true | Draw wooden battens and nails around a fitted canvas |
| `PrefabIndex` | 20517 | Item slot of the canvas. Change it only if another mod uses the same slot, and never once you have canvases in your saves |
| `DebugTools` | false | Debug keys, see below |

### Debug mode

Set `DebugTools = true` to enable:

- **F9** — show overflow zones: red spheres are open, green ones are covered
- **F10** — info window
- **F4** — spawn a free canvas (for testing)

Turn it off for normal play.

## Compatibility

- Works with RadFixes.
- Ships from other mods work if their overflow points are close to the hatches (see *Supported ships*).

## Uninstalling

Before removing the mod, take all canvases off the hatches and sell or drop the canvas items, so your save doesn't keep items that no longer exist.

## Feedback

This is my first mod, made with the help of Claude Opus 5.5. Feedback is appreciated — please report any bugs in the Discord thread!
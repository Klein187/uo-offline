# Housing research spike (IDEAS 4.5)

*2026-07-05. Question: can we scatter ~50 small bot-owned houses (ModernUO
housing) along coasts/roads, cheaply and reversibly? Answer: **yes** — prototype
is `CustomBots/BotHousing.cs` (`[BotHouses scatter <n> / list / clear`).*

## API findings

- **Placement validation** is one call:
  `HousePlacement.Check(Mobile from, int multiID, Point3D center, out List<IEntity> toMove, facing)`
  → `HousePlacementResult.Valid` or a rejection reason. It enforces the real
  era rules (clear yard border, flat foundation, no roads, no towns via
  `Region.AllowHousing`, no treasure-map regions).
  **Gotcha:** `from.AccessLevel >= GameMaster` short-circuits to `Valid` —
  validation must run through a Player-level probe mobile (we reuse the
  EDGEWALK probe trick: hidden/blessed `Rat { Controlled = true }`).
  Second gotcha: `ContentFeatureFlags.HousePlacement` must be true (default).
  Third: `SpellHelper.IsFeluccaT2A` blocks the Lost Lands area only — mainland
  Felucca is fine on our T2A maps.

- **Construction:** `new SmallOldHouse(owner, multiId)` / `new LogCabin(owner)`
  then `MoveToWorld(center, map)`. Era-correct small multis:
  `0x64 0x66 0x68 0x6A 0x6C 0x6E` (small old houses) + `0x9A` (log cabin).
  The ctor calls `CreateKeys(owner)` which touches `owner.BankBox` — so the
  ctor **cannot take a null owner**; pass the probe, then null `Owner` after.

- **Persistence vs bot churn:** PlayerBots are deleted at session end, so a
  bot can't stay the owner. `BaseHouse` null-checks `m_Owner` everywhere and
  **`RestrictDecay = true` forces `DecayType.Ageless`** regardless of owner —
  an ownerless, non-decaying house that survives every world save. The house
  SIGN has a free-form `Name` string (`"Aldric's cottage"`) that carries the
  fiction with zero live mobile behind it.

- **Doors:** the ctor keys the exterior doors; setting `door.Locked = true`
  keeps them shut. The physical keys are created in the probe's pack/bank and
  die with the probe — nobody can ever unlock a bot house, which is what we
  want.

- **Undo:** `BaseHouse.Delete()` removes multi + sign + doors cleanly.
  Registry of placed serials in `Data/Live/bot_houses.json`; `[BotHouses clear`
  deletes them all.

## Placement strategy (what the prototype does)

Sample spots 14–32 tiles off **rural waypoint nodes** (≥45 tiles from any
CityCenter destination, the GatherSpots threshold) so houses appear exactly
where bots actually walk. Two guards before `Check` even runs:

- ≥12 tiles from every waypoint **edge segment** nearby — a house must never
  wall off a route the EDGEWALK audit certified clean (re-run the audit after
  scattering to prove it).
- ≥40 tiles from every other bot house — scattered homesteads, not shantytowns.

Expect a low Valid rate (the flat-foundation + clear-border rules are strict
in rough terrain); the prototype caps at 60 attempts per requested house and
reports `placed/tried/ms`.

## Perf

Placement is a one-off command, not a hot path. 50 `BaseHouse` multis is
noise for ModernUO (production shards carry thousands); the live costs are
one `HouseRegion` per house and a few KB per world save. Measured numbers
from the first live scatter go here:

- `[BotHouses scatter 50` (first live run, 2026-07-05): **50/50 placed,
  2,788 spots tried, 340 ms total** — ~0.12 ms per candidate including the
  full `HousePlacement.Check` on survivors. Zero errors; post-scatter
  EDGEWALK audit stayed clean (no house blocked a trail).
- Gotcha found on the first attempt: rejecting spots where `toMove` was
  non-empty (yard critters that placement would relocate) rejected ~100% of
  wilderness candidates — birds and rabbits are everywhere. Vanilla
  placement relocates them under the sign; let it.

## Recommended build path (post-spike)

1. **Owner-on-the-porch** — new `HomeownerBehavior`: a session bot rolls a
   claim on a registered house at spawn (name updates to the sign — houses
   changing hands is era-true), Traveler routes there occasionally, bot
   stands on the porch / putters / says home lines, then resumes trips.
   Needs: house records exposed as destinations (Type `Landmark` or new
   `BotHome`), claim bookkeeping in the registry.
2. **Decorate once** — 2–3 locked-down era items (chest, chair, lantern) per
   house at placement; `house.LockDowns` API already exists.
3. **Land-rush theater** — very rare scene: two bots race to a clearing with
   a deed, loser gossips about it ("lost the spot by seconds").
4. **Coastal bias** — current sampling is road-biased (waypoint offsets);
   add coast candidates from the walk atlas (walkable tile with water within
   3 tiles) filtered by the same trail/spacing guards.

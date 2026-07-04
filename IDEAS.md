# Ideas — Making the Shard Feel Like a Living T2A Server

Companion to `DESIGN-PLAN.md` (which holds fully-designed systems: Crafter
rebuild, Spawn Editor, Live Events). This file is the idea backlog, written
2026-07-04, organized around one test:

> **Would a player who logged into a real shard in 1999 have seen this?**

A bot passes as a *player* when it has: a persistent identity, visible goals
it pursues badly-but-sincerely, social behavior aimed at other players (not at
you), and an existence that continues when you're not looking. NPCs stand
still and wait; players are always in the middle of something. Every idea
below pushes bots from "convincing NPC" toward "someone who is mid-session."

Legend: 💥 = biggest mmo-feel payoff · 🔧 = improves something that already
exists · 🧱 = needs new system · (S/M/L) = effort guess.

---

## 1. Identity & Presence

### 1.1 💥 Login/logout instead of existing forever (M)
Real players have sessions. Give each bot a schedule: it "logs in" (spawns
with a moment of gear-check idle at an inn or its last logout spot), plays for
1–4 hours, then says a goodbye line ("gtg dinner", "cya tomorrow") and
vanishes. Population then has a daily curve — dead at 5am, packed in the
evening — which is one of the strongest subconscious "this is a live server"
signals there is. The lifecycle manager already swaps behaviors; this wraps it
in a session layer. Inns and taverns finally get their real purpose: log-off
points.

### 1.2 🔧 Unique names + surnames (S)
429 bots / 251 unique names — five Tessas kill the illusion the moment two
are seen together. Expand NamePool, enforce uniqueness at spawn, and give a
minority surnames or the era's exact flavor of name ("Joe Blackthorn",
"xXDragonSlayerXx" was rare in T2A; more common: "Gandalf", "Merlin", lowercase
"bob"). A name is 80% of a player identity.

### 1.3 Home city + haunts (S)
Each bot gets a home city and 2–3 favorite spots (a bank corner, a tavern
table, a forge). Destination rolls get a home-bias multiplier. Regulars emerge
for free: log in near Britain forge for a week and you keep seeing the same
smith, because he actually lives there. (Data already exists: City field on
destinations + class weighting — this is a small multiplier, not a system.)

### 1.4 💥 Friends, rivals, and memory of each other (M)
A tiny per-bot social table: bots that hunted together get a "friend" edge;
a bot that got PKed remembers the killer's name. Effects are cheap but deep:
friends greet each other *by name* on sight ("yo Corwin"), prefer grouping,
and — the killer feature — **gossip references real events**: "Aldreth got
dry looted at Vesper crossing yesterday" only if that actually happened. One
shard-wide event journal (kills, PKs, rare loot, house purchases) + chat
templates that pull from it = a server that talks about itself. This is the
single biggest "these are people" unlock after guilds.

### 1.5 The recognizable archetypes (S each, add over time)
Every 1999 server had these people. Each is a thin behavior skin over
existing systems:
- **The AFK macroer** — stands at an anvil/spinning wheel for hours making
  the same item, says nothing, doesn't react. (CrafterBehavior minus chatter.)
- **The newbie** — low-tier, starter gear, asks bank crowds "how do i get to
  minoc??" and "can anyone spare gold", follows a random player for a minute.
- **The bank sitter** — full plate at WBB doing absolutely nothing, forever.
  (Exists — lean into it: emotes, showing off 1-in-population rare items.)
- **The beggar** — Karma-farming "gold plz" bot at every bank.
- **The lumberjack in the middle of nowhere** — resource gatherers actually
  out in the forests/mountains, not just in towns (routes exist via
  waypoints; walkmap tool makes wilderness spurs cheap now).

---

## 2. Social Structure

### 2.1 💥 Guilds (L, phased — the top of this whole list)
The T2A social skeleton. Phase it:
1. **Tags + rosters** (S): create 8–15 guilds with era names ("The Syndicate",
   "Knights of Yew", "DOOM"), assign ~40% of bots, show [TAG] via guild title.
   Instant depth: the population stops being 429 individuals.
2. **Guild behavior** (M): guildmates prefer each other for groups, gather at
   a claimed spot (a tavern = guildhouse-lite), guild-flavored chatter.
3. **Order/Chaos** (M): the era-correct faction war. A few guilds take
   shields; Order and Chaos bots fight ON SIGHT, in town, guards ignoring it
   — exactly as T2A worked. This gives open-world PvP a *reason* that isn't
   murder, and street fights outside Brit bank were THE spectacle of the era.
4. **Guild wars/politics** (L): declared wars, war chatter, victory gloating
   in the event journal (feeds 1.4 gossip).

### 2.2 💥 Hunting parties (M)
The `lfg.txt` chatter already asks "LFG despise anyone?" — make it real. A
bot rolls "form group": broadcasts LFG at a bank/gate, 1–3 compatible bots
(class/tier/friend-biased) converge, they travel as a unit (follow-the-leader
on the same waypoint route), enter the dungeon together, fight as a unit
(assist logic exists), and split up with goodbyes after the run timer. Groups
walking down a road in a line is a pure MMO image no NPC system produces.

### 2.3 Duels outside the bank (S)
Two bots emote a challenge, walk 10 tiles from WBB, fight to low HP (no
kill), winner bows/gloats, crowd chatter reacts. Uses existing combat +
chatter; needs only choreography. Era-perfect.

### 2.4 Escorts & taming as visible activities (M)
Tamers already have the skill — have them actually tame wild animals (visible
attempts, failures, "come pet" spam) and then WALK AROUND with the pet, sell
it at the bank ("selling frenzied ostard 2k"). Half of taming's flavor is the
dragging-pets-through-town part.

---

## 3. Danger, Death, and Consequence

### 3.1 💥 Real deaths + the corpse run (M)
Bots currently ~never die (0 deaths / 43 fights — retreat logic too good).
Death is UO's most iconic experience, so let it happen sometimes: ghost
(manifest OoOoOo chatter), run to a healer or **shrine** (the shrines we just
placed become functional — ghosts chanting mantras at Chaos shrine!), res,
then a *corpse retrieval trip* — return to the death spot, hoping the loot's
still there. Loop with existing Traveler + shrine/healer destinations. Tune
retreat thresholds down slightly per tier (novices misjudge fights).

### 3.2 PK ecology (M) 🔧
PKBehavior exists; make murder a *social event*:
- Reds camp known chokepoints (crossroads, dungeon entrances, moongates) —
  destination weighting, not new AI.
- Blues who SPOT a red flee toward guards and **broadcast it**: "RED AT BRIT
  GY!! " → nearby bots actually route away for a while (a per-region danger
  score with decay). The world visibly reacting to a PK is the mmo feel.
- Murder counts + T2A stat-loss consequences for reds; bounty gossip.
- Anti-PK guild (2.1) that responds to those broadcasts and hunts the red.

### 3.3 Region danger reputation (S)
The per-region danger score above, fed by the event journal, also drives
chatter ("stay out of despise, 3 reds camping") and slightly biases everyone's
destination rolls. Danger becomes *information that propagates through the
population* — which is exactly how it worked on real servers.

---

## 4. Economy (extends the DESIGN-PLAN Crafter rebuild)

### 4.1 Closed-loop trade between bots (L, after crafter rebuild)
Adventurers *buy from crafter bots* (handoff at forge/vendor: coins + item
swap with emotes), crafters restock from gatherer bots' materials. Even a
90%-illusion version (items/gold not strictly conserved) reads as a living
economy because you SEE the transactions happen at forges and banks.

### 4.2 WTS/WTB that concludes (M) 🔧
`wts.txt`/`wtb.txt` spam exists — occasionally let two bots actually meet,
trade-window emote, and exchange ("sold!"). The spam becomes believable the
moment any of it ever visibly works.

### 4.3 Visible gear progression (M)
Tier is rolled at birth today. Let dungeon runs pay: crawler survives a run →
small chance to upgrade one equipment slot next bank visit (banked loot →
better sword, then hued armor at high tiers). Regulars you see at the bank
*getting better gear over weeks* — long-horizon believability that costs
almost nothing (equipment tables already tier-aware).

### 4.4 Treasure hunting & fishing SOS (M/L)
T2A staples that produce great open-world theater: a bot at a dock fishing up
a bottle, then organizing a trip; a T-hunter walking to a wilderness spot,
digging, fighting the spawn. Both are Traveler + destination + scripted-scene
work, no new nav needed (walkmap tool covers wilderness spurs).

### 4.5 Housing (L, research spike first)
The T2A land rush. Scatter small bot-owned houses (ModernUO housing) along
coasts/roads; owners go home sometimes, lock doors, decorate once. Even
static-but-owned houses with the owner occasionally standing on the porch
changes the wilderness completely. Spike: check ModernUO house placement API
+ perf with ~50 houses.

---

## 5. Chatter & Texture (all 🔧 S–M)

- **Era voice pass**: 1999 idiom — "ne1", "thx m8", "lol", "afk sec", spell
  mantras mid-fight (already real), occasional typos at low frequency, ZERO
  modern slang/memes. Also: rare all-caps drama ("WHO LOOTED MY CORPSE").
- **Time/context-aware lines**: night lines, rain lines, in-dungeon whispers
  ("quiet... something ahead"), post-victory loot squabbles ("i had looting
  rights on that").
- **Event-journal gossip** (see 1.4) — the highest-value chat upgrade.
- **Emotes**: bows, salutes, *dances at bank* — bots occasionally emote at
  each other; a wordless social layer that's very "players".
- **Idle keyboard noise**: extremely rare "asdf" / "test" / accidentally-
  walked-into-a-wall moments. Imperfection reads as human.
- **Vendor haggling theater** at NPC shops ("2k?? npc vendor sells for 800").

---

## 6. Improvements to What Exists

### 6.1 Navigation & data (all unlocked by the 7/4 tooling)
- **Full-map walk grid** (M): run walkmap in 512² strips overnight → one
  offline walkability atlas → auto-generate waypoint spurs anywhere (the
  Sacrifice-trail A* pipeline, world-wide). Kills "no coverage" as a concept.
- **Finish the shrine set**: Justice via the Yew-side pocket (walkmap the
  (740,150)-(1251,661)→Yew bridge next session); Honesty up Verity Isle from
  Moonglow; Honor via a generated Trinsic jungle trail; Humility/Valor need
  island coverage first. Then: an **avatar-pilgrimage archetype** visiting
  all eight shrines in virtue order — pure T2A roleplay flavor.
- **Nujel'm / Occlo / Buc's Den coverage** via the same pipeline (docks/boat
  illusion for arrival: MoveToWorld + "arrived by boat" line at a dock).
- **Deceit next dungeon** (skeleton already generated in
  `tools/skeleton-deceit.json`) — then Shame, Wrong, Covetous. Each needs:
  merge skeleton → author rooms/waypoints per floor → audit → soak.
- **Despise real L2** rooms/waypoints (bots already land there and hunt
  locally — floor is half-working today).
- **Triage the 52 EDGEWALK findings** — separate door false-positives from
  real blocked edges; fix or annotate (add a `"DoorEdge": true` flag the
  audit skips).

### 6.2 Combat & magic 🔧
- **CombatDebug → config knob** (it's a const; make it a Configuration
  setting so verbose spell logging can be toggled without rebuild).
- **Tier-visible fighting styles**: novices panic-flee at higher HP, GMs
  kite perfectly; a Master mage opening with Explosion+EB combo (era PvP
  meta) vs an apprentice spamming Fireball. The *quality* difference between
  players was very visible in T2A.
- **Healing between fights**: bots sitting to heal/meditate after combat
  instead of instantly moving on; bandage self-heal for warriors (skill
  exists in templates).
- **Gate Travel etiquette**: bots occasionally hold a gate open at the bank
  ("gate to despise lvl 2 hurry") and others actually take it — public
  transport as social event (Moongate pairs are already real items others
  can use — this mostly already works, just needs the announce + loiter).

### 6.3 Infrastructure
- **Event journal** (S, enables 1.4/3.3/4.3): append-only JSONL of notable
  bot events (kill, death, PK, level-ish milestone, big sale). One writer,
  many consumers (gossip, danger scores, gear progression, web dashboard?).
- **Shard status page** (S, fun): tiny HTML from LiveMapSnapshot — "who's
  online" list + population graph, like the old shard status pages. Pairs
  hilariously well with 1.1 sessions.
- **Bot "screenshots" soak harness** (M): scheduled headless soak + log-diff
  report (entries/exits/deaths/MAROONED counts vs baseline) so regressions
  announce themselves after every change, like the audit does for data.

---

## Suggested build order (feel-per-effort)

1. **Unique names** (1.2) — hours, immediate.
2. **Guild tags + rosters** (2.1 phase 1) — days, transforms the crowd.
3. **Login/logout sessions** (1.1) — the living-server heartbeat.
4. **Event journal + gossip** (6.3 + 1.4) — the server starts telling its
   own stories.
5. **Hunting parties** (2.2) — uses LFG chatter that already exists.
6. **Real deaths + corpse runs** (3.1) — makes the shrines/healers matter.
7. **Order/Chaos** (2.1 phase 3) — era-correct endgame for the PvP layer.
Then interleave: economy loop after the Crafter rebuild lands; housing spike
whenever curiosity strikes.

# Bot Lifecycle System — Design Document

**Status:** Draft  
**For implementation:** Phase 3 (after AdventurerBehavior and TravelerBehavior)  
**Purpose:** Replace fixed-per-bot behavior with personality-driven phase transitions, so bots have lives that play out over time.

---

## The Core Idea

Today, a bot is assigned a behavior at spawn time and runs that behavior forever. A BankSitter sits at the bank until it dies.

We want bots to **live lives**: a bot born in Trinsic might adventure outside the city for hours, travel to Britain, sit at the bank for a while, then head back to adventuring. Different bots have different inclinations, producing visible variety in the population.

This document describes the system that makes that possible.

---

## Components

### 1. `BotPersonality`

A struct attached to each bot, persisted as part of the bot's serialization. Captures what kind of bot this is, independent of what they're currently doing.

```csharp
public struct BotPersonality
{
    // Weighted preferences for behavior types. Should sum to ~1.0
    // but normalization at runtime is fine.
    public float AdventurerTendency;
    public float BankerTendency;
    public float TravelerTendency;
    public float ShopperTendency;
    // ... add more as behaviors are added

    // Modifier traits — small +/- shifts to base behavior.
    public PersonalityTrait Traits;  // bitmask

    // How long, on average, before this bot switches phases.
    public TimeSpan AveragePhaseDuration;
}

[Flags]
public enum PersonalityTrait
{
    None      = 0,
    Restless  = 1 << 0,  // shorter phases, more transitions
    Homebody  = 1 << 1,  // longer phases, fewer transitions
    Brave     = 1 << 2,  // biases toward Adventurer in dangerous areas
    Cautious  = 1 << 3,  // biases toward Banker / town behaviors
    Wealthy   = 1 << 4,  // prefers banks and shops over wilderness
    Rough     = 1 << 5,  // prefers wilderness over towns
    // ... etc
}
```

Personalities are generated at bot creation. We can:
- Roll them randomly for variety
- Pull from a fixed set of "templates" (Adventurer, Townsman, Wanderer, etc.) with small variation
- Bias toward whatever the spawner specifies (a "wilderness spawner" makes Brave/Rough bots)

### 2. `BotPhase`

The bot's current "what am I doing right now" state. Lighter than a Behavior — phases are decisions, behaviors are how to execute them.

```csharp
public class BotPhase
{
    public BotPhaseType Type;       // Adventurer | Banker | Traveler | ...
    public DateTime StartedAt;
    public DateTime PlannedEnd;     // expected; can extend or end early
    public Point3D AnchorLocation;  // where this phase happens
    public Map AnchorMap;
    public string Notes;            // freeform: "fighting at Despise"
}
```

When a phase is active, it dispatches to the appropriate Behavior class to actually run.

### 3. `BotStoryLog`

A short rolling history of recent phases. Mostly for debugging and "tell me about this bot" features. Keeps last 5-10 phases.

```csharp
public class BotStoryLog
{
    private Queue<BotPhase> _history = new();
    public void Record(BotPhase phase) { ... }
    public IEnumerable<BotPhase> Recent { get; }
}
```

This is also what powers fun in-game features: clicking a bot in the BotPanel could show "This bot adventured at Despise yesterday, then traveled to Britain."

### 4. `BotLifecycleManager`

Single global tick, every 60 seconds. Iterates every bot in the world, decides if they should transition.

```
foreach (bot in World.Mobiles.OfType<PlayerBot>()):
    if (bot.Phase == null):
        bot.Phase = PickInitialPhase(bot)
    elif (ShouldTransition(bot)):
        bot.StoryLog.Record(bot.Phase)
        bot.Phase = PickNextPhase(bot)
        ExecuteTransition(bot)
```

`ShouldTransition` returns true when:
- Phase duration has elapsed (most common)
- Bot died (immediate forced transition or replacement)
- Triggered event (e.g., dungeon raid happening; bot is summoned)
- Bot HP critical (might transition to "flee to bank" phase)

`PickNextPhase` rolls weighted by personality, current location, time of day, and a randomization factor. Personality is the main signal.

### 5. Movement Between Phases

When a phase changes, the bot may need to physically move. Three options:

- **In-place transition** (no movement needed). Banker at Britain bank decides to be Banker still, just refreshes for another phase.
- **Walk** (close transitions). Banker decides to Shopper while staying in Britain — they walk to the new district.
- **Teleport** (long transitions). Britain Banker decides to be Trinsic Banker — would take an hour to walk. Fade out, teleport, fade in (with maybe a "I just got here" chat line).

Teleportation breaks immersion but the alternative is bots that effectively can't transition between regions. UO had moongates for player teleport; bots can use them lore-wise.

### 6. Personality Generation

Three approaches to bot creation:

**Random:** Roll personality weights uniformly random. Most chaotic, most variety.

**Templated:** Pick one of 8-12 personality templates ("Career Adventurer", "Devoted Banker", "Restless Wanderer", "Cautious Merchant", etc.). Apply small random variation to weights. Most controllable, easiest to test.

**Inherited from spawn context:** Wilderness spawn → Brave/Rough. Town spawn → Cautious/Wealthy. Dungeon entrance spawn → very Brave Adventurer. Most thematic.

My recommendation: **templated with location bias**. Pick from a template set; the spawn location nudges the template choice.

---

## Decisions Needed

### D1: Phase duration distribution

Average phase duration matters a lot for "feel." Options:

- 30 minutes — bots transition every game session, world feels dynamic
- 2 hours — bots have meaningful blocks of behavior, recognizable patterns
- Multi-day — bots have "careers", you remember them across sessions

I lean toward **2 hours average with personality scaling.** Restless bots transition every 30 min; Homebody bots stay 6+ hours.

### D2: Initial population strategy

When the system first activates, every existing bot needs an initial personality. Options:

- Derive from current behavior (BankSitter → Banker personality)
- Roll all from scratch
- Re-roll only when bot dies/respawns

I lean toward **derive from current**, since that preserves current placement intent.

### D3: How many simultaneous bots?

Each ticking bot is some CPU. With 800 bots ticking every 2 seconds, that's 400 ticks/sec. ModernUO should handle this but we should monitor. Need a way to throttle if performance suffers.

### D4: What about Spawners?

Spawners currently own bots. With Lifecycle, do spawners still create bots, or does the manager directly populate the world?

I lean toward: **spawners still create bots, lifecycle takes over after creation.** The spawner is the "birthplace" and "death replacement" — it ensures population stays at target. Lifecycle handles what bots do *while alive.*

But if we want totally emergent world, the manager could just create bots from a pool at world startup and ignore spawners entirely. Bigger architectural change.

---

## API Research Needed Before Implementation

These need to be answered against the ModernUO source before we write the manager:

1. **Mobile teleport API:** Best way to move a bot far without breaking serialization. `MoveToWorld` should work but need to verify behavior with bots in combat / mounted / etc.

2. **Pathfinding:** Can a bot reliably path-find from Britain to Vesper? ModernUO has MovementPath system. Need to test reliability across map distance.

3. **Mobile.OnAfterMove / OnLocationChange:** Hooks we'd want to use for "did the bot arrive at destination."

4. **Performance:** What's the cost of iterating 800 bots? Profile a tick.

---

## Phase 1 Scoping (when we build this)

For an MVP of lifecycle, we don't need all the polish. Minimum viable:

1. Two-phase rotation (only Adventurer ↔ Banker for now)
2. Templated personalities (4-6 templates)
3. Time-based transitions only (no death-driven, no HP-driven)
4. Teleport between phases (no walking — too complex for v1)
5. Story log shown via gump on bot click

Build that, see how it feels, iterate. The full design above is the target, but Phase 1 of Lifecycle could ship much smaller.

---

## Open Risks

**Risk: Bots feel mechanical because transitions are time-based.** Mitigation: add HP/health-based triggers, environmental triggers (other bots nearby), and randomness.

**Risk: Performance degrades at 800+ bots.** Mitigation: spread bot ticks across frames (only tick 100 bots per 0.25 sec); profile early.

**Risk: Personality is too random — every bot feels identical despite different weights.** Mitigation: bigger weight differences between bot types; stronger trait effects.

**Risk: Players never notice the changes.** Mitigation: chat lines that reference recent phases ("just got here from Trinsic, what a walk"). Visible coming-and-going at banks.

**Risk: Save data bloats with stories and personalities.** Mitigation: limit story log to 5 entries; personality is tiny struct anyway. Probably fine.

---

## What This Document Is and Isn't

This is the **architecture**. It doesn't have code yet. Code happens in Phase 3 after we have AdventurerBehavior and TravelerBehavior to give the lifecycle something interesting to switch between.

For the implementer (likely future Claude): start with the API Research section. Confirm those APIs work as expected. Then implement Phase 1 Scoping. Show the world a working transition between two behaviors. Then add the rest incrementally.

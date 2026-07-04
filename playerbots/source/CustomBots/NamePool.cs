// =========================================================================
// NamePool.cs — Period-appropriate name generation for PlayerBots.
//
// Sources combined:
//   1. Curated lists per gender (~200 names each). Period medieval /
//      fantasy / Anglo-Saxon / Old Norse / Celtic stylings.
//   2. "Player handle" lists — the names real 1999 players actually used:
//      fantasy-literature borrows (Gandalf, Drizzt), tough-guy nouns
//      (Blade, Reaper), and plain lowercase real names (bob, steve).
//   3. Algorithmic prefix + suffix for unique-feeling names that don't
//      appear in any curated list.
//   4. Surnames — a minority of bots get one ("Joe Blackthorn", "Mara of
//      Yew", "Halric the Grey"), and they're the first collision escape.
//
// UNIQUENESS: five Tessas at one bank kill the player illusion. Every
// live bot name is tracked in _inUse; PickUnique never hands out a name
// that's already walking around. PlayerBot claims its name at creation
// and releases it on delete.
//
// Use:  var name = NamePool.PickUnique(female: bot.Female);
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class NamePool
    {
        private static readonly string[] MaleNames =
        {
            // Anglo-Saxon / Old English
            "Aldric", "Athelstan", "Beorn", "Cedric", "Cuthbert",
            "Drogo", "Eadwin", "Edric", "Edwyn", "Egbert",
            "Godric", "Hadrian", "Halric", "Hroth", "Hugh",
            "Leofric", "Osric", "Oswin", "Wendric", "Wulfgar",

            // Norse-flavored
            "Bjorn", "Erik", "Gunnar", "Harald", "Helmir",
            "Ivar", "Kjell", "Leif", "Magnus", "Olaf",
            "Ragnar", "Rolf", "Sten", "Sven", "Thorgil",
            "Torvald", "Ulfgar", "Vali", "Vidar",

            // Celtic / Welsh / Gaelic
            "Aiden", "Alistair", "Bran", "Cormac", "Declan",
            "Eamon", "Fergus", "Finn", "Gawain", "Kael",
            "Liam", "Lorcan", "Owen", "Rhys", "Ronan",

            // Fantasy classic
            "Aelius", "Aethel", "Albric", "Alaric",
            "Arden", "Aric", "Bram", "Brandt",
            "Caedmon", "Caelan", "Caine", "Caspian", "Corwin",
            "Daven", "Devin", "Donovan", "Draven", "Dyson",
            "Eldric", "Eldwin", "Elric", "Emeric",
            "Galen", "Garrick", "Gavric", "Gerard", "Gideon",
            "Halcyon", "Hawthorne", "Hektor",
            "Idric", "Ilric", "Ivor",
            "Jareth", "Joren", "Jorgen",
            "Kaelen", "Kestrel", "Korin",
            "Lael", "Loras", "Loric",
            "Mardus", "Maric", "Marius", "Merrick", "Morric",
            "Nessen", "Nyvar",
            "Olric", "Orin",
            "Padric", "Pelric", "Percy",
            "Quill", "Quintus",
            "Rael", "Renly", "Rhett", "Roric", "Rylan",
            "Sael", "Seoric", "Soren", "Stellan",
            "Thane", "Theoric", "Tobias", "Tomric", "Tristan",
            "Ulric", "Uther",
            "Valen", "Varric", "Vesric",
            "Wolfric",
            "Yorick", "Yvain",

            // Shortform / nicknames
            "Bart", "Bert", "Conn", "Dax",
            "Gus", "Hal", "Hux", "Jock", "Kit",
            "Mace", "Ned", "Nyl", "Rick", "Sam",
            "Stan", "Tig", "Tor", "Wat", "Wim"
        };

        private static readonly string[] FemaleNames =
        {
            // Anglo-Saxon / Old English
            "Adelyn", "Aldreth", "Alfreda", "Anwen", "Aria",
            "Bethany", "Brida", "Brunhild", "Edith",
            "Elspeth", "Esme", "Etta", "Faye", "Freya",
            "Gilda", "Hilda", "Imogen", "Isolde", "Lyra",
            "Mara", "Meridian", "Morag", "Morwen", "Nessa",
            "Odette", "Petra", "Riona", "Rowena", "Saoirse",
            "Sigrid", "Tamsin", "Una", "Verity", "Wenna",
            "Wren", "Yseult",

            // Norse-flavored
            "Astrid", "Brunhilde", "Dagny", "Eira", "Frejya",
            "Gerda", "Helga", "Inga", "Ingrid", "Liv",
            "Sif", "Signe", "Sigyn", "Solveig", "Thora",
            "Tove", "Vigdis",

            // Celtic / Welsh / Gaelic
            "Aine", "Bree", "Caitir", "Ceridwen", "Cliodhna",
            "Daire", "Deirdre", "Enid", "Eithne",
            "Fiana", "Grainne", "Iona", "Kayleigh", "Maeve",
            "Niamh", "Roisin", "Siobhan", "Tara",

            // Fantasy classic
            "Aelinor", "Aetha", "Aila", "Alessa", "Amara",
            "Arwyn", "Aurelia", "Aveline", "Bryn",
            "Calliope", "Calyx", "Celene", "Cerys",
            "Dalia", "Delyn", "Drusilla",
            "Elara", "Elowen", "Elyna", "Ember", "Eris",
            "Faela", "Fenra",
            "Gwyn", "Gwendoline",
            "Halia", "Helene", "Iselda", "Isla",
            "Jessa", "Joryn",
            "Kaela", "Kira", "Korin",
            "Lael", "Lara", "Lirien",
            "Marda", "Maren", "Mira", "Myrra",
            "Nala", "Nyra",
            "Orla", "Oryn",
            "Pira", "Pyrra",
            "Rana", "Riven", "Roselin", "Rowan", "Rylee",
            "Sable", "Sael", "Saira", "Selene", "Senna",
            "Shyra", "Sylva",
            "Tessa", "Thira", "Tira",
            "Ursa",
            "Vala", "Vela", "Vesna", "Vyra",
            "Yelena", "Yelka", "Yrsa",
            "Zara", "Zora",

            // Period shortform
            "Bea", "Cat", "Edie", "Fae", "Gertie",
            "Hettie", "Ivy", "Jo", "Liss",
            "May", "Nell", "Pip", "Rea", "Sal",
            "Tess", "Vi", "Win"
        };

        // Algorithmic generator parts. Combining one prefix with one
        // suffix gives names that "sound right" but aren't in any pool.
        private static readonly string[] MalePrefixes =
        {
            "Ael", "Ald", "Alar", "Arn", "Bal", "Bor", "Bran",
            "Cae", "Cor", "Dar", "Dor", "Dur", "Ed",
            "El", "Fal", "Far", "Fen", "Gal", "Gar", "Gor", "Gun",
            "Hal", "Har", "Helm", "Hold", "Ior", "Jar", "Kael",
            "Lor", "Mar", "Mor", "Nael", "Nor", "Oric", "Quin",
            "Rael", "Ric", "Ror", "Sael", "Sor", "Tar", "Thal",
            "Tor", "Tul", "Ulf", "Val", "Vael", "Vor", "Wend",
            "Wulf", "Yor"
        };

        private static readonly string[] MaleSuffixes =
        {
            "ric", "in", "an", "ar", "or", "us", "as", "is",
            "wyn", "win", "den", "dan", "dor", "gar", "mund",
            "old", "olf", "wald", "fred", "ward", "ron",
            "vin", "th", "stan", "fast", "berg", "horn", "moor"
        };

        private static readonly string[] FemalePrefixes =
        {
            "Ael", "Ais", "Aly", "Ari", "Bri", "Cae", "Cera",
            "Dae", "Dyl", "Ela", "Eli", "Eva", "Fae", "Far",
            "Fen", "Gwen", "Hael", "Hel", "Ily", "Iren", "Isol",
            "Kel", "Lael", "Lir", "Lyn", "Mae", "Mar", "Mor",
            "Myr", "Nael", "Niam", "Nyr", "Ori", "Rae", "Rin",
            "Sael", "Sel", "Ser", "Syl", "Thal", "Thi", "Tris",
            "Val", "Vel", "Vyr", "Wen", "Wyn", "Yri"
        };

        private static readonly string[] FemaleSuffixes =
        {
            "a", "ia", "yn", "wen", "lin", "wyn", "ara", "ena",
            "essa", "ira", "elle", "ette", "anna", "issa",
            "ora", "rys", "ndra", "ade", "ene", "ine", "rin",
            "wynn", "lyn", "ya", "ana", "ela", "elia", "ona", "wina"
        };

        // ---- Player handles ----
        //
        // What actual 1999 players named themselves: fantasy-lit borrows,
        // one-word tough-guy nouns, and unadorned lowercase real names.
        // Rolled at a modest rate so they season the population without
        // turning it into a Tolkien convention.
        private static readonly string[] MaleHandles =
        {
            "Gandalf", "Merlin", "Legolas", "Aragorn", "Gimli",
            "Raistlin", "Caramon", "Tanis", "Sturm", "Drizzt",
            "Elminster", "Conan", "Lancelot", "Galahad", "Mordred",
            "Strider", "Beowulf", "Roland", "Tristram",
            "Blade", "Reaper", "Shadow", "Phantom", "Storm",
            "Hawk", "Wolf", "Viper", "Falcon", "Talon",
            "Slasher", "Warlord", "Ranger", "Outlaw", "Bandit",
            "bob", "joe", "dave", "steve", "mike", "matt",
            "chris", "tom", "dan", "rob", "tim", "jeff",
            "kevin", "brian", "nick", "pete", "carl", "gary"
        };

        private static readonly string[] FemaleHandles =
        {
            "Xena", "Morgana", "Guinevere", "Arwen", "Eowyn",
            "Galadriel", "Morrigan", "Circe", "Cassandra", "Ophelia",
            "Raven", "Willow", "Ember", "Mystique", "Tempest",
            "Shadowdancer", "Moonshadow", "Starlight", "Silverwind",
            "Nightshade", "Wildfire", "Whisper",
            "sarah", "jenny", "lisa", "amy", "katie", "beth",
            "meg", "kate", "jess", "nikki", "carrie", "dawn"
        };

        // How often a fresh roll comes from the handle pool instead of the
        // curated period lists.
        private const double HandleChance = 0.08;

        // ---- Surnames ----
        //
        // A minority of the population carries one from birth; the rest
        // pick one up only if their first name is already taken.
        private const double SurnameChance = 0.25;

        private static readonly string[] FamilySurnames =
        {
            "Blackthorn", "Stormrider", "Ironheart", "Ravenwood",
            "Ashdown", "Thornfield", "Winterborne", "Hawkins",
            "Blackwood", "Greenfield", "Stonebridge", "Fairweather",
            "Oakhurst", "Redfern", "Silverleaf", "Grimm",
            "Weatherby", "Holloway", "Marsh", "Frost",
            "Nightingale", "Swift", "Crowe", "Thatcher",
            "Fletcher", "Cooper", "Wainwright", "Ashford",
            "Duskwalker", "Emberfall", "Ironwood", "Wolfsbane",
            "Stormcrow", "Longstrider", "Coldwater", "Highmoor"
        };

        private static readonly string[] PlaceSurnames =
        {
            "of Britain", "of Trinsic", "of Yew", "of Minoc",
            "of Vesper", "of Moonglow", "of Skara Brae", "of Jhelom",
            "of the North", "of the Woods"
        };

        private static readonly string[] EpithetSurnames =
        {
            "the Grey", "the Red", "the Bold", "the Quiet",
            "the Wanderer", "the Younger", "the Elder", "the Swift",
            "the Unlucky", "the Lame", "the Pious", "the Black"
        };

        // ---- Live-name registry ----
        //
        // Names currently walking the world. Claim on bot creation,
        // release on bot delete. Case-insensitive so "Bob" blocks "bob".
        private static readonly HashSet<string> _inUse =
            new(StringComparer.OrdinalIgnoreCase);

        public static int InUseCount => _inUse.Count;

        // Register a name as live. Returns false if already taken.
        public static bool Claim(string name) =>
            !string.IsNullOrEmpty(name) && _inUse.Add(name);

        public static void Release(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _inUse.Remove(name);
            }
        }

        // -------------------------------------------------------------------
        // PickUnique — roll a name no live bot is using, and claim it.
        // Escalating strategy: plain roll → add a surname → algorithmic
        // generation → generated + surname. The combinatorial space of the
        // later rungs is tens of thousands deep, so exhaustion is
        // practically impossible; the final fallback accepts a duplicate
        // rather than fail.
        // -------------------------------------------------------------------
        public static string PickUnique(bool female)
        {
            // 1) Plain roll (some get a surname anyway — flavor, not rescue).
            for (int i = 0; i < 8; i++)
            {
                var name = RollBase(female);
                if (Utility.RandomDouble() < SurnameChance)
                {
                    name = AttachSurname(name);
                }
                if (Claim(name))
                {
                    return name;
                }
            }

            // 2) First name taken — a surname disambiguates ("second Tessa
            //    on the shard becomes Tessa Ravenwood").
            for (int i = 0; i < 12; i++)
            {
                var name = AttachSurname(RollBase(female));
                if (Claim(name))
                {
                    return name;
                }
            }

            // 3) Algorithmic space, then algorithmic + surname.
            for (int i = 0; i < 40; i++)
            {
                var name = Generate(female);
                if (i >= 20)
                {
                    name = AttachSurname(name);
                }
                if (Claim(name))
                {
                    return name;
                }
            }

            // Should never get here; accept a duplicate over failing.
            return PickRandom(female);
        }

        private static string RollBase(bool female)
        {
            if (Utility.RandomDouble() < HandleChance)
            {
                var handles = female ? FemaleHandles : MaleHandles;
                return handles[Utility.Random(handles.Length)];
            }
            return PickRandom(female);
        }

        private static string AttachSurname(string name)
        {
            double r = Utility.RandomDouble();
            var pool = r < 0.70 ? FamilySurnames
                     : r < 0.85 ? PlaceSurnames
                     : EpithetSurnames;
            return $"{name} {pool[Utility.Random(pool.Length)]}";
        }

        public static string PickRandom(bool female)
        {
            if (Utility.RandomDouble() < 0.10)
            {
                return Generate(female);
            }
            var pool = female ? FemaleNames : MaleNames;
            return pool[Utility.Random(pool.Length)];
        }

        private static string Generate(bool female)
        {
            if (female)
            {
                return FemalePrefixes[Utility.Random(FemalePrefixes.Length)]
                     + FemaleSuffixes[Utility.Random(FemaleSuffixes.Length)];
            }
            return MalePrefixes[Utility.Random(MalePrefixes.Length)]
                 + MaleSuffixes[Utility.Random(MaleSuffixes.Length)];
        }
    }
}

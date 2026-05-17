// =========================================================================
// NamePool.cs — Period-appropriate name generation for PlayerBots.
//
// Two sources combined:
//   1. Curated lists per gender (~200 names each). Period medieval /
//      fantasy / Anglo-Saxon / Old Norse / Celtic stylings.
//   2. Algorithmic prefix + suffix (~10% of the time) for unique-feeling
//      names that don't appear in any curated list.
//
// Use:  var name = NamePool.PickRandom(female: bot.Female);
// =========================================================================

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

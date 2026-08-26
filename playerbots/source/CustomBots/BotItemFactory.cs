// =========================================================================
// BotItemFactory.cs — build an Item from a type name, safely.
//
// Several systems need "give me one of these" from a string: the outfit
// roller, the hawker's sale stock, the supply kits. They all hit the same
// two traps, so the logic lives once, here.
//
// TRAP 1 — the single-int constructor. Item types are inconsistent about
// what one int means. On stackables (Gold, Bandage, Kindling, Log,
// Ingot...) the parameter is `amount`; everywhere else it is a hue.
// Passing 0 blindly built Amount = 0 stacks, which made the engine log
// "Item.Amount <= 0" once per item per spawn wave — the ERR storm that
// took a whole session to trace. Inspect the parameter's NAME and pass
// something safe for what it actually is.
//
// TRAP 2 — assembly scanning. GetType across loaded assemblies throws for
// reasons that have nothing to do with us. Swallow and keep looking.
// =========================================================================

using System;
using System.Reflection;
using Server;

namespace Server.CustomBots
{
    public static class BotItemFactory
    {
        // Resolve a type by fully-qualified name across the loaded
        // assemblies. Null when nothing answers to it — callers treat a
        // missing type as "skip this entry", never as an error.
        public static Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var t = Type.GetType(typeName, throwOnError: false);
            if (t != null)
            {
                return t;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(typeName, throwOnError: false);
                    if (t != null)
                    {
                        return t;
                    }
                }
                catch
                {
                    // Some assemblies throw on GetType for various reasons;
                    // skip them and try the next.
                }
            }

            return null;
        }

        // One item of the named type, or null.
        public static Item Create(string typeName)
        {
            try
            {
                var t = FindType(typeName);
                if (t == null)
                {
                    return null;
                }

                // Prefer a true parameterless ctor if present.
                var ctor0 = t.GetConstructor(Type.EmptyTypes);
                if (ctor0 != null)
                {
                    return ctor0.Invoke(null) as Item;
                }

                // Otherwise the single-int ctor — see TRAP 1 above.
                var ctorInt = t.GetConstructor(new[] { typeof(int) });
                if (ctorInt != null)
                {
                    return ctorInt.Invoke(new object[] { IsAmountParam(ctorInt) ? 1 : 0 }) as Item;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // One item of the named type, hued.
        public static Item Create(string typeName, int hue)
        {
            try
            {
                var t = FindType(typeName);
                if (t == null)
                {
                    return null;
                }

                // A hue was asked for, so the (int) ctor is the right one —
                // unless that int is an amount, in which case hue it after.
                var ctorInt = t.GetConstructor(new[] { typeof(int) });
                if (ctorInt != null && !IsAmountParam(ctorInt))
                {
                    return ctorInt.Invoke(new object[] { hue }) as Item;
                }

                var item = Create(typeName);
                if (item != null && hue != 0)
                {
                    item.Hue = hue;
                }
                return item;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsAmountParam(ConstructorInfo ctor) =>
            string.Equals(ctor.GetParameters()[0].Name, "amount",
                StringComparison.OrdinalIgnoreCase);
    }
}

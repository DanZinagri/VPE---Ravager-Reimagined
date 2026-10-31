using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Ability = VEF.Abilities.Ability;

namespace RavagerReimagined
{
    // Savage style: knowing the psycast gives a permanent passive hediff (unarmed damage and armor penetration), and
    // casting it gives the savage fury buff through VEF's AbilityExtension_Hediff. Unlike Sentinel's Ability_PassiveGiver
    // the gizmo stays visible, since this one is also cast.
    public class SavageStyleExtension : DefModExtension
    {
        public HediffDef passive;
    }

    public class Ability_SavageStyle : Ability
    {
        public override void Init()
        {
            base.Init();
            AddPassive();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                AddPassive();
            }
        }

        private void AddPassive()
        {
            HediffDef passive = def.GetModExtension<SavageStyleExtension>()?.passive;
            if (passive != null && pawn != null && !pawn.health.hediffSet.HasHediff(passive))
            {
                pawn.health.AddHediff(passive);
            }
        }

        // The AI only bothers when it is fighting unarmed.
        public override bool AICanUseOn(Thing target)
        {
            return pawn.equipment?.Primary == null && base.AICanUseOn(target);
        }
    }

    // Unarmed melee (any attack not made with equipment, which means no weapon is held) is multiplied by
    // RavagerReimagined_UnarmedDamage and gains RavagerReimagined_UnarmedArmorPenetration. Both stats are neutral by
    // default, so pawns without the hediffs are unaffected. Armor penetration that vanilla derives from damage
    // (tools without an explicit value) already includes the boosted damage.
    // Only the Thing overloads are patched; the ThingDef ones are used for info-card readouts of a weapon def.
    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.AdjustedMeleeDamageAmount),
        typeof(Tool), typeof(Pawn), typeof(Thing), typeof(HediffComp_VerbGiver))]
    public static class UnarmedDamagePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn attacker, Thing equipment, ref float __result)
        {
            if (SavageUnarmed.Unarmed(attacker, equipment))
            {
                __result *= attacker.GetStatValue(SavageStats.RavagerReimagined_UnarmedDamage, true, SavageUnarmed.CacheTicks);
            }
        }
    }

    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.AdjustedArmorPenetration),
        typeof(Tool), typeof(Pawn), typeof(Thing), typeof(HediffComp_VerbGiver))]
    public static class UnarmedArmorPenetrationPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn attacker, Thing equipment, ref float __result)
        {
            if (SavageUnarmed.Unarmed(attacker, equipment))
            {
                __result += attacker.GetStatValue(SavageStats.RavagerReimagined_UnarmedArmorPenetration, true, SavageUnarmed.CacheTicks);
            }
        }
    }

    // Only def fields here: DefOfHelper tries to bind every static field of a [DefOf] class, constants included.
    [DefOf]
    public static class SavageStats
    {
        public static StatDef RavagerReimagined_UnarmedDamage;
        public static StatDef RavagerReimagined_UnarmedArmorPenetration;

        static SavageStats()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(SavageStats));
        }
    }

    public static class SavageUnarmed
    {
        // Melee verb selection asks for damage often; the stats only change when a hediff is added or expires.
        public const int CacheTicks = 60;

        public static bool Unarmed(Pawn attacker, Thing equipment)
        {
            return equipment == null && attacker != null && attacker.equipment?.Primary == null
                && SavageStats.RavagerReimagined_UnarmedDamage != null;
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using VanillaPsycastsExpanded;
using VEF.Abilities;
using Verse;
using Ability = VEF.Abilities.Ability;
using AbilityDef = VEF.Abilities.AbilityDef;

namespace RavagerReimagined
{
    // Chances and stack sizes for the disabling strikes passive, on its hediff def (Common/Defs/DisablingStrikes.xml).
    public class DisablingStrikesExtension : DefModExtension
    {
        public HediffDef nervesHediff;
        public float nervesChance = 0.05f;
        public float nervesStacks = 1f;

        // A defName, not a def reference: the armor hediff only exists on 1.7 (it needs Sentinel Framework's 1.7 stat).
        public string armorHediff;
        public float armorChance = 0.05f;
        public float armorStacks = 1f;

        private HediffDef armorHediffDef;
        private bool armorResolved;

        public HediffDef ArmorHediff
        {
            get
            {
                if (!armorResolved)
                {
                    armorResolved = true;
                    armorHediffDef = armorHediff.NullOrEmpty() ? null : DefDatabase<HediffDef>.GetNamedSilentFail(armorHediff);
                }
                return armorHediffDef;
            }
        }
    }

    public class RavagedNervesExtension : DefModExtension
    {
        public float painPerStack = 0.01f;
    }

    // The Ravager path's abilities, as VPE lists them: every AbilityDef whose psycast extension names the path. Read once,
    // after this mod's patches have taken leg skip and disabling strike out, so they are not counted.
    public static class RavagerPath
    {
        public const string PathDefName = "AAVPE_Ravager";

        private static HashSet<AbilityDef> abilities;

        public static HashSet<AbilityDef> Abilities
        {
            get
            {
                if (abilities == null)
                {
                    PsycasterPathDef path = DefDatabase<PsycasterPathDef>.GetNamedSilentFail(PathDefName);
                    abilities = new HashSet<AbilityDef>(DefDatabase<AbilityDef>.AllDefsListForReading
                        .Where(def => path != null && def.GetModExtension<AbilityExtension_Psycast>()?.path == path));
                }
                return abilities;
            }
        }

        public static int KnownBy(Pawn pawn)
        {
            CompAbilities comp = pawn?.GetComp<CompAbilities>();
            if (comp == null)
            {
                return 0;
            }
            int known = 0;
            foreach (Ability ability in comp.LearnedAbilities)
            {
                if (Abilities.Contains(ability.def))
                {
                    known++;
                }
            }
            return known;
        }
    }

    // The caster's passive. Evolves (adds the ravaged armor roll) once the caster knows every Ravager psycast, as
    // VPE Pyromancer's psionic kindling does with its path.
    public class Hediff_DisablingStrikes : HediffWithComps
    {
        private int cachedTick = -1;
        private int known;

        public DisablingStrikesExtension Ext => def.GetModExtension<DisablingStrikesExtension>();

        public int Total => RavagerPath.Abilities.Count;

        public int Known
        {
            get
            {
                int tick = Find.TickManager.TicksGame;
                if (tick != cachedTick)
                {
                    cachedTick = tick;
                    known = RavagerPath.KnownBy(pawn);
                }
                return known;
            }
        }

        public bool Evolved => Total > 0 && Known >= Total;

        public override string LabelInBrackets => Evolved ? "evolved" : $"{Known}/{Total}";

        public override string Description
        {
            get
            {
                string text = base.Description;
                if (Evolved)
                {
                    text += Ext?.ArmorHediff != null
                        ? "\n\nEvolved: strikes can also ravage the target's armor."
                        : "\n\nEvolved, but the armor effect needs Sentinel Framework's 1.7 build.";
                }
                else
                {
                    text += $"\n\nEvolves when every Ravager psycast is known ({Known}/{Total}).";
                }
                return text;
            }
        }

        public void Notify_RavagerHit(Pawn target)
        {
            DisablingStrikesExtension ext = Ext;
            if (ext == null || target == null || target.Dead)
            {
                return;
            }
            if (ext.nervesHediff != null && Rand.Chance(ext.nervesChance))
            {
                HealthUtility.AdjustSeverity(target, ext.nervesHediff, ext.nervesStacks);
            }
            if (ext.ArmorHediff != null && Evolved && Rand.Chance(ext.armorChance))
            {
                HealthUtility.AdjustSeverity(target, ext.ArmorHediff, ext.armorStacks);
            }
        }
    }

    // Severity is the stack count; pain scales with it (a stage's painOffset can't).
    public class Hediff_RavagedNerves : HediffWithComps
    {
        public override float PainOffset
        {
            get
            {
                if (causesNoPain)
                {
                    return 0f;
                }
                return Severity * (def.GetModExtension<RavagedNervesExtension>()?.painPerStack ?? 0.01f);
            }
        }

        public override string LabelInBrackets => "x" + GenMath.RoundTo(Severity, 1f).ToString("0");
    }
}

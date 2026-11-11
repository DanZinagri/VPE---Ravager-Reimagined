using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using AbilityDef = VEF.Abilities.AbilityDef;

namespace RavagerReimagined
{
    // Hits shared by Ability_RavagerStrike and spin cut's follow-up spins. After every hit the caster's disabling strikes
    // passive rolls, and a guaranteed-bleed psycast makes sure a wound bleeds.
    public static class RavagerStrike
    {
        // (flat damage + unarmedScale x the caster's unarmed strike) x the Melee/sensitivity multiplier. A flat damage of 0
        // marks a psycast that deals no strike damage (KO, savage blow) and stays 0.
        public static float Damage(Pawn caster, RavagerStrikeExtension strike)
        {
            if (strike.damage <= 0f)
            {
                return 0f;
            }
            float total = strike.damage + strike.unarmedScale * UnarmedStrikeDamage(caster);
            return total * RavagerMod.settings.Multiplier(caster);
        }

        // The damage of the caster's hardest-hitting natural attack (fists, bite, claws, and implant weapons such as power
        // claws), as vanilla melee would deal it: part efficiency, life stage, melee damage factor and savage style's
        // unarmed bonus (which only counts while no weapon is held) are all included. 0 if the caster has none.
        public static float UnarmedStrikeDamage(Pawn caster)
        {
            float best = 0f;
            Consider(caster, caster.verbTracker?.AllVerbs, ref best);
            foreach (HediffComp_VerbGiver giver in caster.health.hediffSet.GetHediffComps<HediffComp_VerbGiver>())
            {
                Consider(caster, giver.VerbTracker?.AllVerbs, ref best);
            }
            return best;
        }

        private static void Consider(Pawn caster, List<Verb> verbs, ref float best)
        {
            if (verbs == null)
            {
                return;
            }
            foreach (Verb verb in verbs)
            {
                if (verb.IsMeleeAttack && verb.IsStillUsableBy(caster))
                {
                    best = Mathf.Max(best, verb.verbProps.AdjustedMeleeDamageAmount(verb, caster));
                }
            }
        }

        public static List<BodyPartRecord> PartsFor(Pawn target, BodyPartDef only)
        {
            return target.health.hediffSet.GetNotMissingParts()
                .Where(p => !p.def.conceptual && p.coverageAbs > 0f && (only == null || p.def == only))
                .ToList();
        }

        // One hit on a random part. A strike with no damage still counts as a Ravager hit for disabling strikes.
        public static void StrikePawn(Pawn caster, AbilityDef ability, RavagerStrikeExtension strike, Pawn victim, float amount)
        {
            if (victim.Dead)
            {
                return;
            }
            if (amount <= 0f || strike.damageDef == null)
            {
                NotifyPassive(caster, victim);
                return;
            }
            BodyPartRecord part = PartsFor(victim, strike.bodyPart).RandomElementWithFallback();
            if (part == null)
            {
                return;
            }
            ThingDef weapon = caster.equipment?.Primary?.def ?? caster.def;
            Hit(caster, ability, victim, new DamageInfo(strike.damageDef, amount, strike.armorPenetration, -1f, caster, part, weapon));
        }

        public static DamageWorker.DamageResult Hit(Pawn caster, AbilityDef ability, Thing thing, DamageInfo dinfo)
        {
            DamageWorker.DamageResult result = thing.TakeDamage(dinfo);
            if (thing is Pawn target)
            {
                ability?.GetModExtension<BleedingWoundExtension>()?.EnsureBleeding(target, result, dinfo);
                NotifyPassive(caster, target);
            }
            return result;
        }

        private static void NotifyPassive(Pawn caster, Pawn target)
        {
            if (caster?.health.hediffSet.GetFirstHediff<Hediff_DisablingStrikes>() is Hediff_DisablingStrikes passive)
            {
                passive.Notify_RavagerHit(target);
            }
        }

        // Every live pawn within radius of center other than the caster and except, optionally hostile to the caster only.
        public static List<Pawn> PawnsAround(Pawn caster, IntVec3 center, Map map, float radius, bool hostileOnly, Pawn except = null)
        {
            List<Pawn> pawns = new List<Pawn>();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map))
                {
                    continue;
                }
                foreach (Thing thing in cell.GetThingList(map))
                {
                    if (thing is Pawn other && other != caster && other != except && !other.Dead
                        && (!hostileOnly || other.HostileTo(caster)))
                    {
                        pawns.Add(other);
                    }
                }
            }
            return pawns;
        }

        // A free standable cell next to the target, the one closest to the caster.
        public static IntVec3 CellBeside(Pawn caster, Pawn target)
        {
            Map map = target.Map;
            IntVec3 best = IntVec3.Invalid;
            float bestDistance = float.MaxValue;
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(target))
            {
                if (!cell.InBounds(map) || !cell.Standable(map))
                {
                    continue;
                }
                Pawn occupant = cell.GetFirstPawn(map);
                if (occupant != null && occupant != caster)
                {
                    continue;
                }
                float distance = cell.DistanceToSquared(caster.Position);
                if (distance < bestDistance)
                {
                    best = cell;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }

    // Skewer: every hit leaves a bleeding wound. If the hit already made one, nothing changes; if armor stopped it, turned
    // it blunt, or it hit a part that can't bleed, a wound is opened on a part that can.
    public class BleedingWoundExtension : DefModExtension
    {
        public HediffDef wound;
        public float severity = 6f;

        public void EnsureBleeding(Pawn target, DamageWorker.DamageResult result, DamageInfo dinfo)
        {
            if (target.Dead || !target.health.CanBleed)
            {
                return;
            }
            if (result?.hediffs != null && result.hediffs.Any(h => h.Bleeding))
            {
                return;
            }
            HediffSet set = target.health.hediffSet;
            BodyPartRecord part = result?.LastHitPart;
            if (!CanBleedFrom(set, part))
            {
                part = set.GetNotMissingParts(BodyPartHeight.Undefined, BodyPartDepth.Outside)
                    .Where(p => CanBleedFrom(set, p))
                    .RandomElementWithFallback();
            }
            if (part == null)
            {
                return;
            }
            // Never enough on its own to take the part off.
            float amount = Mathf.Min(severity, set.GetPartHealth(part) - 1f);
            if (amount <= 0f)
            {
                return;
            }
            Hediff injury = HediffMaker.MakeHediff(wound ?? DamageDefOf.Stab.hediff, target, part);
            injury.Severity = amount;
            target.health.AddHediff(injury, part, dinfo);
        }

        private static bool CanBleedFrom(HediffSet set, BodyPartRecord part)
        {
            return part != null && !part.def.conceptual && part.coverageAbs > 0f && part.def.bleedRate > 0f && !set.PartIsMissing(part);
        }
    }

    // Savage blow: one huge hit on a random body part that isn't missing, anything from a finger to a bone, an organ, the
    // spine or the brain; whether it kills depends on the part. organsOnly narrows it to internal, non-solid parts. Flat
    // (not scaled), ignoring armor by default.
    public class PartStrikeExtension : DefModExtension
    {
        public DamageDef damageDef;
        public float damage = 999f;
        public bool ignoreArmor = true;
        public bool organsOnly;

        public void Apply(Pawn caster, AbilityDef ability, Pawn target)
        {
            if (target.Dead)
            {
                return;
            }
            BodyPartRecord part = target.health.hediffSet
                .GetNotMissingParts(BodyPartHeight.Undefined, organsOnly ? BodyPartDepth.Inside : BodyPartDepth.Undefined)
                .Where(p => !p.def.conceptual && (!organsOnly || !p.def.IsSolidInDefinition_Debug))
                .RandomElementWithFallback();
            if (part == null)
            {
                return;
            }
            DamageInfo dinfo = new DamageInfo(damageDef ?? DamageDefOf.Cut, damage, 1f, -1f, caster, part,
                caster.equipment?.Primary?.def ?? caster.def);
            dinfo.SetIgnoreArmor(ignoreArmor);
            RavagerStrike.Hit(caster, ability, target, dinfo);
        }
    }

    // Ravager hunger: biting a flesh creature feeds the caster (nutrition added straight to the food need, as if it had
    // eaten) and gives it a buff hediff. Mechanoids and other non-flesh targets give nothing but the bite.
    public class FeedOnHitExtension : DefModExtension
    {
        public float nutrition = 1f;
        public HediffDef buff;

        public void Apply(Pawn caster, Pawn target)
        {
            if (caster.Dead || !target.RaceProps.IsFlesh)
            {
                return;
            }
            if (caster.needs?.food is Need_Food food)
            {
                food.CurLevel += nutrition;
            }
            if (buff != null)
            {
                caster.health.AddHediff(buff);
            }
        }
    }

    // EMP blow: a small EMP explosion around the target, like a scaled-down Staticlord static pulse. The caster and the
    // struck target are left out (the target already took the blow's own EMP hit).
    public class EmpBurstExtension : DefModExtension
    {
        public float radius = 3.9f;
        public DamageDef damageDef;
        public int damage = -1;
        public SoundDef sound;

        public void Apply(Pawn caster, Pawn target)
        {
            Map map = target.MapHeld;
            if (map == null)
            {
                return;
            }
            GenExplosion.DoExplosion(target.PositionHeld, map, radius, damageDef ?? DamageDefOf.EMP, caster,
                damAmount: damage, explosionSound: sound, ignoredThings: new List<Thing> { caster, target });
        }
    }

    // Spin cut: after the strike the caster keeps spinning, cutting every hostile standing next to it at a fixed interval.
    // The duration is the ability's durationTime (scaled by its durationTimeStatFactors, psychic sensitivity).
    public class SpinCutExtension : DefModExtension
    {
        public HediffDef hediff;
        public int interval = 120;
        public float radius = 1.9f;
        public FleckDef fleck;
        public float fleckScale = 2f;

        // The swirl thrown on every spin: dust puffs spaced around a ring, each sent sideways around the circle and
        // turning as it fades, and slash marks laid along the ring like blade arcs. Rotation is counter-clockwise.
        public FleckDef dustFleck;
        public int dustCount = 10;
        public float dustScale = 1.2f;
        public float dustSpeed = 2.5f;
        public float dustRotationRate = 240f;
        public FleckDef slashFleck;
        public int slashCount = 4;
        public float slashScale = 1.4f;
        public float slashSolidTime = 0.15f;
        public float swirlRadius = 1f;

        public void Apply(Ability_RavagerStrike ability)
        {
            Pawn pawn = ability.pawn;
            int duration = ability.GetDurationForPawn();
            if (hediff == null || pawn == null || pawn.Dead || duration <= 0)
            {
                return;
            }
            if (!(pawn.health.hediffSet.GetFirstHediffOfDef(hediff) is Hediff_SpinCut spin))
            {
                spin = (Hediff_SpinCut)HediffMaker.MakeHediff(hediff, pawn);
                pawn.health.AddHediff(spin);
            }
            spin.Start(ability.def, duration);
            if (pawn.Spawned)
            {
                Hediff_SpinCut.Swirl(this, pawn.Map, pawn.DrawPos);
            }
        }
    }

    public class Hediff_SpinCut : HediffWithComps
    {
        private AbilityDef ability;
        private int ticksLeft;
        private int age;

        public void Start(AbilityDef def, int duration)
        {
            ability = def;
            ticksLeft = duration;
            age = 0;
        }

        public override bool ShouldRemove => ticksLeft <= 0 || base.ShouldRemove;

        public override string LabelInBrackets => ticksLeft.ToStringSecondsFromTicks();

        public override void Tick()
        {
            base.Tick();
            ticksLeft--;
            age++;
            SpinCutExtension ext = ability?.GetModExtension<SpinCutExtension>();
            if (ext == null || pawn.Downed)
            {
                ticksLeft = 0;
                return;
            }
            // Skipped, not delayed, while the caster is mid-leap (despawned inside its flyer).
            if (age % ext.interval == 0 && pawn.Spawned)
            {
                Spin(ext);
            }
        }

        // Standing hostiles only: unlike the cast, the repeated spins never cut allies or finish off the downed.
        private void Spin(SpinCutExtension ext)
        {
            RavagerStrikeExtension strike = ability.GetModExtension<RavagerStrikeExtension>();
            if (strike?.damageDef == null)
            {
                return;
            }
            Map map = pawn.Map;
            IntVec3 center = pawn.Position;
            float amount = RavagerStrike.Damage(pawn, strike);
            foreach (Pawn target in RavagerStrike.PawnsAround(pawn, center, map, ext.radius, true))
            {
                if (!target.Downed)
                {
                    RavagerStrike.StrikePawn(pawn, ability, strike, target, amount);
                }
            }
            if (!strike.mote.NullOrEmpty())
            {
                MoteMaker.ThrowText(center.ToVector3Shifted(), map, strike.mote.Translate(), 3f);
            }
            ability.castSound?.PlayOneShot(new TargetInfo(center, map));
            if (ext.fleck != null)
            {
                FleckMaker.Static(center, map, ext.fleck, ext.fleckScale);
            }
            Swirl(ext, map, pawn.DrawPos);
        }

        public static void Swirl(SpinCutExtension ext, Map map, Vector3 center)
        {
            float start = Rand.Range(0f, 360f);
            if (ext.dustFleck != null)
            {
                for (int i = 0; i < ext.dustCount; i++)
                {
                    float angle = start + 360f * i / ext.dustCount;
                    FleckCreationData data = FleckMaker.GetDataStatic(RingPoint(center, angle, ext.swirlRadius), map, ext.dustFleck,
                        ext.dustScale * Rand.Range(0.8f, 1.2f));
                    // Fleck velocity uses the same angle convention as RingPoint, so +90 is the tangent.
                    data.velocityAngle = angle + 90f;
                    data.velocitySpeed = ext.dustSpeed * Rand.Range(0.85f, 1.15f);
                    data.rotation = Rand.Range(0f, 360f);
                    data.rotationRate = ext.dustRotationRate;
                    map.flecks.CreateFleck(data);
                }
            }
            if (ext.slashFleck != null)
            {
                for (int i = 0; i < ext.slashCount; i++)
                {
                    float angle = start + 360f * i / ext.slashCount;
                    FleckCreationData data = FleckMaker.GetDataStatic(RingPoint(center, angle, ext.swirlRadius), map, ext.slashFleck, ext.slashScale);
                    data.rotation = angle + 90f;
                    data.solidTimeOverride = ext.slashSolidTime;
                    map.flecks.CreateFleck(data);
                }
            }
        }

        private static Vector3 RingPoint(Vector3 center, float angle, float radius)
        {
            return center + Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward * radius;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref ability, "ability");
            Scribe_Values.Look(ref ticksLeft, "ticksLeft");
            Scribe_Values.Look(ref age, "age");
        }
    }

    // KO: the knockout only lands on a roll. The chance starts at baseChance and moves by chancePerLevel for each point
    // of Melee the caster has over (or under) the target, clamped to [minChance, maxChance]. Pawns without skills use
    // their fixed mech skill level if they are mechanoids, levelWithoutSkills otherwise. A failed cast still costs
    // psyfocus, heat and cooldown; Ability_RavagerStrike skips the hediff and shows the failure text.
    public class ContestedChanceExtension : DefModExtension
    {
        public float baseChance = 0.5f;
        public float chancePerLevel = 0.03f;
        public float minChance = 0.01f;
        public float maxChance = 0.99f;
        public float levelWithoutSkills = 10f;

        public float LevelOf(Pawn pawn)
        {
            float level = RavagerSettings.MeleeLevel(pawn);
            if (level >= 0f)
            {
                return level;
            }
            return pawn.RaceProps.IsMechanoid ? pawn.RaceProps.mechFixedSkillLevel : levelWithoutSkills;
        }

        public float ChanceFor(Pawn caster, Pawn target)
        {
            return Mathf.Clamp(baseChance + chancePerLevel * (LevelOf(caster) - LevelOf(target)), minChance, maxChance);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Ability = VEF.Abilities.Ability;

namespace RavagerReimagined
{
    // How a Ravager strike lands. Damage is scaled by RavagerSettings.Multiplier (Melee skill x psychic sensitivity);
    // a damage of 0 strikes for nothing, for psycasts whose effect is elsewhere (KO's hediff, savage blow's part strike).
    public class RavagerStrikeExtension : DefModExtension
    {
        public DamageDef damageDef;
        public float damage;
        public float armorPenetration;
        public int repeatAmount = 1;
        // Only parts of this def are hit; any non-missing part when null.
        public BodyPartDef bodyPart;

        // Teleport next to the target first, or leap next to it (the hit lands on landing); returnJump leaps back after.
        public bool teleport;
        public bool jump;
        public bool returnJump;

        // Each hit also strikes every other pawn within cleaveRadius of the target (hostile to the caster only, by default).
        public bool cleave;
        public float cleaveRadius = 1.9f;
        public bool cleaveHostileOnly = true;

        // Language keys for the text thrown over the target (success) or the caster (failure).
        public string mote;
        public string moteFailed;
    }

    // Every Ravager attack psycast. The VEF base cast pays psyfocus and heat, starts the cooldown and applies any
    // AbilityExtension_Hediff; this class moves the caster if needed, strikes, then runs the optional effect extensions
    // (StrikeEffects.cs). No weapon is needed, and any weapon works: Ravager fights with its own claws.
    public class Ability_RavagerStrike : Ability
    {
        // Set by a failed KO-style roll for the length of the cast, so the hediff and the success text are skipped.
        private bool contestFailed;

        public RavagerStrikeExtension Strike => def.GetModExtension<RavagerStrikeExtension>();

        public override void Cast(params GlobalTargetInfo[] targets)
        {
            Pawn target = targets.Length > 0 ? targets[0].Thing as Pawn : null;
            ContestedChanceExtension contest = def.GetModExtension<ContestedChanceExtension>();
            contestFailed = contest != null && target != null && !Rand.Chance(contest.ChanceFor(pawn, target));

            base.Cast(targets);

            RavagerStrikeExtension strike = Strike;
            if (target == null || strike == null)
            {
                contestFailed = false;
                return;
            }
            if (strike.jump)
            {
                Leap(target, strike);
                return;
            }
            if (strike.teleport && !TeleportBeside(target))
            {
                Fail();
                return;
            }
            Resolve(target);
        }

        public override void ApplyHediffs(params GlobalTargetInfo[] targetInfo)
        {
            if (!contestFailed)
            {
                base.ApplyHediffs(targetInfo);
            }
        }

        // Shows a contested psycast's chance next to the cursor while targeting.
        public override string ExtraLabelMouseAttachment(LocalTargetInfo target)
        {
            string label = base.ExtraLabelMouseAttachment(target);
            if (target.Pawn is Pawn other && pawn != null
                && def.GetModExtension<ContestedChanceExtension>() is ContestedChanceExtension contest)
            {
                string chance = $"{def.LabelCap} chance: {contest.ChanceFor(pawn, other).ToStringPercent()}";
                label = label.NullOrEmpty() ? chance : label + "\n" + chance;
            }
            return label;
        }

        // The strike itself, straight away or when a leap lands (RavagerLeap).
        public void Resolve(Pawn target)
        {
            RavagerStrikeExtension strike = Strike;
            if (strike == null || target == null || !target.Spawned || target.Map != pawn.MapHeld)
            {
                Fail();
                return;
            }
            if (RavagerStrike.PartsFor(target, strike.bodyPart).Count == 0)
            {
                Fail();
                return;
            }

            Map map = target.Map;
            IntVec3 center = target.Position;
            float amount = RavagerStrike.Damage(pawn, strike);
            for (int i = 0; i < strike.repeatAmount; i++)
            {
                RavagerStrike.StrikePawn(pawn, def, strike, target, amount);
                if (strike.cleave)
                {
                    foreach (Pawn other in RavagerStrike.PawnsAround(pawn, center, map, strike.cleaveRadius, strike.cleaveHostileOnly, target))
                    {
                        RavagerStrike.StrikePawn(pawn, def, strike, other, amount);
                    }
                }
            }

            def.GetModExtension<PartStrikeExtension>()?.Apply(pawn, def, target);
            def.GetModExtension<FeedOnHitExtension>()?.Apply(pawn, target);
            def.GetModExtension<EmpBurstExtension>()?.Apply(pawn, target);
            def.GetModExtension<SpinCutExtension>()?.Apply(this);

            string key = contestFailed ? strike.moteFailed : strike.mote;
            if (!key.NullOrEmpty())
            {
                MoteMaker.ThrowText(center.ToVector3Shifted(), map, key.Translate(), 3f);
            }
            contestFailed = false;
        }

        private void Fail()
        {
            string key = Strike?.moteFailed;
            if (!key.NullOrEmpty() && pawn.MapHeld != null)
            {
                MoteMaker.ThrowText(pawn.PositionHeld.ToVector3Shifted(), pawn.MapHeld, key.Translate(), 3f);
            }
            contestFailed = false;
        }

        private bool TeleportBeside(Pawn target)
        {
            IntVec3 cell = RavagerStrike.CellBeside(pawn, target);
            if (!cell.IsValid)
            {
                return false;
            }
            FleckMaker.ThrowDustPuff(pawn.DrawPos, pawn.Map, 1f);
            pawn.Position = cell;
            pawn.Notify_Teleported(endCurrentJob: false);
            FleckMaker.ThrowDustPuff(pawn.DrawPos, pawn.Map, 1f);
            return true;
        }

        private void Leap(Pawn target, RavagerStrikeExtension strike)
        {
            IntVec3 landing = RavagerStrike.CellBeside(pawn, target);
            if (!landing.IsValid)
            {
                Fail();
                return;
            }
            Map map = pawn.Map;
            IntVec3 origin = pawn.Position;
            if (!(PawnFlyer.MakeFlyer(RavagerLeap.FlyerDef, pawn, landing, null, null) is RavagerLeap leap))
            {
                Fail();
                return;
            }
            leap.Setup(this, target, strike.returnJump ? origin : IntVec3.Invalid);
            GenSpawn.Spawn(leap, origin, map);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref contestFailed, "contestFailed");
        }
    }

    // The leap flyer (RavagerReimagined_RavagerLeap). Carries the strike to resolve on landing and, for returnJump, the
    // cell to leap back to; the leap back is a plain flyer with neither.
    public class RavagerLeap : PawnFlyer
    {
        private Ability_RavagerStrike ability;
        private Pawn target;
        private IntVec3 returnTo = IntVec3.Invalid;

        private static ThingDef flyerDef;

        public static ThingDef FlyerDef => flyerDef ?? (flyerDef = DefDatabase<ThingDef>.GetNamed("RavagerReimagined_RavagerLeap"));

        public void Setup(Ability_RavagerStrike ability, Pawn target, IntVec3 returnTo)
        {
            this.ability = ability;
            this.target = target;
            this.returnTo = returnTo;
        }

        protected override void RespawnPawn()
        {
            Pawn pawn = FlyingPawn;
            base.RespawnPawn();
            if (pawn == null || ability == null)
            {
                return;
            }
            ability.Resolve(target);
            if (returnTo.IsValid && pawn.Spawned && !pawn.Dead && !pawn.Downed)
            {
                LeapBack(pawn);
            }
        }

        private void LeapBack(Pawn pawn)
        {
            Map map = pawn.Map;
            IntVec3 cell = returnTo;
            if (!cell.InBounds(map))
            {
                return;
            }
            if (!cell.Standable(map) && !CellFinder.TryFindRandomCellNear(cell, map, 2, c => c.Standable(map), out cell))
            {
                return;
            }
            IntVec3 from = pawn.Position;
            if (cell == from)
            {
                return;
            }
            PawnFlyer back = PawnFlyer.MakeFlyer(def, pawn, cell, null, null);
            if (back != null)
            {
                GenSpawn.Spawn(back, from, map);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref ability, "ability");
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref returnTo, "returnTo", IntVec3.Invalid);
        }
    }
}

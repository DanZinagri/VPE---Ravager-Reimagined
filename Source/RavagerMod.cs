using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RavagerReimagined
{
    public class RavagerSettings : ModSettings
    {
        // Damage bonus per point of the caster's Melee skill: factor = 1 + meleePerPoint * level (Melee 0 = the XML damage).
        public float meleePerPoint = DefaultMeleePerPoint;

        // How much psychic sensitivity counts: factor = 1 + weight * (sensitivity - 1). 1 = straight multiply, 0 = ignored.
        public float sensitivityWeight = DefaultSensitivityWeight;

        public const float DefaultMeleePerPoint = 0.01f;
        public const float MinMeleePerPoint = 0.01f;
        public const float MaxMeleePerPoint = 0.1f;
        public const float DefaultSensitivityWeight = 0.25f;

        // Saved under new keys (2026-10-30): the old three-point Melee curve and a sensitivity weight saved at the old
        // default of 1 are dropped rather than carried over.
        public override void ExposeData()
        {
            Scribe_Values.Look(ref meleePerPoint, "meleePerPoint", DefaultMeleePerPoint);
            Scribe_Values.Look(ref sensitivityWeight, "sensitivityImpact", DefaultSensitivityWeight);
            meleePerPoint = Mathf.Clamp(meleePerPoint, MinMeleePerPoint, MaxMeleePerPoint);
        }

        public float SkillFactor(float level)
        {
            // A pawn without skills (not a normal Ravager caster) is left at x1.
            if (level < 0f)
            {
                return 1f;
            }
            return 1f + meleePerPoint * level;
        }

        public float SensitivityFactor(float sensitivity)
        {
            return Mathf.Max(0f, 1f + sensitivityWeight * (sensitivity - 1f));
        }

        public float Multiplier(Pawn pawn)
        {
            return SkillFactor(MeleeLevel(pawn)) * SensitivityFactor(pawn.GetStatValue(StatDefOf.PsychicSensitivity));
        }

        public static float MeleeLevel(Pawn pawn)
        {
            SkillRecord melee = pawn.skills?.GetSkill(SkillDefOf.Melee);
            return melee == null ? -1f : melee.Level;
        }

        public void Reset()
        {
            meleePerPoint = DefaultMeleePerPoint;
            sensitivityWeight = DefaultSensitivityWeight;
        }
    }

    public class RavagerMod : Mod
    {
        public static RavagerSettings settings;

        // Example casts shown under the sliders: savage psychic slice (20) and skewer (30).
        private static readonly (string label, float damage)[] examples = { ("Savage psychic slice", 20f), ("Skewer", 30f) };
        private static readonly (int skill, float sensitivity)[] exampleCasters = { (0, 1f), (10, 1f), (20, 1f), (20, 2f), (20, 3f) };

        public RavagerMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<RavagerSettings>();
            new Harmony("DanZinagri.VPE.RavagerReimagined").PatchAll(Assembly.GetExecutingAssembly());
        }

        public override string SettingsCategory()
        {
            return "VPE - Ravager Reimagined";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            list.Label("Ravager damage is multiplied by a Melee skill factor and a psychic sensitivity factor. Changes apply straight away.");
            list.GapLine();

            list.Label($"Melee weight: +{settings.meleePerPoint * 100f:0.#}% damage per Melee point (x{settings.SkillFactor(20):0.00} at Melee 20)", tooltip:
                "Factor = 1 + weight x Melee level. Melee 0 deals the listed damage.");
            settings.meleePerPoint = Mathf.Clamp(Mathf.Round(list.Slider(settings.meleePerPoint, RavagerSettings.MinMeleePerPoint,
                RavagerSettings.MaxMeleePerPoint) * 200f) / 200f, RavagerSettings.MinMeleePerPoint, RavagerSettings.MaxMeleePerPoint);
            list.Gap();

            list.Label($"Psychic sensitivity weight: {settings.sensitivityWeight:0.00}", tooltip:
                "Factor = 1 + weight x (sensitivity - 1). At 1 damage is multiplied by sensitivity; at 0 sensitivity is ignored; at 0.5 a 200% caster gets x1.5.");
            settings.sensitivityWeight = Snap(list.Slider(settings.sensitivityWeight, 0f, 2f));
            list.GapLine();

            list.Label("Examples (damage per hit):");
            foreach ((string label, float damage) in examples)
            {
                string line = label + ":";
                foreach ((int skill, float sensitivity) in exampleCasters)
                {
                    float mult = settings.SkillFactor(skill) * settings.SensitivityFactor(sensitivity);
                    line += $"   Melee {skill} / {sensitivity * 100f:0}%: {damage * mult:0}";
                }
                list.Label(line);
            }
            list.Gap();

            if (list.ButtonText("Reset to defaults"))
            {
                settings.Reset();
            }
            list.End();
        }

        private static float Snap(float value)
        {
            return Mathf.Round(value * 20f) / 20f;
        }
    }
}

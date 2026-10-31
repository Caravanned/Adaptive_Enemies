using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace OmniNerd.AdaptiveExpression
{
    [StaticConstructorOnStartup]
    internal static class AdaptiveExpressionPopulation
    {
        private const string GeneDefName = "Omni-Adaptive Lite";
        private const string ExpressionHediffDefName = "Adaptive-ExpressionLite";
        private const float SpawnChance = 0.10f;
        internal const float MaxExpression = 0.20f;
        private const float RimesisExpressionCeiling = 0.50f;
        private const float CountExpressionCeiling = 0.25f;
        private const float EmperorExpressionCeiling = 0.60f;
        private const float ElevatedAdultExpressionRatio = 0.85f;
        private const float ElevatedPlateauExpressionRatio = 0.92f;
        private const float ChildMaxExpression = 0.08f;
        internal const float AdultAge = 18f;
        private const float PlateauAge = 40f;
        private const float MaxAge = 80f;
        private static int? countSeniority;
        private static int? emperorSeniority;
        private static bool missingGeneLogged;
        private static bool missingAllelesLogged;

        static AdaptiveExpressionPopulation()
        {
            MethodInfo generateGenes = AccessTools.Method(typeof(PawnGenerator), "GenerateGenes");
            if (generateGenes == null)
            {
                throw new MissingMethodException(typeof(PawnGenerator).FullName, "GenerateGenes");
            }

            Harmony harmony = new Harmony("OmniNerd.AdaptiveExpression.PopulationPatch");
            harmony.Patch(
                generateGenes,
                postfix: new HarmonyMethod(typeof(AdaptiveExpressionPopulation), "AfterGenerateGenes"));
            PatchRimesisGeneRestoration(harmony);
        }

        private static void PatchRimesisGeneRestoration(Harmony harmony)
        {
            Assembly rimesisAssembly = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, "Rimesis", StringComparison.Ordinal))
                {
                    rimesisAssembly = assembly;
                    break;
                }
            }

            if (rimesisAssembly == null)
            {
                return;
            }

            Type[] rimesisTypes;
            try
            {
                rimesisTypes = rimesisAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                rimesisTypes = exception.Types ?? new Type[0];
                Log.Warning("[Adaptive Expression Population Patch] Some Rimesis types could not be inspected: " + exception.Message);
            }

            int patchedMethods = 0;
            bool foundRestoreMethod = false;
            foreach (Type type in rimesisTypes)
            {
                if (type == null)
                {
                    continue;
                }

                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (MethodInfo method in methods)
                {
                    if (!string.Equals(method.Name, "RestoreRecordedGenes", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foundRestoreMethod = true;
                    int pawnArgumentCount = 0;
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        Type parameterType = parameter.ParameterType;
                        if (parameterType == typeof(Pawn)
                            || (parameterType.IsByRef && parameterType.GetElementType() == typeof(Pawn)))
                        {
                            pawnArgumentCount++;
                        }
                    }

                    if (pawnArgumentCount != 1)
                    {
                        continue;
                    }

                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(typeof(AdaptiveExpressionPopulation), "AfterRimesisGenesRestored"));
                    patchedMethods++;
                }
            }

            if (patchedMethods == 0)
            {
                string reason = foundRestoreMethod
                    ? "its RestoreRecordedGenes method has no compatible signature with exactly one Pawn parameter"
                    : "its RestoreRecordedGenes method could not be found";
                Log.Warning("[Adaptive Expression Population Patch] Rimesis is loaded, but " + reason + ".");
                return;
            }

            Log.Message("[Adaptive Expression Population Patch] Added Rimesis gene-restoration safeguards to "
                + patchedMethods + " method(s).");
        }

        private static void AfterRimesisGenesRestored(object[] __args)
        {
            foreach (object argument in __args)
            {
                Pawn pawn = argument as Pawn;
                if (pawn != null)
                {
                    MakeRimesisExpressionStronger(pawn);
                    return;
                }
            }
        }

        private static void AfterGenerateGenes(Pawn pawn, XenotypeDef xenotype, PawnGenerationRequest request)
        {
            if (pawn == null || pawn.genes == null
                || pawn.health == null || pawn.Faction == Faction.OfPlayer
                || request.Faction == Faction.OfPlayer || pawn.Destroyed || pawn.Dead)
            {
                return;
            }

            if (request.Context.ToString().IndexOf("World", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            if (pawn.ageTracker == null)
            {
                return;
            }

            GeneDef geneDef = DefDatabase<GeneDef>.GetNamedSilentFail(GeneDefName);
            if (geneDef == null)
            {
                if (!missingGeneLogged)
                {
                    Log.Error("[Adaptive Expression Population Patch] Could not find gene def " + GeneDefName + ".");
                    missingGeneLogged = true;
                }

                return;
            }

            bool rimesisContext =
                request.Context.ToString().IndexOf("Rimesis", StringComparison.OrdinalIgnoreCase) >= 0;
            bool alreadyHasGene = HasGene(pawn, geneDef);
            if (rimesisContext)
            {
                if (!alreadyHasGene && pawn.ageTracker.AgeBiologicalYearsFloat < AdultAge)
                {
                    return;
                }

                MakeRimesisExpressionStronger(pawn, geneDef);
                return;
            }

            if (!alreadyHasGene && pawn.ageTracker.AgeBiologicalYearsFloat < AdultAge)
            {
                return;
            }

            bool addedGene = false;
            Gene generatedGene = null;
            if (!alreadyHasGene && Rand.Value < SpawnChance)
            {
                bool rankedImperial = IsRankedImperial(pawn, request.KindDef);
                pawn.genes.AddGene(geneDef, false);
                generatedGene = pawn.genes.GetGene(geneDef);
                float ignoredPotential;
                if (generatedGene == null || !TrySetAlleles(generatedGene, rankedImperial, out ignoredPotential))
                {
                    Log.Error("[Adaptive Expression Population Patch] Could not set alleles on " + GeneDefName + " for " + pawn);
                    if (generatedGene != null)
                    {
                        pawn.genes.RemoveGene(generatedGene);
                    }

                    return;
                }

                addedGene = true;
            }
            else if (!alreadyHasGene)
            {
                return;
            }

            float expressionCeiling = ExpressionCeiling(pawn, request.KindDef, false);
            float allelePotential = AllelePotential(pawn);
            HediffDef hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(ExpressionHediffDefName);
            Hediff hediff = hediffDef == null ? null : pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
            if (hediff == null)
            {
                Log.Error("[Adaptive Expression Population Patch] Gene " + GeneDefName + " did not add its expression hediff to " + pawn);
                if (addedGene && generatedGene != null)
                {
                    pawn.genes.RemoveGene(generatedGene);
                }

                return;
            }

            HediffComp_AdaptiveExpressionAge ageComp =
                hediff.TryGetComp<HediffComp_AdaptiveExpressionAge>();
            if (ageComp == null)
            {
                Log.Error("[Adaptive Expression Population Patch] The Lite gene's expression hediff is missing its age component on " + pawn);
                if (addedGene && generatedGene != null)
                {
                    pawn.genes.RemoveGene(generatedGene);
                }

                return;
            }

            ageComp.MarkManaged(expressionCeiling);
            hediff.Severity = Math.Min(
                ExpressionForAge(pawn.ageTracker.AgeBiologicalYearsFloat, expressionCeiling),
                allelePotential);
        }

        private static void MakeRimesisExpressionStronger(Pawn pawn, GeneDef geneDef = null)
        {
            if (pawn == null || pawn.genes == null || pawn.health == null
                || pawn.ageTracker == null || pawn.Faction == Faction.OfPlayer
                || pawn.Destroyed || pawn.Dead)
            {
                return;
            }

            if (geneDef == null)
            {
                geneDef = DefDatabase<GeneDef>.GetNamedSilentFail(GeneDefName);
            }

            if (geneDef == null)
            {
                if (!missingGeneLogged)
                {
                    Log.Error("[Adaptive Expression Population Patch] Could not find gene def " + GeneDefName + ".");
                    missingGeneLogged = true;
                }

                return;
            }

            Gene gene = pawn.genes.GetGene(geneDef);
            bool addedGene = false;
            if (gene == null)
            {
                if (pawn.ageTracker.AgeBiologicalYearsFloat < AdultAge)
                {
                    return;
                }

                pawn.genes.AddGene(geneDef, false);
                gene = pawn.genes.GetGene(geneDef);
                addedGene = gene != null;
            }

            HediffDef hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(ExpressionHediffDefName);
            Hediff hediff = hediffDef == null ? null : pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
            if (hediff == null)
            {
                Log.Error("[Adaptive Expression Population Patch] Rimesis pawn " + pawn + " has no expression hediff for " + GeneDefName + ".");
                if (addedGene)
                {
                    pawn.genes.RemoveGene(gene);
                }

                return;
            }

            HediffComp_AdaptiveExpressionAge ageComp =
                hediff.TryGetComp<HediffComp_AdaptiveExpressionAge>();
            if (ageComp == null)
            {
                Log.Error("[Adaptive Expression Population Patch] The Lite gene's expression hediff is missing its age component on Rimesis pawn " + pawn);
                if (addedGene)
                {
                    pawn.genes.RemoveGene(gene);
                }

                return;
            }

            float allelePotential;
            if (gene == null || !TryStrengthenAlleles(gene, out allelePotential))
            {
                Log.Error("[Adaptive Expression Population Patch] Could not strengthen the Lite gene for Rimesis pawn " + pawn);
                if (addedGene && gene != null)
                {
                    pawn.genes.RemoveGene(gene);
                }

                return;
            }

            float expressionCeiling = ExpressionCeiling(pawn, null, true);
            ageComp.MarkRimesisEnhanced(expressionCeiling);
            float initialExpression = ExpressionForAge(
                pawn.ageTracker.AgeBiologicalYearsFloat,
                expressionCeiling);
            hediff.Severity = Math.Min(initialExpression, allelePotential);
        }

        private static bool TryStrengthenAlleles(Gene gene, out float potential)
        {
            potential = 0f;
            FieldInfo alleleA = AccessTools.Field(gene.GetType(), "alleleA");
            FieldInfo alleleB = AccessTools.Field(gene.GetType(), "alleleB");
            if (alleleA == null || alleleB == null
                || alleleA.FieldType != typeof(float) || alleleB.FieldType != typeof(float))
            {
                return false;
            }

            float valueA = (float)alleleA.GetValue(gene);
            float valueB = (float)alleleB.GetValue(gene);
            if (float.IsNaN(valueA) || float.IsInfinity(valueA))
            {
                valueA = Rand.Range(0.4f, 0.5f);
            }
            if (float.IsNaN(valueB) || float.IsInfinity(valueB))
            {
                valueB = Rand.Range(0.4f, 0.5f);
            }

            valueA = Math.Max(0.4f, Math.Min(0.5f, valueA));
            valueB = Math.Max(0.4f, Math.Min(0.5f, valueB));
            alleleA.SetValue(gene, valueA);
            alleleB.SetValue(gene, valueB);
            potential = valueA + valueB;

            return true;
        }

        internal static float ExpressionForAge(float age, float expressionCeiling)
        {
            if (age < AdultAge)
            {
                float childProgress = Math.Max(0f, Math.Min(1f, age / AdultAge));
                return 0.01f + (ChildMaxExpression - 0.01f) * childProgress * childProgress;
            }

            float safeCeiling = ClampExpressionCeiling(expressionCeiling);
            bool elevated = safeCeiling > MaxExpression;
            float adultExpression = elevated
                ? safeCeiling * ElevatedAdultExpressionRatio
                : 0.10f;
            float plateauExpression = elevated
                ? safeCeiling * ElevatedPlateauExpressionRatio
                : 0.12f;

            if (age <= PlateauAge)
            {
                float plateauProgress = (age - AdultAge) / (PlateauAge - AdultAge);
                return adultExpression + (plateauExpression - adultExpression) * plateauProgress * plateauProgress;
            }

            float laterProgress = Math.Min(1f, (age - PlateauAge) / (MaxAge - PlateauAge));
            float expression = plateauExpression + (safeCeiling - plateauExpression) * laterProgress * laterProgress;
            return Math.Min(safeCeiling, expression);
        }

        internal static float ClampExpressionCeiling(float expressionCeiling)
        {
            if (float.IsNaN(expressionCeiling) || float.IsInfinity(expressionCeiling))
            {
                return MaxExpression;
            }

            return Math.Max(MaxExpression, Math.Min(EmperorExpressionCeiling, expressionCeiling));
        }

        internal static float ExpressionCeiling(Pawn pawn, PawnKindDef kindDef, bool rimesisEnhanced)
        {
            float ceiling = ImperialExpressionCeiling(pawn, kindDef);
            if (rimesisEnhanced)
            {
                ceiling = Math.Max(ceiling, RimesisExpressionCeiling);
            }

            return ClampExpressionCeiling(ceiling);
        }

        internal static float AllelePotential(Pawn pawn)
        {
            if (pawn == null || pawn.genes == null)
            {
                return 0f;
            }

            GeneDef geneDef = DefDatabase<GeneDef>.GetNamedSilentFail(GeneDefName);
            Gene gene = geneDef == null ? null : pawn.genes.GetGene(geneDef);
            if (gene == null)
            {
                return 0f;
            }

            FieldInfo alleleA = AccessTools.Field(gene.GetType(), "alleleA");
            FieldInfo alleleB = AccessTools.Field(gene.GetType(), "alleleB");
            if (alleleA == null || alleleB == null
                || alleleA.FieldType != typeof(float) || alleleB.FieldType != typeof(float))
            {
                if (!missingAllelesLogged)
                {
                    Log.Error("[Adaptive Expression Population Patch] Could not read the Lite gene's allele fields.");
                    missingAllelesLogged = true;
                }

                return 0f;
            }

            return (float)alleleA.GetValue(gene) + (float)alleleB.GetValue(gene);
        }

        private static bool IsRankedImperial(Pawn pawn, PawnKindDef kindDef)
        {
            return HighestImperialSeniority(pawn, kindDef) >= CountSeniority();
        }

        private static float ImperialExpressionCeiling(Pawn pawn, PawnKindDef kindDef)
        {
            int minimumSeniority = CountSeniority();
            int highestSeniority = HighestImperialSeniority(pawn, kindDef);
            if (highestSeniority < minimumSeniority)
            {
                return MaxExpression;
            }

            int maximumSeniority = EmperorSeniority();
            if (maximumSeniority <= minimumSeniority)
            {
                return CountExpressionCeiling;
            }

            float rankProgress = Math.Max(
                0f,
                Math.Min(1f, (float)(highestSeniority - minimumSeniority) / (maximumSeniority - minimumSeniority)));
            return CountExpressionCeiling
                + (EmperorExpressionCeiling - CountExpressionCeiling) * rankProgress;
        }

        private static int HighestImperialSeniority(Pawn pawn, PawnKindDef kindDef)
        {
            if (pawn == null || pawn.Faction == null || pawn.Faction.def.defName != "Empire")
            {
                return int.MinValue;
            }

            int highestSeniority = kindDef != null && kindDef.titleRequired != null
                ? kindDef.titleRequired.seniority
                : int.MinValue;
            if (pawn.royalty == null)
            {
                return highestSeniority;
            }

            foreach (RoyalTitle title in pawn.royalty.AllTitlesInEffectForReading)
            {
                highestSeniority = Math.Max(highestSeniority, title.def.seniority);
            }

            return highestSeniority;
        }

        private static int CountSeniority()
        {
            if (countSeniority.HasValue)
            {
                return countSeniority.Value;
            }

            foreach (RoyalTitleDef titleDef in DefDatabase<RoyalTitleDef>.AllDefsListForReading)
            {
                if (string.Equals(titleDef.defName, "Count", StringComparison.OrdinalIgnoreCase))
                {
                    countSeniority = titleDef.seniority;
                    return countSeniority.Value;
                }
            }

            Log.Error("[Adaptive Expression Population Patch] Could not resolve the vanilla Count title.");
            countSeniority = int.MaxValue;
            return countSeniority.Value;
        }

        private static int EmperorSeniority()
        {
            if (emperorSeniority.HasValue)
            {
                return emperorSeniority.Value;
            }

            foreach (RoyalTitleDef titleDef in DefDatabase<RoyalTitleDef>.AllDefsListForReading)
            {
                if (string.Equals(titleDef.defName, "Emperor", StringComparison.OrdinalIgnoreCase))
                {
                    emperorSeniority = titleDef.seniority;
                    return emperorSeniority.Value;
                }
            }

            Log.Error("[Adaptive Expression Population Patch] Could not resolve the vanilla Emperor title.");
            emperorSeniority = CountSeniority();
            return emperorSeniority.Value;
        }

        private static bool HasGene(Pawn pawn, GeneDef geneDef)
        {
            foreach (Gene gene in pawn.genes.GenesListForReading)
            {
                if (gene.def == geneDef)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TrySetAlleles(Gene gene, bool rankedImperial, out float potential)
        {
            potential = 0f;
            FieldInfo alleleA = AccessTools.Field(gene.GetType(), "alleleA");
            FieldInfo alleleB = AccessTools.Field(gene.GetType(), "alleleB");
            if (alleleA == null || alleleB == null
                || alleleA.FieldType != typeof(float) || alleleB.FieldType != typeof(float))
            {
                return false;
            }

            if (rankedImperial)
            {
                float valueA = Rand.Range(0.4f, 0.5f);
                float valueB = Rand.Range(0.4f, 0.5f);
                alleleA.SetValue(gene, valueA);
                alleleB.SetValue(gene, valueB);
                potential = valueA + valueB;
            }
            else
            {
                float valueA = Rand.Range(0.1f, 0.5f);
                alleleA.SetValue(gene, valueA);
                alleleB.SetValue(gene, 0f);
                potential = valueA;
            }

            return true;
        }
    }

    public class HediffCompProperties_AdaptiveExpressionAge : HediffCompProperties
    {
        public HediffCompProperties_AdaptiveExpressionAge()
        {
            compClass = typeof(HediffComp_AdaptiveExpressionAge);
        }
    }

    public class HediffComp_AdaptiveExpressionAge : HediffComp
    {
        private bool managedByPopulationPatch;
        private bool rimesisEnhanced;
        private float expressionCeiling = AdaptiveExpressionPopulation.MaxExpression;

        public void MarkManaged(float maximumExpression)
        {
            managedByPopulationPatch = true;
            expressionCeiling = AdaptiveExpressionPopulation.ClampExpressionCeiling(maximumExpression);
        }

        public void MarkRimesisEnhanced(float maximumExpression)
        {
            managedByPopulationPatch = true;
            rimesisEnhanced = true;
            expressionCeiling = AdaptiveExpressionPopulation.ClampExpressionCeiling(maximumExpression);
        }

        public override void CompExposeData()
        {
            base.CompExposeData();
            Scribe_Values.Look(ref managedByPopulationPatch, "managedByPopulationPatch", false);
            Scribe_Values.Look(ref rimesisEnhanced, "rimesisEnhanced", false);
            Scribe_Values.Look(ref expressionCeiling, "expressionCeiling", AdaptiveExpressionPopulation.MaxExpression);
        }

        public override void CompPostTick(ref float severityAdjustment)
        {
            if (!managedByPopulationPatch || parent == null)
            {
                return;
            }

            base.CompPostTick(ref severityAdjustment);
            Pawn pawn = parent.pawn;
            if (pawn == null || pawn.ageTracker == null || pawn.Dead || pawn.Destroyed
                || !pawn.IsHashIntervalTick(2500))
            {
                return;
            }

            expressionCeiling = Math.Max(
                AdaptiveExpressionPopulation.ClampExpressionCeiling(expressionCeiling),
                AdaptiveExpressionPopulation.ExpressionCeiling(pawn, null, rimesisEnhanced));
            float ageExpression = AdaptiveExpressionPopulation.ExpressionForAge(
                pawn.ageTracker.AgeBiologicalYearsFloat,
                expressionCeiling);

            ageExpression = Math.Min(
                ageExpression,
                Math.Min(expressionCeiling, AdaptiveExpressionPopulation.AllelePotential(pawn)));

            if (parent.Severity > expressionCeiling)
            {
                parent.Severity = expressionCeiling;
            }

            if (pawn.ageTracker.AgeBiologicalYearsFloat < AdaptiveExpressionPopulation.AdultAge)
            {
                parent.Severity = ageExpression;
            }
            else if (parent.Severity < ageExpression)
            {
                parent.Severity = ageExpression;
            }
        }
    }
}

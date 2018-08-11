﻿using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Radiology
{
    public class CompIrradiator : ThingComp
    {
        public new CompPropertiesIrradiator props => base.props as CompPropertiesIrradiator;

        public HediffRadiation GetHediffRadition(Chamber chamber, Pawn pawn)
        {
            BodyPartRecord part = pawn.health.hediffSet.GetNotMissingParts().RandomElementByWeight(x => props.PartsMap.TryGetValue(x.def, 0f));
            foreach (var v in pawn.health.hediffSet.GetHediffs<HediffRadiation>())
            {
                if (v.Part == part) return v;
            }
            HediffRadiation hediff = HediffMaker.MakeHediff(HediffDefOf.RadiologyRadiation, pawn, part) as HediffRadiation;
            if (hediff == null) return hediff;

            pawn.health.AddHediff(hediff, null, null, null);
            return hediff;
        }

        IEnumerable<ThingWithComps> GetFacilitiesBetweenThisAndChamber(Chamber chamber)
        {
            foreach (var v in parent.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading)
            {
                ThingWithComps thing = v as ThingWithComps;
                if (thing == null) continue;

                if (parent.Rotation.IsHorizontal && !MathHelper.IsBetween(thing.Position.x, parent.Position.x, chamber.Position.x)) continue;
                if (!parent.Rotation.IsHorizontal && !MathHelper.IsBetween(thing.Position.z, parent.Position.z, chamber.Position.z)) continue;

                yield return thing;
            }

            yield break;
        }

        IEnumerable<CompBlocker> GetBlockers(Chamber chamber)
        {
            foreach (ThingWithComps thing in GetFacilitiesBetweenThisAndChamber(chamber))
            {
                foreach (CompBlocker comp in thing.GetComps<CompBlocker>())
                {
                    yield return comp;
                }
            }

            yield break;
        }

        public void Irradiate(Chamber chamber, Pawn pawn, int ticks)
        {
            HediffRadiation radiation = GetHediffRadition(chamber, pawn);
            if (radiation == null) return;

            SoundDefOf.RadiologyIrradiateBasic.PlayOneShot(new TargetInfo(parent.Position, parent.Map, false));

            ticksCooldown = ticks;

            bool stop = false;
            motesReflectAt.Clear();
            foreach (CompBlocker blocker in GetBlockers(chamber))
            {
                motesReflectAt.Add((parent.Rotation.IsHorizontal ? blocker.parent.Position.x : blocker.parent.Position.z) + 0.5f);
                bool match = blocker.enabledParts.Contains(radiation.Part);
                if (match && Rand.Range(0.0f,1.0f)<blocker.props.blockChance)
                    stop=true;
            }

            if (stop) return;

            var radiationAmount = props.mutate.perSecond.RandomInRange;
            var radiationRareAmount = props.mutateRare.perSecond.RandomInRange;
            var burningAmount = props.burn.perSecond.RandomInRange;

            radiation.radiation += radiationAmount;
            radiation.radiationRare += radiationRareAmount;
            radiation.burning += burningAmount;

            chamber.radiationTracker.radiation += radiationAmount;
            chamber.radiationTracker.radiationRare += radiationRareAmount;
            chamber.radiationTracker.burn += burningAmount;

            float burnThreshold = chamber.def.burnThreshold.RandomInRange;
            float burnAmount = radiation.burning - burnThreshold;
            if (burnAmount > 0)
            {
                radiation.burning -= chamber.def.burnThreshold.min;

                DamageInfo dinfo = new DamageInfo(DamageDefOf.Burn, burnAmount * props.burn.multiplier, 999999f, -1f, chamber, radiation.Part, null, DamageInfo.SourceCategory.ThingOrUnknown, null);
                pawn.TakeDamage(dinfo);
            }

            float mutateThreshold = chamber.def.mutateThreshold.RandomInRange;
            float mutateAmount = radiation.radiation + radiation.radiationRare - mutateThreshold;
            if (mutateAmount > 0)
            {
                float ratio = radiation.radiationRare / (radiation.radiation + radiation.radiationRare);
                radiation.radiationRare -= chamber.def.mutateThreshold.min * ratio;
                radiation.radiation -= chamber.def.mutateThreshold.min * (1f - ratio);

                var mutatedParts = RadiationHelper.MutatePawn(pawn, radiation, mutateAmount * props.mutate.multiplier, ratio);
                if (mutatedParts != null)
                {
                    foreach (var anotherRadiation in pawn.health.hediffSet.GetHediffs<HediffRadiation>())
                    {
                        if (mutatedParts.Contains(anotherRadiation.Part) && radiation != anotherRadiation)
                        {
                            anotherRadiation.radiationRare -= chamber.def.mutateThreshold.min * ratio;
                            anotherRadiation.radiation -= chamber.def.mutateThreshold.min * (1f - ratio);
                        }
                    }
                }
            }
        }

        public static string causeNoPower = "IrradiatorNoPower";

        public string CanIrradiateNow(Pawn pawn)
        {
            if (powerComp == null || !powerComp.PowerOn)
                return causeNoPower;

            return null;
        }

        public bool IsHealthyEnoughForIrradiation(Pawn pawn)
        {
            var pawnParts = pawn.health.hediffSet.GetNotMissingParts();
            var parts = props.bodyParts.Join(pawnParts, left => left.part, right => right.def, (left, right) => right);

            foreach (var part in parts)
            {
                float health = PawnCapacityUtility.CalculatePartEfficiency(pawn.health.hediffSet, part, false, null);
                if (health < damageThreshold) return false;
            }

            return true;
        }

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref damageThreshold, "damageThreshold");
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            powerComp = parent.TryGetComp<CompPowerTrader>();
        }

        List<Thing> Chambers => parent.GetComp<CompFacility>().LinkedBuildings();

        public void SpawnRadiationMote()
        {
            if (props.motes == null || !props.motes.Any()) return;
            var def = props.motes.RandomElement();
            if (def.skip) return;

            MoteRadiation moteThrown = ThingMaker.MakeThing(def.mote, null) as MoteRadiation;
            if (moteThrown == null) return;

            List<Thing> chambers = Chambers;
            if (!chambers.Any()) return;
            Thing chamber = chambers.RandomElement();

            Vector2 rotationVector2 = parent.Rotation.AsVector2.RotatedBy(90);
            Vector3 rotationVector = new Vector3(rotationVector2.x, 0, rotationVector2.y);
            Vector3 origin = parent.ExactPosition() + Rand.Range(-def.initialSpread, def.initialSpread) * rotationVector;
            Vector3 destination = chamber.ExactPosition();
            Vector3 offset = destination - origin;
            Vector3 dir = offset.normalized;
            Vector3 dirOrtho = dir.RotatedBy(90);

            float position = def.offset.RandomInRange;
            float scale = def.scaleRange.RandomInRange;

            Vector3 startOffset = offset * position + dirOrtho * position * Rand.Range(-def.spread, def.spread);
            float angle = startOffset.AngleFlat();

            moteThrown.exactPosition = origin + startOffset;
            moteThrown.exactRotation = angle;
            moteThrown.reflectAt = motesReflectAt;
            moteThrown.reflectChance = def.reflectChance;

            if (parent.Rotation.IsHorizontal)
            {
                moteThrown.deathLocation = chamber.Position.x + 0.5f;
                moteThrown.isHorizontal = true;
                moteThrown.reflectIndex = parent.Rotation == Rot4.West ? 0 : motesReflectAt.Count() - 1;
                moteThrown.reflectIndexChange = parent.Rotation == Rot4.West ? 1 : -1;
            }
            else
            {
                moteThrown.deathLocation = chamber.Position.z + 0.5f;
                moteThrown.isHorizontal = false;
                moteThrown.reflectIndex = parent.Rotation == Rot4.North ? 0 : motesReflectAt.Count() - 1;
                moteThrown.reflectIndexChange = parent.Rotation == Rot4.North ? 1 : -1;
            }

 
            moteThrown.exactScale = new Vector3(scale, scale, scale);
            moteThrown.SetVelocity(angle, def.speed.RandomInRange);
            GenSpawn.Spawn(moteThrown, parent.Position, parent.Map, WipeMode.Vanish);
        }

        public override void CompTick()
        {
            if (powerComp == null) return;

            if (ticksCooldown <= 0)
            {
                powerComp.PowerOutput = -powerComp.Props.basePowerConsumption;
                return;
            }

            powerComp.PowerOutput = -props.powerConsumption;

            SpawnRadiationMote();

            ticksCooldown--;
        }

        public float damageThreshold = 0.5f;
        public int ticksCooldown=0;
        public List<float> motesReflectAt = new List<float>();

        private CompPowerTrader powerComp;
    }
}
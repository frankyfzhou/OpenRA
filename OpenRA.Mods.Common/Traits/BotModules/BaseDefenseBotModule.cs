#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Coordinates emergency base defense when under sustained attack.",
		"Tracks attack intensity and triggers rally, harvester retreat, and emergency defense construction.")]
	public class BaseDefenseBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Time window (in ticks) for counting recent attacks. Attacks older than this are pruned.")]
		public readonly int ThreatWindowTicks = 150;

		[Desc("Number of attacks within the threat window to trigger Alert (rally idle units).")]
		public readonly int AlertThreshold = 2;

		[Desc("Number of attacks within the threat window to trigger Emergency (defense build + harvester retreat).")]
		public readonly int EmergencyThreshold = 4;

		[Desc("Radius (in cells) around base center to scan for idle combat units to rally.")]
		public readonly int RallyRadius = 20;

		[Desc("Minimum ticks between rally waves.")]
		public readonly int RallyCooldown = 100;

		[ActorReference]
		[Desc("Defense structure types to emergency-build when in Emergency tier, in priority order.")]
		public readonly FrozenSet<string> EmergencyDefenseTypes = FrozenSet<string>.Empty;

		[Desc("Minimum ticks between emergency defense build orders.")]
		public readonly int EmergencyBuildCooldown = 250;

		[Desc("Minimum cash required to queue an emergency defense structure.")]
		public readonly int EmergencyBuildMinCash = 800;

		[ActorReference]
		[Desc("Harvester actor types to retreat when in Emergency tier.")]
		public readonly FrozenSet<string> HarvesterTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Refinery actor types for harvester dock retreat orders.")]
		public readonly FrozenSet<string> RefineryTypes = FrozenSet<string>.Empty;

		[Desc("Radius (in cells) around defense center within which harvesters are ordered to retreat.")]
		public readonly int HarvesterDangerRadius = 15;

		[Desc("Minimum ticks between harvester retreat orders.")]
		public readonly int HarvesterRetreatCooldown = 200;

		[ActorReference]
		[Desc("Building types that immediately escalate to Emergency when attacked (e.g. ConYard, Refinery).")]
		public readonly FrozenSet<string> CriticalBuildingTypes = FrozenSet<string>.Empty;

		public override object Create(ActorInitializer init) { return new BaseDefenseBotModule(init.Self, this); }
	}

	public class BaseDefenseBotModule : ConditionalTrait<BaseDefenseBotModuleInfo>,
		IBotTick, IBotRespondToAttack, IBotPositionsUpdated
	{
		readonly World world;
		readonly Player player;
		readonly List<(int Tick, CPos Location)> recentAttacks = [];

		CPos defenseCenter;
		CPos baseCenter;
		PlayerResources playerResources;
		int rallyCooldown;
		int emergencyBuildCooldown;
		int harvesterRetreatCooldown;
		bool criticalAttackThisTick;

		public BaseDefenseBotModule(Actor self, BaseDefenseBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
		}

		void IBotPositionsUpdated.UpdatedBaseCenter(CPos newLocation)
		{
			baseCenter = newLocation;
		}

		void IBotPositionsUpdated.UpdatedDefenseCenter(CPos newLocation)
		{
			defenseCenter = newLocation;
		}

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (e.Attacker == null || e.Attacker.Disposed)
				return;

			if (e.Attacker.Owner.RelationshipWith(player) != PlayerRelationship.Enemy)
				return;

			recentAttacks.Add((world.WorldTick, e.Attacker.Location));

			// Critical building attack → immediate escalation
			if (Info.CriticalBuildingTypes.Contains(self.Info.Name))
				criticalAttackThisTick = true;
		}

		void IBotTick.BotTick(IBot bot)
		{
			// Prune old attacks
			var cutoff = world.WorldTick - Info.ThreatWindowTicks;
			recentAttacks.RemoveAll(a => a.Tick < cutoff);

			// Decrement cooldowns
			if (rallyCooldown > 0) rallyCooldown--;
			if (emergencyBuildCooldown > 0) emergencyBuildCooldown--;
			if (harvesterRetreatCooldown > 0) harvesterRetreatCooldown--;

			var threatCount = recentAttacks.Count;
			var isEmergency = criticalAttackThisTick || threatCount >= Info.EmergencyThreshold;
			var isAlert = isEmergency || threatCount >= Info.AlertThreshold;

			criticalAttackThisTick = false;

			if (!isAlert)
				return;

			// R1: Rally idle combat units toward defense center
			if (rallyCooldown <= 0)
				RallyIdleUnits(bot);

			if (!isEmergency)
				return;

			// R2: Emergency defense construction
			if (emergencyBuildCooldown <= 0)
				QueueEmergencyDefense(bot);

			// R3: Retreat harvesters near danger zone
			if (harvesterRetreatCooldown <= 0)
				RetreatHarvesters(bot);
		}

		void RallyIdleUnits(IBot bot)
		{
			var target = defenseCenter != CPos.Zero ? defenseCenter : baseCenter;
			if (target == CPos.Zero)
				return;

			var rallyTarget = Target.FromCell(world, target);
			var rallyRadiusSq = Info.RallyRadius * Info.RallyRadius;
			var rallied = 0;

			foreach (var actor in world.ActorsHavingTrait<AttackBase>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				// Skip harvesters and aircraft
				if (Info.HarvesterTypes.Contains(actor.Info.Name))
					continue;

				if (actor.Info.HasTraitInfo<AircraftInfo>())
					continue;

				// Must be a mobile ground combat unit
				if (!actor.Info.HasTraitInfo<MobileInfo>())
					continue;

				// Only rally units near the base
				var distSq = (actor.Location - baseCenter).LengthSquared;
				if (distSq > rallyRadiusSq)
					continue;

				// Only rally units that are idle (no current activity)
				if (actor.CurrentActivity != null)
					continue;

				bot.QueueOrder(new Order("AttackMove", actor, rallyTarget, false));
				rallied++;
			}

			if (rallied > 0)
			{
				AIUtils.BotDebug("{0} BaseDefense: Rallied {1} units to {2} (threat={3})",
					player, rallied, target, recentAttacks.Count);
				rallyCooldown = Info.RallyCooldown;
			}
		}

		void QueueEmergencyDefense(IBot bot)
		{
			if (Info.EmergencyDefenseTypes.Count == 0)
				return;

			if (playerResources.GetCashAndResources() < Info.EmergencyBuildMinCash)
				return;

			// Find a defense queue with no current production
			var queues = AIUtils.FindQueuesByCategory(player);
			ProductionQueue availableQueue = null;
			foreach (var category in new[] { "Defense", "Building" })
			{
				if (!queues.Contains(category))
					continue;

				foreach (var q in queues[category])
				{
					if (!q.AllQueued().Any())
					{
						availableQueue = q;
						break;
					}
				}

				if (availableQueue != null)
					break;
			}

			if (availableQueue == null)
				return;

			// Pick first buildable defense type
			foreach (var defType in Info.EmergencyDefenseTypes)
			{
				var buildable = availableQueue.BuildableItems().FirstOrDefault(b => b.Name == defType);
				if (buildable != null)
				{
					bot.QueueOrder(Order.StartProduction(availableQueue.Actor, buildable.Name, 1));
					AIUtils.BotDebug("{0} BaseDefense: Emergency building {1}", player, defType);
					emergencyBuildCooldown = Info.EmergencyBuildCooldown;
					return;
				}
			}
		}

		void RetreatHarvesters(IBot bot)
		{
			if (Info.HarvesterTypes.Count == 0 || Info.RefineryTypes.Count == 0)
				return;

			var dangerCenter = defenseCenter != CPos.Zero ? defenseCenter : baseCenter;
			if (dangerCenter == CPos.Zero)
				return;

			var dangerRadiusSq = Info.HarvesterDangerRadius * Info.HarvesterDangerRadius;
			var retreated = 0;

			foreach (var actor in world.ActorsHavingTrait<Harvester>())
			{
				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld)
					continue;

				if (!Info.HarvesterTypes.Contains(actor.Info.Name))
					continue;

				// Only retreat harvesters near the danger zone
				var distSq = (actor.Location - dangerCenter).LengthSquared;
				if (distSq > dangerRadiusSq)
					continue;

				// Skip harvesters already docked/reserved
				var dockClient = actor.Trait<DockClientManager>();
				if (dockClient.ReservedHostActor != null)
					continue;

				// Find closest dock to retreat to
				var closestDock = dockClient.ClosestDock(null, forceEnter: true, ignoreOccupancy: true)?.Actor;
				if (closestDock != null)
				{
					bot.QueueOrder(new Order("Dock", actor, Target.FromActor(closestDock), false));
					retreated++;
				}
			}

			if (retreated > 0)
			{
				AIUtils.BotDebug("{0} BaseDefense: Retreated {1} harvesters", player, retreated);
				harvesterRetreatCooldown = Info.HarvesterRetreatCooldown;
			}
		}
	}
}

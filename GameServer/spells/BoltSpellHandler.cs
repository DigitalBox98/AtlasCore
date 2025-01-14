/*
 * DAWN OF LIGHT - The first free open source DAoC server emulator
 * 
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
 *
 */
using System;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.Effects;
using DOL.GS.Keeps;
using DOL.GS.RealmAbilities;
using DOL.GS.SkillHandler;

namespace DOL.GS.Spells
{
	/// <summary>
	/// Spell Handler for firing bolts
	/// </summary>
	[SpellHandlerAttribute("Bolt")]
	public class BoltSpellHandler : SpellHandler
	{
        // constructor
        public BoltSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }

        /// <summary>
        /// Fire bolt
        /// </summary>
        /// <param name="target"></param>
        public override void FinishSpellCast(GameLiving target)
		{
			m_caster.Mana -= PowerCost(target);
			if ((target is Keeps.GameKeepDoor || target is Keeps.GameKeepComponent) && Spell.SpellType != (byte)eSpellType.SiegeArrow && Spell.SpellType != (byte)eSpellType.SiegeDirectDamage)
			{
				MessageToCaster(String.Format("Your spell has no effect on the {0}!", target.Name), eChatType.CT_SpellResisted);
				return;
			}
			base.FinishSpellCast(target);
		}

		#region LOS Checks for Keeps
		/// <summary>
		/// called when spell effect has to be started and applied to targets
		/// </summary>
		public override bool StartSpell(GameLiving target)
		{
			foreach (GameLiving targ in SelectTargets(target))
			{
				if (targ is GamePlayer && Spell.Target.ToLower() == "cone")
				{
					GamePlayer player = targ as GamePlayer;
					player.Out.SendCheckLOS(Caster, player, new CheckLOSResponse(DealDamageCheckLOS));
				}
				else
				{
					DealDamage(targ);
				}
			}

			return true;
		}

		private void DealDamageCheckLOS(GamePlayer player, ushort response, ushort targetOID)
		{
			if ((response & 0x100) == 0x100)
			{
				GameLiving target = (GameLiving)(Caster.CurrentRegion.GetObject(targetOID));
				if (target != null)
					DealDamage(target);
			}
		}

		private void DealDamage(GameLiving target)
		{
            int ticksToTarget = m_caster.GetDistanceTo( target ) * 100 / 85; // 85 units per 1/10s
			int delay = 1 + ticksToTarget / 100;
			foreach (GamePlayer player in target.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
			{
				player.Out.SendSpellEffectAnimation(m_caster, target, m_spell.ClientEffect, (ushort)(delay), false, 1);
			}
			BoltOnTargetAction bolt = new BoltOnTargetAction(Caster, target, this);
			bolt.Start(1 + ticksToTarget);
		}
		#endregion

		/// <summary>
		/// Delayed action when bolt reach the target
		/// </summary>
		protected class BoltOnTargetAction : RegionECSAction
		{
			/// <summary>
			/// The bolt target
			/// </summary>
			protected readonly GameLiving m_boltTarget;

			/// <summary>
			/// The spell handler
			/// </summary>
			protected readonly BoltSpellHandler m_handler;

			/// <summary>
			/// Constructs a new BoltOnTargetAction
			/// </summary>
			/// <param name="actionSource">The action source</param>
			/// <param name="boltTarget">The bolt target</param>
			/// <param name="spellHandler"></param>
			public BoltOnTargetAction(GameLiving actionSource, GameLiving boltTarget, BoltSpellHandler spellHandler) : base(actionSource)
			{
				if (boltTarget == null)
					throw new ArgumentNullException("boltTarget");
				if (spellHandler == null)
					throw new ArgumentNullException("spellHandler");
				m_boltTarget = boltTarget;
				m_handler = spellHandler;
			}

			/// <summary>
			/// Called on every timer tick
			/// </summary>
			protected override int OnTick(ECSGameTimer timer)
			{
				GameLiving target = m_boltTarget;
				GameLiving caster = (GameLiving)m_actionSource;
				if (target == null) return 0;
				if (target.CurrentRegionID != caster.CurrentRegionID) return 0;
				if (target.ObjectState != GameObject.eObjectState.Active) return 0;
				if (!target.IsAlive) return 0;

				// Related to PvP hitchance
				// http://www.camelotherald.com/news/news_article.php?storyid=2444
				// No information on bolt hitchance against npc's
				// Bolts are treated as physical attacks for the purpose of ABS only
				// Based on this I am normalizing the miss rate for npc's to be that of a standard spell

				int missrate = m_handler.CalculateSpellResistChance(target);
				bool combatMiss = false;

				if (caster is GamePlayer && target is GamePlayer)
				{
					if (target.InCombat)
					{
						foreach (GameLiving attacker in target.attackComponent.Attackers)
						         //200 unit range restriction added in 1.84 - reverted for Atlas 1.65 target
						         //re-implementing the 200 unit restriction to make bolts a little friendlier 6/6/2022
						{
							if (attacker != caster && target.GetDistanceTo(attacker) <= 200)
							{
								// each attacker adds a 20% chance to miss
								
								missrate += 20;
								combatMiss = true;
							}
						}
					}
				}

				if (target is GameNPC || caster is GameNPC)
				{
					missrate += (int)(ServerProperties.Properties.PVE_SPELL_CONHITPERCENT * caster.GetConLevel(target));
				}

				// add defence bonus from last executed style if any
				AttackData targetAD = (AttackData)target.TempProperties.getProperty<object>(GameLiving.LAST_ATTACK_DATA, null);
				if (targetAD != null
					&& targetAD.AttackResult == eAttackResult.HitStyle
					&& targetAD.Style != null)
				{
					missrate += targetAD.Style.BonusToDefense;
					combatMiss = true;
				}

				AttackData ad = m_handler.CalculateDamageToTarget(target, 0.65);

				int rand = 0;
				if (caster is GamePlayer p)
					rand = p.RandomNumberDeck.GetInt();
				else
					rand = Util.Random(100);

				if (target is GameKeepDoor)
					missrate = 0;

				if (missrate > rand) 
				{
					ad.AttackResult = eAttackResult.Missed;
					if(combatMiss)
						m_handler.MessageToCaster($"{target.Name} is in combat and your bolt misses!", eChatType.CT_YouHit);
					else
						m_handler.MessageToCaster($"You miss!", eChatType.CT_YouHit);
					m_handler.MessageToLiving(target, caster.GetName(0, false) + " missed!", eChatType.CT_Missed);
					target.OnAttackedByEnemy(ad);
					target.StartInterruptTimer(target.SpellInterruptDuration, ad.AttackType, caster);
					if(target is GameNPC)
					{
						IOldAggressiveBrain aggroBrain = ((GameNPC)target).Brain as IOldAggressiveBrain;
						if (aggroBrain != null)
							aggroBrain.AddToAggroList(caster, 1);
					}
					return 0;
				}

				ad.Damage = (int)((double)(ad.Damage) * (1.0 + caster.GetModified(eProperty.SpellDamage) * 0.01));
				ad.Modifier = (int)(ad.Damage * (ad.Target.GetResist(ad.DamageType)) / -100.0);
				ad.Damage += ad.Modifier;


				// Block
				bool blocked = false;
				if (target is GamePlayer) 
				{ // mobs left out yet
					GamePlayer player = (GamePlayer)target;
					InventoryItem lefthand = player.Inventory.GetItem(eInventorySlot.LeftHandWeapon);
					if (lefthand!=null && (player.AttackWeapon==null || player.AttackWeapon.Item_Type==Slot.RIGHTHAND || player.AttackWeapon.Item_Type==Slot.LEFTHAND)) 
					{
						if (target.IsObjectInFront(caster, 180) && lefthand.Object_Type == (int)eObjectType.Shield) 
						{
							double shield = 0.5 * player.GetModifiedSpecLevel(Specs.Shields);
							double blockchance = ((player.Dexterity*2)-100)/40.0 + shield + 5;
							// Removed 30% increased chance to block, can find no clear evidence this is correct - tolakram
							blockchance -= target.GetConLevel(caster) * 5;
							if (blockchance >= 100) blockchance = 99;
							if (blockchance <= 0) blockchance = 1;

							if (target.IsEngaging)
							{
								EngageECSGameEffect engage = (EngageECSGameEffect)EffectListService.GetEffectOnTarget(target, eEffect.Engage);
								if (engage != null && target.attackComponent.AttackState && engage.EngageTarget == caster)
								{
									// Engage raised block change to 85% if attacker is engageTarget and player is in attackstate							
									// You cannot engage a mob that was attacked within the last X seconds...
									if (engage.EngageTarget.LastAttackedByEnemyTick > GameLoop.GameLoopTime - EngageAbilityHandler.ENGAGE_ATTACK_DELAY_TICK)
									{
										if (engage.Owner is GamePlayer)
											(engage.Owner as GamePlayer).Out.SendMessage(engage.EngageTarget.GetName(0, true) + " has been attacked recently and you are unable to engage.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
									}  // Check if player has enough endurance left to engage
									else if (engage.Owner.Endurance < EngageAbilityHandler.ENGAGE_DURATION_LOST)
									{
										engage.Cancel(false); // if player ran out of endurance cancel engage effect
									}
									else
									{
										engage.Owner.Endurance -= EngageAbilityHandler.ENGAGE_DURATION_LOST;
										if (engage.Owner is GamePlayer)
											(engage.Owner as GamePlayer).Out.SendMessage("You concentrate on blocking the blow!", eChatType.CT_Skill, eChatLoc.CL_SystemWindow);

										if (blockchance < 95)
											blockchance = 95;
									}
								}
							}

							if (blockchance >= Util.Random(1, 100)) 
							{
								m_handler.MessageToLiving(player, "You partially block " + caster.GetName(0, false) + "'s spell!", eChatType.CT_Missed);
								m_handler.MessageToCaster(player.GetName(0, true) + " blocks!", eChatType.CT_YouHit);
								blocked = true;
							}
						}
					}

					// Nature's shield, 100% block chance, 120�� frontal angle
					if (target.IsObjectInFront(caster, 120) && (target.styleComponent.NextCombatStyle?.ID == 394 || target.styleComponent.NextCombatBackupStyle?.ID == 394))
					{
						m_handler.MessageToLiving(player, "You block " + caster.GetName(0, false) + "'s spell!", eChatType.CT_Missed);
						m_handler.MessageToCaster(player.GetName(0, true) + " blocks!", eChatType.CT_YouHit);
						blocked = true;
						ad.Damage = 0;
					}
				}

				double effectiveness = 1.0 + (caster.GetModified(eProperty.SpellDamage) * 0.01);

				// simplified melee damage calculation
				if (blocked == false)
				{
					// TODO: armor resists to damage type

					double damage = m_handler.Spell.Damage / 2; // another half is physical damage
					if(target is GamePlayer)
						ad.ArmorHitLocation = ((GamePlayer)target).CalculateArmorHitLocation(ad);

					InventoryItem armor = null;
					if (target.Inventory != null)
						armor = target.Inventory.GetItem((eInventorySlot)ad.ArmorHitLocation);

					double ws = (caster.Level * 2.85 * (1.0 + (caster.GetModified(eProperty.Dexterity) - 50)/200.0));
					double playerBaseAF = ad.Target is GamePlayer ? ad.Target.Level * 45 / 50d : 45;

					double armorMod = (playerBaseAF + ad.Target.GetArmorAF(ad.ArmorHitLocation))/
					                  (1 - ad.Target.GetArmorAbsorb(ad.ArmorHitLocation));
					damage *= ws / armorMod;
					//damage *= ((ws + 90.68) / (target.GetArmorAF(ad.ArmorHitLocation) + 20*4.67));
					//damage *= 1.0 - Math.Min(0.85, ad.Target.GetArmorAbsorb(ad.ArmorHitLocation));
					int physMod = (int)(damage * (ad.Target.GetResist(ad.DamageType)) / -100.0);
					ad.Modifier += physMod;
					damage += physMod;

					damage = damage * effectiveness;
					damage *= (1.0 + RelicMgr.GetRelicBonusModifier(caster.Realm, eRelicType.Magic));

					if (damage < 0) damage = 0;
					ad.Damage += (int)damage;
				}

				if (m_handler is SiegeArrow == false)
				{
					ad.UncappedDamage = ad.Damage;
					ad.Damage = (int)Math.Min(ad.Damage, m_handler.DamageCap(effectiveness));
				}

				ad.Damage = (int)(ad.Damage * caster.Effectiveness);

				if (blocked == false && ad.CriticalDamage > 0)
				{
					int critMax = (target is GamePlayer) ? ad.Damage/2 : ad.Damage;
					ad.CriticalDamage = Util.Random(critMax / 10, critMax);
				}

				//target.damageComponent.DamageToDeal += ad.Damage;

				caster.OnAttackEnemy(ad);
				if (ad.Damage > 0)
				{
					// "A bolt of runic energy hits you!"
					m_handler.MessageToLiving(target, m_handler.Spell.Message1, eChatType.CT_Spell);
					// "{0} is hit by a bolt of runic energy!"
					m_handler.MessageToCaster(eChatType.CT_Spell, m_handler.Spell.Message2, target.GetName(0, true));
					// "{0} is hit by a bolt of runic energy!"
					Message.SystemToArea(target, Util.MakeSentence(m_handler.Spell.Message2, target.GetName(0, true)), eChatType.CT_System, target, caster);
					
					m_handler.SendDamageMessages(ad);
				}
				m_handler.DamageTarget(ad, false, (blocked ? 0x02 : 0x14));

                target.StartInterruptTimer(target.SpellInterruptDuration, ad.AttackType, caster);

                return 0;
			}
		}
    }
}

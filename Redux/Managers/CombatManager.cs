﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using Redux.Enum;
using Redux.Database;
using Redux.Structures;
using Redux.Game_Server;
using Redux.Packets.Game;
using Redux.Database.Domain;
using Redux.Space;
using System.Threading;

namespace Redux.Managers
{
    public class CombatManager
    {
        #region Variables
        private Entity owner;
        public ConcurrentDictionary<ushort, ConquerSkill> skills;
        public ConcurrentDictionary<ushort, ConquerProficiency> proficiencies;

        private bool canceled = false;

        //We need to track the state. 
        private SkillState state;
        //We need to track the current skill (for skill lockon and passing between methods)
        private DbMagicType skill;
        //We need to remember the target we are attacking
        private uint targetUID;
        public Entity target;
        //We need a skill target location which is read from the interaction packet. This is again to simply avoid passing the packet to 50 diff methods
        private Point location;
        //We need to know when the next combat event will occur (if 0 no events are required and combat is not active)
        public long nextTrigger;

        private SkillEffectPacket packet;
        #endregion

        #region Constructor
        public CombatManager(Entity _owner)
        {
            owner = _owner;
            skills = new ConcurrentDictionary<ushort, ConquerSkill>();
            proficiencies = new ConcurrentDictionary<ushort, ConquerProficiency>();
            state = SkillState.None;
            skill = null;
            nextTrigger = 0;
            targetUID = 0;
            packet = new SkillEffectPacket(owner.UID);
            foreach (var dbSkill in ServerDatabase.Context.Skills.GetUserSkills(owner.UID))
            {
                var coSkill = new ConquerSkill(dbSkill);
                if (skills.TryAdd(coSkill.ID, coSkill))
                    owner.Send(ConquerSkillPacket.Create(coSkill));
            }

            foreach (var dbProf in ServerDatabase.Context.Proficiencies.GetUserProficiency(owner.UID))
            {
                var coProf = new ConquerProficiency(dbProf);
                if (proficiencies.TryAdd(coProf.ID, coProf))
                    owner.Send(WeaponProficiencyPacket.Create((coProf)));
            }
        }
        #endregion

        #region OnTimer
        public void OnTimer()
        {
            switch (state)
            {
                case SkillState.Attack:
                    LaunchAttack();
                    break;
                case SkillState.Intone:
                    if (!skills.ContainsKey(skill.ID))
                        return;
                    if (!HasCosts(skills[skill.ID].Info))
                    { AbortAttack(); return; }
                    else
                        TakeCosts(skills[skill.ID].Info);
                    skill = skills[skill.ID].Info;
                    LaunchSkill();
                    break;
                case SkillState.Launch:
                    DealSkill();
                    break;
                default:
                    break;
            }
        }
        #endregion

        #region Combat Management

        #region Process Interaction Packet
        public void ProcessInteractionPacket(InteractPacket _packet)
        {
            canceled = true;
            if (state == SkillState.Intone)
                AbortAttack(true);
            else if (owner is Player && state == SkillState.Launch)
                return;

            //Must be added in before packet is sent
            if (owner.HasEffect(ClientEffect.LuckDiffuse) || owner.HasEffect(ClientEffect.LuckAbsorb))
            {
                (owner as Player).LuckyTimeCheck();
                if ((owner as Player).LuckyAbsorbTimer != DateTime.MinValue)
                    (owner as Player).LuckyAbsorbTimer = DateTime.MinValue;
            }

            switch (_packet.Action)
            {
                case InteractAction.MagicAttack:
                    ProcessSkill(_packet);
                    break;
                case InteractAction.Shoot:
                case InteractAction.Attack:
                    ProcessAttack(_packet);
                    break;
                
                default:
                    Console.WriteLine("Unhandled interaction type {0} from {1}", owner.Name, _packet.Action);
                    break;
            }


            #region SuperGemEffects
            ///<summary>
            ///Handler for super gem effects. Shows effect to every player within a 30 radius
            ///Written by Aceking 9-24-13
            ///</summary>

            if (owner is Player)
            {   //If chance of success is true, check equipment for super gems and add effect. No point checking if not success.
                if (Common.PercentSuccess(10))
                {
                    List<String> gemlist = new List<String>();
                    for (var i = ItemLocation.Helmet; i <= ItemLocation.Boots; i++)
                    {
                        Structures.ConquerItem equip;
                        if (owner.Equipment.TryGetItemBySlot((byte)i, out equip))
                        {
                            if (equip.Gem1 == 3 || equip.Gem2 == 3)
                                gemlist.Add("phoenix");
                            else if (equip.Gem1 == 13 || equip.Gem2 == 13)
                                gemlist.Add("goldendragon");
                            else if (equip.Gem1 == 23 || equip.Gem2 == 23)
                                gemlist.Add("lounder1");
                            else if (equip.Gem1 == 33 || equip.Gem2 == 33)
                                gemlist.Add("rainbow");
                            else if (equip.Gem1 == 43 || equip.Gem2 == 43)
                                gemlist.Add("goldenkylin");
                            else if (equip.Gem1 == 53 || equip.Gem2 == 53)
                                gemlist.Add("purpleray");
                            else if (equip.Gem1 == 63 || equip.Gem2 == 63)
                                gemlist.Add("moon");
                            else if (equip.Gem1 == 73 || equip.Gem2 == 73)
                                gemlist.Add("recovery");
                        }
                    }
                    if (gemlist.Count > 0)
                    {

                        int X = Common.Random.Next(0, gemlist.Count - 1);
                        owner.Send(StringsPacket.Create(owner.UID, StringAction.RoleEffect, gemlist[X]));

                        IEnumerable<ILocatableObject> posTargets = owner.Map.QueryScreen(owner);
                        var possibleTargets =
                                 from I in posTargets
                                 where (I is Entity && ((Entity)I).Alive && Calculations.GetDistance(owner.X, owner.Y, (ushort)I.Location.X, (ushort)I.Location.Y) < 30)
                                 select I;
                        if (possibleTargets.Count() < 1)
                            return;
                        foreach (Entity role in possibleTargets)
                        {
                            role.Send(StringsPacket.Create(owner.UID, StringAction.RoleEffect, gemlist[X]));
                        }


                    }
                }
            }

            #endregion

            if (owner is Player)
                if ((owner as Player).Mining)
                    (owner as Player).StopMining();
        }
        #endregion

        #region Process Skill
        private void ProcessSkill(InteractPacket _packet)
        {
            if (!owner.Alive)
            { AbortAttack(); return; }
            if (!skills.ContainsKey(_packet.MagicType))
                return;
            if (!HasCosts(skills[_packet.MagicType].Info))
            { AbortAttack(); return; }
            else
                TakeCosts(skills[_packet.MagicType].Info);
            skill = skills[_packet.MagicType].Info;


            targetUID = _packet.Target;
            location = new Point(_packet.X, _packet.Y);

            //TG Check
            if (owner.MapID == 1039)
            {
                if (_packet.Target != owner.UID)
                {
                    var target = owner.Map.Search<SOB>(_packet.Target);
                    if (target == null)
                    { AbortAttack(); return; }
                    if (!Common.TGScarecrows.ContainsKey((ushort)target.Mesh))
                    { AbortAttack(); return; }
                    else
                    {
                        if (owner.Level < Common.TGScarecrows[(ushort)target.Mesh])
                        { AbortAttack(); return; }
                    }
                }

            }

            canceled = false;
            if (Common.Clock < nextTrigger)
            { state = SkillState.Intone; return; }
            if (skill.IntoneSpeed > 0)
            {
                state = SkillState.Intone;
                nextTrigger = Common.Clock + skill.IntoneSpeed;
            }
            else
                LaunchSkill();
        }
        #endregion

        #region Launch Skill
        private void LaunchSkill()
        {
            if (!owner.Alive || skill == null)
            { AbortAttack(); return; }

            packet = SkillEffectPacket.Create(owner.UID, 0, skill.ID, skill.Level);
            switch (skill.Type)
            {
                case SkillSort.Area:
                case SkillSort.Square:
                    LaunchAreaSkill();
                    break;
                case SkillSort.HealTarget:
                    LaunchHealTargetSkill();
                    break;
                case SkillSort.AddMana:
                    LaunchAddManaSkill();
                    break;
                case SkillSort.StatusAttack:
                    LaunchStatusAttackSkill();
                    break;
                case SkillSort.DetachStatus:
                    LaunchDetachStatusSkill();
                    break;
                case SkillSort.AttachStatus:
                    LaunchAttachStatusSkill();
                    break;
                case SkillSort.Attack:
                    LaunchAttackSkill();
                    break;
                case SkillSort.Sector:
                    LaunchSectorSkill();
                    break;
                case SkillSort.Line:
                    LaunchLineSkill();
                    break;
                case SkillSort.Transform:
                    LaunchTransformSkill();
                    break;
                case SkillSort.CallPet:
                    LaunchSummonSkill();
                    break;
                default:
                    Console.WriteLine("Cannot launch unhandled skill type {0} from {1}", skill.Type, owner.Name);
                    break;
            }
            if (skill == null)
                return;
            if (packet.Data > 0)
                owner.SendToScreen(packet, true);
            if (skill != null && skill.DelayMS > 0)
            {
                state = SkillState.Launch;
                nextTrigger = Common.Clock + skill.DelayMS;
            }
            else
                DealSkill();
        }

        #region Launch Skill By Type

        #region Launch Attack Skill
        private void LaunchAttackSkill()
        {
            var target = owner.Map.Search<Entity>(targetUID);
            this.target = target;
            if (!IsInSkillRange(target, skill) || !IsValidTarget(target))
                AbortAttack();
            else
            {
                packet.Data = targetUID;
                uint dmg;
                if (skill.WeaponSubtype == 500)
                    dmg = owner.CalculateBowDamage(target, skill);
                else
                    dmg = owner.CalculateSkillDamage(target, skill, skill.Percent < 100);
                packet.AddTarget(target.UID, dmg);
                if (owner is Player)
                {
                    var expGain = owner.CalculateExperienceGain(target, dmg);
                    ((Player)owner).GainExperience(expGain);
                    AddSkillExperience(skill.ID, expGain);
                }
                else if (owner is Pet)
                {
                    if (((Pet)owner).PetOwner != null)
                    {
                        var expGain = owner.CalculateExperienceGain(target, dmg);
                        ((Pet)owner).PetOwner.GainExperience(expGain);
                    }
                }
                target.ReceiveDamage(dmg, owner.UID);
            }
        }
        #endregion

        #region Launch Heal Target Skill
        private void LaunchHealTargetSkill()
        {

            Entity target = owner;
            if (skill.Target == TargetType.BuffPlayer)
                target = owner.Map.Search<Entity>(targetUID);
            if (!IsInSkillRange(target, skill))
                AbortAttack();
            else
            {
                uint skillexp = 0;
                packet.Data = target.UID;
                packet.AddTarget(target.UID, (uint)skill.Power);

                uint diff = target.MaximumLife - target.Life;
                skillexp += (uint)((Math.Min((double)diff / skill.Power, 1)) * (double)skill.Power);
                target.Life += (ushort)skill.Power;
                if (skill.Multi && owner is Player && (owner as Player).Team != null)
                {
                    foreach (var member in (owner as Player).Team.Members)
                    {
                        if ((Calculations.InScreen(owner.X, owner.Y, member.X, member.Y)) && member.UID != target.UID)
                        {
                            packet.AddTarget(member.UID, (uint)skill.Power);
                            diff = member.MaximumLife - member.Life;
                            skillexp += (uint)(Math.Min((double)diff / skill.Power, 1) * (double)skill.Power);

                            member.Life += (ushort)skill.Power;
                        }

                    }
                    if ((owner as Player).Team.Leader.UID != target.UID)
                        if ((owner as Player).Team.Leader.UID == owner.UID || (Calculations.InScreen(owner.X, owner.Y, (owner as Player).Team.Leader.X, (owner as Player).Team.Leader.Y)))
                        {
                            packet.AddTarget((owner as Player).Team.Leader.UID, (uint)skill.Power);
                            diff = (owner as Player).Team.Leader.MaximumLife - (owner as Player).Team.Leader.Life;
                            skillexp += (uint)(Math.Min((double)diff / skill.Power, 1) * (double)skill.Power);

                            (owner as Player).Team.Leader.Life += (ushort)skill.Power;
                        }
                }
                if (owner is Player)
                    AddSkillExperience(skill.ID, (ulong)skillexp);
            }
        }
        #endregion

        #region Launch AddMana Skill
        private void LaunchAddManaSkill()
        {
            packet.Data = owner.UID;
            packet.AddTarget(owner.UID, (uint)skill.Power);
            owner.Mana += (ushort)skill.Power;
            if (owner is Player)
                AddSkillExperience(skill.ID, (ulong)skill.Power);
        }
        #endregion

        #region Launch Detach Status Skill
        private void LaunchDetachStatusSkill()
        {
            var target = owner.Map.Search<Player>(targetUID);
            if (target == null || !IsInSkillRange(target, skill))
                AbortAttack();
            else
            {
                packet.Data = target.UID;
                packet.AddTarget(target.UID, 0);
                if (!target.Alive)
                    target.Revive(false);
            }
        }
        #endregion

        #region Launch Attach Status Skill
        private void LaunchAttachStatusSkill()
        {
            Entity target = owner;
            if ((skill.Status == 23 || skill.Status == 18) && !owner.HasEffect(ClientEffect.Superman) && !owner.HasEffect(ClientEffect.Cyclone))
            {
                (owner as Player).KOCount = 0;
            }
            if (skill.Target == TargetType.BuffPlayer)
                target = owner.Map.Search<Entity>(targetUID);
            if (!IsInSkillRange(target, skill))
                AbortAttack();
            else if (target.AddEffect((ClientEffect)(1U << skill.Status), skill.StepSecs * Common.MS_PER_SECOND))
            {
                packet.Data = target.UID;
                packet.AddTarget(target.UID, 0);
                target.AddStatus((Enum.ClientStatus)skill.Status, skill.Power % 10000, skill.StepSecs * Common.MS_PER_SECOND);
                AddSkillExperience(skill.ID, 1);

                if (skill.ID == 9876)
                {
                    (owner as Player).LuckyTimeStarted = DateTime.Now;

                    //So the update procedure doesnt trigger
                    (owner as Player).LuckyCheck = Common.Clock + (Common.MS_PER_HOUR * 24);

                    var screen = (owner as Player).Map.QueryScreen<Player>(owner);
                    //var screen = Map.QueryBox(X, Y, 7, 7);
                    foreach (var obj in screen)
                    {
                        if (obj.UID == owner.UID)
                            continue;
                        if (obj.RebornCount != 2 && Space.Calculations.GetDistance(owner.X, owner.Y, obj.X, obj.Y) <= 3 && !obj.CombatManager.IsActive)
                        {

                            obj.AddEffect(ClientEffect.LuckAbsorb, 999999, true);
                            obj.SendToScreen(GeneralActionPacket.Create(obj.UID, DataAction.ChangeAction, (uint)ActionType.Sit), true);
                            obj.SpawnPacket.Action = ActionType.Sit;
                            obj.LuckyTimeStarted = DateTime.Now;
                            obj.LuckyAbsorbTimer = DateTime.MinValue;
                        }
                    }
                }

            }
        }
        #endregion

        #region Launch Status Attack Skill
        private void LaunchStatusAttackSkill()
        {
            var target = owner.Map.Search<Entity>(targetUID);
            this.target = target;
            if (!IsInSkillRange(target, skill) || !IsValidTarget(target))
                AbortAttack();
            else
            {
                packet.Data = targetUID;
                uint dmg = owner.CalculatePhysicalDamage(target, skill);
                packet.AddTarget(target.UID, dmg);
                if (owner is Player)
                {
                    var expGain = owner.CalculateExperienceGain(target, dmg);
                    ((Player)owner).GainExperience(expGain);
                    AddSkillExperience(skill.ID, 1);
                }
                target.ReceiveDamage(dmg, owner.UID);
            }
        }
        #endregion

        #region Launch Line Skill
        private void LaunchLineSkill()
        {
            packet.DataLow = (ushort)location.X;
            packet.DataHigh = (ushort)location.Y;
            ulong expGain = 0;
            var points = GetLinePoints(owner.Location, location, skill.Range);
            foreach (var t in owner.Map.QueryScreen<Entity>(owner))
                if (points.Contains(t.Location))
                    if (IsValidTarget(t))
                    {
                        this.target = t;
                        uint dmg = owner.CalculatePhysicalDamage(t, skill);
                        packet.AddTarget(t.UID, dmg);
                        if (owner is Player)
                            expGain += owner.CalculateExperienceGain(t, dmg);
                        t.ReceiveDamage(dmg, owner.UID);
                    }
            if (owner is Player && expGain > 0)
            {
                ((Player)owner).GainExperience(expGain);
                AddSkillExperience(skill.ID, expGain);
            }
        }
        #endregion

        #region Launch Sector Skill
        private void LaunchSectorSkill()
        {
            packet.DataLow = (ushort)location.X;
            packet.DataHigh = (ushort)location.Y;
            ulong expGain = 0;
            foreach (var t in owner.Map.QueryScreen<Entity>(owner))
                if (IsInArc(owner.Location, location, t.Location, skill.Range))
                    if (IsValidTarget(t))
                    {
                        uint dmg;
                        if (skill.WeaponSubtype == 500)
                            dmg = owner.CalculateBowDamage(t, skill);
                        else
                            dmg = owner.CalculatePhysicalDamage(t, skill);
                        packet.AddTarget(t.UID, dmg);
                        if (owner is Player)
                            expGain += owner.CalculateExperienceGain(t, dmg);
                        t.ReceiveDamage(dmg, owner.UID);
                    }
            if (owner is Player && skill != null && expGain > 0)
            {
                ((Player)owner).GainExperience(expGain);
                AddSkillExperience(skill.ID, expGain);
            }
        }
        #endregion

        #region Launch Area Skill
        private void LaunchAreaSkill()
        {
            if (skill.Target == TargetType.TargetPlayer)
            {
                var target = owner.Map.Search<Entity>(targetUID);
                this.target = target;
                if (!IsInSkillRange(target, skill))
                { AbortAttack(); return; }
                else
                    location = target.Location;
            }
            else
                location = owner.Location;
            packet.DataLow = (ushort)location.X;
            packet.DataHigh = (ushort)location.Y;
            ulong expGain = 0;
            foreach (var t in owner.Map.QueryScreen<Entity>(owner))
                if (Calculations.GetDistance(location, t.Location) <= skill.Range)
                    if (IsValidTarget(t))
                    {
                        uint dmg;
                        if (skill.Target == TargetType.WeaponPassive)
                            dmg = owner.CalculatePhysicalDamage(t, skill);
                        else if (skill.WeaponSubtype == 500)
                            dmg = owner.CalculateBowDamage(t, skill);
                        else
                            dmg = owner.CalculateSkillDamage(t, skill);
                        packet.AddTarget(t.UID, dmg);
                        if (owner is Player)
                            expGain += owner.CalculateExperienceGain(t, dmg);
                        t.ReceiveDamage(dmg, owner.UID);
                    }
            if (owner is Player && skill != null && expGain > 0)
            {
                ((Player)owner).GainExperience(expGain);
                AddSkillExperience(skill.ID, expGain);
            }


        }
        #endregion

        #region Launch Transform Skill
        private void LaunchTransformSkill()
        {
            var dbMob = ServerDatabase.Context.Monstertype.GetById((uint)skill.Power);
            if (dbMob == null)
                AbortAttack();
            else
            {
                owner.SetDisguise(dbMob, skill.StepSecs * Common.MS_PER_SECOND);
                packet.Data = owner.UID;
                packet.AddTarget(owner.UID, 0);
                AddSkillExperience(skill.ID, 1);
            }
        }
        #endregion

        #region Launch Summon Skill
        private void LaunchSummonSkill()
        {
            if ((owner as Player).Pet != null)
            {
                (owner as Player).Pet.RemovePet();
            }
            Pet pet = new Pet((owner as Player), ServerDatabase.Context.Monstertype.GetById((uint)(skill.Power)));

            pet.X = owner.X;
            pet.Y = owner.Y;
            pet.Map = owner.Map;
            pet.LastMove = Common.Clock;
            //pet.SpawnPacket.Lookface = 379;//179 or 279
            owner.Map.Insert(pet);
            owner.UpdateSurroundings();
            owner.SendToScreen(pet.SpawnPacket);
            pet.SendToScreen(GeneralActionPacket.Create(pet.UID, DataAction.SpawnEffect, 0));
            (owner as Player).Pet = pet;

            packet.Data = owner.UID;

            AddSkillExperience(skill.ID, 1);

        }
        #endregion

        #endregion
        #endregion

        #region Deal Skill
        private void DealSkill()
        {
            if (!owner.Alive || skill == null)
            { AbortAttack(); return; }
            //Deal damage based on type
            switch (skill.Type)
            {
                case SkillSort.Attack:
                case SkillSort.Area:
                case SkillSort.Line:
                case SkillSort.Sector:
                case SkillSort.Square:
                case SkillSort.StatusAttack:
                    DealSkillDamage();
                    break;
                // somehow it is being called twice therefore using a break
                case SkillSort.HealTarget:
                case SkillSort.CallPet:
                    break;
                default:
                    Console.WriteLine("Cannot deal unhandled skill type {0} from {1}", skill.Type, owner.Name);
                    break;
            }
            if (skill == null)
                return;
            if (canceled)
            {
                nextTrigger = Common.Clock + Math.Max(500, skill.DelayMS + skill.IntoneSpeed);
                state = SkillState.None;
                skill = null;
                return;
            }
            if (owner.Map.IsTGMap)
            {
                state = SkillState.Intone;
                nextTrigger = Common.Clock + Math.Max(500, skill.DelayMS + skill.IntoneSpeed);
            }
            else if (skill.NextMagic != 0)
            {
                skill = ServerDatabase.Context.MagicType.GetById(skill.NextMagic);
                state = SkillState.Intone;
                nextTrigger = Common.Clock + Math.Max(800, skill.DelayMS + skill.IntoneSpeed);
            }
            else if (Common.WeaponSkills.ContainsValue(skill.ID))
            {
                skill = null;
                state = SkillState.Attack;
                nextTrigger = Common.Clock + owner.AttackSpeed / (owner.HasEffect(ClientEffect.Cyclone) ? 3 : 1);
            }
            else
            {
                nextTrigger = Common.Clock + Math.Max(500, skill.DelayMS + skill.IntoneSpeed);
                skill = null;
                state = SkillState.None;
            }
        }

        #region Deal Skill By Type
        private void DealSkillDamage()
        {
            if (packet.TargetCount > 0)
                foreach (var t in packet.GetTargets())
                {
                    var target = owner.Map.Search<Entity>(t.Key);
                    if (target == null)
                        continue;
                    if (!target.Alive)
                    {
                        if (owner.IsInXP)
                        {
                            if (owner.HasEffect(ClientEffect.Cyclone))
                            {
                                owner.ClientEffects[ClientEffect.Cyclone] += 700;
                                target.Kill(65541 * ((owner as Player).KOCount + 1), owner.UID);
                            }
                            else if (owner.HasEffect(ClientEffect.Superman))
                            {
                                owner.ClientEffects[ClientEffect.Superman] += 700;
                                target.Kill(65541 * ((owner as Player).KOCount + 1), owner.UID);
                            }
                            else
                                target.Kill(3, owner.UID);
                        }
                        else
                            target.Kill(3, owner.UID);
                    }
                }
            if (owner.HasStatus(Enum.ClientStatus.Intensify))
            {
                owner.RemoveStatus(Enum.ClientStatus.Intensify);
                owner.RemoveEffect(ClientEffect.Intensify);
            }

            if (packet.TargetCount > 0)
                foreach (var target in packet.GetTargets())
                {

                    var targ = owner.Map.Search<Entity>(target.Key);
                    if (targ == null)
                        continue;
                    if (skill != null && targ != null)
                        if (owner is Player)
                            if ((owner as Player).Pet != null && IsValidTarget(targ))
                            {
                                (owner as Player).Pet.Target = targ;
                                break;
                            }
                }
        }
        #endregion
        #endregion

        #region Process Attack
        private void ProcessAttack(InteractPacket _packet)
        {
            if (!owner.Alive)
            { AbortAttack(); return; }
            var target = owner.Map.Search<Entity>(_packet.Target);
            if (target == null)
            { AbortAttack(); return; }
            var dist = Calculations.GetDistance(owner, target);
            if (owner is Player && dist > owner.AttackRange &&
                Common.Clock > nextTrigger &&
                Calculations.GetDistance(owner.X, owner.Y, _packet.X, _packet.Y) < 6)
            {
                Console.WriteLine("{0} attacking {1} with incorrect location. Resetting to {2},{3}", owner.Name, target.UID, _packet.X, _packet.Y);
                nextTrigger = Common.Clock + owner.AttackSpeed;
                owner.X = _packet.X;
                owner.Y = _packet.Y;
                targetUID = target.UID;
                state = SkillState.Attack;
                return;
            }
            if (target == null || !IsValidTarget(target) || !IsInAttackRange(target))
            { AbortAttack(); return; }
            targetUID = target.UID;
            if (Common.Clock < nextTrigger)
                return;
            canceled = false;

            //TG Check
            if (owner.MapID == 1039)
            {
                if (_packet.Target != owner.UID)
                {
                    var targ = owner.Map.Search<SOB>(_packet.Target);
                    if (targ == null)
                    { AbortAttack(); return; }
                    if (!Common.TGStakes.ContainsKey((ushort)targ.Mesh))
                    { AbortAttack(); return; }
                    else
                    {
                        if (owner.Level < Common.TGStakes[(ushort)targ.Mesh])
                        { AbortAttack(); return; }
                    }
                }

            }

            if (owner is Player)
                if ((owner as Player).Pet != null)
                    (owner as Player).Pet.Target = target;
            LaunchAttack();
        }

        #region Launch Attack
        private void LaunchAttack()
        {
            var target = owner.Map.Search<Entity>(targetUID);
            this.target = target;
            if (!owner.Alive || target == null || !IsValidTarget(target) || !IsInAttackRange(target))
            { AbortAttack(); return; }

            //Do not check equipment if we have none
            if (owner.Equipment != null)
            {
                skill = GetWeaponSkill();
                if (skill != null)
                { location = target.Location; LaunchSkill(); return; }
            }

            //Deal damage to target
            state = SkillState.Attack;
            nextTrigger = Common.Clock + owner.AttackSpeed / (owner.HasEffect(ClientEffect.Cyclone) ? 3 : 1);
            uint dmg;
            if (owner.WeaponType == 500)
            {
                ConquerItem item;
                owner.Equipment.TryGetItemBySlot(5, out item);
                if (item == null)
                {
                    AbortAttack(); return;
                }
                if (item.Durability >= 1)
                {
                    item.Durability -= 1;
                    owner.Send(ItemInformationPacket.Create(item, ItemInfoAction.Update));
                    if (item.Durability == 0)
                    {
                        owner.Equipment.UnequipItem(5);
                        (owner as Player).DeleteItem(item);
                    }
                }
                else
                {
                    AbortAttack(); return;
                }
                dmg = owner.CalculateBowDamage(target, null, true);

            }
            else
                dmg = owner.CalculatePhysicalDamage(target, null, true);
            //owner.SendToScreen(InteractPacket.Create(owner.UID, target.UID, (ushort)target.Location.X, (ushort)target.Location.Y, (owner.WeaponType == 500 ? InteractAction.Shoot : InteractAction.Attack), dmg), true);

            if (owner is Player)
            {
                var expGain = owner.CalculateExperienceGain(target, dmg);
                ((Player)owner).GainExperience(expGain);
                AddProficiencyExperience(owner.WeaponType, expGain);
            }
            else if (owner is Pet)
            {
                if (((Pet)owner).PetOwner != null)
                {
                    var expGain = owner.CalculateExperienceGain(target, dmg);
                    ((Pet)owner).PetOwner.GainExperience(expGain);
                }

            }
            target.ReceiveDamage(dmg, owner.UID);

            if (!target.Alive)
            {
                if (target is SOB)
                {
                    target.Kill(1, owner.UID);
                    owner.SendToScreen(InteractPacket.Create(owner.UID, target.UID, (ushort)target.Location.X, (ushort)target.Location.Y, (owner.WeaponType == 500 ? InteractAction.Shoot : InteractAction.Attack), dmg), true);
                }
                else
                {
                    owner.SendToScreen(InteractPacket.Create(owner.UID, target.UID, (ushort)target.Location.X, (ushort)target.Location.Y, (owner.WeaponType == 500 ? InteractAction.Shoot : InteractAction.Attack), dmg), true);
                    if (owner.HasEffect(ClientEffect.Cyclone))
                    {
                        owner.ClientEffects[ClientEffect.Cyclone] += 700;
                        target.Kill(65541 * ((owner as Player).KOCount + 1), owner.UID);
                    }
                    else if (owner.HasEffect(ClientEffect.Superman))
                    {
                        owner.ClientEffects[ClientEffect.Superman] += 700;
                        target.Kill(65541 * ((owner as Player).KOCount + 1), owner.UID);
                    }
                    else
                        target.Kill(1, owner.UID);
                }
            }
            else
                owner.SendToScreen(InteractPacket.Create(owner.UID, target.UID, (ushort)target.Location.X, (ushort)target.Location.Y, (owner.WeaponType == 500 ? InteractAction.Shoot : InteractAction.Attack), dmg), true);



        }
        #endregion

        #region GetWeaponSkill
        public DbMagicType GetWeaponSkill()
        {
            DbMagicType skill = null;
            try
            {
                var eq = owner.Equipment.GetItemBySlot(ItemLocation.WeaponR);
                if (eq != null && Common.WeaponSkills.ContainsKey(eq.EquipmentType) && skills.ContainsKey(Common.WeaponSkills[eq.EquipmentType]) && Common.PercentSuccess(skills[Common.WeaponSkills[eq.EquipmentType]].Info.Percent))
                    skill = skills[Common.WeaponSkills[eq.EquipmentType]].Info;
                else
                {
                    eq = owner.Equipment.GetItemBySlot(ItemLocation.WeaponL);
                    if (eq != null && Common.WeaponSkills.ContainsKey(eq.EquipmentType) && skills.ContainsKey(Common.WeaponSkills[eq.EquipmentType]) && Common.PercentSuccess(skills[Common.WeaponSkills[eq.EquipmentType]].Info.Percent))
                        skill = skills[Common.WeaponSkills[eq.EquipmentType]].Info;
                }
            }
            catch (Exception p) { Console.WriteLine(p); }

            return skill;
        }
        #endregion

        #endregion

        #region Abort Attack
        public void AbortAttack(bool _sync = false)
        {
            if (state != SkillState.Launch)
            {
                packet = new SkillEffectPacket(owner.UID);
                state = SkillState.None;
                nextTrigger = 0;
                skill = null;
                targetUID = 0;
                target = null;
            }
            canceled = true;
            if (_sync)
                owner.Send(GeneralActionPacket.Create(owner.UID, DataAction.AbortMagic, 0, 0));
        }
        #endregion

        #endregion

        #region Targeting

        #region Arc
        private bool IsInArc(Point _from, Point _end, Point _target, int _range)
        {
            const int DEFAULT_MAGIC_ARC = 90;
            const double RADIAN_DELTA = Math.PI * DEFAULT_MAGIC_ARC / 180;

            var centerLine = Common.GetRadian(_from.X, _from.Y, _end.X, _end.Y);
            var targetLine = Common.GetRadian(_from.X, _from.Y, _target.X, _target.Y);
            var delta = Math.Abs(centerLine - targetLine);

            return (delta <= RADIAN_DELTA || delta >= 2 * Math.PI - RADIAN_DELTA) && Calculations.GetLineLength(_from, _target) < _range;
        }
        #endregion

        #region Line
        private List<Point> GetLinePoints(Point _start, Point _end, int _distance)
        {
            var points = new List<Point>();
            if (_start.X == _end.X && _start.Y == _end.Y)
                return points;

            int dx = _end.X - _start.X, dy = _end.Y - _start.Y, steps, k;
            float xincrement, yincrement, x = _start.X, y = _start.Y;

            if (Math.Abs(dx) > Math.Abs(dy)) steps = Math.Abs(dx);
            else steps = Math.Abs(dy);

            xincrement = dx / (float)steps;
            yincrement = dy / (float)steps;
            points.Add(new Point((int)Math.Round(x), (int)Math.Round(y)));

            for (k = 0; k < _distance; k++)
            {
                x += xincrement;
                y += yincrement;
                points.Add(new Point((int)Math.Round(x), (int)Math.Round(y)));
            }
            return points;
        }
        #endregion

        #endregion

        #region Sanity Checks

        private bool HasCosts(DbMagicType _magicType)
        {
            if (_magicType == null)
                return false;
            if (owner.Map.IsTGMap || owner is Pet)
                return true;
            if (_magicType.WeaponSubtype == 500)
            {
                if (_magicType.UseItem == 50 && _magicType.UseItemNum > 0)
                {
                    ConquerItem item;
                    owner.Equipment.TryGetItemBySlot(5, out item);
                    if (item == null)
                        return false;
                    if (item.Durability - _magicType.UseItemNum < 0)
                        return false;
                }
            }
            return owner.Stamina >= _magicType.UseStamina
                && owner.Mana >= _magicType.UseMP && (owner.HasEffect(ClientEffect.XpStart) || _magicType.UseXP != 1);
        }

        private bool TakeCosts(DbMagicType _magicType)
        {
            if (owner.Map.IsTGMap || owner is Pet)
                return true;
            if (!HasCosts(_magicType))
                return false;
            if (_magicType.UseXP == 1)
            { owner.RemoveEffect(ClientEffect.XpStart); }
            owner.Stamina -= _magicType.UseStamina;
            owner.Mana -= _magicType.UseMP;
            //Take arrow count
            if (_magicType.WeaponSubtype == 500)
            {
                if (_magicType.UseItem == 50 && _magicType.UseItemNum > 0)
                {
                    ConquerItem item;
                    owner.Equipment.TryGetItemBySlot(5, out item);
                    if (item == null)
                        return false;

                    item.Durability -= _magicType.UseItemNum;
                    owner.Send(ItemInformationPacket.Create(item, ItemInfoAction.Update));
                    if (item.Durability == 0)
                    {
                        owner.Equipment.UnequipItem(5);
                        (owner as Player).DeleteItem(item);
                    }
                }
            }
            return true;
        }
        public bool IsValidTarget(Entity _target)
        {
            if (_target == owner)
                return false;
            if (!_target.Alive)
                return false;
            if (_target.HasStatus(Enum.ClientStatus.ReviveProtection))
                return false;
            if (_target is Pet)
                if (((_target as Pet).PetOwner.UID == owner.UID) || (owner.SpawnPacket.Lookface == 900 && (!(_target as Pet).PetOwner.HasEffect(ClientEffect.Blue) || !(_target as Pet).PetOwner.HasEffect(ClientEffect.Black))))
                    return false;

            bool valid = true;
            if (_target is Player)
            {
                if (!_target.Map.IsPKEnabled)
                    return false;
                switch (owner.PkMode)
                {
                    case PKMode.Peace:
                        valid = false;
                        break;
                    case PKMode.Capture:
                        valid = _target.HasEffect(ClientEffect.Blue) || _target.HasEffect(ClientEffect.Black);
                        break;
                    case PKMode.Team:
                        valid = true;
                        if (owner is Player)
                        {
                            var ownerPlayer = (Player)owner;
                            if (ownerPlayer.Team != null && (ownerPlayer.Team.Leader == _target || ownerPlayer.Team.Members.Contains(_target)))
                                valid = false;
                            if (ownerPlayer.Guild.Members().Contains(_target))
                                valid = false;
                        }
                        break;
                }
            }
            if (_target is Monster)
            {

                //Player->monster is valid as long as the type is 3 OR we are on pk/team mode
                if (owner is Player)
                    valid = ((Monster)_target).BaseMonster.AttackMode == 3 || owner.PkMode == PKMode.PK || owner.PkMode == PKMode.Team;

                //Monster->monster is valid as long as they are not the same type
                else if (owner is Monster)
                    valid = ((Monster)_target).BaseMonster.AttackMode != ((Monster)owner).BaseMonster.AttackMode;

            }
            if (_target is SOB)
            {
                if (_target.UID == 6700 && GuildWar.CurrentWinner == (owner as Player).Guild)
                    return false;

            }

            return valid;
        }
        private bool IsInAttackRange(Entity _target)
        {
            return owner.AttackRange >= Calculations.GetDistance(owner.Location.X, owner.Location.Y, _target.Location.X, _target.Location.Y);
        }
        private bool IsInSkillRange(Entity _target, DbMagicType _magicType)
        {
            if (_target == null)
                return false;
            return _magicType.Distance >= Calculations.GetDistance(owner.Location.X, owner.Location.Y, _target.Location.X, _target.Location.Y);
        }
        #endregion

        #region Skills and Profs management
        #region AddOrUpdateProf
        public void AddOrUpdateProf(ushort _id, ushort _level, uint _exp = 0)
        {
            ConquerProficiency prof;
            if (proficiencies.ContainsKey(_id))
            {
                proficiencies[_id].Level = _level;
                proficiencies[_id].Experience = _exp;
                proficiencies[_id].Save();
                prof = proficiencies[_id];
            }
            else
            {
                prof = ConquerProficiency.Create(owner.UID, _id, _level, _exp);
                proficiencies.TryAdd(prof.ID, prof);
                prof.Save();
            }
            prof.Send(owner);

        }
        public void AddProficiencyExperience(ushort _id, ulong _amount)
        {
            if (_amount == 0)
                return;
            if (!proficiencies.ContainsKey(_id))
                AddOrUpdateProf(_id, 1);
            else
            {
                var prof = proficiencies[_id];
                if (prof.Level >= 20)
                    return;
                prof.Experience += (uint)_amount;
                while (prof.Level < Constants.ProficiencyLevelExperience.Length && prof.Experience >= Constants.ProficiencyLevelExperience[prof.Level])
                {
                    prof.Experience -= Constants.ProficiencyLevelExperience[prof.Level];
                    prof.Level++;
                    if (prof.PreviousLevel != 0 && (prof.PreviousLevel / 2) < prof.Level)
                    {
                        prof.Level = prof.PreviousLevel;
                        prof.PreviousLevel = 0;
                        prof.Save();
                    }
                }
                prof.Send(owner);
            }
        }
        #endregion
        #region AddOrUpdateSkill
        public void AddSkillExperience(ushort _id, ulong _amount)
        {
            if (_amount == 0)
                return;
            if (!skills.ContainsKey(_id))
                return;
            var skill = skills[_id];
            if (owner.Level < skill.Info.NeedLevel && skill.Info.NeedLevel > 0)
                return;
            skill.Experience += (uint)_amount;
            while (skill.Experience > skill.Info.NeedExp && skill.Info.NeedExp > 0)
            {

                byte AddOn = 0;
                if (skill.PreviousLevel != 0 && (skill.PreviousLevel / 2) < (skill.Level + 1))
                {
                    AddOn = (byte)(skill.PreviousLevel - (skill.Level + 1));
                }
                var dbSkill = Database.ServerDatabase.Context.MagicType.GetMagictypeByIDAndLevel(skill.ID, (ushort)(skill.Level + 1 + AddOn));
                if (dbSkill == null)
                    dbSkill = Database.ServerDatabase.Context.MagicType.GetMagictypeByIDAndLevel(skill.ID, (ushort)(skill.Level + 1));//Failsafe
                if (dbSkill == null)
                    return;

                ConquerSkill x;
                if (skills.TryRemove(skill.ID, out x))
                {
                    skill.Experience -= skill.Info.NeedExp;
                    skill.Level = dbSkill.Level;
                    skill.ID = dbSkill.ID;
                    skill.Save();
                    skills.TryAdd(skill.ID, skill);
                }
            }
            skill.Send(owner);
        }
        public void LearnNewSkill(SkillID id)
        {
            if (!skills.ContainsKey((ushort)id))
                AddOrUpdateSkill(id, 0);
        }
        public void AddOrUpdateSkill(SkillID id, ushort level, uint exp = 0)
        {
            AddOrUpdateSkill((ushort)id, level, exp);
        }
        public void TryRemoveSkill(ushort _id, bool update = true)
        {
            if (!skills.ContainsKey(_id))
                return;
            ConquerSkill skill;
            skills.TryRemove(_id, out skill);
            if (update)
                owner.Send(GeneralActionPacket.Create(owner.UID, DataAction.DropMagic, skill.ID));
            ServerDatabase.Context.Skills.Remove(skill.Database);
        }
        public void AddOrUpdateSkill(ushort _id, ushort _level, uint _exp = 0)
        {
            ConquerSkill skill;
            if (skills.ContainsKey(_id))
            {
                skill = skills[_id];
                skills[_id].Level = _level;
                skills[_id].Experience = _exp;
                skills[_id].Save();
            }
            else
            {
                skill = ConquerSkill.Create(owner.UID, _id, _level, _exp);
                skills.TryAdd(skill.ID, skill);
                skill.Save();
            }
            skill.Send(owner);

        }
        #endregion
        #region KnowsSkill
        public bool KnowsSkillLevel(SkillID id, byte level)
        {
            ConquerSkill skill;
            skills.TryGetValue((ushort)id, out skill);
            if (skill == null)
                return false;
            return skill.Level >= level;
        }
        public bool KnowsSkill(SkillID id)
        {
            return (skills.ContainsKey((ushort)id));
        }
        public bool KnowsSkill(ushort id)
        {
            return (skills.ContainsKey(id));
        }
        #endregion
        #region CheckProficiency
        public bool CheckProficiency(ushort id, byte level)
        {
            ConquerProficiency prof;
            proficiencies.TryGetValue(id, out prof);
            if (prof == null)
                return false;
            return prof.Level >= level;
        }
        #endregion
        #region Save Skills/Proficiencies
        public void Save()
        {
            foreach (var skill in skills.Values)
                skill.Save();
            foreach (var prof in proficiencies.Values)
                prof.Save();
        }
        #endregion
        #endregion

        #region Public Accessors
        public int Power
        {
            get { if (skill == null) return 0; return skill.Power; }
        }
        public bool IsInIntone { get { return state == SkillState.Intone; } }
        public bool IsInLaunch { get { return state == SkillState.Launch; } }
        public bool IsActive { get { return state != SkillState.None; } }
        #endregion
    }
}

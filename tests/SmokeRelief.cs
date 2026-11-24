using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

public static partial class SmokeFixtures
{
    private static bool reliefEnabled;
    private static bool EnableReliefFixture() => reliefEnabled;
    private static bool siegeEnabled;
    private static bool EnableSiegeFixture() => siegeEnabled;

    private sealed class FixtureSiegeSight : DefaultMapVisibilityModel
    {
        public float Sight = 20;
        public override ExplainedNumber GetPartySpottingRange(MobileParty party, bool includeDescriptions = false) =>
            new ExplainedNumber(Sight, includeDescriptions);
    }

    private sealed class FixtureFiefValue : DefaultSettlementValueModel
    {
        public override float CalculateSettlementValueForFaction(Settlement settlement, IFaction faction)
        {
            fixtureValueQuotes++;
            FixtureDelay(45);
            return settlement.GatePosition.X;
        }
    }
    private static int fixtureValueQuotes;

    private sealed class FixtureReliefDiplomacy : DefaultDiplomacyModel
    {
        public override bool IsAtConstantWar(IFaction faction1, IFaction faction2) => false;
        public override DiplomacyStance? GetShallowDiplomaticStance(IFaction faction1, IFaction faction2) =>
            faction1 != null && faction2 != null && faction1.FactionsAtWarWith.Contains(faction2) ? DiplomacyStance.War : DiplomacyStance.Neutral;
    }

    private static bool AcceptFixtureLandPoint(ref bool __result) { __result = true; return false; }
    // Supply map geometry only; the installed defender model still scores and creates the proposal.
    private static bool SoloDefenseRoute(MobileParty __0, Settlement __1, ref MobileParty.NavigationType __3, ref float __4, ref bool __5)
    {
        __3 = MobileParty.NavigationType.Default;
        __4 = Math.Abs(__0.Position.X - __1.GatePosition.X);
        __5 = false;
        return false;
    }
    private static bool UseFixtureCampPosition(BesiegerCamp __instance, ref CampaignVec2 __result)
    {
        __result = __instance.SiegeEvent.BesiegedSettlement.GatePosition;
        return false; // The headless fixture has no map scene to place the tent.
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearOrderForTrace(MobileParty leader) => leader.SetMoveModeHold();

    private static MobileParty refusedSiegeReissue;
    private static bool RefuseSiegeReissue(MobileParty __0) => __0 != refusedSiegeReissue;

    private static void TestRelief(Assembly mod, Harmony harmony, Campaign campaign, object tracker, MobileParty player, Type logType)
    {
        var behavior = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.MilitaryCommandBehavior", true);
        var takeoverType = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.ArmyTakeoverBehavior", true);
        var acceptance = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.CampaignAcceptanceBehavior");
        var sessionLog = logType.GetMethod("Open", flags).Invoke(null, new object[]{campaign});
        Kingdom home = null, enemy = null;
        Clan payer = null, hostile = null;
        CampaignBehaviorBase command = null, takeover = null;
        AllianceCampaignBehavior alliances = null;
        CampaignEvents events = null;
        CampaignEventDispatcher dispatcher = null;
        CampaignObjectManager objects = null;
        FixtureCallPrice price = null;
        FixtureNeighbors graph = null;
        FixtureSiegeScore score = null;
        FixtureSiegeSight sight = null;
        var gameFrame = typeof(Game).GetField("_current", flags);
        var priorGame = gameFrame.GetValue(null);
        int caseId = 0;
        double now = 1100;
        void Time(double hour) { now = hour; Field(tracker, "_numTicks", (long)(hour * 1000)); }
        CampaignVec2 Point(float x) => new CampaignVec2(new Vec2(x, 0), true);
        Clan ClanFor(Kingdom kingdom, string id, float influence)
        {
            var clan = Blank<Clan>();
            clan.StringId = id;
            Field(clan, "_kingdom", kingdom);
            clan.Influence = influence;
            return clan;
        }
        void AttachCommand()
        {
            campaign.AddCampaignBehaviorManager(new CampaignBehaviorManager(new CampaignBehaviorBase[]{command, alliances}));
            command.RegisterEvents();
            Field(command, "_ready", true);
            Field(command, "_nextReportHour", double.MaxValue);
        }
        void World()
        {
            caseId++;
            blockedFixtureRoute = null;
            blockedFixtureVisit = null;
            fixtureRouteDistance = null;
            gameFrame.SetValue(null, priorGame);
            home = FixtureKingdom("relief-home");
            enemy = FixtureKingdom("relief-enemy");
            payer = ClanFor(home, "relief-payer", 250);
            hostile = ClanFor(enemy, "relief-hostile", 300);
            home.RulingClan = payer;
            enemy.RulingClan = hostile;
            Field(home, "_factionsAtWarWith", new MBList<IFaction>(new[]{enemy}));
            Field(enemy, "_factionsAtWarWith", new MBList<IFaction>(new[]{home}));
            Field(home, "_fiefsCache", new MBList<Town>());
            Field(enemy, "_fiefsCache", new MBList<Town>());
            Field(player, "_army", null);
            Field(player, "_currentSettlement", null);
            Field(player.Party, "_mapEventSide", null);
            var playerClan = ClanFor(home, "relief-player", 500);
            Property(campaign, "PlayerDefaultFaction", playerClan);
            Field(player, "_actualClan", playerClan);
            Field(player.LeaderHero, "_clan", playerClan);
            objects = Blank<CampaignObjectManager>();
            Field(objects, "_kingdoms", new MBList<Kingdom>(new[]{home, enemy}));
            Property(objects, "Settlements", new MBList<Settlement>());
            Property(campaign, "CampaignObjectManager", objects);
            events = new CampaignEvents();
            Property(campaign, "CampaignEvents", events);
            dispatcher = (CampaignEventDispatcher)Activator.CreateInstance(typeof(CampaignEventDispatcher), flags, null,
                new object[]{new CampaignEventReceiver[]{events}}, null);
            Property(campaign, "CampaignEventDispatcher", dispatcher);
            Property(campaign, "CustomPeriodicCampaignEvents", new List<MBCampaignEvent>());
            campaign.SkillLevelingManager = new DefaultSkillLevelingManager();
            var models = Blank<GameModels>();
            price = Blank<FixtureCallPrice>();
            price.Cost = 50;
            price.Upkeep = gain => (int)Math.Ceiling(gain * .75);
            graph = Blank<FixtureNeighbors>();
            graph.Links = new Dictionary<Settlement, MBList<Settlement>>();
            Property(models, "MapDistanceModel", graph);
            Property(models, "ArmyManagementCalculationModel", price);
            Property(models, "MobilePartyAIModel", Blank<DefaultMobilePartyAIModel>());
            Property(models, "EncounterModel", Blank<DefaultEncounterModel>());
            Property(models, "PartyImpairmentModel", Blank<DefaultPartyImpairmentModel>());
            Property(models, "PartyNavigationModel", Blank<DefaultPartyNavigationModel>());
            Property(models, "SettlementValueModel", Blank<FixtureFiefValue>());
            Property(models, "DiplomacyModel", Blank<FixtureReliefDiplomacy>());
            score = Blank<FixtureSiegeScore>();
            score.Minimum = 200;
            Property(models, "TargetScoreCalculatingModel", score);
            Property(models, "MilitaryPowerModel", Blank<FixtureCampPower>());
            sight = new FixtureSiegeSight();
            Property(models, "MapVisibilityModel", sight);
            Field(campaign, "_mobilePartyLocator", null);
            Field(campaign, "_gameModels", models);
            Property(campaign, "DefaultSiegeStrategies", Blank<DefaultSiegeStrategies>());
            takeover = (CampaignBehaviorBase)Activator.CreateInstance(takeoverType, flags, null, new object[]{campaign, sessionLog}, null);
            Property(takeover, "InitialCheckComplete", true);
            command = (CampaignBehaviorBase)Activator.CreateInstance(behavior, flags, null, new[]{sessionLog}, null);
            alliances = new AllianceCampaignBehavior();
            AttachCommand();
            Time(1100);
        }
        Clan NewKingdom(string name)
        {
            var kingdom = FixtureKingdom(name);
            var clan = ClanFor(kingdom, name + "-clan", 250);
            kingdom.RulingClan = clan;
            Field(kingdom, "_fiefsCache", new MBList<Town>());
            Field(kingdom, "_factionsAtWarWith", new MBList<IFaction>());
            ((IList)typeof(CampaignObjectManager).GetField("_kingdoms", flags).GetValue(objects)).Add(kingdom);
            return clan;
        }
        void War(Kingdom a, Kingdom b, bool active)
        {
            foreach (var pair in new[]{(a, b), (b, a)})
            {
                var wars = (IList)typeof(Kingdom).GetField("_factionsAtWarWith", flags).GetValue(pair.Item1);
                if (active && !wars.Contains(pair.Item2)) wars.Add(pair.Item2);
                if (!active) wars.Remove(pair.Item2);
            }
        }
        void Alliance(Kingdom a, Kingdom b, bool active)
        {
            // Keep the installed alliance query real. Seed its native records
            // only in the headless campaign, with no UI or kingdom decisions.
            var records = (IList)typeof(AllianceCampaignBehavior).GetField("_alliances", flags).GetValue(alliances);
            if (active)
            {
                var record = records.GetType().GetGenericArguments()[0];
                records.Add(Activator.CreateInstance(record, flags, null, new object[]{a, b, CampaignTime.Hours(2400)}, null));
                dispatcher.OnAllianceStarted(a, b);
            }
            else
            {
                alliances.EndAlliance(a, b);
            }
        }
        void Move(MobileParty party, float x)
        {
            if (party.CurrentSettlement != null)
                ((IList)party.CurrentSettlement.Parties).Remove(party);
            Field(party, "_currentSettlement", null);
            Field(party, "_position", Point(x));
        }
        void Enter(MobileParty party, Settlement settlement)
        {
            Move(party, settlement.GatePosition.X);
            Field(party, "_currentSettlement", settlement);
            ((IList)settlement.Parties).Add(party);
            dispatcher.OnAfterSettlementEntered(party, settlement, party.LeaderHero);
        }
        MobileParty Lord(string name, Clan clan, float power, float x, bool clanLeader = false)
        {
            var party = Party($"r{caseId}-{name}");
            FixtureLord(party, clan, power);
            Property(party, "HasLandNavigationCapability", true);
            Field(party.Ai, "_mobileParty", party);
            Field(party.LeaderHero, "_name", new TextObject("{=!}" + name));
            if (clanLeader)
                Field(clan, "_leader", party.LeaderHero);
            Move(party, x);
            return party;
        }
        Settlement Site(string id, float x, Clan owner = null, bool castle = false)
        {
            owner = owner ?? payer;
            var site = Blank<Settlement>();
            site.StringId = id;
            Property(site, "GatePosition", Point(x));
            var town = Blank<Town>();
            var baseParty = new PartyBase(site);
            typeof(SettlementComponent).GetField("_owner", flags).SetValue(town, baseParty);
            Property(site, "Party", baseParty);
            Property(site, "SettlementComponent", town);
            Field(site, "Town", town);
            Field(town, "_ownerClan", owner);
            Field(town, "_isCastle", castle);
            Field(site, "_partiesCache", new MBList<MobileParty>());
            Field(site, "_heroesWithoutPartyCache", new MBList<Hero>());
            Field(site, "_notablesCache", new MBList<Hero>());
            ((IList)owner.Kingdom.Fiefs).Add(town);
            // Ordinary fixture sites share a compact front. Geography scenarios
            // below supply their own links using the same native graph interface.
            if (objects.Settlements.Count != 0)
                graph.Link(objects.Settlements[0], site);
            ((IList)objects.Settlements).Add(site);
            return site;
        }
        Army Ready(MobileParty leader)
        {
            var army = Formation(leader);
            army.Cohesion = 100f;
            Field(army, "_kingdom", leader.MapFaction);
            ((IList)army.Kingdom.Armies).Add(army);
            Field(army, "_hourlyTickEvent", new MBCampaignEvent("fixture-army-hourly"));
            Field(army, "_tickEvent", new MBCampaignEvent("fixture-army-tick"));
            leader.SetMoveModeHold();
            return army;
        }
        SiegeEvent Camp(Settlement target, MobileParty attacker, double age = 0)
        {
            var siege = Blank<SiegeEvent>();
            Field(siege, "BesiegedSettlement", target);
            Property(siege, "SiegeStartTime", CampaignTime.Hours((float)(now - age)));
            var camp = new BesiegerCamp(siege, (Kingdom)attacker.MapFaction);
            camp.SetSiegeStrategy(new SiegeStrategy("fixture-already-chosen"));
            Field(siege, "BesiegerCamp", camp);
            Field(camp, "_leaderParty", attacker);
            ((IList)typeof(BesiegerCamp).GetField("_besiegerParties", flags).GetValue(camp)).Add(attacker);
            Field(attacker, "_besiegerCamp", camp);
            Property(target, "SiegeEvent", siege);
            Property(target, "LastAttackerParty", attacker);
            dispatcher.OnSiegeEventStarted(siege);
            return siege;
        }
        void Lift(Settlement target)
        {
            var siege = target.SiegeEvent;
            Property(target, "SiegeEvent", null);
            if (siege != null)
            {
                Field(siege.BesiegerCamp.LeaderParty, "_besiegerCamp", null);
                dispatcher.OnSiegeEventEnded(siege);
            }
        }
        bool Defends(MobileParty party, Settlement target) => party.Army?.AiBehaviorObject == target &&
            party.TargetSettlement == target && party.DefaultBehavior == AiBehavior.DefendSettlement;
        void Tick(double hour)
        {
            CampaignTick(hour);
            if ((bool)behavior.GetField("_offensiveDue", flags).GetValue(command))
                DrainOffensiveFrames(hour);
        }
        void CampaignTick(double hour)
        {
            Time(hour);
            Field(command, "_reliefReview", true);
            behavior.GetMethod("OnTick", flags).Invoke(command, new object[]{0f});
        }
        void ApplicationFrame(double hour) => behavior.GetMethod("OffensiveFrame", flags).Invoke(command, new object[]{hour});
        bool SweepPending() => behavior.GetField("_offensiveSearch", flags).GetValue(command) != null ||
            behavior.GetField("_offensiveCommit", flags).GetValue(command) != null;
        void DrainOffensiveFrames(double hour)
        {
            for (int i = 0; i < 5000; i++)
            {
                ApplicationFrame(hour);
                if (!SweepPending()) return;
            }
            throw new InvalidOperationException("offensive fixture did not finish within its frame budget");
        }
        IDictionary Assignments() => (IDictionary)behavior.GetField("_relief", flags).GetValue(command);
        IDictionary IdlePeriods() => (IDictionary)behavior.GetField("_idle", flags).GetValue(command);
        bool Idle(MobileParty leader) => leader.Army != null && IdlePeriods().Contains(leader.Army) &&
            leader.Army.AiBehaviorObject == null && leader.DefaultBehavior == AiBehavior.Hold && leader.TargetSettlement == null;
        Array Claims() => (Array)behavior.GetMethod("ReadCommitments", flags).Invoke(command, new object[]{null});
        T Claim<T>(object claim, string name) => (T)claim.GetType().GetField(name, flags).GetValue(claim);
        object FindClaim(Army army) => Claims().Cast<object>().Single(row => Claim<Army>(row, "Army") == army);
        bool OwnsClaim(Army army, Settlement target, string kind, bool ordered = true) =>
            Claims().Cast<object>().Any(row => Claim<Army>(row, "Army") == army && Claim<Settlement>(row, "Target") == target &&
                Claim<string>(row, "Job") == kind && Claim<bool>(row, "NativeOrder") == ordered && !Claim<bool>(row, "Closing"));
        MobileParty[] DuplicateClaims() =>
            (MobileParty[])behavior.GetMethod("ConflictingClaims", flags).Invoke(null, new object[]{Claims()});
        void Reload(bool legacyHome = false)
        {
            var type = typeof(Campaign).Assembly.GetType("TaleWorlds.CampaignSystem.CampaignBehaviorDataStore", true);
            var store = Activator.CreateInstance(type, true);
            type.GetMethod("SaveBehaviorData", flags).Invoke(store, new object[]{command});
            var dict = (IDictionary)type.GetField("_behaviorDict", flags).GetValue(store);
            var saved = dict[command.StringId];
            var records = (IDictionary)saved.GetType().GetField("_records", flags).GetValue(saved);
            if (legacyHome)
            {
                string current = (string)records["reliefStates"];
                string legacy = current.Replace("\"Owner\":\"" + home.StringId + "\",", "");
                if (legacy == current)
                    throw new Exception("Could not model the Stage 4 payload without its new owner field.");
                records["reliefStates"] = legacy;
            }
            var loading = Activator.CreateInstance(saved.GetType(), flags, null, new object[]{false}, null);
            Field(loading, "_records", records);
            dict[command.StringId] = loading;
            events.RemoveListeners(command);
            command = (CampaignBehaviorBase)Activator.CreateInstance(behavior, flags, null, new[]{sessionLog}, null);
            type.GetMethod("LoadBehaviorData", flags).Invoke(store, new object[]{command});
            AttachCommand();
        }

        void TestIndependentDefense()
        {
            World();
            var ally = NewKingdom("solo-ally");
            var allyFort = Site("allied-castle", 40, ally, castle: true);
            var homeFort = Site("home-town", 40);
            var solo = Lord("solo", payer, 50, 15, true);
            var committed = Lord("already-responding", payer, 1000, 16);
            var besieger = Lord("besieger", hostile, 500, 40, true);
            var allyBesieger = Lord("allied-besieger", hostile, 500, 40);
            var raider = Lord("village-raider", hostile, 200, 120);
            var villageSite = Blank<Settlement>();
            villageSite.StringId = "allied-village";
            Property(villageSite, "GatePosition", Point(120));
            var village = Blank<Village>();
            Field(village, "_bound", allyFort);
            var villagers = new PartyBase(villageSite);
            typeof(SettlementComponent).GetField("_owner", flags).SetValue(village, villagers);
            Property(villageSite, "SettlementComponent", village);
            Property(villageSite, "Party", villagers);
            Field(villageSite, "Village", village);
            var aid = new MBList<Settlement> { allyFort };
            Field(ally.Kingdom, "_settlementsCache", aid);
            Field(home, "_settlementsCache", new MBList<Settlement> { homeFort });
            Alliance(home, ally.Kingdom, true);
            home.UpdateAlliedKingdoms();
            War(ally.Kingdom, enemy, true);
            var model = new DefaultTargetScoreCalculatingModel();
            Property(campaign.Models, "TargetScoreCalculatingModel", model);
            var averages = new Dictionary<MobileParty.NavigationType, float> { [MobileParty.NavigationType.All] = 30 };
            Field(campaign, "_averageDistanceBetweenClosestTwoTowns", averages);
            campaign.EstimatedAverageLordPartySpeed = 5;
            var game = Blank<Game>();
            var character = Blank<CharacterObject>();
            Property(character, "HeroObject", player.LeaderHero);
            game.PlayerTroop = character;
            gameFrame.SetValue(null, game);
            var navigation = typeof(Helpers.AiHelper).GetMethod("GetBestNavigationTypeAndAdjustedDistanceOfSettlementForMobileParty", flags);
            var route = typeof(SmokeFixtures).GetMethod(nameof(SoloDefenseRoute), flags);
            harmony.Patch(navigation, prefix: new HarmonyMethod(route));
            int oldDay = CampaignTime.HoursInDay;
            CampaignTime.HoursInDay = 24;
            try
            {
                Camp(homeFort, besieger);
                besieger.Aggressiveness = 1;
                allyBesieger.Aggressiveness = 1;
                MobileParty.UpdateLocator(besieger);
                float Score(float power) => model.GetTargetScoreForFaction(homeFort, Army.ArmyTypes.Defender, solo, power);
                float floor = Score(50);
                Check(floor > 0 && floor == Score(500) && Math.Abs(Score(750) / floor - 1.5f) < .001f,
                    "installed home-defense model gives a free lord at 1:10 the parity score and preserves genuine strength advantage");
                SetPartyAiAction.GetActionForDefendingSettlement(committed, homeFort, MobileParty.NavigationType.Default, false, false);
                Check(Score(50) < floor, "a well-covered home siege still discounts another independent responder");
                committed.SetMoveModeHold();
                var army = Ready(solo);
                Check(Score(50) < floor / 5, "joining an army restores vanilla's understrength defense score");
                Field(solo, "_army", null);
                ((IList)home.Armies).Remove(army);
                Camp(allyFort, allyBesieger);
                MobileParty.UpdateLocator(allyBesieger);
                var ai = new AiMilitaryBehavior();
                var pass = typeof(AiMilitaryBehavior).GetMethod("CalculateMilitaryBehaviorForFactionSettlements", flags);
                PartyThinkParams Choices(bool gathering = false, MobileParty who = null)
                {
                    var p = new PartyThinkParams(who ?? solo) { WillGatherAnArmy = gathering };
                    pass.Invoke(ai, new object[] { home, p, Army.ArmyTypes.Defender, AiBehavior.DefendSettlement, 50f, 1f, 1f, 1f });
                    return p;
                }
                bool Offered(PartyThinkParams p, Settlement target) => p.AIBehaviorScores.Any(s => s.Item1.Party == target &&
                    s.Item1.AiBehavior == AiBehavior.DefendSettlement && !s.Item1.WillGatherArmy && s.Item2 > 0);
                var choices = Choices();
                Check(Offered(choices, homeFort) && !Offered(choices, allyFort), "a besieged home fort preempts allied party proposals");
                Lift(homeFort);
                Property(homeFort, "LastAttackerParty", null);
                choices = Choices();
                Check(Offered(choices, allyFort) && choices.AIBehaviorScores.Count == 1 && solo.TargetSettlement == null &&
                    model.GetTargetScoreForFaction(allyFort, Army.ArmyTypes.Defender, solo, 50) <
                    model.GetTargetScoreForFaction(allyFort, Army.ArmyTypes.Defender, solo, 500) / 5,
                    "native scoring adds one standalone allied option, with normal strength weighting and no issued order");
                Check(!Choices(true).AIBehaviorScores.Any(s => s.Item1.Party == allyFort),
                    "allied party aid never proposes gathering a new army");
                Field(solo, "_army", army);
                Field(committed, "_army", army); // summoned but not yet attached
                Check(!Offered(Choices(), allyFort) && !Offered(Choices(who: committed), allyFort) &&
                    !Offered(Choices(who: player), allyFort), "solo allied aid excludes army leaders, traveling summons and the player");
                Field(solo, "_army", null);
                Field(committed, "_army", null);
                var raid = Blank<MapEvent>();
                var attack = Blank<MapEventSide>();
                var defense = Blank<MapEventSide>();
                Field(attack, "_mapEvent", raid);
                Field(defense, "_mapEvent", raid);
                Field(raid, "_sides", new[]{defense, attack});
                Property(attack, "LeaderParty", raider.Party);
                var component = Blank<RaidEventComponent>();
                Property(raid, "Component", component);
                Property(component, "MapEventSettlement", villageSite);
                Field(villagers, "_mapEventSide", defense);
                Field(raider.Party, "_mapEventSide", attack);
                raider.Aggressiveness = 1;
                MobileParty.UpdateLocator(raider);
                Property(villageSite, "LastAttackerParty", raider);
                aid.Add(villageSite);
                Check(Offered(Choices(), villageSite), "an independent lord can also respond to an allied village's active raid");
                Field(villagers, "_mapEventSide", null);
                Check(!Offered(Choices(), villageSite), "a past raider reference alone does not keep a village defense candidate alive");
                War(ally.Kingdom, enemy, false);
                Check(!Offered(Choices(), allyFort), "a siege still on the map after the owner makes peace is not an allied defense candidate");
                War(ally.Kingdom, enemy, true);
                War(home, enemy, false);
                Check(!Offered(Choices(), allyFort), "party aid does not enter an ally's war");
                War(home, enemy, true);
                Alliance(home, ally.Kingdom, false);
                Check(!Offered(Choices(), allyFort), "a common enemy without an active alliance does not qualify");
            }
            finally
            {
                harmony.Unpatch(navigation, route);
                CampaignTime.HoursInDay = oldDay;
                gameFrame.SetValue(null, priorGame);
            }
        }

        reliefEnabled = true;
        try
        {
            TestIndependentDefense();
            World();
            var noSiege = Site("hero-only-entry", 0);
            var visitor = Blank<Hero>();
            Field(visitor, "_clan", hostile);
            Property(visitor, "Occupation", Occupation.Lord);
            Field(command, "_reliefReview", false);
            bool sawHeroOnly = false;
            CampaignEvents.AfterSettlementEntered.AddNonSerializedListener(command,
                (party, site, hero) => sawHeroOnly |= party == null && site == noSiege && hero == visitor);
            var heroEntryGame = Blank<Game>();
            var heroEntryPlayer = Blank<CharacterObject>();
            Property(heroEntryPlayer, "HeroObject", player.LeaderHero);
            heroEntryGame.PlayerTroop = heroEntryPlayer;
            gameFrame.SetValue(null, heroEntryGame);
            try { EnterSettlementAction.ApplyForCharacterOnly(visitor, noSiege); }
            finally { gameFrame.SetValue(null, priorGame); }
            Check(sawHeroOnly && visitor.StayingInSettlement == noSiege && visitor.PartyBelongedTo == null &&
                !(bool)behavior.GetField("_reliefReview", flags).GetValue(command),
                "installed character-only settlement action dispatches a null mobile party without triggering a relief review");

            World();
            var town = Site("long-sieged-town", 100);
            var oldCamp = Camp(town, Lord("besieger", hostile, 600, 100, true), 220);
            var slow = Lord("long-response", payer, 400, 460, true);
            var original = Ready(slow);
            var tooFar = Lord("over-three-days", payer, 2000, 1130);
            Ready(tooFar);
            var pending = Ready(Lord("legacy", payer, 900, 110));
            ((IList)takeoverType.GetField("_pending", flags).GetValue(takeover)).Add(pending);
            var manual = Lord("external-order", payer, 900, 105);
            var manualArmy = Ready(manual);
            var unbesieged = Site("manual-target", 80);
            SetPartyAiAction.GetActionForVisitingSettlement(manual, unbesieged, MobileParty.NavigationType.Default, false, false);
            var optOut = Lord("opt-out", payer, 900, 103);
            optOut.LeaderHero.CanJoinArmy = false;
            int start = log.Count;
            Tick(1100);
            Check(oldCamp.SiegeStartTime.ElapsedDaysUntilNow > 8.02 && Defends(slow, town) &&
                tooFar.DefaultBehavior == AiBehavior.Hold &&
                log.Skip(start).Any(line => line.Contains("routeETA=+72h") && line.Contains("send-available")),
                "an already-old siege gets an understrength response at exactly 72h, but not the stronger army over range");
            Check(pending.LeaderParty.DefaultBehavior == AiBehavior.Hold && manualArmy.LeaderParty.TargetSettlement == unbesieged &&
                optOut.Army == null && Assignments().Count == 1, "legacy pending, explicit external orders, and opt-outs are never claimed");
            Reload(legacyHome: true);
            Tick(1320);
            Check(slow.Army == original && Defends(slow, town) && Assignments().Count == 1 &&
                log.Skip(start).Count(line => line.Contains("Relief departure:")) == 1,
                "siege age alone does not expire a valid relief, and a loaded response does not dispatch twice");
            Move(slow, 102);
            typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(slow, new object[]{AiBehavior.Hold, null});
            payer.Influence = 230;
            original.Cohesion = 40;
            Tick(1321);
            Check(Defends(slow, town) && payer.Influence == 200 && Math.Abs(original.Cohesion - 80f) < .01 &&
                log.Skip(start).Any(line => line.Contains("phase=local-waiting") && line.Contains("local=400")),
                "native local waiting is observed and receives only affordable upkeep above the clan reserve");
            Field(town.Town, "_ownerClan", hostile);
            dispatcher.OnSettlementOwnerChanged(town, false, hostile.Leader, payer.Leader, hostile.Leader,
                ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege);
            Tick(1321.25);
            Check(Idle(slow) && log.Skip(start).Any(line => line.Contains("reason=settlement-fell-by-native-siege")),
                "actual siege capture ends relief, not a projected fall");

            World();
            var highTown = Site("valuable-town", 100);
            var lowTown = Site("other-town", 80);
            var castle = Site("expensive-castle", 150, castle: true);
            Camp(highTown, Lord("camp-one", hostile, 600, 100, true));
            Camp(lowTown, Lord("camp-two", hostile, 300, 80));
            Camp(castle, Lord("camp-three", hostile, 200, 150));
            var one = Lord("joint-one", payer, 400, 108, true);
            var two = Lord("joint-two", payer, 350, 112);
            var three = Lord("other-objective", payer, 500, 65);
            Ready(one); Ready(two); Ready(three);
            start = log.Count;
            Tick(1100);
            Check(Defends(one, highTown) && Defends(two, highTown) && Defends(three, lowTown) && Assignments().Count == 3,
                "two nearby forces cover one town while the third covers its nearer town instead of the more valuable castle");
            Check(log.Skip(start).Count(line => line.Contains("Relief route match:") && line.Contains("target=valuable-town")) == 2 &&
                log.Skip(start).Any(line => line.Contains("localOwned=0") && line.Contains("committed=750")),
                "joint planned power is not credited as local arrival");
            var fourth = Lord("spare-castle-response", payer, 250, 148);
            Ready(fourth);
            Tick(1101);
            Check(Defends(fourth, castle) && Assignments().Count == 4, "the uncovered castle stays eligible when another army becomes available");
            Lift(highTown);
            Tick(1102);
            Check(Idle(one) && Idle(two) && Defends(three, lowTown) && Defends(fourth, castle),
                "ending one siege starts idle grace only for its unassigned formations");

            World();
            var fort = Site("safe-muster", 0);
            town = Site("fresh-relief-town", 40);
            Camp(town, Lord("fresh-camp", hostile, 600, 40, true));
            var leader = Lord("fresh-leader", payer, 240, -5, true);
            var recruit = Lord("fresh-summons", ClanFor(home, "recruit-clan", 0), 190, 10);
            var excluded = Lord("unused-optout", payer, 850, 8);
            excluded.LeaderHero.CanJoinArmy = false;
            start = log.Count;
            Enter(leader, fort);
            Tick(1100);
            var freshArmy = leader.Army;
            Check(freshArmy != null && recruit.Army == freshArmy && recruit.AttachedTo == null && payer.Influence == 200 &&
                leader.DefaultBehavior == AiBehavior.GoToSettlement && excluded.Army == null,
                "a new understrength muster uses the real summons debit and waits for physical attachment");
            Reload();
            Tick(1100.25);
            Check(leader.Army == freshArmy && payer.Influence == 200 && log.Skip(start).Count(line =>
                line.Contains("Muster debit") && line.Contains("actualDebit=50")) == 1, "a loaded relief muster keeps army identity and never repays a summon");
            Enter(recruit, fort);
            Tick(1101);
            Check(recruit.AttachedTo == leader && Defends(leader, town) && leader.Army == freshArmy &&
                log.Skip(start).Any(line => line.Contains("Relief departure:") && line.Contains("attached=430") && line.Contains("send-available")),
                "the assembled fresh 430 vs 600 formation departs on native defend without waiting for a ratio");
            Lift(town);
            Tick(1102);
            Check(Idle(leader) && recruit.Army == freshArmy, "ending a relief order keeps its intact response for idle grace");

            World();
            fort = Site("cancel-muster", 0);
            town = Site("lift-during-muster", 40);
            Camp(town, Lord("lift-camp", hostile, 600, 40, true));
            leader = Lord("cancel-leader", payer, 240, -5, true);
            recruit = Lord("protected-summon", ClanFor(home, "travel-clan", 0), 190, 10);
            Enter(leader, fort);
            Tick(1100);
            var held = leader.Army;
            var battle = Blank<MapEvent>();
            Property(battle, "Component", Blank<FieldBattleEventComponent>());
            var side = Blank<MapEventSide>();
            Field(side, "_mapEvent", battle);
            Field(recruit.Party, "_mapEventSide", side);
            Lift(town);
            start = log.Count;
            Tick(1101);
            Check(held != null && leader.Army == held && recruit.Army == held && payer.Influence == 200 &&
                log.Skip(start).Any(line => line.Contains("phase=muster") && line.Contains("siege-ended-owner-retained")) &&
                log.Skip(start).Any(line => line.Contains("Relief release deferred:") &&
                    line.Contains(recruit.StringId + ":native-event") && line.Contains("battle=FieldBattle")),
                "ending during muster identifies the battle-held summon blocking release");
            Field(recruit.Party, "_mapEventSide", null);
            Tick(1101.25);
            Check(Idle(leader) && recruit.Army == held && recruit.TargetParty == leader && payer.Influence == 200,
                "safe cancellation keeps the original escort without refunding or replaying the native payment");

            foreach (bool changeMuster in new[]{false, true})
            {
                World();
                fort = Site("paid-castle-muster", 0);
                castle = Site("gathering-castle", 30, castle: true);
                town = Site("urgent-home-town", 60);
                Camp(castle, Lord("castle-besieger", hostile, 600, 30, true));
                leader = Lord("redirect-muster-leader", payer, 240, -15, true);
                recruit = Lord("already-paid-escort", ClanFor(home, "paid-escort-clan", 0), 190, -30);
                start = log.Count;
                Tick(1100);
                var gathering = leader.Army;
                Check(gathering != null && leader.TargetSettlement == fort && recruit.Army == gathering &&
                    recruit.AttachedTo == null && recruit.TargetParty == leader && payer.Influence == 200,
                    "the lower-priority castle has a real paid muster with its leader and summons still traveling");
                var nextMuster = changeMuster ? Site("reachable-town-muster", 12) : fort;
                fixtureRouteDistance = (from, to) => to == 30 ? 100 : Math.Abs(from - to);
                if (changeMuster)
                    blockedFixtureRoute = (from, to) => from == 0 && to == 60;
                var lockedBattle = Blank<MapEvent>();
                Property(lockedBattle, "Component", Blank<FieldBattleEventComponent>());
                var lockedSide = Blank<MapEventSide>();
                Field(lockedSide, "_mapEvent", lockedBattle);
                Field(recruit.Party, "_mapEventSide", lockedSide);
                Camp(town, Lord("urgent-besieger", hostile, 600, 60));
                Tick(1100.25);
                Check(leader.Army == gathering && leader.TargetSettlement == fort && recruit.MapEvent == lockedBattle &&
                    !log.Skip(start).Any(line => line.Contains("Relief muster redirected")),
                    "diversion waits while a paid traveler is protected by a native battle");
                Field(recruit.Party, "_mapEventSide", null);
                dispatcher.OnMapEventEnded(lockedBattle);
                Tick(1100.5);
                Check(leader.Army == gathering && recruit.Army == gathering && recruit.TargetParty == leader &&
                    recruit.AttachedTo == null && leader.TargetSettlement == nextMuster && payer.Influence == 200 &&
                    log.Skip(start).Any(line => line.Contains("Relief muster redirected:") && line.Contains("previous=gathering-castle") &&
                        line.Contains("present=240 projected=430")) &&
                    log.Skip(start).Any(line => line.Contains("target=gathering-castle") && line.Contains("committed=0")),
                    "town takes the intact muster with separate present and projected strength; castle stays open and uncommitted");
                Reload();
                Enter(leader, nextMuster);
                Tick(1101);
                Check(leader.Army == gathering && leader.DefaultBehavior == AiBehavior.GoToSettlement && recruit.Army == gathering,
                    "loaded town muster keeps the original paid escort and waits for attachment");
                Enter(recruit, nextMuster);
                Tick(1102);
                Check(leader.Army == gathering && recruit.AttachedTo == leader && Defends(leader, town) && castle.SiegeEvent != null &&
                    Assignments().Count == 1 && log.Skip(start).Count(line => line.Contains("Muster debit") && line.Contains("actualDebit=50")) == 1,
                    "redirected muster departs for the town with its original army and no duplicate summon debit");
            }

            World();
            fort = Site("in-transit-upkeep-muster", 0);
            town = Site("in-transit-upkeep-town", 40);
            Camp(town, Lord("upkeep-besieger", hostile, 600, 40, true));
            leader = Lord("upkeep-marching-to-muster", payer, 240, -30, true);
            recruit = Lord("upkeep-locked-traveler", ClanFor(home, "upkeep-escort-clan", 0), 190, -40);
            Tick(1100);
            var maintained = leader.Army;
            var travelerBattle = Blank<MapEvent>();
            Property(travelerBattle, "Component", Blank<FieldBattleEventComponent>());
            var travelerSide = Blank<MapEventSide>();
            Field(travelerSide, "_mapEvent", travelerBattle);
            Field(recruit.Party, "_mapEventSide", travelerSide);
            payer.Influence = 230;
            maintained.Cohesion = 40;
            Tick(1101);
            Check(leader.CurrentSettlement == null && leader.TargetSettlement == fort &&
                Math.Abs(maintained.Cohesion - 80f) < .01 && payer.Influence == 200 &&
                recruit.Army == maintained && recruit.TargetParty == leader && recruit.MapEvent == travelerBattle,
                "en-route muster receives an affordable native boost without touching an unrelated battle-held traveler");
            Enter(leader, fort);
            Tick(1102);
            Check(Defends(leader, town) && recruit.Army == maintained && recruit.MapEvent == travelerBattle && recruit.TargetParty == leader,
                "a locked traveler remains deferred as the attached force departs understrength");
            payer.Influence = 230;
            maintained.Cohesion = 40;
            Tick(1103);
            Check(Defends(leader, town) && Math.Abs(maintained.Cohesion - 80f) < .01 && payer.Influence == 200 &&
                recruit.Army == maintained && recruit.TargetParty == leader && recruit.MapEvent == travelerBattle,
                "native defending army can buy upkeep without detaching or ordering its unrelated locked traveler");
            payer.Influence = 230;
            maintained.Cohesion = 40;
            Field(leader.Party, "_mapEventSide", travelerSide);
            Tick(1104);
            Check(maintained.Cohesion == 40 && payer.Influence == 230 && Defends(leader, town),
                "the same upkeep is blocked when the leader is in a native battle");
            Field(leader.Party, "_mapEventSide", null);
            var lockedAttached = Lord("upkeep-attached-in-battle", payer, 100, 0);
            Enter(lockedAttached, fort);
            lockedAttached.Army = maintained;
            maintained.AddPartyToMergedParties(lockedAttached);
            Field(lockedAttached.Party, "_mapEventSide", travelerSide);
            Tick(1105);
            Check(maintained.Cohesion == 40 && payer.Influence == 230 && lockedAttached.AttachedTo == leader,
                "a battle involving an attached member still protects the present formation from upkeep changes");

            World();
            town = Site("route-priority-town", 40);
            castle = Site("route-only-castle", 70, castle: true);
            Camp(town, Lord("route-town-camp", hostile, 300, 40, true));
            Camp(castle, Lord("route-castle-camp", hostile, 300, 70));
            var flexible = Lord("closer-flexible", payer, 400, 38, true);
            var inflexible = Lord("further-town-only", payer, 400, 20);
            Ready(flexible); Ready(inflexible);
            blockedFixtureRoute = (from, to) => (from == 20 || from == 32) && to == 70;
            Tick(1100);
            Check(Defends(inflexible, town) && Defends(flexible, castle),
                "an adequate farther response frees the only other army with a route to the second home siege");
            var successor = Lord("after-dispersal", payer, 420, 32);
            Ready(successor);
            typeof(Army).GetMethod("DisperseInternal", flags).Invoke(inflexible.Army, new object[]{Army.ArmyDispersionReason.CohesionDepleted});
            Tick(1101);
            Check(inflexible.Army == null && Defends(successor, town) && town.SiegeEvent != null &&
                log.Any(line => line.Contains("Relief natural dispersal:") && line.Contains("CohesionDepleted")),
                "native dispersal ends the army but the still-active siege gets a replacement");

            World();
            fort = Site("remuster-site", 20);
            town = Site("remuster-objective", 40);
            Camp(town, Lord("remuster-attacker", hostile, 500, 40, true));
            var trappedRoute = Lord("needs-remuster", payer, 625, 5, true);
            var oldArmy = Ready(trappedRoute);
            blockedFixtureRoute = (from, to) => from == 5 && to == 40;
            start = log.Count;
            Tick(1100);
            Check(trappedRoute.Army != null && trappedRoute.Army != oldArmy && !home.Armies.Contains(oldArmy) &&
                trappedRoute.TargetSettlement == fort && trappedRoute.Position.X == 5 &&
                log.Skip(start).Any(line => line.Contains("home-relief-remuster")),
                "a safely released formation can remuster through a reachable fort without teleporting");

            World();
            castle = Site("first-castle", 40, castle: true);
            town = Site("later-town", 70);
            Camp(castle, Lord("first-camp", hostile, 300, 40, true));
            var redirected = Lord("priority-response", payer, 400, 30, true);
            var redirectArmy = Ready(redirected);
            Tick(1100);
            Check(Defends(redirected, castle), "the available army initially responds to the only home siege");
            Move(redirected, 68);
            Camp(town, Lord("priority-camp", hostile, 300, 70));
            Tick(1100.25);
            Check(Defends(redirected, town) && redirected.Army == redirectArmy && castle.SiegeEvent != null,
                "a newly besieged town can redirect an unlocked army now closer to it without new formation costs");
            var foreignOrder = Site("player-or-external-destination", 35);
            SetPartyAiAction.GetActionForVisitingSettlement(redirected, foreignOrder, MobileParty.NavigationType.Default, false, false);
            Lift(town);
            Tick(1100.5);
            Check(redirected.Army == redirectArmy && redirected.TargetSettlement == foreignOrder && Assignments().Count == 0,
                "ending a siege does not cancel a replacement order issued outside the commander");

            World();
            town = Site("field-strength-town", 40);
            var host = Lord("field-siege-leader", hostile, 600, 40, true);
            Camp(town, host);
            var fieldLeader = Lord("field-response", payer, 400, 5, true);
            Ready(fieldLeader);
            var helpingLord = Lord("vanilla-help", ClanFor(home, "other-clan", 0), 320, 100);
            helpingLord.SetMoveDefendSettlement(town, false, MobileParty.NavigationType.Default);
            payer.Influence = 200;
            start = log.Count;
            Tick(1100);
            Check(Defends(fieldLeader, town) && helpingLord.Army == null && log.Skip(start).Any(line =>
                line.Contains("committed=400") && line.Contains("localOther=0")), "distant vanilla response does not count as local support");
            Move(fieldLeader, 42);
            Move(helpingLord, 43);
            Tick(1101);
            Check(log.Skip(start).Any(line => line.Contains("localOwned=400") && line.Contains("localOther=320") &&
                line.Contains("localOver120=False")), "exactly 120 percent is not a sufficient local field-force benchmark");
            forcePowers[helpingLord.Party] = 321;
            Tick(1102);
            Check(log.Skip(start).Any(line => line.Contains("localOwned=400") && line.Contains("localOther=321") &&
                line.Contains("localOver120=True")) && helpingLord.Army == null,
                "observed unassigned local help can tip the benchmark without commandeering the party");
            var field = Blank<MapEvent>();
            Property(field, "Component", Blank<FieldBattleEventComponent>());
            var attackerSide = Blank<MapEventSide>();
            var reliefSide = Blank<MapEventSide>();
            Field(attackerSide, "_mapEvent", field);
            Field(reliefSide, "_mapEvent", field);
            Field(host.Party, "_mapEventSide", attackerSide);
            Field(fieldLeader.Party, "_mapEventSide", reliefSide);
            Tick(1103);
            Check(log.Skip(start).Any(line => line.Contains("phase=relief-battle") && line.Contains("physical=True")) &&
                Defends(fieldLeader, town), "native combat is observed without replacing the defend order");

            World();
            town = Site("naval-only-town", 40);
            host = Lord("naval-camp", hostile, 600, 40, true);
            Field(host, "_isCurrentlyAtSea", true);
            Camp(town, host);
            fieldLeader = Lord("land-only-response", payer, 400, 5, true);
            Ready(fieldLeader);
            start = log.Count;
            Tick(1100);
            Check(Assignments().Count == 0 && town.SiegeEvent != null && log.Skip(start).Any(line =>
                line.Contains("execution=unsupported-naval-camp")), "naval-only siege remains open and reports the land-planner limitation");

            TestAlliedRelief();
            TestCompetingReliefRoutes();
            TestInterruptedReliefRemuster();
            TestPostReliefRemuster();
            TestWinnableReliefRoutes();
            TestStableReliefAssignments();
            TestReliefTravelLimit();
            TestReliefNoOpMusterCost();
            TestReliefOrderTrace();
            TestFleeingReliefRecovery();
            TestRecoveredReliefKeepsAssignment();
            if (acceptance != null)
            {
                TestReliefAcceptance();
                TestReliefAcceptance(generateCamp: true);
                TestReliefAcceptance(allied: true);
                TestReliefAcceptance(generateCamp: true, allied: true);
                TestReliefAcceptance(allied: true, ineligible: true);
                TestReliefAcceptance(generateCamp: true, allied: true, unreachable: true);
            }
            else
                Check(mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.CampaignAcceptanceBehavior") == null,
                    "ordinary relief runs without acceptance or an attack orchestrator");
            TestOffensiveSieges();
            if (Environment.GetEnvironmentVariable("MAIO_BENCHMARK") == "1")
            {
                BenchmarkSiegeSearch(false);
                BenchmarkSiegeSearch(true);
                BenchmarkReliefScan();
            }
        }
        finally
        {
            reliefEnabled = false;
            siegeEnabled = false;
            blockedFixtureRoute = null;
            fixtureRouteDistance = null;
            gameFrame.SetValue(null, priorGame);
        }

        void BenchmarkSiegeSearch(bool pathRejected)
        {
            World();
            // Same free parties, same failed native strength checks at every
            // fort, no order or debit, identical queries on every sample.
            for (int i = 0; i < 8; i++) Site("benchmark-muster-" + i, 20 + i * 3);
            for (int i = 0; i < 7; i++) Site("benchmark-enemy-" + i, 74 + i * 4, hostile);
            for (int i = 0; i < 14; i++)
                Lord("benchmark-party-" + i, ClanFor(home, "benchmark-payer-" + i, 250), 220, 7 + i, true);
            score.Minimum = pathRejected ? 300 : 2000;
            visitPathAvailable = !pathRejected;
            var allocate = behavior.GetMethod("AllocateSiege", flags);
            var args = new object[] { home, 1100d };
            void Whole() { foreach (int _ in (IEnumerable<int>)allocate.Invoke(command, args)) { } }
            void Reset() { fixtureDistanceCalls = fixturePathTrials = score.Calls = price.Quotes = fixtureValueQuotes = 0; }
            (int score, int distance, int path, int price, int value) Counts() =>
                (score.Calls, fixtureDistanceCalls, fixturePathTrials, price.Quotes, fixtureValueQuotes);
            var full = new List<double>();
            var slices = new List<double>();
            var sweeps = new List<double>();
            int maxCalls = 0;
            double singleQuerySlice = 0;
            (int score, int distance, int path, int price, int value) perVisit = default;
            expensiveSiegeFixture = true;
            try
            {
                for (int i = -2; i < 12; i++)
                {
                    Reset();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    Whole();
                    if (i >= 0) full.Add(timer.Elapsed.TotalMilliseconds);
                    var whole = Counts();
                    perVisit = whole;
                    Reset();
                    Field(command, "_offensiveDue", true);
                    Field(command, "_nextSiegeSearchSlot", 0d);
                    ((IDictionary)behavior.GetField("_nextSiegeSearch", flags).GetValue(command)).Clear();
                    int frames = 0;
                    double sum = 0;
                    do
                    {
                        var before = Counts();
                        var frame = System.Diagnostics.Stopwatch.StartNew();
                        ApplicationFrame(1100);
                        frame.Stop();
                        var after = Counts();
                        maxCalls = Math.Max(maxCalls, after.score - before.score + after.distance - before.distance +
                            after.path - before.path + after.price - before.price + after.value - before.value);
                        if (i >= 0) slices.Add(frame.Elapsed.TotalMilliseconds);
                        sum += frame.Elapsed.TotalMilliseconds;
                        if (++frames > 5000) throw new Exception("benchmark sweep stalled");
                    } while (SweepPending());
                    if (i >= 0) sweeps.Add(sum);
                    if (Counts() != whole) throw new Exception($"same-input sliced search differed: {Counts()} vs {whole}");
                }
                Field(command, "_offensiveDue", true);
                Field(command, "_nextSiegeSearchSlot", 0d);
                ((IDictionary)behavior.GetField("_nextSiegeSearch", flags).GetValue(command)).Clear();
                fixtureNextDistanceDelay = 12000;
                do
                {
                    var frame = System.Diagnostics.Stopwatch.StartNew();
                    ApplicationFrame(1100);
                    singleQuerySlice = Math.Max(singleQuerySlice, frame.Elapsed.TotalMilliseconds);
                } while (SweepPending());
            }
            finally { expensiveSiegeFixture = false; fixtureNextDistanceDelay = 0; visitPathAvailable = true; }
            double P(List<double> values, double q) => values.OrderBy(x => x).ElementAt((int)Math.Ceiling(values.Count * q) - 1);
            Console.WriteLine($"BENCH repeated identical siege no-op ({(pathRejected ? "path-rejected" : "strength-rejected")}), per visit (score,distance,pathTrial,price,value)={perVisit} " +
                $"monolithic visits={full.Count} p50/p95/p99/maxMs={P(full,.5):F2}/{P(full,.95):F2}/{P(full,.99):F2}/{full.Max():F2} " +
                $"sliced callbacks={slices.Count} p50/p95/p99/maxMs={P(slices,.5):F2}/{P(slices,.95):F2}/{P(slices,.99):F2}/{slices.Max():F2} " +
                $"maxModeledCallsPerCallback={maxCalls} medianAggregateMs={P(sweeps,.5):F2} isolated12msQueryMaxCallbackMs={singleQuerySlice:F2}");
            Check(maxCalls <= 8 && (perVisit.path > 0) == pathRejected && perVisit.score > 0,
                "expensive no-op sweeps bound top-level native quotes per application callback; only positive scores reach path trials");
        }

        void BenchmarkReliefScan()
        {
            World();
            for (int i = 0; i < 6; i++)
            {
                var ally = NewKingdom("benchmark-ally-" + i);
                War(ally.Kingdom, enemy, true);
                Alliance(home, ally.Kingdom, true);
                for (int j = 0; j < 24; j++) Site($"benchmark-fief-{i}-{j}", 5 + j * 2, ally);
            }
            var besieged = Site("benchmark-only-camp", 74);
            Camp(besieged, Lord("benchmark-camp", hostile, 500, 74, true));
            var reply = Ready(Lord("benchmark-reliever", payer, 750, 52, true));
            Tick(1100);
            var objective = mod.GetType("BannerlordMilitaryAIOverhaul.Command.MilitaryObjective", true).GetMethod("Relief", flags);
            var probe = new HarmonyMethod(typeof(SmokeFixtures).GetMethod(nameof(CountFixtureObjective), flags));
            harmony.Patch(objective, prefix: probe);
            try
            {
                var scan = behavior.GetMethod("TickRelief", flags);
                var args = new object[] { 1100.25d };
                var requested = behavior.GetField("_reliefReview", flags);
                void Scan() { requested.SetValue(command, true); scan.Invoke(command, args); }
                for (int i = 0; i < 250; i++) Scan();
                var ms = new List<double>();
                for (int sample = 0; sample < 5; sample++)
                {
                    fixtureObjectiveCalls = 0;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 1000; i++) Scan();
                    ms.Add(timer.Elapsed.TotalMilliseconds);
                }
                Console.WriteLine($"BENCH relief 1000 unchanged active reviews, objectiveChecks={fixtureObjectiveCalls}, samplesMs=[{string.Join(", ", ms.Select(x => x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}] medianMs={ms.OrderBy(x => x).ElementAt(2):F2}");
                Check(OwnsClaim(reply, besieged, "home"), "performance fixture leaves the actual defense order unchanged");
            }
            finally { harmony.Unpatch(objective, probe.method); }
        }

        void TestReliefNoOpMusterCost()
        {
            World();
            const int siteCount = 12;
            for (int i = 0; i < siteCount; i++) Site($"no-op-muster-{i}", 40 + i);
            var target = Site("no-op-siege", 80);
            Camp(target, Lord("no-op-camp", hostile, 400, 80, true));
            var leader = Lord("no-op-leader", payer, 290, 10, true);
            var escort = Lord("no-op-escort", ClanFor(home, "no-op-escort-clan", 0), 225, 29);
            for (int i = 0; i < 8; i++)
                Lord($"no-op-other-escort-{i}", ClanFor(home, $"no-op-other-clan-{i}", 0), 100, 20 + i);

            int campLegs = 0, leaderLegs = 0, escortLegs = 0;
            bool marchTooLong = true, inwardTooLong = true;
            fixtureRouteDistance = (from, to) =>
            {
                if (from >= 40 && from < 40 + siteCount && to == 80)
                {
                    campLegs++;
                    if (marchTooLong) return 400;
                }
                if (to >= 40 && to < 40 + siteCount)
                {
                    if (from == 10)
                    {
                        leaderLegs++;
                        if (inwardTooLong) return 400;
                    }
                    else if (from >= 20 && from <= 29)
                        escortLegs++;
                }
                return Math.Abs(from - to);
            };
            void Review(double hour)
            {
                Time(hour);
                Field(command, "_reliefPlan", true);
                behavior.GetMethod("TickRelief", flags).Invoke(command, new object[]{hour});
            }

            Review(1100);
            Check(campLegs == siteCount && leaderLegs == 0 && escortLegs == 0 && price.Quotes == 0 &&
                leader.Army == null && payer.Influence == 250,
                "an impossible march quotes each camp leg once and does not path to the muster or repeat the search");
            campLegs = 0;
            marchTooLong = false;
            Review(1101);
            Check(campLegs == siteCount && leaderLegs == siteCount && escortLegs == 0 && price.Quotes == 0 &&
                leader.Army == null && payer.Influence == 250,
                "when all leaders fail the complete travel limit, no escort routes or replacement/fallback queries are needed");

            inwardTooLong = false;
            Review(1102);
            Check(leader.Army != null && escort.Army == leader.Army && leader.TargetSettlement == home.Fiefs[11].Settlement &&
                payer.Influence == 200, "the next pass re-reads live routes and chooses the nearest viable muster and affordable escort");
        }

        void TestCompetingReliefRoutes()
        {
            foreach (bool detour in new[]{false, true})
            {
                World();
                var lageta = Site("lageta", 40);
                var amitatys = Site("amitatys", 120); // higher native value, both home towns
                var vipon = Lord("vipon", hostile, 800, 40, true);
                Camp(lageta, vipon);
                var hecaron = Lord("hecaron", payer, 1260, 41, true);
                var hecaronArmy = Ready(hecaron);
                Tick(1100);
                var saratis = Lord("saratis", payer, 1524, 43);
                var saratisArmy = Ready(saratis);
                Camp(amitatys, Lord("other-besieger", hostile, 3273, 120));
                Tick(1101);
                Check(Defends(hecaron, lageta) && Defends(saratis, amitatys),
                    "one ready local defender suffices, so the spare gets the distant understrength defense");

                for (int i = 0; i < 2; i++)
                {
                    hecaronArmy.FinishArmyObjective();
                    Tick(1102 + i);
                }
                Check(Defends(hecaron, lageta), "the original relief army has used both ordinary restores before the new battle interruption");
                // Save the old orders with Saratis still beside Lageta. Hecaron
                // has just lost his native order to an unrelated field encounter.
                var battle = Blank<MapEvent>();
                Property(battle, "Component", Blank<FieldBattleEventComponent>());
                var side = Blank<MapEventSide>();
                Field(side, "_mapEvent", battle);
                hecaronArmy.FinishArmyObjective();
                Field(hecaron.Party, "_mapEventSide", side);
                forcePowers[vipon.Party] = 2492;
                if (detour) fixtureRouteDistance = (from, to) => from == 43 && to == 40 ? 400 : Math.Abs(from - to);
                int mark = log.Count;
                Reload(); Tick(1104);
                Check(Defends(saratis, detour ? amitatys : lageta) && saratis.Army == saratisArmy &&
                    !Defends(hecaron, lageta) && hecaron.MapEvent == battle && DuplicateClaims().Length == 0,
                    detour ? "a genuine route detour beats visual proximity after reload" :
                    "reload switches Saratis from the 77-unit trip back to the uncovered camp three route units away");
                Check(log.Skip(mark).Any(l => l.Contains("Relief order state:") && l.Contains("assigned=lageta order=Hold") &&
                    l.Contains("lock=native-event")) && log.Skip(mark).Any(l => l.Contains("Relief review:") &&
                    l.Contains("target=lageta") && l.Contains(detour ? "localOwned=0" : "localOwned=1524")),
                    "a locked defender keeps his assignment but contributes no ready local power");
                if (detour) continue;
                Tick(1105);
                Check(log.Skip(mark).Count(l => l.Contains("Relief departure:") && l.Contains("target=lageta")) == 1,
                    "an unchanged route match does not reissue orders every review");
                dispatcher.OnMapEventEnded(battle);
                Field(hecaron.Party, "_mapEventSide", null);
                Tick(1106);
                Check(Defends(hecaron, lageta) && Defends(saratis, lageta) && log.Skip(mark).Any(l =>
                    l.Contains("Relief native defend reissued:") && l.Contains("restored=True")),
                    "after battle cleanup the cleared defense is restored; both nearby armies stay while needed");
                typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(hecaron, new object[]{AiBehavior.Hold, null});
                forcePowers[vipon.Party] = 800;
                Tick(1107);
                Check(Defends(hecaron, lageta) && Defends(saratis, amitatys) && saratis.Army == saratisArmy &&
                    log.Skip(mark).Count(l => l.Contains("Relief native defend reissued:")) == 1 &&
                    !log.Skip(mark).Any(l => l.Contains("Muster debit") || l.Contains("Muster release:")),
                    "native tactical Hold is valid local cover; only surplus Saratis leaves, without a new army or charge");
            }

            World();
            var near = Site("promised-but-not-yet-covered", 40);
            var far = Site("other-front", 120);
            Camp(near, Lord("besieger-one", hostile, 700, 40, true));
            var marching = Lord("slow-inbound", payer, 1000, 150, true);
            Ready(marching); Tick(1100);
            var here = Lord("available-at-the-camp", payer, 950, 42);
            Ready(here);
            Camp(far, Lord("besieger-two", hostile, 500, 120));
            Tick(1101);
            Check(Defends(here, near) && Defends(marching, far),
                "a sufficient distant promise cannot mask a nearer defender; the inbound army covers its own closer siege instead");
        }

        void TestWinnableReliefRoutes()
        {
            World();
            var georgios = Site("georgios", 40, castle: true);
            var lageta = Site("lageta", 64);
            var georgiosCamp = Lord("strong-camp", hostile, 3349, 40, true);
            var lagetaCamp = Lord("second-camp", hostile, 2750, 64);
            Camp(georgios, georgiosCamp);
            Camp(lageta, lagetaCamp);
            var saratis = Lord("saratis", payer, 450, 35, true);
            var hecaron = Lord("hecaron", payer, 426, 34);
            var sArmy = Ready(saratis);
            var hArmy = Ready(hecaron);
            Tick(1100);
            Check(Defends(saratis, georgios) && Defends(hecaron, georgios),
                "two understrength armies still respond to the nearer camp when neither siege can be covered");
            Move(saratis, 41); Move(hecaron, 42);
            forcePowers[saratis.Party] = 880;
            forcePowers[hecaron.Party] = 855;
            int mark = log.Count;
            Lift(lageta);
            forcePowers[lagetaCamp.Party] = 746;
            Camp(lageta, lagetaCamp);
            blockedFixtureRoute = (from, to) => from == 42 && to == 64;
            Tick(1101);
            Check(Defends(saratis, georgios) && Defends(hecaron, georgios),
                "a weaker camp is not a viable joint response if one of the armies it needs has no route");
            blockedFixtureRoute = null;
            Reload(); Tick(1102);
            Check(Defends(saratis, lageta) && Defends(hecaron, lageta) && saratis.Army == sArmy && hecaron.Army == hArmy &&
                georgios.SiegeEvent != null && DuplicateClaims().Length == 0 &&
                log.Skip(mark).Any(l => l.Contains("Relief response selected:") && l.Contains("target=lageta") &&
                    l.Contains("selectedPower=1735") && l.Contains("why=adequate-route-match")) &&
                !log.Skip(mark).Any(l => l.Contains("Muster release:") || l.Contains("Relief muster issued:")),
                "both local understrength defenders redirect intact when only together can they cover the farther camp");

            // They later become enough for their current camp. Keep the stationed
            // defender needed there, even if a spare marching in could replace it.
            Move(saratis, 63);
            forcePowers[lagetaCamp.Party] = 600;
            forcePowers[georgiosCamp.Party] = 600;
            Tick(1103);
            Check(Defends(saratis, lageta) && Defends(hecaron, georgios),
                "already adequate local cover stays put while the spare relieves the other camp");

            World();
            var close = Site("closest-to-first-army", 40);
            var other = Site("earliest-combined-arrival", 52);
            Camp(close, Lord("near-camp", hostile, 400, 40, true));
            Camp(other, Lord("far-camp", hostile, 400, 52));
            saratis = Lord("first-inbound", payer, 260, 41, true);
            hecaron = Lord("second-inbound", payer, 260, 65);
            Ready(saratis); Ready(hecaron);
            Tick(1100);
            Check(Defends(saratis, other) && Defends(hecaron, other),
                "when both camps are beatable together, choose the earlier combined ETA rather than the earliest single arrival");

            World();
            close = Site("adequately-defended-camp", 40);
            other = Site("closer-new-camp", 46);
            Camp(close, Lord("original-besieger", hostile, 600, 40, true));
            saratis = Lord("stationed-defender", payer, 850, 45, true);
            Ready(saratis);
            Tick(1100);
            Camp(other, Lord("nearer-besieger", hostile, 400, 46));
            Tick(1101);
            Check(Defends(saratis, close),
                "a faster route to a new beatable siege does not strip the only adequate defender already on station");
            hecaron = Lord("spare-defender", payer, 550, 47);
            Ready(hecaron);
            Tick(1102);
            Check(Defends(saratis, close) && Defends(hecaron, other),
                "a spare army relieves the new camp without stripping the adequately defended camp");
        }

        void TestStableReliefAssignments()
        {
            World();
            var castle = Site("viable-castle", 40, castle: true);
            var town = Site("understrength-town", 80);
            Camp(castle, Lord("castle-besieger", hostile, 400, 40, true));
            Camp(town, Lord("town-besieger", hostile, 1500, 80));
            var saratis = Lord("saratis", payer, 650, 10, true);
            var hecaron = Lord("hecaron", payer, 640, 14);
            var sArmy = Ready(saratis);
            var hArmy = Ready(hecaron);
            double sEta = 20, hEta = 25;
            fixtureRouteDistance = (from, to) => to == 40 ? 5 * (from == 10 ? sEta : hEta) : Math.Abs(from - to);
            void Plan(double hour)
            {
                Field(command, "_reliefPlan", true);
                Tick(hour);
            }
            bool OriginalPlan() => Defends(saratis, castle) && Defends(hecaron, town);
            Plan(1100);
            Check(OriginalPlan(), "one army covers the viable castle, and the spare responds understrength to the town");
            int mark = log.Count;
            sEta = 25; hEta = 24.5;
            Plan(1101); Plan(1102);
            Check(OriginalPlan(), "a sustained half-hour ETA gain does not swap two viable assignments");
            sEta = 40; hEta = 36;
            Plan(1102.25); Plan(1103.25);
            Check(OriginalPlan(), "a four-hour gain on a forty-hour trip is too small to trade assignments");
            sEta = 25;
            hEta = 20;
            Plan(1103.5); Plan(1104);
            Check(OriginalPlan(), "a burst of planning passes does not confirm an ETA-only swap");
            sEta = 19;
            Plan(1104.25);
            sEta = 25;
            Plan(1104.5);
            Check(OriginalPlan() && !log.Skip(mark).Any(l => l.Contains("Relief departure:")),
                "a lead that flips between reviews does not issue new orders");
            Plan(1105.5);
            Check(Defends(hecaron, castle) && Defends(saratis, town) && saratis.Army == sArmy && hecaron.Army == hArmy,
                "a clear ETA gain lasting a full planning interval permits the intact swap");
            forcePowers[hecaron.Party] = 470;
            Plan(1105.75);
            Check(OriginalPlan() && DuplicateClaims().Length == 0,
                "losing sufficient strength triggers immediate reassignment without waiting for ETA confirmation");

            World();
            castle = Site("two-cover-castle", 40, castle: true);
            town = Site("route-only-town", 90);
            var other = Site("near-weak-response", 12);
            Camp(castle, Lord("castle-besieger", hostile, 400, 40, true));
            Camp(town, Lord("town-besieger", hostile, 400, 90));
            saratis = Lord("castle-only", payer, 600, 14, true);
            hecaron = Lord("castle-and-town", payer, 570, 38);
            Ready(saratis); Ready(hecaron);
            blockedFixtureRoute = (from, to) => (from == 14 && to == 90) || (from == 38 && to == 40);
            Plan(1100);
            Check(OriginalPlan(), "two separate viable orders are assigned using their only routes");
            blockedFixtureRoute = (from, to) => from == 14 && to == 90;
            Camp(other, Lord("strong-camp", hostile, 1500, 12));
            Plan(1100.25);
            Check(OriginalPlan(), "a faster arrival at one siege does not leave fewer sieges adequately covered");
        }

        void TestPostReliefRemuster()
        {
            World();
            var lageta = Site("lageta", 40);
            var jalmarys = Site("jalmarys", 80);
            Camp(lageta, Lord("lageta-besieger", hostile, 696, 40, true));
            var saratis = Lord("saratis", payer, 997, 41, true);
            var army = Ready(saratis);
            army.Cohesion = 94;
            Tick(1100);
            Check(Defends(saratis, lageta), "the existing army relieves Lageta before the replacement decision");

            Lift(lageta);
            Camp(jalmarys, Lord("jalmarys-besieger", hostile, 2231, 80));
            blockedFixtureRoute = (from, to) => from == 41 && to == 80;
            int mark = log.Count;
            Tick(1101);
            Check(Idle(saratis) && saratis.Army == army && army.Cohesion == 94 &&
                !log.Skip(mark).Any(l => l.Contains("Muster release:") || l.Contains("Muster debit")),
                "a post-relief army enters idle grace rather than being broken up for an understrength replacement");
            Reload();
            Tick(1102);
            Check(Idle(saratis) && saratis.Army == army,
                "the understrength replacement cannot bypass grace on later reviews or after reload");
            var volunteer = Lord("free-lord", ClanFor(home, "free-clan", 300), 300, 38, true);
            Tick(1103);
            Check(volunteer.Army != null && volunteer.Army != army && OwnsClaim(volunteer.Army, jalmarys, "home", ordered: false) &&
                Idle(saratis) && saratis.Army == army,
                "free lords may still muster an understrength response without taking apart an intact army");
        }

        void TestReliefOrderTrace()
        {
            World();
            var lageta = Site("trace-relief-target", 40);
            var besieger = Lord("trace-relief-besieger", hostile, 3500, 40, true);
            Camp(lageta, besieger);
            var meritor = Lord("trace-relief-meritor", payer, 750, 43, true);
            var arytha = Lord("trace-relief-arytha", payer, 700, 44);
            var other = Lord("trace-relief-other", payer, 200, 45);
            var meritorArmy = Ready(meritor);
            var arythaArmy = Ready(arytha);
            Ready(other);
            CampaignTick(1100);
            Check(Defends(meritor, lageta) && Defends(arytha, lageta) && Defends(other, lageta),
                "the relief trace fixture begins with three active native defend orders");
            var unrelated = Lord("trace-relief-unrelated", hostile, 1500, 45);
            var shortOrder = typeof(MobileParty).GetMethod("SetShortTermBehavior", flags);
            foreach (var leader in new[] { meritor, arytha })
            {
                ClearOrderForTrace(leader);
                shortOrder.Invoke(leader, new object[]{AiBehavior.FleeToPoint, unrelated.Party});
            }
            Reload();

            var trace = mod.GetType("BannerlordMilitaryAIOverhaul.Diagnostics.SiegeOrderTrace", true);
            var native = typeof(MobileParty).GetMethod("SetMoveModeHold", Type.EmptyTypes);
            var folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "maio-relief-trace-" + Guid.NewGuid().ToString("N")));
            string marker = System.IO.Path.Combine(folder.FullName, "trace-v1.armed");
            string run = Guid.NewGuid().ToString("N");
            bool Arm() => (bool)trace.GetMethod("TryClaim", flags).Invoke(null, new object[]{folder.FullName, command, sessionLog});
            int start = log.Count;
            string[] Rows() => log.Skip(start).Where(line => line.Contains($"Relief order trace: run={run}")).ToArray();
            try
            {
                System.IO.File.WriteAllText(marker, $"relief-order-v1 {run} {lageta.StringId} {meritor.StringId} {arytha.StringId}");
                Check(Arm() && !System.IO.File.Exists(marker) &&
                    System.IO.File.Exists(System.IO.Path.Combine(folder.FullName, $"relief-order-v1.{run}.claimed")),
                    "the focused relief trace claims a one-shot marker");
                CampaignTick(1101);
                Check(Rows().Count(line => line.Contains(" initial ") && line.Contains("activeDepartedAssignment=True") &&
                    line.Contains("default=Hold") && line.Contains("short=FleeToPoint")) == 2 &&
                    Rows().Count(line => line.Contains("seq=0 decision=formation-locked") &&
                        line.Contains(":native-movement-lock") && line.Contains("orderRestored=False")) == 2,
                    "both saved Hold orders are visible from the first tick even with no new writer; fleeing blocks restoration");
                Move(arytha, 45);
                CampaignTick(1103);
                Check(Rows().Any(line => line.Contains($"army={arytha.StringId}@") &&
                    line.Contains("decision=formation-locked") && line.Contains("position=(45,0,land)")),
                    "a continuing lock gets a periodic position sample even without a change of orders");
                shortOrder.Invoke(meritor, new object[]{AiBehavior.Hold, null});
                shortOrder.Invoke(arytha, new object[]{AiBehavior.Hold, null});
                CampaignTick(1104);
                Check(Defends(meritor, lageta) && Defends(arytha, lageta) && Rows().Count(line =>
                    line.Contains("seq=0 decision=reissue-attempted") && line.Contains("orderRestored=True")) == 2,
                    "normal relief recovery restores both orders after fleeing without new summons");
                ClearOrderForTrace(other);
                ClearOrderForTrace(arytha);
                ClearOrderForTrace(arytha);
                Check(Rows().Count(line => line.Contains(" hold ")) == 1 && Rows().Any(line =>
                    line.Contains($"army={arytha.StringId}@") && line.Contains("ClearOrderForTrace") &&
                    line.Contains("before=[default=DefendSettlement") && line.Contains("after=[default=Hold")),
                    "only the selected defender losing a real order gets a before/after writer trace");
                CampaignTick(1105);
                Check(Defends(arytha, lageta) && meritor.Army == meritorArmy && arytha.Army == arythaArmy &&
                    Rows().Any(line => line.Contains("seq=1 decision=reissue-attempted") && line.Contains("orderRestored=True")) &&
                    payer.Influence == 250, "the trace leaves the armies, reissue rules and influence unchanged");
                CampaignTick(1150);
                Check(Rows().Count(line => line.Contains("finished reason=campaign-window-ended")) == 1,
                    "the relief trace ends after its bounded window");
            }
            finally
            {
                trace.GetMethod("Close", flags).Invoke(null, null);
                foreach (var file in folder.GetFiles()) file.Delete();
                folder.Delete();
            }
            Check(Harmony.GetPatchInfo(native)?.Owners.Contains("BannerlordMilitaryAIOverhaul.SiegeOrderTrace") != true,
                "the one-shot relief observer does not leave its Hold hook installed after campaign exit");
        }

        void TestFleeingReliefRecovery()
        {
            foreach (string scenario in new[] { "restore", "other-threat", "leader-battle", "attached-battle",
                "disabled", "unroutable", "out-of-range", "order-intact" })
            {
                World();
                var lageta = Site("flee-relief-target", 40);
                var besieger = Lord("flee-relief-camp", hostile, 3100, 40, true);
                Camp(lageta, besieger);
                var leader = Lord("flee-relief-leader", payer, 750, 44, true);
                var army = Ready(leader);
                CampaignTick(1100);
                Check(Defends(leader, lageta), $"an original relief order precedes the fleeing case ({scenario})");
                if (scenario != "order-intact")
                    ClearOrderForTrace(leader);
                var contact = scenario == "other-threat" ? Lord("flee-relief-unrelated", hostile, 2000, 45) : besieger;
                var shortOrder = typeof(MobileParty).GetMethod("SetShortTermBehavior", flags);
                void Flee() => shortOrder.Invoke(leader, new object[]{AiBehavior.FleeToPoint, contact.Party});
                Flee();
                if (scenario == "disabled")
                    leader.Ai.DisableAi();
                if (scenario == "leader-battle" || scenario == "attached-battle")
                {
                    var party = leader;
                    if (scenario == "attached-battle")
                    {
                        party = Lord("flee-relief-attached", payer, 300, 44);
                        party.Army = army;
                        army.AddPartyToMergedParties(party);
                    }
                    var side = Blank<MapEventSide>();
                    var battle = Blank<MapEvent>();
                    Property(battle, "Component", Blank<FieldBattleEventComponent>());
                    Field(side, "_mapEvent", battle);
                    Field(party.Party, "_mapEventSide", side);
                }
                if (scenario == "unroutable")
                    blockedFixtureRoute = (from, to) => to == 40;
                if (scenario == "out-of-range")
                    Move(leader, 406);
                int mark = log.Count;
                Reload();
                CampaignTick(1101);
                bool Reissued() => log.Skip(mark).Any(line => line.Contains("Relief native defend reissued:") &&
                    line.Contains($"army={leader.StringId}@") && line.Contains("restored=True reason=own-camp-flee"));
                if (scenario != "restore")
                {
                    Check(leader.Army == army && Assignments().Contains(army) && !Reissued() &&
                        Defends(leader, lageta) == (scenario == "order-intact") && leader.IsFleeing(),
                        $"the narrowly scoped exception leaves the native movement lock intact ({scenario})");
                    continue;
                }
                Check(Defends(leader, lageta) && Reissued() && leader.Army == army && payer.Influence == 250 &&
                    army.Cohesion == 100 && !log.Skip(mark).Any(line => line.Contains("Muster debit") || line.Contains("Muster release")),
                    "the saved own-camp flee restores only the cleared native defense, without replacing or paying the army");
                ClearOrderForTrace(leader); Flee();
                CampaignTick(1101.5);
                Check(!Defends(leader, lageta) && leader.IsFleeing(), "the normal one-hour reissue delay still applies while fleeing");
                CampaignTick(1102);
                Check(Defends(leader, lageta), "the second normal reissue can restore the same order");
                ClearOrderForTrace(leader); Flee();
                CampaignTick(1103);
                Check(!Defends(leader, lageta) && leader.IsFleeing() && Assignments().Contains(army) &&
                    log.Skip(mark).Count(line => line.Contains("reason=own-camp-flee")) == 2,
                    "the existing two-reissue limit is not bypassed and does not force release while fleeing");
            }
        }

        void TestRecoveredReliefKeepsAssignment()
        {
            foreach (bool allied in new[]{false, true})
            {
                World();
                var owner = payer;
                if (allied)
                {
                    owner = NewKingdom("recovery-allied-owner");
                    War(owner.Kingdom, enemy, true);
                    Alliance(home, owner.Kingdom, true);
                }
                var original = Site("recovery-original", 40, owner, castle: true);
                var competing = Site("recovery-competing-home", 64);
                var besieger = Lord("recovery-original-camp", hostile, 3100, 40, true);
                Camp(original, besieger);
                var leader = Lord("recovery-leader", payer, 750, 44, true);
                var army = Ready(leader);
                CampaignTick(1100);
                Check(Defends(leader, original), "the competing-siege fixture starts with an active defend order");

                ClearOrderForTrace(leader);
                typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(leader,
                    new object[]{AiBehavior.FleeToPoint, besieger.Party});
                Camp(competing, Lord("recovery-competing-camp", hostile, 500, 64));
                int mark = log.Count;
                CampaignTick(1100.25);
                Check(log.Skip(mark).Any(line => line.Contains("target=recovery-original") &&
                    line.Contains("restored=True reason=own-camp-flee")) &&
                    Defends(leader, original) && leader.Army == army && !leader.IsFleeing() &&
                    log.Skip(mark).Any(line => line.Contains("Relief review:") && line.Contains("target=recovery-competing-home") &&
                        line.Contains("defend-recovered-this-pass:1")),
                    $"same-tick planning cannot redirect a newly restored defense (original allied={allied}, target={leader.TargetSettlement?.StringId})");

                CampaignTick(1101.25);
                Check(Defends(leader, competing) && leader.Army == army && original.IsUnderSiege && payer.Influence == 250,
                    $"the next planning pass can redirect the same army intact to the competing siege (original allied={allied})");
            }
        }

        void TestReliefTravelLimit()
        {
            foreach (bool alternate in new[]{false, true})
            {
                World();
                var far = Site("original-home-defense", 40);
                var near = Site("other-home-defense", 70);
                Camp(far, Lord("first-besieger", hostile, 500, 40, true));
                var leader = Lord("marching-relief", payer, 700, 30, true);
                var original = Ready(leader);
                Tick(1100);
                Check(Defends(leader, far), "the original relief march is within the travel limit");
                fixtureRouteDistance = (from, to) => to == 40 ? 365 : Math.Abs(from - to);
                if (alternate) Camp(near, Lord("second-besieger", hostile, 500, 70));
                int mark = log.Count;
                Reload(); Tick(1100.25);
                Check(log.Skip(mark).Any(line => line.Contains("Relief travel limit:") && line.Contains("eta=73h limit=72h")) &&
                    far.SiegeEvent != null && (alternate ? leader.Army == original && Defends(leader, near) :
                        leader.Army == null && log.Skip(mark).Any(line => line.Contains("reason=relief:relief-travel-limit"))),
                    alternate ? "a saved over-limit march redirects intact to another reachable home siege" :
                        "a saved over-limit march releases the army without closing the objective");
            }

            World();
            var fort = Site("near-muster", 20);
            var town = Site("overrange-muster-defense", 40);
            Camp(town, Lord("muster-besieger", hostile, 600, 40, true));
            var caller = Lord("muster-leader", payer, 400, 25, true);
            var late = Lord("late-escort", ClanFor(home, "late-clan", 200), 450, 15);
            fixtureRouteDistance = (from, to) => from == 20 && to == 40 ? 350 :
                from == 15 && to == 20 ? 25 : Math.Abs(from - to);
            Tick(1100);
            Check(caller.Army != null && caller.TargetSettlement == fort && late.Army == null && payer.Influence == 250,
                "a relief muster does not buy an escort whose arrival plus the camp march exceeds 72 hours");
        }

        void TestInterruptedReliefRemuster()
        {
            World();
            var lageta = Site("lageta", 40);
            var amitatys = Site("amitatys", 120);
            Site("open-remuster-fort", 60);
            var vipon = Lord("vipon", hostile, 800, 40, true);
            Camp(lageta, vipon);
            var hecaron = Lord("hecaron", payer, 1100, 41, true);
            var hecaronArmy = Ready(hecaron);
            MobileParty AttachLord(MobileParty leader, string name)
            {
                var party = Lord(name, ClanFor(home, name, 300), 200, leader.Position.X, true);
                party.Army = leader.Army;
                leader.Army.AddPartyToMergedParties(party);
                return party;
            }
            var hMember = AttachLord(hecaron, "hecaron-escort");
            Tick(1100);
            var saratis = Lord("saratis", ClanFor(home, "saratis-clan", 300), 1300, 43, true);
            var saratisArmy = Ready(saratis);
            var sMember = AttachLord(saratis, "saratis-escort");
            Camp(amitatys, Lord("other-besieger", hostile, 3300, 120));
            Tick(1101);
            Check(Defends(hecaron, lageta) && Defends(saratis, amitatys),
                "the remuster replay starts with the original armies assigned to different sieges");

            void Contact(MobileParty party, AiBehavior mode, MobileParty other) =>
                typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(party, new object[]{mode, other?.Party});
            var passingEnemy = Lord("unrelated-contact", hostile, 50, 44);
            forcePowers[vipon.Party] = 2492;
            hecaron.SetMoveModeHold(); // native party order cleared; army objective still Lageta
            Contact(hecaron, AiBehavior.FleeToPoint, passingEnemy);
            int mark = log.Count;
            Reload(); Tick(1102);
            Check(hecaron.DefaultBehavior == AiBehavior.Hold && OwnsClaim(hecaronArmy, lageta, "home", ordered: false) &&
                saratis.Army == saratisArmy && Defends(saratis, lageta),
                "the fleeing army keeps its claim after reload and Saratis returns intact");

            // Each attached lord could lead a fresh army via the open fort.
            Contact(hecaron, AiBehavior.EngageParty, vipon);
            Tick(1103);
            Check(hecaron.Army == hecaronArmy && hMember.Army == hecaronArmy && hMember.AttachedTo == hecaron &&
                hecaron.DefaultBehavior == AiBehavior.Hold && hecaron.ShortTermTargetParty == vipon,
                "engaging the besieger with a cleared defend order does not release Hecaron's roster for Amitatys");
            Contact(saratis, AiBehavior.EngageParty, passingEnemy);
            Tick(1104);
            Check(saratis.Army == saratisArmy && sMember.Army == saratisArmy && sMember.AttachedTo == saratis &&
                Defends(saratis, lageta) && saratis.ShortTermTargetParty == passingEnemy &&
                log.Skip(mark).Any(l => l.Contains("Relief review:") && l.Contains("target=lageta") && l.Contains("committed=0 localOwned=0")),
                "an unrelated pursuit gives no ready coverage and does not release Saratis's roster for Amitatys");

            Contact(hecaron, AiBehavior.Hold, null);
            Contact(saratis, AiBehavior.Hold, null);
            Tick(1105);
            Check(OwnsClaim(hecaronArmy, lageta, "home") && OwnsClaim(saratisArmy, lageta, "home") &&
                hMember.AttachedTo == hecaron && sMember.AttachedTo == saratis && DuplicateClaims().Length == 0 &&
                !log.Skip(mark).Any(l => l.Contains("Muster release:") || l.Contains("Relief muster issued:")),
                "after both contacts clear the original armies defend together without disbanding or buying a replacement");
        }

        void TestAlliedRelief()
        {
            World();
            var nearbyAlly = NewKingdom("overlapping-allied-owner");
            War(nearbyAlly.Kingdom, enemy, true);
            Alliance(home, nearbyAlly.Kingdom, true);
            var coveredOnlyOnPaper = Site("overlapping-home", 40);
            var actualResponse = Site("overlapping-ally", 45, nearbyAlly);
            Camp(coveredOnlyOnPaper, Lord("home-camp", hostile, 300, 40, true));
            Camp(actualResponse, Lord("ally-camp", hostile, 300, 45));
            var volunteer = Lord("independent-changing-contact", payer, 400, 42);
            SetPartyAiAction.GetActionForDefendingSettlement(volunteer, coveredOnlyOnPaper, MobileParty.NavigationType.Default, false, false);
            typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(volunteer,
                new object[]{AiBehavior.EngageParty, actualResponse.LastAttackerParty.Party});
            var available = Lord("real-home-response", payer, 400, 15, true);
            var availableArmy = Ready(available);
            Tick(1100);
            Check(Defends(available, coveredOnlyOnPaper) && Assignments().Contains(availableArmy) && volunteer.Army == null,
                "one independent field party reacting to the allied camp cannot also cover the home camp by its older defend order");

            foreach (string plan in new[]{"one", "split", "joint"})
            {
                World();
                bool disjoint = plan != "one";
                var ally = NewKingdom("formal-ally");
                War(ally.Kingdom, enemy, true);
                Alliance(home, ally.Kingdom, true);
                var homeCastle = Site("home-castle-before-allied-town", 40, castle: true);
                var allyTown = Site("high-value-allied-town", 80, ally);
                Camp(homeCastle, Lord("home-castle-besieger", hostile, plan == "joint" ? 600 : 300, 40, true));
                Camp(allyTown, Lord("allied-town-besieger", hostile, 300, 80));
                var west = Lord("near-home-and-only-allied-route", payer, 400, 38, true);
                var east = disjoint ? Lord("home-route-only", payer, 400, 15) : null;
                var westArmy = Ready(west);
                var eastArmy = east == null ? null : Ready(east);
                blockedFixtureRoute = (from, to) => from == 15 && to == 80;
                int before = log.Count;
                Tick(1100);
                Check(Defends(east ?? west, homeCastle) && Defends(west, plan == "split" ? allyTown : homeCastle),
                    plan == "joint" ? "both armies defend the home castle when neither can suffice alone; allied town stays open without a promised army" :
                    disjoint ? "uses the home-only army for the castle and preserves the sole allied route, even though that army is closer to home" :
                        "the only viable army goes to the home castle before the more valuable allied town");
                if (!disjoint) continue;
                Check(OwnsClaim(eastArmy, homeCastle, "home") &&
                    OwnsClaim(westArmy, plan == "split" ? allyTown : homeCastle, plan == "split" ? "allied" : "home") &&
                    DuplicateClaims().Length == 0, "same-tick production registries name two distinct relief formations and no duplicate party");
                Check(log.Skip(before).Any(line => line.Contains("Coordination commitment:") &&
                    line.Contains(plan == "joint" ? "action=joint-response" : "action=single-response") && line.Contains("duplicate=[]")) &&
                    (plan != "joint" || log.Skip(before).Any(line => line.Contains("target=high-value-allied-town") &&
                        line.Contains("committed=0") && line.Contains("committed-higher-priority"))),
                    "split/joint decisions record native formation IDs and an explicit priority blocker when the allied objective stays uncovered");
                if (plan == "joint")
                {
                    Check(log.Skip(before).Any(line => line.Contains("firstETA=+0.4h combinedETA=+5h")),
                        "joint defense reports separate first and combined readiness and issues both orders in the same tick");
                    Reload();
                    Tick(1100.25);
                    Check(OwnsClaim(eastArmy, homeCastle, "home") && OwnsClaim(westArmy, homeCastle, "home") &&
                        DuplicateClaims().Length == 0 && !log.Skip(before).Any(line => line.Contains("actualDebit=50")),
                        "joint defense survives save/load with both original orders and no summons replay");
                    continue;
                }
                ((IList)eastArmy.Parties).Add(west); // only corrupt the audit fixture; native party.Army remains unchanged
                Check(west.Army == westArmy && DuplicateClaims().SequenceEqual(new[]{west}),
                    "cross-job claim audit detects a duplicate roster reference even though native party.Army still points to only one army");
                ((IList)eastArmy.Parties).Remove(west);
                Check(log.Skip(before).Any(line => line.Contains("scope=allied") && line.Contains("committed=400") &&
                    line.Contains("localOwned=0")), "allied transit counts as commitment, not a physical response");

                // Run the installed defend behavior's field branch against
                // this foreign settlement, with only the scene probes stubbed.
                var okay = new HarmonyMethod(typeof(SmokeFixtures).GetMethod(nameof(AcceptFixtureLandPoint), flags));
                var validity = typeof(CampaignVec2).GetMethod("IsValid", flags);
                var nav = typeof(Campaign).Assembly.GetType("Helpers.NavigationHelper", true).GetMethod(
                    "IsPositionValidForNavigationType", flags, null, new[]{typeof(CampaignVec2), typeof(MobileParty.NavigationType)}, null);
                harmony.Patch(validity, prefix: okay);
                harmony.Patch(nav, prefix: okay);
                try
                {
                    Move(allyTown.LastAttackerParty, 94); // distinguish the camp from the settlement gate
                    var args = new object[]{allyTown, AiBehavior.None, default(CampaignVec2), null};
                    typeof(MobilePartyAi).GetMethod("GetDefendSettlementBehavior", flags).Invoke(west.Ai, args);
                    var destination = (CampaignVec2)args[2];
                    Check(west.Army == westArmy && west.MapFaction != allyTown.MapFaction &&
                        (AiBehavior)args[1] == AiBehavior.GoToPoint && (MobileParty)args[3] == allyTown.LastAttackerParty &&
                        destination.IsOnLand && destination.X > 86 && destination.X <= 94,
                        $"installed vanilla defend execution computes a field approach to the besieger at an allied-owned fort " +
                        $"({args[1]}, point={destination.X},{destination.Y}, land={destination.IsOnLand}, camp={args[3] == allyTown.LastAttackerParty})");
                }
                finally
                {
                    harmony.Unpatch(validity, okay.method);
                    harmony.Unpatch(nav, okay.method);
                }
            }

            foreach (bool formal in new[]{false, true})
            {
                World();
                var owner = NewKingdom("unqualified-owner");
                War(owner.Kingdom, enemy, true);
                War(home, enemy, !formal);
                if (formal) Alliance(home, owner.Kingdom, true);
                var town = Site("ineligible-allied-town", 40, owner);
                Camp(town, Lord("enemy", hostile, 500, 40, true));
                var commander = Lord("means-to-respond", payer, 600, 15, true);
                Ready(commander);
                Tick(1100);
                Check(commander.DefaultBehavior == AiBehavior.Hold && Assignments().Count == 0,
                    formal ? "formal alliance without war against the actual camp does not dispatch" :
                        "common war without a formal alliance does not dispatch");
            }

            World();
            var alliedHost = NewKingdom("external-support-owner");
            War(alliedHost.Kingdom, enemy, true);
            Alliance(home, alliedHost.Kingdom, true);
            var supportedTown = Site("shared-support-town", 40, alliedHost);
            Camp(supportedTown, Lord("shared-camp", hostile, 500, 40, true));
            var away = Lord("formal-aid", payer, 400, 5, true);
            var resident = Lord("owner-army", alliedHost, 400, 10, true);
            Ready(away); Ready(resident);
            Tick(1100);
            Check(Defends(away, supportedTown) && Defends(resident, supportedTown), "owner and ally can commit independently to the same native camp");
            Move(away, 42); Move(resident, 43);
            ((IList)alliedHost.Kingdom.WarPartyComponents).Add(resident.WarPartyComponent);
            int supportLog = log.Count;
            Tick(1101);
            Check(log.Skip(supportLog).Count(line => line.Contains("target=shared-support-town") && line.Contains("committed=400") &&
                line.Contains("localOwned=400") && line.Contains("localOther=400") && line.Contains("localOver120=True")) == 2,
                "each kingdom counts the other arrived formation once as external support, not as a second own commitment");

            foreach (bool musterPhase in new[]{false, true})
            foreach (string change in new[]{"alliance", "peace", "owner"})
            {
                World();
                var owner = NewKingdom("original-allied-owner");
                War(owner.Kingdom, enemy, true);
                Alliance(home, owner.Kingdom, true);
                var fort = Site("allied-aid-muster", 0);
                var town = Site("diplomacy-changes", 70, owner);
                Camp(town, Lord("enemy", hostile, 600, 70, true));
                var commander = Lord("ally-leader", payer, 250, -20, true);
                var traveler = musterPhase ? Lord("paid-escort", ClanFor(home, "escort-clan", 0), 190, -35) : null;
                if (!musterPhase) Ready(commander);
                int before = log.Count;
                Tick(1100);
                var original = commander.Army;
                Check(original != null && (musterPhase ? commander.TargetSettlement == fort && traveler.Army == original :
                    Defends(commander, town)), "eligible allied aid starts on the shared muster or native dispatch path");
                Reload();
                Tick(1100.25);
                Check(commander.Army == original && payer.Influence == (musterPhase ? 200 : 250),
                    "allied owner and existing commitment survive reload without another debit");
                var lockBattle = Blank<MapEvent>();
                Property(lockBattle, "Component", Blank<FieldBattleEventComponent>());
                var side = Blank<MapEventSide>();
                Field(side, "_mapEvent", lockBattle);
                Field((traveler ?? commander).Party, "_mapEventSide", side);
                string reason;
                if (change == "alliance")
                {
                    Alliance(home, owner.Kingdom, false);
                    reason = "formal-alliance-not-active";
                }
                else if (change == "peace")
                {
                    War(home, enemy, false);
                    dispatcher.OnMakePeace(home, enemy, MakePeaceAction.MakePeaceDetail.Default);
                    reason = "reliever-not-at-war-with-besieger";
                }
                else
                {
                    var successor = NewKingdom("new-allied-owner");
                    War(successor.Kingdom, enemy, true);
                    // Even another formal ally is a new objective, not the
                    // same order under a silently substituted owner.
                    var records = (IList)typeof(AllianceCampaignBehavior).GetField("_alliances", flags).GetValue(alliances);
                    var record = records.GetType().GetGenericArguments()[0];
                    records.Add(Activator.CreateInstance(record, flags, null, new object[]{home, successor.Kingdom, CampaignTime.Hours(2400)}, null));
                    Field(town.Town, "_ownerClan", successor);
                    ((IList)owner.Kingdom.Fiefs).Remove(town.Town);
                    ((IList)successor.Kingdom.Fiefs).Add(town.Town);
                    dispatcher.OnSettlementOwnerChanged(town, false, null, null, null, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByGift);
                    reason = "settlement-owner-changed";
                }
                Tick(1100.5);
                Check(commander.Army == original && log.Skip(before).Any(line => line.Contains("reason=" + reason) && line.Contains("scope=allied")),
                    "ineligible allied aid closes for " + change + " without disturbing its protected native battle");
                Army concurrent = null;
                Settlement concurrentTarget = null;
                if (change == "alliance" && musterPhase)
                {
                    concurrentTarget = Site("home-order-while-allied-closure-pending", 30);
                    Camp(concurrentTarget, Lord("new-camp-for-home", hostile, 320, 30));
                    concurrent = Ready(Lord("second-formation", ClanFor(home, "second-formation-clan", 300), 500, 24, true));
                    Tick(1100.75);
                    var closing = FindClaim(original);
                    Check(Claim<bool>(closing, "Closing") && Claim<MobileParty[]>(closing, "Protected").Contains(traveler) &&
                        Claim<MobileParty[]>(closing, "Traveling").Contains(traveler) &&
                        OwnsClaim(concurrent, concurrentTarget, "home") && DuplicateClaims().Length == 0,
                        "a battle-held traveler remains reserved by the closing allied assignment while a different army defends home");
                }
                Reload();
                if (concurrent != null)
                    Check(Claim<bool>(FindClaim(original), "Closing") && OwnsClaim(concurrent, concurrentTarget, "home") &&
                        Claim<MobileParty[]>(FindClaim(original), "Protected").Contains(traveler) && DuplicateClaims().Length == 0,
                        "reload preserves closing, protected, and active claims independently without reallocating the old escort");
                if (change == "owner") War(home, enemy, false); // don't immediately start the distinct new owner's job
                Field((traveler ?? commander).Party, "_mapEventSide", null);
                Tick(1101);
                Check(Idle(commander) && (traveler == null || traveler.Army == original) &&
                    log.Skip(before).Count(line => line.Contains("actualDebit=50")) == (musterPhase ? 1 : 0),
                    "ineligible allied relief safely closes after reload without replaying payment (" + change + ")");
            }

            foreach (bool musterPhase in new[]{false, true})
            {
                World();
                var ally = NewKingdom("allied-diversion-owner");
                War(ally.Kingdom, enemy, true);
                Alliance(home, ally.Kingdom, true);
                var fort = Site("allied-diversion-muster", 0);
                var castle = Site("new-home-castle", 40, castle: true);
                var foreignTown = Site("first-allied-town", 80, ally);
                Camp(foreignTown, Lord("foreign-besieger", hostile, 600, 80, true));
                var commander = Lord("allied-diversion-leader", payer, 250, -5, true);
                var traveler = musterPhase ? Lord("already-called", ClanFor(home, "traveler-clan", 0), 190, -20) : null;
                if (!musterPhase) Ready(commander);
                Tick(1100);
                var original = commander.Army;
                Camp(castle, Lord("new-home-besieger", hostile, 600, 40));
                Tick(1100.25);
                if (musterPhase)
                {
                    Enter(commander, fort); Enter(traveler, fort); Tick(1101);
                }
                Check(commander.Army == original && Defends(commander, castle) && foreignTown.SiegeEvent != null &&
                    (traveler == null || (traveler.Army == original && payer.Influence == 200)),
                    "an active or mustering allied-town assignment can yield intact to an earlier-priority home castle");
            }
        }

        void TestOffensiveSieges()
        {
            siegeEnabled = true;
            IDictionary Retries() => (IDictionary)behavior.GetField("_siegeRetry", flags).GetValue(command);
            IDictionary PaidEpisodes() => (IDictionary)behavior.GetField("_paidSiegeFailures", flags).GetValue(command);
            object SiegeState(Army army)
            {
                var assignment = ((IDictionary)behavior.GetField("_sieges", flags).GetValue(command))[army];
                return assignment.GetType().GetField("State", flags).GetValue(assignment);
            }
            T Saved<T>(object state, string name) => (T)state.GetType().GetField(name, flags).GetValue(state);
            void Search(double hour)
            {
                Field(command, "_nextSiegeSearchSlot", 0d);
                ((IDictionary)behavior.GetField("_nextSiegeSearch", flags).GetValue(command)).Clear();
                Tick(hour);
            }
            void NoNewSearch() => Field(command, "_nextSiegeSearchSlot", double.MaxValue);
            bool Besieges(MobileParty party, Settlement target) => party.Army?.AiBehaviorObject == target &&
                party.TargetSettlement == target && party.DefaultBehavior == AiBehavior.BesiegeSettlement;
            void PlaceContact(MobileParty party, float x)
            {
                Move(party, x);
                MobileParty.UpdateLocator(party);
            }
            void JoinCamp(SiegeEvent siege, MobileParty party)
            {
                ((IList)typeof(BesiegerCamp).GetField("_besiegerParties", flags).GetValue(siege.BesiegerCamp)).Add(party);
                Field(party, "_besiegerCamp", siege.BesiegerCamp);
            }
            MapEvent NativeEvent(MobileParty party, Settlement target, MapEventComponent component)
            {
                var battle = Blank<MapEvent>();
                var side = Blank<MapEventSide>();
                Field(side, "_mapEvent", battle);
                var attackerSide = Blank<MapEventSide>();
                Field(attackerSide, "_mapEvent", battle);
                Field(battle, "_sides", new[]{side, attackerSide});
                Property(battle, "Component", component);
                Property(component, "MapEventSettlement", target);
                typeof(MapEventComponent).GetProperty("MapEvent", flags).SetValue(component, battle);
                var roster = new MBList<MapEventParty>();
                var entry = Blank<MapEventParty>();
                Property(entry, "Party", party.Party);
                roster.Add(entry);
                Field(side, "_battleParties", roster);
                Field(party.Party, "_mapEventSide", side);
                return battle;
            }

            World();
            Site("border-home", 8);
            var near = Site("near-castle", 30, hostile, castle: true);
            var far = Site("valuable-distant-town", 90, hostile);
            var attacker = Lord("autonomous-army", payer, 500, 4, true);
            var formation = Ready(attacker);
            Search(1100);
            Check(Besieges(attacker, near) && near.SiegeEvent == null && far.SiegeEvent == null,
                "within the frontier the nearest viable castle beats the higher-value town without creating a remote camp");
            var nativeBesiege = typeof(MobilePartyAi).GetMethod("GetBesiegeBehavior", flags);
            var path = new object[]{AiBehavior.None, default(CampaignVec2), null};
            nativeBesiege.Invoke(attacker.Ai, path);
            Check((AiBehavior)path[0] == AiBehavior.GoToSettlement && (Settlement)path[2] == near,
                "installed siege execution turns the production order into native travel to the gate");
            NoNewSearch();
            Move(attacker, 32);
            var nativeCamp = Camp(near, attacker);
            Tick(1100.25);
            Reload(); NoNewSearch();
            score.Minimum = 900;
            forcePowers[attacker.Party] = 450;
            Site("now-closer-fief", 34, hostile);
            Tick(1100.5);
            Check(attacker.Army == formation && attacker.BesiegerCamp == nativeCamp.BesiegerCamp && Besieges(attacker, near),
                "a sustainable native camp survives reload, losses below the new-siege threshold, and discovery of a closer fief");
            nativeBesiege.Invoke(attacker.Ai, path);
            Check((AiBehavior)path[0] == AiBehavior.Hold, "installed siege execution holds in an unprepared native camp");
            var readiness = typeof(BesiegerCamp).GetMethod("StartingAssaultOnBesiegedSettlementIsLogical", flags);
            var readyPrefix = new HarmonyMethod(typeof(SmokeFixtures).GetMethod(nameof(AcceptFixtureLandPoint), flags));
            harmony.Patch(readiness, prefix: readyPrefix);
            try
            {
                var prep = new SiegeEvent.SiegeEngineConstructionProgress(null, 1f, 100f);
                Property(nativeCamp.BesiegerCamp, "SiegeEngines", new SiegeEvent.SiegeEnginesContainer(BattleSideEnum.Attacker, prep));
                nativeBesiege.Invoke(attacker.Ai, path);
                Check((AiBehavior)path[0] == AiBehavior.AssaultSettlement && (Settlement)path[2] == near,
                    "installed siege execution selects assault from the same order when native readiness is satisfied");
            }
            finally { harmony.Unpatch(readiness, readyPrefix.method); }

            foreach (bool tie in new[]{true, false})
            {
                World();
                Site("route-border", 8);
                var geographicallyNear = Site("straight-line-short-but-detour", 18, hostile, castle: true);
                var fartherTown = Site("native-route-winner", 48, hostile);
                var army = Lord("route-comparison", payer, 600, 8, true);
                Ready(army);
                fixtureRouteDistance = (from, to) => to == geographicallyNear.GatePosition.X ? (tie ? 40 : 85) : Math.Abs(from - to);
                Search(1100);
                Check(Besieges(army, fartherTown), tie ? "equal navigable route distances break toward higher native fief value" :
                    "actual navigable route wins even when straight-line distance suggests the opposite");
            }

            World();
            Site("path-border", 8);
            var protectedTarget = Site("no-route-no-offense", 40, hostile);
            var protectedLeader = Lord("land-route-required", payer, 700, 15, true);
            var protectedFormation = Ready(protectedLeader);
            blockedFixtureRoute = (from, to) => to == 40;
            Search(1100);
            Check(protectedLeader.Army == protectedFormation && !Besieges(protectedLeader, protectedTarget),
                "a strong ready army cannot select an unreachable enemy fortification");
            blockedFixtureRoute = null;
            protectedLeader.LeaderHero.CanJoinArmy = false;
            Search(1100.25);
            Check(!Besieges(protectedLeader, protectedTarget), "an opted-out army leader cannot be redirected into an autonomous siege");

            World();
            var priorityMuster = Site("closer-proposed-muster", 30);
            var priorityTarget = Site("compare-live-formation", 40, hostile);
            var alreadyArmy = Lord("existing-farther-formation", payer, 500, 5);
            Ready(alreadyArmy);
            var prospective = Lord("feasible-fresh-leader", ClanFor(home, "fresh-payer", 250), 500, 27, true);
            Search(1100);
            Check(prospective.Army == null && Besieges(alreadyArmy, priorityTarget),
                "reuse a viable existing army before spending on a closer fresh muster");

            TestTwoFiefFrontier();
            TestDefensiveWarStance();
            TestSiegeOrderTrace();
            TestSiegeRecoveryProgress();
            TestSamePassRebuy();
            TestIdleGrace();
            TestHistoricalLifecycleReplay();
            TestCrossTargetPaidRebuy();
            TestOffensiveDepartureThreshold();
            TestPaidSiegeContinuation();
            TestPaidOffenseRetry();
            TestPaidRecoveryDeparture();
            TestPaidRecoveryEpisode();
            TestPartialPaidAttempt();
            TestSlicedOffense();
            TestLiveSiegeSweepInvalidation();
            TestTransientMuster();
            TestDefenseOnly();
            World();
            var scoreMuster = Site("native-score-muster", 20);
            var strongerTarget = Site("native-score-target", 55, hostile);
            var scorer = Lord("native-score-leader", payer, 400, 20, true);
            payer.Influence = 300;
            var earliest = Lord("early-affordable-call", ClanFor(home, "early-affordable", 0), 250, 22);
            var last = Lord("late-affordable-call", ClanFor(home, "late-affordable", 0), 200, 27);
            score.Minimum = 580;
            int scoreStart = log.Count;
            Search(1100);
            Check(scorer.Army != null && earliest.Army == scorer.Army && last.Army == null && payer.Influence == 250 &&
                log.Skip(scoreStart).Any(line => line.Contains("projected=650 affordablePrefixPower=850")),
                "smallest prefix with headroom uses 650 of 850 quoted power without paying for unused slack");
            score.Minimum = 820; // same attacking force; the defense rose beyond even the paid retention floor
            Enter(scorer, scoreMuster); Enter(earliest, scoreMuster);
            NoNewSearch();
            Tick(1100.25);
            Tick(1100.5);
            Check(Idle(scorer) && strongerTarget.SiegeEvent == null && payer.Influence == 250 && Retries().Count == 0 &&
                log.Skip(scoreStart).Any(line => line.Contains("check=native-strength present=650 projected=650 nativeScore=0")),
                "attachment does not bypass a newly failed native strength check, and a legitimate cancellation spends nothing more");

            void TestDefenseOnly()
            {
                World();
                var border = Site("battania-border", 20);
                var homeA = Site("distant-home-castle", 50, castle: true);
                var homeB = Site("second-home-siege", 75);
                var offensive = Site("offensive-target", 38, hostile, castle: true);
                graph.Link(border, offensive);
                var leader = Lord("offensive-army", payer, 800, 25, true);
                var original = Ready(leader);
                Search(1100);
                Check(Besieges(leader, offensive), "the test army is marching on a lawful offensive order before the home siege");
                fixtureRouteDistance = (from, to) => to == 50 || to == 75 ? 985 : Math.Abs(from - to);
                Camp(homeA, Lord("distant-besieger", hostile, 900, 50, true));
                Camp(homeB, Lord("second-besieger", hostile, 750, 75));
                int mark = log.Count;
                Reload(); NoNewSearch(); Tick(1101);
                Check(leader.Army == null && !home.Armies.Contains(original) &&
                    log.Skip(mark).Any(l => l.Contains("reason=siege:home-siege-defense-only")) &&
                    !log.Skip(mark).Any(l => l.Contains("Relief departure:")),
                    "a saved march to attack is recalled and disbanded when home relief would take 197 hours");
                var spare = Lord("cannot-help-home", payer, 850, 25);
                Ready(spare);
                Lift(homeA);
                Search(1102);
                Check(spare.DefaultBehavior == AiBehavior.Hold && homeB.SiegeEvent != null &&
                    log.Skip(mark).Any(l => l.Contains("blocked=home-siege-defense-only")),
                    "ending one of two home sieges still blocks a fresh offensive order");
                Lift(homeB);
                Search(1111);
                Check(Besieges(spare, offensive), "offensive planning resumes once the last home siege ends");

                World();
                border = Site("paid-muster-at-home", 0);
                offensive = Site("paid-offensive-target", 40, hostile, castle: true);
                graph.Link(border, offensive);
                leader = Lord("paid-offensive-leader", payer, 200, -4, true);
                var escort = Lord("paid-traveler", ClanFor(home, "paid-clan", 200), 200, -20);
                score.Minimum = 350;
                Search(1100);
                original = leader.Army;
                Check(original != null && escort.Army == original && leader.TargetSettlement == border && payer.Influence == 200,
                    "an offensive muster has a paid escort and has not issued a march");
                homeA = Site("new-home-camp", 60);
                Camp(homeA, Lord("home-besieger", hostile, 600, 60, true));
                NoNewSearch(); Tick(1100.25);
                Check(leader.Army == original && escort.Army == original && OwnsClaim(original, homeA, "home", ordered: false) &&
                    payer.Influence == 200 && Claims().Length == 1,
                    "the offense ban redirects a timely paid muster intact without buying its escorts again");
            }

            foreach (bool jointly in new[]{false, true})
            {
                World();
                var left = Site("first-offense", 20, hostile, castle: true);
                var right = Site("second-offense", 80, hostile);
                var castle = Site("uncovered-home-defense", 24, castle: true);
                var a = Lord("offense-west", payer, 500, 18, true);
                var b = Lord("offense-east", payer, 500, 77);
                var armyA = Ready(a);
                var armyB = Ready(b);
                Search(1100); Search(1100.25);
                Check(Besieges(a, left) && Besieges(b, right), "separate production armies have distinct offensive objectives");
                Check(OwnsClaim(armyA, left, "siege") && OwnsClaim(armyB, right, "siege") && DuplicateClaims().Length == 0,
                    "both original siege jobs exist contemporaneously in the production registry");
                Move(a, 20); Move(b, 80);
                Camp(left, a); Camp(right, b);
                Camp(castle, Lord("new-home-besieger", hostile, jointly ? 650 : 350, 24, true));
                NoNewSearch(); Tick(1100.5);
                Check(Defends(a, castle) && left.SiegeEvent == null && right.SiegeEvent == null && Retries().Count == 0 &&
                    (jointly ? Defends(b, castle) : b.Army == null),
                    jointly ? "uncovered home defense diverts both encamped armies when their combined response is needed, without a repulse" :
                        "even with adequate home relief en route, the spare offensive camp is recalled and released");
                Check(OwnsClaim(armyA, castle, "home") && (!jointly || OwnsClaim(armyB, castle, "home")) &&
                    Claims().Length == (jointly ? 2 : 1) && DuplicateClaims().Length == 0,
                    "defense-only leaves no stale offensive claims or duplicate troops");
                Reload(); NoNewSearch();
                Tick(1100.75);
                Check(OwnsClaim(armyA, castle, "home") && (jointly ? OwnsClaim(armyB, castle, "home") : b.Army == null) &&
                    DuplicateClaims().Length == 0, "same-kingdom siege/relief plan and native commitments survive reload with no crossover");
            }

            World();
            var withdrawable = Site("diversion-with-independent-besieger", 20, hostile);
            var protectedOffense = Site("battle-protected-offensive-camp", 78, hostile);
            var threatenedTown = Site("priority-home-town-at-risk", 70);
            var freeToDivert = Lord("ends-only-own-camp-membership", payer, 500, 17, true);
            var inBattle = Lord("locked-offensive-leader", payer, 500, 76);
            var divertedArmy = Ready(freeToDivert);
            var protectedArmy = Ready(inBattle);
            Search(1100); Search(1100.25);
            Move(freeToDivert, 20); Move(inBattle, 78);
            var openCamp = Camp(withdrawable, freeToDivert);
            var lockedCamp = Camp(protectedOffense, inBattle);
            var campGuest = Lord("independent-camp-participant", ClanFor(home, "camp-independent", 0), 75, 20);
            Property(campGuest.LeaderHero, "PartyBelongedTo", campGuest);
            JoinCamp(openCamp, campGuest);
            var lockedAssault = Blank<SiegeAssaultEventComponent>();
            Field(lockedAssault, "_eventType", MapEvent.BattleTypes.Siege);
            var heldEvent = NativeEvent(inBattle, protectedOffense, lockedAssault);
            Camp(threatenedTown, Lord("threatened-town-opponent", hostile, 650, 70, true));
            NoNewSearch(); Tick(1100.5);
            Check(OwnsClaim(divertedArmy, threatenedTown, "home") && OwnsClaim(protectedArmy, protectedOffense, "siege") &&
                Claim<MobileParty[]>(FindClaim(protectedArmy), "Protected").Contains(inBattle) && inBattle.MapEvent == heldEvent &&
                withdrawable.SiegeEvent == openCamp && openCamp.BesiegerCamp.LeaderParty == campGuest && campGuest.Army == null &&
                !Claims().Cast<object>().Any(row => Claim<MobileParty[]>(row, "Members").Contains(campGuest)) && DuplicateClaims().Length == 0,
                "defense sends the available army understrength, never extracts an ongoing assault, and leaves independent camp participants unclaimed");
            Field(inBattle.Party, "_mapEventSide", null);
            dispatcher.OnMapEventEnded(heldEvent);
            Tick(1100.75);
            Check(OwnsClaim(protectedArmy, threatenedTown, "home") && protectedOffense.SiegeEvent != lockedCamp &&
                withdrawable.SiegeEvent == openCamp && Retries().Count == 0 && DuplicateClaims().Length == 0,
                "the second formation can join the home response after native protection ends; no repulse or duplicate commitment");

            World();
            var failingLeft = Site("cannot-directly-relieve-from-west", 20, hostile);
            var failingRight = Site("cannot-directly-relieve-from-east", 90, hostile);
            var intermediate = Site("native-replacement-muster", 35);
            var defended = Site("only-replacement-route-works", 65);
            var west = Lord("west-original-leader", payer, 430, 19, true);
            var east = Lord("east-original-leader", ClanFor(home, "east-clan", 250), 270, 89, true);
            var westArmy = Ready(west);
            var eastArmy = Ready(east);
            Search(1100); Search(1100.25);
            Move(west, 20); Move(east, 90);
            Camp(failingLeft, west); Camp(failingRight, east);
            blockedFixtureRoute = (from, to) => (from == 20 || from == 90) && to == 65;
            Camp(defended, Lord("home-threat-for-replacement", hostile, 550, 65, true));
            NoNewSearch(); Tick(1100.5);
            Check(west.Army != null && west.Army != westArmy && west.Army != eastArmy && west.Army == east.Army &&
                west.TargetSettlement == intermediate && west.Position.X == 20 && east.Position.X == 90 &&
                failingLeft.SiegeEvent == null && failingRight.SiegeEvent == null && Retries().Count == 0,
                "defense can break up unroutable siege armies and muster a viable native replacement without teleporting");
            var replacementArmy = west.Army;
            var replacementClaim = FindClaim(replacementArmy);
            Check(Claims().Length == 1 && OwnsClaim(replacementArmy, defended, "home", ordered: false) &&
                Claim<MobileParty[]>(replacementClaim, "Present").SequenceEqual(new[]{west}) &&
                Claim<MobileParty[]>(replacementClaim, "Traveling").SequenceEqual(new[]{east}) && payer.Influence == 200,
                "replacement holds only one claim; the distant paid escort is promised exactly once but not present");
            Reload(); NoNewSearch();
            Tick(1100.75);
            Check(OwnsClaim(replacementArmy, defended, "home", ordered: false) && DuplicateClaims().Length == 0 &&
                Claim<MobileParty[]>(FindClaim(replacementArmy), "Traveling").SequenceEqual(new[]{east}) && payer.Influence == 200,
                "replacement transit survives reload with one claim and no repeated escort payment");
            Enter(west, intermediate); Enter(east, intermediate);
            Tick(1101);
            Check(OwnsClaim(replacementArmy, defended, "home") && Claim<MobileParty[]>(FindClaim(replacementArmy), "Present").Length == 2 &&
                Claim<MobileParty[]>(FindClaim(replacementArmy), "Traveling").Length == 0 && payer.Influence == 200,
                "actual arrival switches traveling to attached and turns the same replacement commitment into a native defense order");

            foreach (bool allied in new[]{false, true})
            {
                World();
                var fort = Site("tied-muster", 0);
                var target = Site("affordable-offense", 40, hostile);
                var commander = Lord("affordable-leader", payer, 200, -4, true);
                var traveler = Lord("affordable-escort", ClanFor(home, "escort-payer", 200), 200, -20);
                score.Minimum = 350;
                price.Cost = 51;
                Search(1100);
                Check(commander.Army == null && traveler.Army == null && payer.Influence == 250,
                    "a summons costing 51 at 250 influence cannot form a viable offensive army");
                price.Cost = 50;
                Search(1100.25);
                Check(commander.Army != null && commander.Army == traveler.Army && payer.Influence == 200 &&
                    commander.TargetSettlement == fort && target.SiegeEvent == null, "affordable offensive muster pays exactly once and waits for real arrivals");
                var same = commander.Army;
                Reload(); NoNewSearch();
                Tick(1100.5);
                Check(commander.TargetSettlement == fort && traveler.Army == same && payer.Influence == 200,
                    "reload retains a paid offense muster without treating a summons as present or replaying its debit");
                Settlement defense = null;
                if (allied)
                {
                    var ally = NewKingdom("formal-defense-priority");
                    War(ally.Kingdom, enemy, true); Alliance(home, ally.Kingdom, true);
                    defense = Site("allied-defense-before-offense", 60, ally);
                    Camp(defense, Lord("new-allied-besieger", hostile, 400, 60, true));
                    Tick(1100.75);
                }
                Enter(commander, fort); Enter(traveler, fort);
                Tick(1101);
                Check(commander.Army == same && payer.Influence == 200 && target.SiegeEvent == null && PaidEpisodes().Count == 0 &&
                    (allied ? Defends(commander, defense) : Besieges(commander, target)),
                    allied ? "formal allied defense diverts the paid offensive muster intact before departure" :
                        "present offensive muster departs on native besiege order without forming camp or replaying payment");
                if (!allied)
                {
                    War(home, enemy, false);
                    dispatcher.OnMakePeace(home, enemy, MakePeaceAction.MakePeaceDetail.Default);
                    Tick(1101.25);
                    Check(Idle(commander) && traveler.Army == same && Retries().Count == 0 && PaidEpisodes().Count == 0,
                        "peace ends an offensive march safely, with no cooldown or new declaration");
                }
            }

            foreach (string reason in new[]{"local-threat", "remaining-camp", "starvation", "changed-owner", "native-relief-battle"})
            {
                World();
                Site("repulse-border", 10);
                var target = Site("repulse-target", 40, hostile);
                var leader = Lord("camp-observer", payer, 500, 10, true);
                var original = Ready(leader);
                Search(1100); NoNewSearch();
                Move(leader, 46); // center deliberately differs from the gate
                var siege = Camp(target, leader);
                var threat = Lord("visible-hostile-root", hostile, 300, 52, true);
                var wing = Lord("attached-hostile-wing", hostile, 250, 52);
                var independent = Lord("independent-hostile", hostile, 50, 52);
                var locked = Lord("unrelated-battle-held", hostile, 1000, 47);
                var interior = Lord("behind-the-walls", hostile, 1000, 40);
                Enter(interior, target);
                Field(wing, "_attachedTo", threat);
                ((IList)threat.AttachedParties).Add(wing);
                PlaceContact(threat, 55); PlaceContact(wing, 55); PlaceContact(independent, 55);
                PlaceContact(locked, 47); MobileParty.UpdateLocator(interior);
                NativeEvent(locked, null, Blank<FieldBattleEventComponent>());
                Tick(1100.25);
                if (reason == "native-relief-battle")
                {
                    var component = Blank<SiegeOutsideEventComponent>();
                    Field(component, "_eventType", MapEvent.BattleTypes.SiegeOutside);
                    var battle = NativeEvent(leader, target, component);
                    Field(battle, "_battleState", BattleState.AttackerVictory);
                    dispatcher.OnMapEventEnded(battle);
                    Check(Retries().Count == 0 && target.SiegeEvent == siege, "native relief victory alone does not start cooldown before camp actually breaks");
                    Field(leader.Party, "_mapEventSide", null);
                    Time(1100.75);
                    siege.BesiegerCamp.RemoveAllSiegeParties();
                    Check((double)Retries()[home.StringId + "/" + target.StringId] == 1268.75,
                        "actual siege-end callback after native outside defeat records seven days from the break, not battle sighting");
                    continue;
                }
                if (reason == "changed-owner")
                {
                    var other = NewKingdom("another-hostile-owner");
                    War(home, other.Kingdom, true);
                    Field(target.Town, "_ownerClan", other);
                    dispatcher.OnSettlementOwnerChanged(target, false, null, null, null, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByGift);
                    Tick(1100.5);
                    Check(Idle(leader) && target.SiegeEvent == null && Retries().Count == 0,
                        "even another enemy owner invalidates the old offensive commitment without a cooldown");
                    continue;
                }
                if (reason == "starvation")
                {
                    Field(leader.Party, "_remainingFoodPercentage", -1);
                    Tick(1100.5);
                    Check(leader.Army == null && target.SiegeEvent == null && Retries().Count == 0,
                        "actual starvation can lift a camp but does not count as being repulsed");
                    continue;
                }
                var ownIndependent = reason == "remaining-camp" ? Lord("separate-friendly-besieger", ClanFor(home, "unmanaged-camp", 0), 50, 46) : null;
                if (ownIndependent != null)
                {
                    Property(ownIndependent.LeaderHero, "PartyBelongedTo", ownIndependent);
                    JoinCamp(siege, ownIndependent);
                }
                Tick(1100.5);
                Check(leader.Army == original && target.SiegeEvent == siege, "out-of-radius, interior, and battle-held hostiles cannot lift a siege");
                PlaceContact(threat, 52); PlaceContact(wing, 52); PlaceContact(independent, 52);
                sight.Sight = 5;
                forcePowers[independent.Party] = 121;
                Tick(1100.75);
                Check(target.SiegeEvent == siege, "contact inside the camp radius but outside the camp's own visibility does not count");
                sight.Sight = 20;
                forcePowers[independent.Party] = reason == "remaining-camp" ? 110 : 50;
                Tick(1101);
                Check(target.SiegeEvent == siege, "exactly 120% counts attached and independent troops once but does not force withdrawal");
                forcePowers[independent.Party]++;
                NativeEvent(leader, target, Blank<FieldBattleEventComponent>());
                Tick(1101.25);
                Check(target.SiegeEvent == siege && leader.Army == original, "a real native battle lock prevents withdrawal even over 120%");
                Field(leader.Party, "_mapEventSide", null);
                Tick(1101.5);
                if (ownIndependent != null)
                {
                    Check(Idle(leader) && target.SiegeEvent == siege && ownIndependent.BesiegerCamp == siege.BesiegerCamp &&
                        siege.BesiegerCamp.LeaderParty == ownIndependent && Retries().Count == 0,
                        "withdrawal removes only the commanded formation; an independent camp survives and no cooldown starts");
                    continue;
                }
                string key = home.StringId + "/" + target.StringId;
                Check(Idle(leader) && target.SiegeEvent == null && (double)Retries()[key] == 1269.5,
                    "visible 601 against 500 after the lock ends lifts the camp and records the exact seven-day expiry once");
                var replacement = Lord("different-army-after-repulse", payer, 500, 42, true);
                Ready(replacement);
                PlaceContact(threat, 90); PlaceContact(wing, 90); PlaceContact(independent, 90);
                Search(1245.5);
                Check(!Besieges(replacement, target) && (double)Retries()[key] == 1269.5, "a different army is rejected on day six despite the threat leaving");
                Reload();
                Search(1269.25);
                Check(!Besieges(replacement, target) && (double)Retries()[key] == 1269.5, "reload retains the original expiry and rejects a pre-boundary retry");
                PlaceContact(threat, 52); PlaceContact(wing, 52); PlaceContact(independent, 52);
                Search(1269.5);
                Check(!Besieges(replacement, target),
                    "seven full days do not authorize retry against a currently visible force at the former camp, outside the gate radius");
                PlaceContact(threat, 90); PlaceContact(wing, 90); PlaceContact(independent, 90);
                score.Minimum = 626;
                Search(1269.75);
                Check(!Besieges(replacement, target), "expiry and cleared contacts do not bypass ordinary new-siege strength checks");
                score.Minimum = 200;
                Search(1270);
                Check(Besieges(replacement, target) && target.SiegeEvent == null,
                    "once the threat is no longer observed an eligible different army makes a fresh native departure");
            }

            foreach (bool validNeighbor in new[]{false, true})
            {
                World();
                Site("capture-border", 5);
                var target = Site("captured-objective", 30, hostile);
                var other = Site("near-non-neighbor", 34, hostile, castle: true);
                var neighbor = Site("one-ring-enemy", 55, hostile);
                graph.Link(target, neighbor);
                var leader = Lord("capturing-army", payer, 500, 5, true);
                var original = Ready(leader);
                Search(1100); NoNewSearch();
                Move(leader, 30);
                Camp(target, leader);
                Tick(1100.25);
                if (!validNeighbor)
                    Retries()[home.StringId + "/" + neighbor.StringId] = 1268d;
                Field(target.Town, "_ownerClan", payer);
                Lift(target);
                dispatcher.OnSettlementOwnerChanged(target, false, null, null, null, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege);
                Tick(1100.5);
                Check(validNeighbor ? leader.Army == original && Besieges(leader, neighbor) : Idle(leader) && other.SiegeEvent == null,
                    validNeighbor ? "capture reuses only the viable one-ring neighbor, not a much nearer non-neighbor" :
                        "a repulsed one-ring follow-on is vetoed and the completed formation releases");
            }
            World();
            var savedTown = Site("successfully-relieved-fort", 20);
            var adjacent = Site("relief-follow-on", 45, hostile);
            var responder = Lord("ready-relief", payer, 650, 12, true);
            var response = Ready(responder);
            Camp(savedTown, Lord("enemy-camp", hostile, 400, 20, true));
            NoNewSearch(); Tick(1100);
            Move(responder, 21);
            Tick(1100.25);
            Lift(savedTown);
            Tick(1100.5);
            Check(responder.Army == response && Besieges(responder, adjacent) && Assignments().Count == 0,
                "a physically responding relief army can transfer to a viable neighboring siege, without double commitment");

            if (acceptance != null)
            {
                TestObserver(observeAssault: true);
                TestObserver(observeAssault: false);
                foreach (string caseName in new[]{"split", "joint", "diversion", "joint-diversion", "non-overlapping", "different-kingdom", "offense-only"})
                    TestCoordinationObserver(caseName);
                TestCoordinationObserver("understrength-fresh");
                TestCoordinationObserver("understrength-lost-route");
                TestCoordinationObserver("understrength-transfer");
            }
            siegeEnabled = false;

            void TestDefensiveWarStance()
            {
                World();
                var fort = Site("stance-muster", 20);
                var goal = Site("stance-first-enemy", 40, hostile);
                var other = NewKingdom("stance-other-enemy");
                var farther = Site("stance-second-enemy", 70, other);
                var leader = Lord("stance-field-leader", payer, 400, 20, true);
                var escort = Lord("stance-escort", ClanFor(home, "stance-escort-clan", 0), 300, 21);
                var second = Lord("stance-ready", payer, 720, 25);
                second.LeaderHero.CanJoinArmy = false;
                home.RulingClan = Clan.PlayerClan;
                Field(home.RulingClan, "_leader", player.LeaderHero);
                home.Id = new TaleWorlds.ObjectSystem.MBGUID(1);
                enemy.Id = new TaleWorlds.ObjectSystem.MBGUID(2);
                other.Kingdom.Id = new TaleWorlds.ObjectSystem.MBGUID(3);
                var previousManager = campaign.FactionManager;
                Property(campaign, "FactionManager", new FactionManager());
                // Fixture diplomacy models war without storing links; seed the same native
                // link the diplomacy selector writes, including its shared reverse lookup.
                var link = home.GetStanceWith(enemy);
                var stances = campaign.FactionManager.GetType().GetField("_stances", flags).GetValue(campaign.FactionManager);
                stances.GetType().GetMethod("AddStance", flags).Invoke(stances, new object[]{link});
                var game = Blank<Game>();
                var character = Blank<CharacterObject>();
                Property(character, "HeroObject", player.LeaderHero);
                game.PlayerTroop = character;
                gameFrame.SetValue(null, game);
                try
                {
                    score.Minimum = 600;
                    int start = log.Count;
                    CampaignTick(1100);
                    for (int i = 0; i < 100 && behavior.GetField("_offensiveCommit", flags).GetValue(command) == null; ++i)
                        ApplicationFrame(now);
                    Check(behavior.GetField("_offensiveCommit", flags).GetValue(command) != null && leader.Army == null,
                        "Balanced permits an affordable siege proposal");
                    link.BehaviorPriority = 1;
                    ApplicationFrame(now); Search(1101);
                    Check(leader.Army == null && escort.Army == null && payer.Influence == 250 &&
                        log.Skip(start).Any(l => l.Contains("defensive-war-stance")), "Defensive drops the pending proposal before payment and excludes that front on fresh searches");
                    var check = mod.GetType("BannerlordMilitaryAIOverhaul.Command.SiegeWarStance", true).GetMethod("BlocksNewTarget", flags);
                    bool Blocks(Kingdom actor, Kingdom foe) => (bool)check.Invoke(null, new object[]{actor, foe});
                    Check(enemy.GetStanceWith(home) == link && !Blocks(enemy, home), "the same native stance link never orders the opponent onto the defensive");
                    var cai = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "BannerlordCAIOverhaul");
                    if (cai != null)
                    {
                        // Only seed a campaign posture; read it through the installed CAI public API.
                        var host = cai.GetType("BannerlordCAIOverhaul.Host.HarnessRuntime", true);
                        var harness = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                            cai.GetType("BannerlordCAIOverhaul.Host.HarnessCampaignBehavior", true));
                        var root = Activator.CreateInstance(cai.GetType("BannerlordCAIOverhaul.Harness.HarnessRoot", true));
                        var defensive = Enum.Parse(cai.GetType("BannerlordCAIOverhaul.Harness.WarPosture", true), "Defensive");
                        Field(harness, "_root", root); Field(harness, "_loaded", true); Field(harness, "_diplomacyOn", true);
                        cai.GetType("BannerlordCAIOverhaul.Harness.WarPostures", true).GetMethod("Set", flags).Invoke(null,
                            new object[]{root, enemy.StringId, home.StringId, (long)link.WarStartDate.ToMilliseconds, defensive});
                        host.GetMethod("Install", flags).Invoke(null, new[]{harness});
                        try
                        {
                            link.BehaviorPriority = 0;
                            Check(Blocks(enemy, home) && !Blocks(home, enemy) && !Blocks(enemy, other.Kingdom),
                                "the installed CAI's Defensive directive applies only to the issuing AI kingdom against its named opponent");
                        }
                        finally { host.GetMethod("Uninstall", flags).Invoke(null, new[]{harness}); }
                    }
                    link.BehaviorPriority = 2;
                    Search(1102);
                    var army = leader.Army;
                    Check(army != null && escort.Army == army && OwnsClaim(army, goal, "siege", false) && payer.Influence == 200,
                        "Offensive reopens the target and pays only once");
                    link.BehaviorPriority = 1;
                    Reload(); NoNewSearch(); Enter(leader, fort); Tick(1103);
                    Check(Besieges(leader, goal) && leader.Army == army && payer.Influence == 200,
                        "an already assigned muster survives Defensive and reload and departs for its original target");
                    Move(leader, 38); Camp(goal, leader); Tick(1104);
                    War(home, other.Kingdom, true);
                    second.LeaderHero.CanJoinArmy = true;
                    Ready(second); Search(1105);
                    Check(Besieges(second, farther) && leader.BesiegerCamp == goal.SiegeEvent.BesiegerCamp,
                        "a second front stays eligible and the existing siege camp remains active");
                }
                finally
                {
                    Property(campaign, "FactionManager", previousManager);
                    gameFrame.SetValue(null, priorGame);
                }
            }

            void TestTwoFiefFrontier()
            {
                // Deliberately put the third-link target closest to the army and the
                // muster by road. A viable paid roster exists even when no siege is legal.
                foreach (string phase in new[]{ "quote", "muster", "march", "camp" })
                {
                    World();
                    var ally = NewKingdom("frontier-ally");
                    Alliance(home, ally.Kingdom, true);
                    var baseFort = Site("home-muster", 15);
                    var foothold = phase == "quote" ? Site("forward-holding", 400) : baseFort;
                    var first = Site("first-enemy-fort", 90, hostile);
                    var second = Site("second-enemy-fort", 80, hostile);
                    var deep = Site("danustica", 16, hostile);
                    var alliedFort = Site("allied-anchor-only", 17, ally);
                    graph.Links.Clear();
                    if (baseFort != foothold) graph.Link(baseFort, foothold);
                    graph.Link(foothold, first); graph.Link(first, second); graph.Link(second, deep);
                    graph.Link(alliedFort, deep);
                    var leader = Lord("frontier-leader", payer, 400, 15, true);
                    var escort = Lord("frontier-traveler", ClanFor(home, "frontier-escort", 0), 300, 20);
                    payer.Influence = 300;
                    score.OverrideMinimum = t => t == deep ? 550 : 1000;
                    int mark = log.Count;
                    Search(1100);
                    Check(leader.Army == null && escort.Army == null && payer.Influence == 300 &&
                        log.Skip(mark).Any(l => l.Contains("outside-two-fief-frontier")),
                        "no purchase for a third-link town even next to the prospective army and a formal ally's castle");
                    score.OverrideMinimum = t => t == first ? 1000 : 550;
                    if (phase == "quote")
                    {
                        Field(command, "_nextSiegeSearchSlot", 0d);
                        ((IDictionary)behavior.GetField("_nextSiegeSearch", flags).GetValue(command)).Clear();
                        CampaignTick(1101);
                        for (int i = 0; i < 100 && behavior.GetField("_offensiveCommit", flags).GetValue(command) == null; ++i)
                            ApplicationFrame(now);
                        Check(behavior.GetField("_offensiveCommit", flags).GetValue(command) != null && leader.Army == null,
                            "two-link target is quoted before summoning");
                        // Leave the muster legal; change only the ownership of the
                        // foothold that put this target in reach.
                        Transfer(foothold, ally);
                        ApplicationFrame(now);
                        Check(leader.Army == null && escort.Army == null && payer.Influence == 300,
                            "a proposal that loses its frontier before commit cannot form or charge");
                        continue;
                    }
                    Search(1101);
                    var army = leader.Army;
                    Check(army != null && OwnsClaim(army, second, "siege", false) && escort.Army == army && payer.Influence == 250 &&
                        log.Skip(mark).Any(l => l.Contains("Siege selected:") && l.Contains("frontierAnchor=home-muster frontierLinks=2")),
                        "the second fief is eligible through an uncaptured enemy first fief; the closer third fief loses");
                    NoNewSearch();
                    if (phase != "muster")
                    {
                        Enter(leader, baseFort); Tick(1101.25);
                        Check(Besieges(leader, second), "second-ring army departs with the paid escort on its roster");
                        Move(leader, 78);
                    }
                    if (phase == "camp") Camp(second, leader);
                    Transfer(baseFort, ally);
                    Reload(); NoNewSearch();
                    if (phase == "march")
                        NativeEvent(leader, null, Blank<FieldBattleEventComponent>());
                    Tick(1102);
                    if (phase == "camp")
                    {
                        Check(leader.BesiegerCamp == second.SiegeEvent?.BesiegerCamp && Besieges(leader, second),
                            "reload preserves an established native camp beyond the new border");
                        continue;
                    }
                    if (phase == "march")
                    {
                        Check(Besieges(leader, second), "frontier loss cannot interrupt a native battle during the march");
                        Field(leader.Party, "_mapEventSide", null);
                        Tick(1102.5);
                    }
                    Check(Idle(leader) && leader.Army == army && escort.Army == army && payer.Influence == 250 &&
                        PaidEpisodes().Count == 0 && Retries().Count == 0 &&
                        log.Skip(mark).Any(l => l.Contains("Siege closes:") && l.Contains("reason=outside-two-fief-frontier")),
                        $"out-of-range saved {phase} releases its objective into idle grace without a retry penalty");
                    if (phase == "march")
                    {
                        // Even right at the former target, this landless kingdom gets no
                        // attack in the final idle review.
                        Tick(1127);
                        Check(leader.Army == null && escort.Army == null, "idle expiry cannot reuse a landless army for a distant siege");
                    }
                    else
                    {
                        Transfer(first, payer);
                        Search(1103);
                        Check(Besieges(leader, deep) && leader.Army == army && payer.Influence == 250,
                            "gaining the first fief opens the former third fief and the idle army reuses its roster");
                    }
                }
                void Transfer(Settlement site, Clan to)
                {
                    ((IList)((Kingdom)site.MapFaction).Fiefs).Remove(site.Town);
                    Field(site.Town, "_ownerClan", to);
                    ((IList)to.Kingdom.Fiefs).Add(site.Town);
                    dispatcher.OnSettlementOwnerChanged(site, false, null, null, null, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByGift);
                }
            }

            void TestObserver(bool observeAssault)
            {
                World();
                Site("observer-border", 10);
                var target = Site("observed-autonomous-siege", 40, hostile);
                var distractor = Site("external-camp-not-accepted", 70, hostile);
                var outsider = Lord("external-camp-leader", payer, 550, 70);
                var priorArmy = Ready(outsider);
                SetPartyAiAction.GetActionForBesiegingSettlement(outsider, distractor, MobileParty.NavigationType.Default, false);
                Camp(distractor, outsider);
                var leader = Lord("production-attacker", payer, 450, 10, true);
                var own = Ready(leader);
                var temp = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maio-siege-" + Guid.NewGuid().ToString("N")));
                string id = Guid.NewGuid().ToString("N");
                string Receipt(string kind) => System.IO.Path.Combine(temp.FullName, $"acceptance-siege-v1.{id}.{kind}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(temp.FullName, "acceptance-siege-v1.armed"), "siege-v1 " + id);
                object Runner() => Activator.CreateInstance(acceptance, flags, null, new object[]{command, sessionLog, temp.FullName}, null);
                var runner = Runner();
                object Run(string method, params object[] args) => acceptance.GetMethod(method, flags).Invoke(runner, args);
                object Read(string field) => acceptance.GetField(field, flags).GetValue(runner);
                long wall = DateTime.UtcNow.Ticks;
                int popups = 0;
                void Popup(InquiryData data, bool pause, bool priority) { if (data.Text.Contains(id)) popups++; }
                InformationManager.OnShowInquiry += Popup;
                try
                {
                    Run("OnLoaded");
                    Run("Tick", 1100d, wall);
                    Check(Read("_original") == null, "the siege observer ignores pre-existing external orders and camps");
                    Search(1100); NoNewSearch();
                    Run("Tick", 1100d, wall);
                    Check((bool)Read("_handoff") && !(bool)Read("_campContact") && Read("_result") == null &&
                        Besieges(leader, target) && target.SiegeEvent == null && System.IO.File.Exists(Receipt("claimed")),
                        "auto-armed observer records production departure, but a native siege order alone cannot pass");
                    Move(leader, 40);
                    Camp(target, leader);
                    typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(leader, new object[]{AiBehavior.AssaultSettlement, null});
                    Tick(1100.25);
                    Run("Tick", 1100.25d, wall);
                    Check((bool)Read("_campContact") && Read("_result") == null && (Army)Read("_original") == own,
                        "same-army camp membership and native assault selection without battle are not a pass");
                    var data = new Store { IsSaving = true };
                    ((CampaignBehaviorBase)runner).SyncData(data);
                    if (observeAssault)
                    {
                        var assault = Blank<SiegeAssaultEventComponent>();
                        Field(assault, "_eventType", MapEvent.BattleTypes.Siege);
                        var battle = NativeEvent(leader, target, assault);
                        dispatcher.OnMapEventStarted(battle, leader.Party, target.Party);
                        Tick(1100.5);
                        Run("Tick", 1100.5d, wall);
                    }
                    else
                        Run("Tick", 1460d, wall);
                    string result = (string)Read("_result");
                    Check(result.Contains(observeAssault ? "began a native assault" : "but no native assault") &&
                        Besieges(leader, target) && target.SiegeEvent != null && outsider.Army == priorArmy &&
                        result.Contains("production orders left intact"),
                        observeAssault ? "observer passes only on real production order, native camp, and matching native assault evidence; no cleanup" :
                            "the bounded observer reports a camp without assault as inconclusive and leaves both camps intact");
                    Run("Notify");
                    data.IsSaving = false;
                    runner = Runner();
                    ((CampaignBehaviorBase)runner).SyncData(data);
                    Run("OnLoaded");
                    Run("Notify");
                    Check(popups == 1 && System.IO.File.Exists(Receipt("shown")), "siege result and single popup survive a stale observer save reload");
                }
                finally
                {
                    InformationManager.OnShowInquiry -= Popup;
                    foreach (var file in temp.GetFiles()) file.Delete();
                    temp.Delete();
                }
            }

            void TestSiegeOrderTrace()
            {
                World();
                var muster = Site("hold-trace-muster", 20);
                var target = Site("hold-trace-target", 60, hostile);
                var leader = Lord("hold-trace-leader", payer, 400, 18, true);
                var escort = Lord("hold-trace-escort", ClanFor(home, "hold-trace-escort", 0), 300, 24);
                score.Minimum = 600;
                Search(1100); NoNewSearch();
                var own = leader.Army;
                var trace = mod.GetType("BannerlordMilitaryAIOverhaul.Diagnostics.SiegeOrderTrace", true);
                var directory = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "maio-hold-smoke-" + Guid.NewGuid().ToString("N")));
                var marker = System.IO.Path.Combine(directory.FullName, "trace-v1.armed");
                var native = typeof(MobileParty).GetMethod("SetMoveModeHold", Type.EmptyTypes);
                bool Arm() => (bool)trace.GetMethod("TryClaim", flags).Invoke(null, new object[]{directory.FullName, command, sessionLog});
                int start = log.Count;
                try
                {
                    Check(!Arm() && Harmony.GetPatchInfo(native)?.Owners.Contains("BannerlordMilitaryAIOverhaul.SiegeOrderTrace") != true,
                        "an unarmed siege trace installs no Hold patch");
                    string id = Guid.NewGuid().ToString("N");
                    System.IO.File.WriteAllText(marker, $"siege-order-v1 {id}");
                    Check(Arm() && !System.IO.File.Exists(marker) &&
                        Harmony.GetPatchInfo(native)?.Owners.Contains("BannerlordMilitaryAIOverhaul.SiegeOrderTrace") == true,
                        "the opt-in trace claims one marker and observes the exact native Hold method");
                    ClearOrderForTrace(player);
                    ClearOrderForTrace(escort);
                    Check(!log.Skip(start).Any(line => line.Contains($"run={id} hold ")),
                        "the trace ignores the player and nonleader parties");
                    escort.SetMoveEscortParty(leader, MobileParty.NavigationType.Default, false);
                    ClearOrderForTrace(leader);
                    Check(leader.DefaultBehavior == AiBehavior.Hold && leader.TargetSettlement == null &&
                        own.AiBehaviorObject == muster && log.Skip(start).Any(line => line.Contains("phase=muster") &&
                            line.Contains("ClearOrderForTrace") && line.Contains("after=[default=Hold")),
                        "the native Hold writer and before/after movement are captured without vetoing the clear");
                    Tick(1100.25);
                    Check(leader.TargetSettlement == muster && log.Skip(start).Any(line => line.Contains("phase=muster") &&
                        line.Contains("decision=reissue-attempted") && line.Contains("orderRestored=True")),
                        "muster review records the original reissue and verifies the resulting native order");

                    Enter(leader, muster); Enter(escort, muster);
                    Tick(1101);
                    Check(Besieges(leader, target), "traced muster can still depart normally");
                    for (int i = 0; i < 3; i++)
                    {
                        Time(1102 + 2 * i);
                        ClearOrderForTrace(leader);
                        Tick(now + 0.25);
                    }
                    var notes = log.Skip(start).Where(line => line.Contains($"run={id}")).ToArray();
                    Check(notes.Count(line => line.Contains(" hold ") && line.Contains("phase=march")) == 3 &&
                        notes.Count(line => line.Contains("phase=march") && line.Contains("decision=reissue-attempted") &&
                            line.Contains("orderRestored=True")) == 2 &&
                        notes.Any(line => line.Contains("phase=march") && line.Contains("decision=reissue-budget-exhausted")) &&
                        Idle(leader) && escort.Army == leader.Army && payer.Influence == 200,
                        "read-only march tracing links all three stationary clears to the two-reissue limit and terminal decision");
                    Tick(1869);
                    Check(log.Skip(start).Count(line => line.Contains($"run={id} finished reason=campaign-window-ended")) == 1,
                        "the one-shot observer stops at its campaign-time bound");
                }
                finally
                {
                    trace.GetMethod("Close", flags).Invoke(null, null);
                    foreach (var file in directory.GetFiles()) file.Delete();
                    directory.Delete();
                }
                Check(Harmony.GetPatchInfo(native)?.Owners.Contains("BannerlordMilitaryAIOverhaul.SiegeOrderTrace") != true,
                    "ending the campaign removes only the optional Hold observer");
            }

            void TestSiegeRecoveryProgress()
            {
                foreach (bool march in new[] { false, true })
                {
                    foreach (string outcome in new[] { "progress", "backtrack", "stationary" })
                    {
                        World();
                        var muster = Site("recovery-progress-muster", 20);
                        var target = Site("recovery-progress-target", 60, hostile);
                        var leader = Lord("recovery-progress-leader", payer, 400, 5, true);
                        var escort = Lord("recovery-progress-escort", ClanFor(home, "recovery-progress-escort", 0), 300, 7);
                        score.Minimum = 600;
                        Search(1100); NoNewSearch();
                        var army = leader.Army;
                        if (march)
                        {
                            Enter(leader, muster); Enter(escort, muster);
                            Tick(1100.25);
                            Check(Besieges(leader, target), "the recovery fixture starts an actual siege march");
                        }
                        string phase = march ? "march" : "muster";
                        float from = march ? 22 : 6;
                        var waypoint = march ? target : muster;
                        bool HasOrder() => march ? Besieges(leader, target) :
                            leader.Army == army && leader.TargetSettlement == muster && leader.DefaultBehavior == AiBehavior.GoToSettlement;
                        void Interrupt(double hour)
                        {
                            Time(hour);
                            ClearOrderForTrace(leader);
                            Tick(hour + 0.25);
                        }
                        Move(leader, from);
                        Interrupt(1102);
                        var state = SiegeState(army);
                        double checkpoint = Saved<double>(state, "ReissueBestDistance");
                        Check(HasOrder() && Saved<bool>(state, "ReissueOrderConfirmed") &&
                            Saved<int>(state, "Reissues") == 1 && checkpoint > 0,
                            $"{phase} recovery starts with a verified native order and real route checkpoint");

                        if (outcome == "stationary")
                        {
                            // Route estimates can change without the army moving.
                            fixtureRouteDistance = (a, b) => a == from && b == waypoint.GatePosition.X ?
                                checkpoint - 5 : Math.Abs(a - b);
                            Tick(1104);
                            Reload(); NoNewSearch();
                            Check(HasOrder() && Saved<int>(SiegeState(army), "Reissues") == 1 &&
                                Saved<double>(SiegeState(army), "ReissueBestDistance") == checkpoint,
                                $"a shorter route estimate while physically stationary neither renews the {phase} budget nor changes its saved checkpoint");
                            NativeEvent(leader, null, Blank<FieldBattleEventComponent>());
                            Time(1126);
                            ClearOrderForTrace(leader);
                            Tick(1126.5);
                            Check(leader.Army == army && leader.DefaultBehavior == AiBehavior.Hold &&
                                Saved<int>(SiegeState(army), "Reissues") == 1,
                                $"an expired {phase} recovery leaves an active native battle untouched");
                            Field(leader.Party, "_mapEventSide", null);
                            Tick(1127);
                            Check(Idle(leader) && escort.Army == army && payer.Influence == 200 &&
                                log.Any(line => line.Contains("Siege order recovery stalled:") && line.Contains($"phase={phase}")),
                                $"a stalled {phase} ends after the lock clears, without extending the timer on reload or paying again");
                            continue;
                        }

                        int mark = log.Count;
                        Move(leader, from + 2);
                        Tick(1104);
                        Check(HasOrder() && Saved<int>(SiegeState(army), "Reissues") == 0 &&
                            log.Skip(mark).Any(line => line.Contains("Siege order recovery progress:") && line.Contains($"phase={phase}")),
                            $"confirmed {phase} recovery earns back its budget only after physical progress on the route");
                        Reload(); NoNewSearch();
                        if (outcome == "backtrack")
                        {
                            Interrupt(1106);
                            Move(leader, from);
                            Interrupt(1108);
                            Move(leader, from + 2);
                            Interrupt(1110);
                            Check(Idle(leader) && payer.Influence == 200,
                                $"backtracking to the saved {phase} checkpoint does not renew the budget; the third clear ends the assignment");
                            continue;
                        }

                        for (int i = 0; i < 3; i++)
                        {
                            Interrupt(1106 + 4 * i);
                            Check(HasOrder() && leader.Army == army && escort.Army == army && payer.Influence == 200,
                                $"interruption {i + 2} restores the same paid {phase} formation without new summons");
                            Move(leader, from + 4 + 2 * i);
                            Tick(1108 + 4 * i);
                        }
                        if (!march)
                        {
                            Enter(leader, muster); Enter(escort, muster);
                            Tick(1117);
                            Check(Besieges(leader, target) && Saved<double>(SiegeState(army), "ReissueProgressAt") < 0,
                                "leaving muster starts a separate march recovery budget");
                        }
                        Camp(target, leader);
                        Tick(1117.25);
                        Check(leader.Army == army && target.SiegeEvent != null && payer.Influence == 200,
                            $"the original formation reaches native camp after more than two interrupted {phase} orders");
                    }
                }

                World();
                var alternate = Site("competing-order-home", 20);
                var hostileSite = Site("competing-order-target", 60, hostile);
                var commander = Lord("competing-order-leader", payer, 800, 16, true);
                var competingArmy = Ready(commander);
                Search(1100); NoNewSearch();
                Check(Besieges(commander, hostileSite), "existing army receives a valid siege order before a competing command");
                SetPartyAiAction.GetActionForVisitingSettlement(commander, alternate, MobileParty.NavigationType.Default, false, false);
                Tick(1101);
                Check(commander.Army == competingArmy && commander.TargetSettlement == alternate &&
                    commander.DefaultBehavior == AiBehavior.GoToSettlement && !OwnsClaim(competingArmy, hostileSite, "siege"),
                    "a competing order is neither treated as a recoverable Hold nor overwritten by siege recovery");

                World();
                Site("reissue-border", 15);
                var invalidated = Site("strength-before-reissue", 60, hostile);
                var weaker = Lord("strength-before-reissue-leader", payer, 800, 16, true);
                Ready(weaker);
                Search(1100); NoNewSearch();
                Check(Besieges(weaker, invalidated), "the army is initially strong enough to march");
                ClearOrderForTrace(weaker);
                score.Minimum = 1001;
                int strengthLoss = log.Count;
                Tick(1100.25);
                Tick(1100.5);
                Check(Idle(weaker) && log.Skip(strengthLoss).Any(line => line.Contains("reason=march-no-longer-meets-native-strength")) &&
                    !log.Skip(strengthLoss).Any(line => line.Contains("Siege native order reissued:")),
                    "a cleared order cannot be restored between regular reviews if the current native strength check fails");

                World();
                Site("refusal-border", 15);
                var rejectedTarget = Site("refused-native-reissue", 60, hostile);
                var rejectedLeader = Lord("refused-native-reissue-leader", payer, 800, 16, true);
                var rejectedArmy = Ready(rejectedLeader);
                Search(1100); NoNewSearch();
                var nativeIssue = typeof(SetPartyAiAction).GetMethod("ApplyInternal", flags);
                var refusal = typeof(SmokeFixtures).GetMethod(nameof(RefuseSiegeReissue), flags);
                try
                {
                    harmony.Patch(nativeIssue, prefix: new HarmonyMethod(refusal));
                    refusedSiegeReissue = rejectedLeader;
                    ClearOrderForTrace(rejectedLeader);
                    Tick(1102);
                    Check(rejectedLeader.DefaultBehavior == AiBehavior.Hold &&
                        !Saved<bool>(SiegeState(rejectedArmy), "ReissueOrderConfirmed"),
                        "a rejected native reissue does not count as a restored order");
                    Move(rejectedLeader, 20);
                    refusedSiegeReissue = null;
                    Tick(1103.25);
                    Check(Besieges(rejectedLeader, rejectedTarget) && Saved<int>(SiegeState(rejectedArmy), "Reissues") == 2,
                        "moving while the first order was unconfirmed cannot renew the march budget");
                    ClearOrderForTrace(rejectedLeader);
                    Tick(1105);
                Check(Idle(rejectedLeader), "the next stationary clear still exhausts the two-attempt limit");
                }
                finally
                {
                    refusedSiegeReissue = null;
                    harmony.Unpatch(nativeIssue, refusal);
                }
            }

            // Replay recorded strength transitions through production decisions.
            // Fixture score cutoffs represent observed pass/fail bands, not a
            // reconstruction of the native campaign's unknown adjusted defense.
            void TestIdleGrace()
            {
                foreach (string outcome in new[] { "expiry", "protected-expiry", "daytime-reuse", "final-siege", "final-defense", "native-loss", "external" })
                {
                    World();
                    var site = Site("idle-muster", 20);
                    var initial = Site("idle-first-target", 70, hostile);
                    var leader = Lord("idle-leader", payer, 400, 19, true);
                    var escort = Lord("idle-escort", ClanFor(home, "idle-escort-clan", 0), 300, 24);
                    payer.Influence = 350;
                    score.Minimum = 595;
                    Search(1100);
                    var army = leader.Army;
                    NoNewSearch();
                    Enter(leader, site); Tick(1100.25);
                    ClearOrderForTrace(leader); Tick(1101.5);
                    Check(Besieges(leader, initial) && !IdlePeriods().Contains(army), "an active march with a recoverable Hold is never idle");
                    score.Minimum = 1000;
                    Tick(1103); Tick(1104.25);
                    double until = 1128.25;
                    Check(Idle(leader) && escort.Army == army && escort.TargetParty == leader && !OwnsClaim(army, initial, "siege") &&
                        Saved<double>(IdlePeriods()[army], "Until") == until,
                        "only assignment closure starts the 24-hour deadline, clears the leader's target and keeps its roster");
                    army.Cohesion = 40;
                    Reload(); NoNewSearch(); Tick(1111);
                    Check(Idle(leader) && payer.Influence == 300 && army.Cohesion == 40 &&
                        Saved<double>(IdlePeriods()[army], "Until") == until, "idle reload neither extends the deadline nor pays upkeep");
                    if (outcome == "native-loss")
                    {
                        DisbandArmyAction.ApplyByCohesionDepleted(army); Tick(1111.25);
                        Check(leader.Army == null && !IdlePeriods().Contains(army), "native dispersal ends grace immediately");
                        continue;
                    }
                    if (outcome == "external")
                    {
                        SetPartyAiAction.GetActionForVisitingSettlement(leader, site, MobileParty.NavigationType.Default, false, false);
                        Tick(1111.25); Tick(until);
                        Check(!IdlePeriods().Contains(army) && leader.Army == army && leader.TargetSettlement == site,
                            "an external order cancels the old idle deadline without disband or override");
                        continue;
                    }
                    if (outcome == "daytime-reuse")
                    {
                        initial.Town.FoodStocks = 0;
                        forcePowers[escort.Party] = 400; // material roster change permits this abandoned objective
                        score.Minimum = 620;
                        Search(1121.25);
                        Check(Besieges(leader, initial) && leader.Army == army && !IdlePeriods().Contains(army) && payer.Influence == 300,
                            "after 17 hours the planner reuses the same viable roster without rebuying escorts");
                        NoNewSearch(); War(home, enemy, false); Tick(1122);
                        Check(Idle(leader) && Saved<double>(IdlePeriods()[army], "Until") == 1146,
                            "a genuinely new assignment ending starts a new idle period");
                        continue;
                    }
                    Tick(until - 0.25);
                    Check(Idle(leader) && payer.Influence == 300, "no early expiry at 23.75 campaign hours");
                    if (outcome == "final-siege")
                    {
                        var viable = Site("idle-final-target", 65, hostile);
                        score.OverrideMinimum = t => t == viable ? 600 : 1000;
                        Tick(until);
                        Check(leader.Army == army && Besieges(leader, viable) && !IdlePeriods().Contains(army) && payer.Influence == 300,
                            "at the exact deadline the planner dispatches the intact army even between ordinary search slots");
                    }
                    else if (outcome == "final-defense")
                    {
                        var defense = Site("idle-home-under-siege", 34, castle: true);
                        Camp(defense, Lord("idle-home-besieger", hostile, 300, 34, true));
                        Tick(until);
                        Check(leader.Army == army && Defends(leader, defense) && !IdlePeriods().Contains(army),
                            "new urgent relief takes the idle army before expiry can disperse it");
                    }
                    else
                    {
                        if (outcome == "protected-expiry")
                        {
                            NativeEvent(escort, null, Blank<FieldBattleEventComponent>());
                            Tick(until); Reload(); NoNewSearch();
                            Check(Idle(leader) && Saved<double>(IdlePeriods()[army], "Until") == until,
                                "battle at expiry defers release without resetting the saved deadline");
                            Field(escort.Party, "_mapEventSide", null);
                        }
                        Tick(outcome == "expiry" ? until : until + 1);
                        Check(leader.Army == null && escort.Army == null && !IdlePeriods().Contains(army) && payer.Influence == 300,
                            "without a viable objective expiry releases the roster exactly once and spends nothing");
                    }
                }
            }

            void TestHistoricalLifecycleReplay()
            {
                foreach (string run in new[] { "Jalmarys", "Amitatys", "Vladiv", "field-regroup", "no-progress" })
                {
                    World();
                    var site = Site("replay-home", 20);
                    var target = Site("replay-" + run, 60, hostile);
                    bool amitatys = run == "Amitatys", vladiv = run == "Vladiv";
                    float present = amitatys ? 1532 : vladiv ? 1024 : 1701;
                    var leader = Lord("replay-leader", payer, 401, 19, true);
                    var core = Lord("replay-core", ClanFor(home, "replay-core", 0), present - 401, 21);
                    var merc = ClanFor(home, "replay-merc", 0);
                    int count = amitatys ? 3 : vladiv ? 2 : 4;
                    float extra = amitatys ? 677 : vladiv ? 486 : 848;
                    var called = Enumerable.Range(0, count).Select(i =>
                        Lord("replay-escort-" + i, merc, extra / count, 22 + i)).ToArray();
                    payer.Influence = 700;
                    price.PriceOverride = (_, p) => p == called[0] ? 0 : 16;
                    score.Minimum = (present + extra - 2) * .90f;
                    Search(1100);
                    var original = leader.Army;
                    float afterDebit = payer.Influence;
                    Check(original != null && afterDebit < 700 && called.All(p => p.Army == original),
                        run + " replay buys one roster passing the buffered purchase gate");
                    Enter(leader, site); Enter(core, site);
                    if (amitatys) foreach (var p in called) Enter(p, site);
                    score.Minimum = amitatys ? 2450 : vladiv ? 1240 : 2100;
                    NoNewSearch(); Tick(1101);
                    Check(Besieges(leader, target) && called.All(p => p.Army == original), run + " dispatch retains the entire roster");
                    Move(leader, 42);
                    Reload(); NoNewSearch();
                    if (run == "field-regroup")
                    {
                        var incoming = Lord("replay-visible-threat", hostile, 2250, 59);
                        PlaceContact(incoming, 59);
                        Tick(1102.25);
                        Check(leader.Army == original && leader.TargetSettlement == site && called.All(p => p.Army == original) &&
                            payer.Influence == afterDebit, "field threat exceeds present power but not roster: regroup intact");
                        Enter(leader, site); Tick(1103);
                        Check(!Besieges(leader, target), "loss of sight at the muster does not cause immediate redeparture");
                        foreach (var p in called) Enter(p, site);
                        Tick(1104);
                        Check(Besieges(leader, target) && payer.Influence == afterDebit, "regroup resumes after real catch-up without payment");
                        continue;
                    }
                    if (amitatys)
                    {
                        var other = Site("replay-alternate", 55, hostile);
                        foreach (var p in called) p.Army = null;
                        score.OverrideMinimum = t => t == other ? 1800 : 2450;
                        Tick(1102.25);
                        Check(Besieges(leader, target), "Amitatys: one failed review alone does not dissolve the army");
                        Reload(); NoNewSearch(); Tick(1103.5);
                        Check(Besieges(leader, other) && leader.Army == original && core.Army == original &&
                            called.All(p => p.Army == null) && payer.Influence == afterDebit && PaidEpisodes().Count == 1,
                            "Amitatys: 2209 to 1532 after three genuine exits; intact survivors reassigned without a rebuy");
                        Reload(); NoNewSearch();
                        score.OverrideMinimum = t => t == other ? 2600 : 1000;
                        Tick(1105); Tick(1106.25);
                        Check(Idle(leader) && !Besieges(leader, target) && payer.Influence == afterDebit,
                            "no viable untried job: stand down rather than ping-ponging to the abandoned objective after reload");
                        continue;
                    }
                    score.Minimum = vladiv ? 1250 : 2150; // 1701 * 1.25 fails; 2549 roster passes.
                    Tick(1102.25);
                    Check(leader.Army == original && Besieges(leader, target) && payer.Influence == afterDebit &&
                        called.All(p => p.Army == original) && PaidEpisodes().Count == 0,
                        run + " replay survives first march review with unchanged roster, no release and no rebuy");
                    if (run == "no-progress")
                    {
                        Tick(1127);
                        Check(Idle(leader) && log.Any(l => l.Contains("ended=march-no-observed-progress")),
                            "a roster on paper cannot sustain a stranded army indefinitely");
                        continue;
                    }
                    Move(leader, 60); Camp(target, leader); Tick(1108);
                    Field(target.Town, "_ownerClan", payer);
                    Lift(target);
                    dispatcher.OnSettlementOwnerChanged(target, false, null, null, null, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege);
                    Tick(1109);
                    Check(Idle(leader) && PaidEpisodes().Count == 0 && payer.Influence == afterDebit &&
                        log.Any(l => l.Contains("Siege closes:") && l.Contains("target=replay-" + run) && l.Contains("reason=native-capture")),
                        run + " replay accepts camp and capture events without mistaking mission completion for paid failure");
                }
            }

            void TestSamePassRebuy()
            {
                World();
                var muster = Site("churn-muster", 20);
                var firstTarget = Site("churn-first-target", 60, hostile, castle: true);
                var secondTarget = Site("churn-second-target", 85, hostile);
                var leader = Lord("churn-leader", payer, 200, 20, true);
                payer.Influence = 400;
                var veteran = Lord("surviving-paid-escort", ClanFor(home, "old-escort", 0), 250, 23);
                var departed = Lord("defecting-paid-escort", ClanFor(home, "defecting-escort", 0), 230, 28);
                score.Minimum = 600;
                Search(1100);
                var oldArmy = leader.Army;
                Check(oldArmy != null && OwnsClaim(oldArmy, firstTarget, "siege", ordered: false) && payer.Influence == 300,
                    "first offensive muster needs two paid escorts");
                var other = NewKingdom("mercenary-successor");
                // The fixture models the native membership loss when the clan
                // changes sides; the surviving paid escort remains in the army.
                departed.Army = null;
                Field(departed.LeaderHero.Clan, "_kingdom", other.Kingdom);
                var fresh = Lord("new-affordable-reinforcement", ClanFor(home, "new-escort", 0), 230, 28);
                // Both towns have legal land routes. A separate score floor for
                // the old target makes the replacement a new job, not a key hit.
                score.OverrideMinimum = target => target == firstTarget ? 700 : 600;
                NoNewSearch(); Tick(1100.25); Search(1100.5);
                Check(Idle(leader) && veteran.Army == oldArmy && fresh.Army == null && payer.Influence == 300 &&
                    secondTarget.SiegeEvent == null && Retries().Count == 0,
                    "an invalidation pass cannot immediately buy back a just-released offensive escort for another objective");
                NoNewSearch(); Tick(1124.5); Search(1124.75);
                Check(leader.Army != null && leader.Army != oldArmy && OwnsClaim(leader.Army, secondTarget, "siege", ordered: false) &&
                    veteran.Army == leader.Army && fresh.Army == leader.Army && payer.Influence == 200,
                    "a later normal search can form with genuine reinforcement and live affordable native costs");
                Enter(leader, muster); Enter(veteran, muster); Enter(fresh, muster);
                NoNewSearch(); Tick(1125);
                Check(OwnsClaim(leader.Army, secondTarget, "siege") && secondTarget.SiegeEvent == null && DuplicateClaims().Length == 0,
                    "the replenished formation must still gather and pass the native departure gate");
            }

            void TestOffensiveDepartureThreshold()
            {
                foreach (string outcome in new[] { "last-escort-arrives", "catch-up-on-march", "catch-up-in-camp",
                    "traveling-roster-on-march", "free-traveler", "ineligible-traveler", "legacy-paid", "no-progress", "unpaid" })
                {
                    World();
                    var muster = Site("headroom-home", 20);
                    var target = Site("headroom-target", 60, hostile);
                    var leader = Lord("headroom-leader", payer, 400, 20, true);
                    var early = Lord("headroom-early", ClanFor(home, "headroom-early-clan", 0), 240, 23);
                    var late = Lord("headroom-late", ClanFor(home, "headroom-late-clan", 0), 70, 30);
                    payer.Influence = 300;
                    score.Minimum = 590;
                    bool unpaid = outcome == "unpaid";
                    bool freeTraveler = outcome == "free-traveler";
                    if (unpaid) price.Cost = 0;
                    if (freeTraveler) price.PriceOverride = (_, escort) => escort == late ? 0 : 50;
                    Search(1100);
                    var army = leader.Army;
                    float balance = unpaid ? 300 : freeTraveler ? 250 : 200;
                    Check(army != null && early.Army == army && late.Army == army && payer.Influence == balance,
                        "the initial plan needs both escorts to meet the purchase buffer");
                    if (!unpaid) score.Minimum = 875; // 700 actual is now needed at the paid continuation threshold.
                    Move(leader, 19); Enter(early, muster);
                    NoNewSearch(); Tick(1100.25);
                    Check(OwnsClaim(army, target, "siege", ordered: false) &&
                        late.Army == army && late.AttachedTo == null && payer.Influence == balance,
                        "the viable roster remains intact while the leader is traveling to muster");
                    if (outcome == "legacy-paid")
                        Field(SiegeState(army), "PaidEscorts", null);
                    Reload(); NoNewSearch(); Tick(1100.5);
                    Check(OwnsClaim(army, target, "siege", ordered: false) && late.Army == army && payer.Influence == balance,
                        "reload preserves the gathering roster without repaying");

                    if (outcome == "no-progress")
                    {
                        Tick(1125);
                    Check(Idle(leader) && early.Army == army && late.Army == army && payer.Influence == 200 &&
                            log.Any(line => line.Contains("Siege closes:") && line.Contains("reason=muster-no-observed-progress")),
                            "a stalled muster still cancels above the paid continuation threshold");
                        continue;
                    }
                    bool needLast = unpaid || outcome == "last-escort-arrives";
                    Enter(leader, muster);
                    if (needLast)
                        Enter(late, muster);
                    else
                        forcePowers[early.Party] = 300;
                    bool ineligibleTraveler = outcome == "ineligible-traveler";
                    if (ineligibleTraveler)
                        late.LeaderHero.CanJoinArmy = false;
                    Tick(1100.75);
                    Check(Besieges(leader, target) && early.AttachedTo == leader && payer.Influence == balance &&
                        (needLast ? late.AttachedTo == leader : ineligibleTraveler ? late.Army == null :
                            late.Army == army && late.AttachedTo == null && late.DefaultBehavior == AiBehavior.EscortParty && late.TargetParty == leader),
                        unpaid ? "an unpaid member is a real member of the formation" :
                            ineligibleTraveler ? "a past debit does not keep an ineligible escort in the formation" :
                            freeTraveler ? "an eligible zero-cost traveler remains on a mixed-cost roster" :
                            "a last paid traveler is not released merely because the attached force can depart");
                    if (needLast || freeTraveler || ineligibleTraveler)
                        continue;

                    Move(leader, 40);
                    Move(late, 35);
                    Reload(); NoNewSearch();
                    Tick(1102);
                    Check(late.Army == army && late.AttachedTo == null && late.TargetParty == leader && payer.Influence == balance,
                        "a paid traveler keeps its escort order across reload and a march review without another debit");
                    if (outcome == "traveling-roster-on-march")
                    {
                        score.Minimum = 900; // 700 attached fails; 770 including the escort would pass.
                        Tick(1103.25);
                        Check(Besieges(leader, target) && leader.Army == army && late.Army == army && payer.Influence == balance,
                            "the whole native roster counts toward keeping the assignment after departure");
                        continue;
                    }
                    SiegeEvent siege = null;
                    if (outcome == "catch-up-in-camp")
                    {
                        Move(leader, 60);
                        Field(leader.LeaderHero, "_partyBelongedTo", leader);
                        Field(late.LeaderHero, "_partyBelongedTo", late);
                        siege = Camp(target, leader);
                        Tick(1102.25);
                        Check(late.Army == army && late.AttachedTo == null && late.TargetParty == leader,
                            "camp start does not dismiss a paid escort that is still catching up");
                    }
                    // Drive the installed native army tick with the escort
                    // now in encounter range of its actual short-term target.
                    Move(late, leader.Position.X);
                    typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(late,
                        new object[] { AiBehavior.EscortParty, leader.Party });
                    var campPosition = typeof(BesiegerCamp).GetMethod("GetSiegeCampPartyPosition", flags);
                    var fixturePosition = typeof(SmokeFixtures).GetMethod(nameof(UseFixtureCampPosition), flags);
                    var validity = typeof(CampaignVec2).GetMethod("IsValid", flags);
                    var fixtureValidity = typeof(SmokeFixtures).GetMethod(nameof(AcceptFixtureLandPoint), flags);
                    if (siege != null)
                    {
                        harmony.Patch(campPosition, prefix: new HarmonyMethod(fixturePosition));
                        harmony.Patch(validity, prefix: new HarmonyMethod(fixtureValidity));
                    }
                    try { typeof(Army).GetMethod("Tick", flags).Invoke(army, new object[] { null, null }); }
                    finally
                    {
                        if (siege != null)
                        {
                            harmony.Unpatch(validity, fixtureValidity);
                            harmony.Unpatch(campPosition, fixturePosition);
                        }
                    }
                    Check(late.Army == army && late.AttachedTo == leader && payer.Influence == balance &&
                        (siege == null || late.BesiegerCamp == siege.BesiegerCamp &&
                            siege.BesiegerCamp.HasInvolvedPartyForEventType(late.Party)),
                        siege == null ? "the native army tick attaches the paid escort on the march without a second debit" :
                            "the native army tick brings a late paid escort into the established siege camp without a second debit");
                }
            }

            void TestPaidSiegeContinuation()
            {
                foreach (string outcome in new[] { "camp", "below-floor", "route-lost", "field-threat", "unpaid" })
                {
                    World();
                    var muster = Site("retention-march-home", 20);
                    var target = Site("retention-march-target", 60, hostile);
                    var leader = Lord("retention-march-leader", payer, 400, 20, true);
                    var escort = Lord("retention-march-escort", ClanFor(home, "retention-march-clan", 0), 400, 25);
                    payer.Influence = 350;
                    bool unpaid = outcome == "unpaid";
                    if (unpaid) price.Cost = 0;
                    score.Minimum = 710;
                    Search(1100);
                    var army = leader.Army;
                    Enter(leader, muster); Enter(escort, muster);
                    NoNewSearch(); Tick(1100.25);
                    Check(Besieges(leader, target), "the formation leaves after its attached force passes the applicable departure check");

                    Move(leader, 54);
                    score.Minimum = outcome == "below-floor" ? 1001 : 1000;
                    if (outcome == "route-lost") blockedFixtureRoute = (_, to) => to == 60;
                    if (outcome == "field-threat") PlaceContact(Lord("retention-visible-threat", hostile, 970, 59, true), 59);
                    int mark = log.Count;
                    Tick(1101.5);
                    if (outcome == "camp" || outcome == "unpaid")
                    {
                        Check(leader.Army == army && Besieges(leader, target) && payer.Influence == (unpaid ? 350 : 300) &&
                            log.Skip(mark).Any(line => line.Contains("phase=march power=800 continuationProbePower=1000")),
                            "a paid march continues at exactly the lower threshold without repurchasing escorts");
                        Reload(); NoNewSearch();
                        ClearOrderForTrace(leader);
                        Tick(1102);
                        Check(leader.Army == army && Besieges(leader, target) && payer.Influence == (unpaid ? 350 : 300),
                            "a native order clear after reload can reissue the same paid march at the lower threshold");
                        Camp(target, leader); Tick(1102.25);
                        Check(leader.Army == army && target.SiegeEvent != null && PaidEpisodes().Count == 0,
                            "the original paid march reaches a native camp with no recorded failed purchase");
                    }
                    else
                    {
                        Tick(1102.75);
                        string reason = outcome == "route-lost" ? "march-land-path-lost" : outcome == "field-threat" ?
                            "visible-overwhelming-threat-before-camp" : "march-no-longer-meets-native-strength";
                        Check(Idle(leader) && payer.Influence == (unpaid ? 350 : 300) &&
                            log.Skip(mark).Any(line => line.Contains("Siege closes:") && line.Contains("reason=" + reason)),
                            "continuation does not override " + outcome);
                        if (outcome == "below-floor")
                            Check(log.Skip(mark).Any(line => line.Contains("failedCheckPower=800")),
                                "a strength failure records real strength, not the boosted continuation probe");
                    }
                }

                foreach (bool aboveFloor in new[] { true, false })
                {
                    World();
                    var muster = Site("retention-muster-home", 20);
                    var target = Site("retention-muster-target", 60, hostile);
                    var leader = Lord("retention-muster-leader", payer, 400, 19, true);
                    var escort = Lord("retention-muster-escort", ClanFor(home, "retention-muster-clan", 0), 400, 25);
                    var lost = Lord("retention-muster-lost", ClanFor(home, "retention-muster-lost", 0), 250, 30);
                    payer.Influence = 350;
                    score.Minimum = 920;
                    Search(1100);
                    var army = leader.Army;
                    Check(lost.Army == army && escort.Army == army && payer.Influence == 250,
                        "the purchase still requires the full buffered plan");
                    army.Cohesion = 40;
                    lost.Army = null;
                    score.Minimum = aboveFloor ? 1000 : 1001;
                    NoNewSearch(); Tick(1100.25);
                    if (!aboveFloor)
                    {
                        Tick(1100.5);
                        Check(Idle(leader) && payer.Influence == 250,
                            "losing an escort drops the projected roster below the continuation threshold and closes without more summons");
                        continue;
                    }
                    Check(OwnsClaim(army, target, "siege", ordered: false) && payer.Influence == 250 && army.Cohesion == 40,
                        "a paid muster keeps gathering after escort loss at 160%, without new summons or upkeep");
                    Reload(); NoNewSearch();
                    Move(escort, 22); Tick(1113);
                    Move(leader, 19.5f); Tick(1125);
                    Check(OwnsClaim(army, target, "siege", ordered: false) && payer.Influence == 250,
                        "reload and more than 24 hours below the old departure gate do not cancel a progressing paid muster");
                    Enter(leader, muster); Tick(1125.25);
                    Check(Besieges(leader, target) && leader.Army == army && payer.Influence == 250 &&
                        log.Any(line => line.Contains("present=400") && line.Contains("roster=800 checkedPower=1000 continuationOverride=True")),
                        "the same paid army departs at 160% of adjusted defense with the traveling escort still counted");
                    Camp(target, leader); Tick(1126.5);
                    Check(leader.Army == army && target.SiegeEvent != null && PaidEpisodes().Count == 0,
                        "the paid army kept despite escort loss reaches its native camp");
                }
            }

            void TestCrossTargetPaidRebuy()
            {
                foreach (string outcome in new[] { "expiry", "different-roster", "half", "price-drift", "free", "existing", "relief", "stale-quote" })
                {
                    World();
                    var muster = Site("cross-target-home", 20);
                    var first = Site("cross-target-jalmarys", 60, hostile);
                    var next = Site("cross-target-thorios", 75, hostile);
                    var leader = Lord("cross-target-first-leader", payer, 400, 19, true);
                    var a = Lord("cross-target-a", ClanFor(home, "cross-target-a", 0), 240, 22);
                    var b = Lord("cross-target-b", ClanFor(home, "cross-target-b", 0), 230, 25);
                    var detached = Lord("cross-target-detached", ClanFor(home, "cross-target-detached", 0), 220, 28);
                    payer.Influence = 500;
                    score.Minimum = 900;
                    Search(1100);
                    var original = leader.Army;
                    Check(OwnsClaim(original, first, "siege", ordered: false) && detached.Army == original &&
                        payer.Influence == 350, "the first plan pays for all three escorts before it fails");
                    b.Army = detached.Army = null;
                    Enter(leader, muster);
                    score.OverrideMinimum = target => target == first ? 2000 : 900;
                    NoNewSearch(); Tick(1100.25); Tick(1100.5);
                    Check(Idle(leader) && a.Army == original && b.Army == null && detached.Army == null,
                        "the surviving roster stays intact after the failed assignment");
                    // Exercise the purchase guards when a genuine native disband
                    // frees the remaining paid members before the idle deadline.
                    DisbandArmyAction.ApplyByCohesionDepleted(original);
                    Reload(); Search(1102.25);
                    Check(leader.Army == null && detached.Army == null && payer.Influence == 350,
                        "changing the target after two hours cannot buy back the same paid roster even across save/load");

                    if (outcome == "expiry")
                    {
                        Search(1108.5);
                        Check(OwnsClaim(leader.Army, next, "siege", ordered: false) && payer.Influence == 200,
                            "the cross-target exclusion expires; it is not an army cap or a permanent roster ban");
                    }
                    else if (outcome == "different-roster")
                    {
                        a.LeaderHero.CanJoinArmy = b.LeaderHero.CanJoinArmy = false;
                        var aa = Lord("cross-target-new-a", ClanFor(home, "cross-target-new-a", 0), 240, 22);
                        var bb = Lord("cross-target-new-b", ClanFor(home, "cross-target-new-b", 0), 230, 25);
                        Search(1103);
                        Check(OwnsClaim(leader.Army, next, "siege", ordered: false) && aa.Army == leader.Army &&
                            bb.Army == leader.Army && detached.Army == leader.Army && payer.Influence == 200,
                            "the kingdom can still buy a different roster for another objective during the window");
                    }
                    else if (outcome == "half" || outcome == "price-drift")
                    {
                        a.LeaderHero.CanJoinArmy = false;
                        var aa = Lord("cross-target-extra-a", ClanFor(home, "cross-target-extra-a", 0), 110, 29);
                        var bb = Lord("cross-target-extra-b", ClanFor(home, "cross-target-extra-b", 0), 110, 30);
                        if (outcome == "price-drift")
                            price.PriceOverride = (_, escort) => escort == bb && escort.Army != null ? 0 : 30;
                        else
                            price.Cost = 30;
                        int mark = log.Count;
                        Search(1103);
                        var replacement = leader.Army;
                        Check(OwnsClaim(replacement, next, "siege", ordered: false) && aa.Army == replacement && bb.Army == replacement &&
                            (outcome == "half" ? b.Army == replacement && detached.Army == replacement && payer.Influence == 230 :
                                (b.Army == replacement) != (detached.Army == replacement) && payer.Influence == 290 &&
                                log.Skip(mark).Any(line => line.Contains("Siege cross-target purchase blocked:") && line.Contains("stage=debit"))),
                            outcome == "half" ? "exactly half of the new paid parties is not a majority" :
                                "when native pricing removes a distinct positive charge, the debit hook blocks the new majority");
                    }
                    else if (outcome == "free")
                    {
                        price.Cost = 0;
                        Search(1103);
                        Check(OwnsClaim(leader.Army, next, "siege", ordered: false) && detached.Army == leader.Army &&
                            payer.Influence == 350, "the purchase guard leaves truly free new formations alone");
                    }
                    else if (outcome == "existing")
                    {
                        forcePowers[leader.Party] = 920;
                        Ready(leader);
                        Search(1103);
                        Check(Besieges(leader, next) && payer.Influence == 350,
                            "the purchase guard leaves viable existing-army dispatch alone");
                    }
                    else if (outcome == "relief")
                    {
                        Enter(leader, muster);
                        var homeSiege = Site("cross-target-home-siege", 29, castle: true);
                        Camp(homeSiege, Lord("cross-target-home-besieger", hostile, 1200, 29, true));
                        NoNewSearch(); Tick(1103);
                        Check(leader.Army != null && OwnsClaim(leader.Army, homeSiege, "home", ordered: false) &&
                            payer.Influence < 350, "urgent relief can pay for the released escorts even during the offense window");
                    }
                    else
                    {
                        price.Cost = 0;
                        Field(command, "_nextSiegeSearchSlot", 0d);
                        ((IDictionary)behavior.GetField("_nextSiegeSearch", flags).GetValue(command)).Clear();
                        CampaignTick(1103);
                        for (int i = 0; i < 100 && behavior.GetField("_offensiveCommit", flags).GetValue(command) == null; i++)
                            ApplicationFrame(1103);
                        Check(behavior.GetField("_offensiveCommit", flags).GetValue(command) != null && leader.Army == null,
                            "a free proposal is legal in the read-only scan");
                        price.Cost = 50;
                        ApplicationFrame(1103);
                        Check(leader.Army == null && a.Army == null && b.Army == null && detached.Army == null && payer.Influence == 350,
                            "the live commit rechecks real prices and rejects a stale cross-target proposal before forming");
                    }
                }

                World();
                var site = Site("sturgian-replay-muster", 20);
                var jalmarys = Site("sturgian-replay-jalmarys", 60, hostile);
                Site("sturgian-replay-thorios", 75, hostile);
                var raganvad = Lord("sturgian-replay-leader", payer, 400, 19, true);
                var freeEscort = Lord("sturgian-replay-free", payer, 100, 21);
                var charged = Enumerable.Range(0, 10).Select(i =>
                    Lord("sturgian-replay-escort-" + i, ClanFor(home, "sturgian-replay-clan-" + i, 0), 100, 22 + i)).ToArray();
                payer.Influence = 1200;
                score.Minimum = 1300;
                Search(1100);
                var formationBeforeSwitch = raganvad.Army;
                Check(OwnsClaim(formationBeforeSwitch, jalmarys, "siege", ordered: false) && freeEscort.Army == formationBeforeSwitch &&
                    charged.All(party => party.Army == formationBeforeSwitch) && payer.Influence == 700,
                    "Sturgian regression: eleven summons include ten actually paid parties");
                charged[0].Army = charged[1].Army = charged[2].Army = null;
                Enter(raganvad, site);
                score.OverrideMinimum = target => target == jalmarys ? 2000 : 1520;
                NoNewSearch(); Tick(1100.25); Tick(1100.5);
                Check(Idle(raganvad), "the failed Sturgian roster enters grace before a native cohesion loss");
                DisbandArmyAction.ApplyByCohesionDepleted(formationBeforeSwitch);
                score.OverrideMinimum = target => target == jalmarys ? 2000 : 1300;
                charged[0].LeaderHero.CanJoinArmy = false;
                var newEscort = Lord("sturgian-replay-alternate", ClanFor(home, "sturgian-replay-alternate", 0), 100, 32);
                Reload(); Search(1102.33);
                Check(raganvad.Army == null && freeEscort.Army == null && newEscort.Army == null &&
                    charged.All(party => party.Army == null) && payer.Influence == 700,
                    "Sturgian regression: nine of ten paid escorts cannot be bought again for a different target 2.08 hours after failure");
            }

            void TestPaidOffenseRetry()
            {
                foreach (string retry in new[] { "stronger-roster", "weaker-defense", "existing-army" })
                {
                    World();
                    var muster = Site("guarded-muster", 20);
                    var target = Site("guarded-target", 60, hostile);
                    target.Town.FoodStocks = 100;
                    var leader = Lord("guarded-original-leader", payer, 400, 20, true);
                    var escort = Lord("guarded-original-escort", ClanFor(home, "guarded-escort-clan", 0), 300, 24);
                    score.Minimum = 650;
                    Search(1100);
                    Check(leader.Army == null && escort.Army == null && payer.Influence == 250,
                        "fresh roster which would pass only at full strength is rejected before formation or payment");
                    score.Minimum = 600;
                    Search(1108);
                    var original = leader.Army;
                    Check(original != null && escort.Army == original && payer.Influence == 200,
                        "fresh roster that passes at 90 percent forms and pays exactly once");
                    Enter(leader, muster); Enter(escort, muster);
                    NoNewSearch();
                    bool failOnMarch = retry == "stronger-roster";
                    if (failOnMarch)
                    {
                        score.Minimum = 625;
                        Tick(1108.25);
                         Check(Besieges(leader, target), "a paid muster departs with sufficient attached strength");
                        score.Minimum = 650;
                        Tick(1109.5);
                        Check(Besieges(leader, target), "a marching army uses its actual strength without reapplying the departure buffer");
                    }
                    if (retry == "weaker-defense")
                        forcePowers[escort.Party] = 250;
                    score.Minimum = 900;
                    Tick(failOnMarch ? 1110.75 : 1109.5);
                    Tick(failOnMarch ? 1112 : 1109.75);
                    var failures = (IDictionary)behavior.GetField("_preCampFailures", flags).GetValue(command);
                    Check(Idle(leader) && escort.Army == original && failures.Count == 1 && Retries().Count == 0,
                        $"confirmed {(failOnMarch ? "march" : "muster")} strength loss records a fresh-formation guard, not a repulse");
                    Reload();
                    leader.LeaderHero.CanJoinArmy = false;
                    NoNewSearch(); Tick(now + 0.25);
                    var closer = Site("guarded-different-muster", 45);
                    var alternativeClan = ClanFor(home, "guarded-different-payer", 250);
                    var alternative = Lord("guarded-different-leader", alternativeClan, 400, 45, true);
                    Move(escort, 47);
                    score.Minimum = 600; // Positive quote alone does not prove why the failed plan is better.
                    Search(1117.5);
                    Check(alternative.Army == null && escort.Army == null && alternativeClan.Influence == 250,
                        "reload, a different paying leader, a closer muster and movement do not reopen the same paid siege");

                    if (retry == "stronger-roster")
                        forcePowers[escort.Party] = 380;
                    else if (retry == "weaker-defense")
                    {
                        target.Town.FoodStocks = 70;
                        forcePowers[escort.Party] = 330;
                    }
                    else
                    {
                        forcePowers[alternative.Party] = 610;
                        Ready(alternative);
                    }

                    Search(1125.5);
                    Check(alternative.Army != null && (retry == "existing-army"
                            ? Besieges(alternative, target) && escort.Army == null && alternativeClan.Influence == 250
                            : OwnsClaim(alternative.Army, target, "siege", ordered: false) && alternative.TargetSettlement == closer &&
                                escort.Army == alternative.Army && alternativeClan.Influence == 200),
                        $"same target is eligible with {retry} and a live passing native check");
                    if (retry == "existing-army")
                    {
                        NoNewSearch();
                        score.Minimum = 620;
                        Tick(1127);
                        forcePowers[alternative.Party] = 400;
                        score.Minimum = 600;
                        Search(1135);
                        NoNewSearch(); Tick(1136.25);
                        Check(Idle(alternative) && escort.Army == null && alternativeClan.Influence == 250,
                            "a weaker existing army's failure does not erase the stronger prior paid retry guard");
                    }
                }
            }

            void TestPaidRecoveryDeparture()
            {
                foreach (string outcome in new[] { "reinforced", "weaker", "weaker-reversed", "deadline" })
                {
                    World();
                    var muster = Site("physical-retry-home", 20);
                    var target = Site("physical-retry-ew1", 60, hostile);
                    target.Town.FoodStocks = 100;
                    var leader = Lord("physical-retry-first", payer, 400, 19, true);
                    var early = Lord("physical-retry-early", ClanFor(home, "physical-retry-early", 0), 1801, 23);
                    score.Minimum = 1900;
                    Search(1100);
                    Enter(leader, muster); Enter(early, muster);
                    NoNewSearch();
                    score.Minimum = 2752;
                    Tick(1100.25);
                    Tick(1100.5);
                    bool firstFailed = Idle(leader) && payer.Influence == 200 && PaidEpisodes().Count == 1;
                    leader.LeaderHero.CanJoinArmy = false;
                    var retryClan = ClanFor(home, "physical-retry-clan", 400);
                    var replacement = Lord("physical-retry-leader", retryClan, 400, 19, true);
                    var late = Lord("physical-retry-late", ClanFor(home, "physical-retry-late", 0), 250, 26);
                    var lastEscort = Lord("physical-retry-last", ClanFor(home, "physical-retry-last", 0), 290, 32);
                    forcePowers[early.Party] = 1600;
                    score.Minimum = 1750;
                    if (outcome == "weaker-reversed")
                    {
                        target.Town.FoodStocks = 70;
                        lastEscort.LeaderHero.CanJoinArmy = false;
                        Search(1108);
                        Check(firstFailed && replacement.Army == null && retryClan.Influence == 400,
                            "a weaker-defense retry with maximum available power below the actual failed-input floor cannot form or charge");
                        lastEscort.LeaderHero.CanJoinArmy = true;
                    }
                    Search(1108);
                    var recovery = replacement.Army;
                    if (outcome == "weaker-reversed")
                    {
                        Check(firstFailed && recovery != null && retryClan.Influence == 250 &&
                            late.Army == recovery && lastEscort.Army == recovery,
                            "the affordable extra escorts are kept through trimming despite the live weaker-defense exception");
                        target.Town.FoodStocks = 100;
                        score.Minimum = 2600;
                        Enter(replacement, muster); Enter(early, muster); Enter(late, muster);
                        lastEscort.Army = null;
                        lastEscort.LeaderHero.CanJoinArmy = false;
                        NoNewSearch(); Tick(1108.25);
                        Check(Besieges(replacement, target) && retryClan.Influence == 250 &&
                            log.Any(line => line.Contains("present=2250") && line.Contains("continuationOverride=True")),
                            "after a qualified retry is paid, stronger defenses do not restore the old purchase floor at departure");
                        continue;
                    }
                    Check(firstFailed && recovery != null && retryClan.Influence == 250 && late.Army == recovery && lastEscort.Army == recovery,
                        "a paid recovery can buy 2540 after the original native check failed at 2201");
                    score.Minimum = 3050;
                    Enter(early, muster);
                    NoNewSearch(); Tick(1108.25);
                    var state = SiegeState(recovery);
                    double deadline = Saved<double>(state, "RecoveryDeadline");
                    Reload(); NoNewSearch(); Tick(1108.5);
                    Check(OwnsClaim(recovery, target, "siege", ordered: false) && late.Army == recovery && lastEscort.Army == recovery &&
                        retryClan.Influence == 250 && Saved<double>(SiegeState(recovery), "RecoveryDeadline") == deadline,
                         "a paid retry retains its original deadline and roster while traveling to the muster");
                    if (outcome == "deadline")
                    {
                        Move(late, 21); Move(lastEscort, 22);
                        Tick(deadline - 0.25);
                        Check(replacement.Army == recovery, "movement shortly before the fixed retry deadline still counts as ordinary gathering progress");
                        Enter(replacement, muster); Enter(late, muster); Enter(lastEscort, muster);
                        Tick(deadline);
                        Check(Idle(replacement) && retryClan.Influence == 250 &&
                            log.Any(line => line.Contains("reason=paid-retry-assembly-deadline")),
                            "progress and a last-minute arrival do not extend the persisted retry deadline or authorize a late departure");
                        continue;
                    }
                    if (outcome == "weaker")
                    {
                        target.Town.FoodStocks = 70;
                        score.Minimum = 2450;
                    }
                    else
                    {
                        Enter(late, muster); Enter(lastEscort, muster);
                    }
                    Enter(replacement, muster); Tick(1108.75);
                    Check(Besieges(replacement, target) && retryClan.Influence == 250 &&
                         (outcome == "weaker" ? late.Army == recovery && lastEscort.Army == recovery &&
                             late.AttachedTo == null && lastEscort.AttachedTo == null :
                             late.AttachedTo == replacement && lastEscort.AttachedTo == replacement),
                        "a paid recovery departs when attached strength passes the current continuation threshold");
                }

                World();
                var site = Site("low-failure-home", 20);
                var fort = Site("low-failure-es4", 60, hostile);
                var original = Lord("low-failure-first-leader", payer, 400, 19, true);
                var surviving = Lord("low-failure-surviving", ClanFor(home, "low-failure-surviving", 0), 815, 23);
                var lost = Lord("low-failure-lost", ClanFor(home, "low-failure-lost", 0), 850, 27);
                payer.Influence = 300;
                score.Minimum = 1700;
                Search(1100);
                Enter(original, site); Enter(surviving, site);
                lost.Army = null;
                lost.LeaderHero.CanJoinArmy = false;
                NoNewSearch(); Tick(1100.25); Tick(1100.5);
                original.LeaderHero.CanJoinArmy = false;
                var clan = ClanFor(home, "low-failure-retry-clan", 300);
                var successor = Lord("low-failure-retry-leader", clan, 400, 20, true);
                var reserve = Lord("low-failure-late-reserve", ClanFor(home, "low-failure-reserve", 0), 500, 27);
                forcePowers[surviving.Party] = 1462;
                score.Minimum = 1600;
                Search(1108);
                Enter(successor, site);
                NoNewSearch(); Tick(1108.25);
                Check(Besieges(successor, fort) && reserve.Army == successor.Army && reserve.AttachedTo == null && clan.Influence == 200 &&
                    log.Any(line => line.Contains("paidRetryFloor=1336.5")) &&
                    log.Any(line => line.Contains("Siege departure:") && line.Contains("present=1862")),
                    "Khuzait replay: departure uses attached strength without dismissing the paid reserve that qualified the purchase");
                Camp(fort, successor);
                Check(PaidEpisodes().Count == 0, "reaching the matching native camp clears the paid episode");
            }

            void TestPaidRecoveryEpisode()
            {
                World();
                Site("stalled-first-home", 20);
                var target = Site("stalled-target", 60, hostile);
                var initial = Lord("stalled-first-leader", payer, 400, 18, true);
                var member = Lord("stalled-paid-member", ClanFor(home, "stalled-member", 0), 300, 24);
                score.Minimum = 600;
                Search(1100);
                NoNewSearch(); Tick(1100.25); Tick(1124.5);
                Check(Idle(initial) && payer.Influence == 200 && PaidEpisodes().Count == 1,
                    "a paid stalled operation records a target-level gathering failure");
                initial.LeaderHero.CanJoinArmy = false;
                Tick(1124.75);
                var closer = Site("stalled-second-home", 45);
                var recoveryClan = ClanFor(home, "stalled-new-payer", 300);
                var recoveryLeader = Lord("stalled-new-leader", recoveryClan, 400, 43, true);
                Move(member, 47);
                Reload();
                Search(1125);
                Check(recoveryLeader.Army == null && recoveryClan.Influence == 300,
                    "a new leader, payer and nearer fort do not buy a stalled target again without actual gathering readiness");
                Enter(recoveryLeader, closer);
                fixtureRouteDistance = (from, to) => from == 47 && to == 45 ? 150 : Math.Abs(from - to);
                Search(1126);
                Check(recoveryLeader.Army == null && recoveryClan.Influence == 300,
                    "an on-site recovery leader cannot buy escorts whose arrival estimate exceeds 24 hours");
                fixtureRouteDistance = null;
                blockedFixtureVisit = (party, site) => party == member && site == closer;
                Search(1126.125);
                Check(recoveryLeader.Army == null && recoveryClan.Influence == 300,
                    "a short arrival estimate without a legal native land path cannot authorize a paid recovery");
                blockedFixtureVisit = null;
                Field(command, "_nextSiegeSearchSlot", 0d);
                ((IDictionary)behavior.GetField("_nextSiegeSearch", flags).GetValue(command)).Clear();
                CampaignTick(1126.25);
                for (int i = 0; i < 100 && behavior.GetField("_offensiveCommit", flags).GetValue(command) == null; i++)
                    ApplicationFrame(1126.25);
                Check(behavior.GetField("_offensiveCommit", flags).GetValue(command) != null && recoveryLeader.Army == null,
                    "a ready paid recovery is proposed without charging during the sliced sweep");
                Move(recoveryLeader, 44);
                ApplicationFrame(1126.25);
                Check(recoveryLeader.Army == null && recoveryClan.Influence == 300,
                    "a stale paid recovery proposal cannot charge after its leader leaves the muster point");
                Enter(recoveryLeader, closer);
                Search(1127);
                var recovery = recoveryLeader.Army;
                Check(recovery != null && member.Army == recovery && recoveryClan.Influence == 250 &&
                    Saved<bool>(SiegeState(recovery), "PaidRecovery"), "on-site leader and reachable, timely escorts qualify for one paid retry");
                Reload(); NoNewSearch();
                Time(1128);
                int start = log.Count;
                DisbandArmyAction.ApplyByCohesionDepleted(recovery);
                dispatcher.OnArmyDispersed(recovery, Army.ArmyDispersionReason.CohesionDepleted, false);
                Tick(1128.25);
                var failure = PaidEpisodes().Values.Cast<object>().Single();
                Check(recoveryLeader.Army == null && Saved<double>(failure, "Until") == 1176 &&
                    log.Skip(start).Count(line => line.Contains("Siege paid pre-camp failure:") && line.Contains("policy=RecoveryFailed")) == 1,
                    "a different-type failure on the paid retry starts one exact 48-hour stand-down, even with duplicate native/lifecycle observations");

                recoveryLeader.LeaderHero.CanJoinArmy = false;
                var changedSite = Site("stalled-third-home", 50);
                var changedClan = ClanFor(home, "stalled-third-payer", 300);
                var laterLeader = Lord("stalled-third-leader", changedClan, 500, 50, true);
                forcePowers[member.Party] = 450;
                Enter(laterLeader, changedSite);
                Reload(); Search(1175.75);
                Check(laterLeader.Army == null && member.Army == null && changedClan.Influence == 300,
                    "a stronger roster, new leader, site and payer cannot sneak in a third paid attempt before expiry, even after reload");
                Search(1176);
                Check(laterLeader.Army != null && member.Army == laterLeader.Army && changedClan.Influence == 250 &&
                    !Saved<bool>(SiegeState(laterLeader.Army), "PaidRecovery"),
                    "exact expiry allows normal evaluation without leaving a spent-recovery flag on a new episode");
            }

            void TestPartialPaidAttempt()
            {
                foreach (string follow in new[] { "free", "free-price-drift", "existing", "owner-change" })
                {
                    World();
                    var muster = Site("partial-debit-home", 20);
                    var target = Site("partial-debit-target", 65, hostile);
                    var leader = Lord("partial-debit-leader", payer, 400, 19, true);
                    var first = Lord("partial-debit-first", ClanFor(home, "partial-debit-first", 0), 250, 22);
                    var refused = Lord("partial-debit-refused", ClanFor(home, "partial-debit-refused", 0), 250, 26);
                    payer.Influence = 350;
                    score.Minimum = 700;
                    price.PriceOverride = (_, member) => member == refused && payer.Influence <= 300 ? 101 : 50;
                    Search(1100);
                    var army = leader.Army;
                    Check(army != null && first.Army == army && refused.Army == null && payer.Influence == 300 &&
                        Saved<float>(SiegeState(army), "PaidSummons") == 50,
                        "the first actual positive debit survives a later refused summons and does not count its quoted cost");
                    Reload(); NoNewSearch(); Time(1101);
                    DisbandArmyAction.ApplyByCohesionDepleted(army);
                    Tick(1101.25);
                    Check(PaidEpisodes().Count == 1 &&
                        Saved<object>(PaidEpisodes().Values.Cast<object>().Single(), "Kind").ToString() == "Unverified",
                        "an unexplained native loss of a partially paid army does not invent early retry evidence");
                    price.PriceOverride = null;
                    Reload(); Search(1102);
                    Check(leader.Army == null && payer.Influence == 300, "unverified loss blocks fresh paid formation before any new charge");
                    if (follow == "free")
                    {
                        var companion = Lord("partial-debit-free", payer, 420, 23);
                        Search(1103);
                        Check(leader.Army != null && companion.Army == leader.Army && first.Army == null && refused.Army == null &&
                            payer.Influence == 300 && Saved<float>(SiegeState(leader.Army), "PaidSummons") == 0,
                            "stand-down still allows a genuine zero-cost formation, not a nonzero quote disguised as a free one");
                    }
                    if (follow == "free-price-drift")
                    {
                        var companion = Lord("partial-debit-price-drift", ClanFor(home, "partial-debit-price-drift", 0), 420, 23);
                        price.PriceOverride = (_, member) => member == companion && member.Army == null ? 0 : 50;
                        Search(1103);
                        Check(leader.Army != null && companion.Army == null && payer.Influence == 300 &&
                            Saved<float>(SiegeState(leader.Army), "PaidSummons") == 0,
                            "the native debit hook refuses a positive live price on a strictly free plan under stand-down");
                    }
                    if (follow == "existing")
                    {
                        forcePowers[leader.Party] = 750;
                        Ready(leader);
                        Search(1103);
                        Check(Besieges(leader, target) && payer.Influence == 300,
                            "an existing viable army ignores the paid stand-down and its physical retry floor");
                    }
                    if (follow == "owner-change")
                    {
                        var newOwner = NewKingdom("new-partial-debit-owner");
                        War(home, newOwner.Kingdom, true);
                        ((IList)enemy.Fiefs).Remove(target.Town);
                        ((IList)newOwner.Kingdom.Fiefs).Add(target.Town);
                        Field(target.Town, "_ownerClan", newOwner);
                        dispatcher.OnSettlementOwnerChanged(target, false, null, null, null, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByGift);
                        Search(1103);
                        Check(leader.Army != null && payer.Influence == 200 && PaidEpisodes().Count == 0,
                            "a real change of target faction ends the old episode and allows a legal paid formation against the new owner");
                    }
                }
            }

            void TestSlicedOffense()
            {
                World();
                Site("sliced-home", 20);
                var firstStraight = Site("sliced-first-straight", 52, hostile);
                var better = Site("sliced-nearest-native", 55, hostile);
                var leader = Lord("sliced-leader", payer, 400, 20, true);
                var escort = Lord("sliced-escort", ClanFor(home, "sliced-escort", 0), 300, 26);
                score.Minimum = 600;
                fixtureRouteDistance = (from, to) => from == 20 && to == 52 ? 70 : Math.Abs(from - to);
                int before = score.Calls;
                for (int i = 0; i < 18; i++) CampaignTick(1100 + i * .02);
                Check(score.Calls == before && leader.Army == null, "campaign catch-up ticks alone cannot spend an offensive application-frame budget");
                DrainOffensiveFrames(now);
                Check(OwnsClaim(leader.Army, better, "siege", ordered: false) && escort.Army == leader.Army && firstStraight.SiegeEvent == null,
                    "sliced sweep still picks the shortest viable native route, not the first straight-line target");

                foreach (string change in new[] { "balance", "strength", "headroom", "target", "nearer-target", "defense", "distant-defense" })
                {
                    World();
                    var homeSite = Site("stale-home", 20);
                    var goal = Site("stale-target", 55, hostile);
                    leader = Lord("stale-leader", payer, 400, 20, true);
                    escort = Lord("stale-escort", ClanFor(home, "stale-escort", 0), 300, 24);
                    score.Minimum = 600;
                    CampaignTick(1100);
                    for (int i = 0; i < 100 && behavior.GetField("_offensiveCommit", flags).GetValue(command) == null; i++) ApplicationFrame(1100);
                    Check(behavior.GetField("_offensiveCommit", flags).GetValue(command) != null && leader.Army == null,
                        $"completing the proposal cannot form or charge ({change})");
                    Army defense = null;
                    if (change == "balance") payer.Influence = 249;
                    if (change == "strength") score.Minimum = 710;
                    if (change == "headroom") score.Minimum = 650;
                    if (change == "target") War(home, enemy, false);
                    if (change == "nearer-target") Site("new-closer-legal-target", 41, hostile);
                    if (change == "defense")
                    {
                        defense = Ready(leader);
                        Camp(homeSite, Lord("urgent-defense-camp", hostile, 100, 20, true));
                        CampaignTick(1100.1);
                    }
                    if (change == "distant-defense")
                    {
                        var remote = Site("out-of-range-home-siege", 1050);
                        Camp(remote, Lord("remote-besieger", hostile, 100, 1050, true));
                        CampaignTick(1100.1);
                    }
                    ApplicationFrame(now);
                    Check((change == "defense" ? leader.Army == defense && Defends(leader, homeSite) : leader.Army == null) &&
                        escort.Army == null && payer.Influence == (change == "balance" ? 249 : 250) && goal.SiegeEvent == null,
                        $"commit discards stale {change} proposal before offensive payment; defense remains first");
                }
                World();
                Site("fair-first-home", 20);
                Site("fair-next-home", 72, hostile);
                Lord("fair-first", payer, 400, 20, true);
                Lord("fair-next", hostile, 400, 72, true);
                War(enemy, home, true);
                score.Minimum = 900;
                CampaignTick(1100);
                DrainOffensiveFrames(1100);
                CampaignTick(1110);
                DrainOffensiveFrames(1110);
                var timers = (IDictionary)behavior.GetField("_nextSiegeSearch", flags).GetValue(command);
                Check(timers[home] is double firstDue && firstDue == 1108 && timers[enemy] is double nextDue && nextDue == 1118,
                    "a long sweep cannot give a returning kingdom priority over an unvisited due kingdom");
            }

            void TestLiveSiegeSweepInvalidation()
            {
                foreach (string changed in new[] { "army-leader", "payer", "escort" })
                {
                    World();
                    Site("sliced-invalidation-home", 20);
                    Site("sliced-invalidation-enemy", 55, hostile);
                    var leader = Lord("sliced-invalidation-leader", payer, 400, 20, true);
                    var escort = changed == "army-leader" ? null :
                        Lord("sliced-invalidation-escort", ClanFor(home, "sliced-invalidation-escort", 0), 350, 24);
                    payer.Influence = 300;
                    score.Minimum = changed == "army-leader" ? 350 : 600;
                    var ready = changed == "army-leader" ? Ready(leader) : null;
                    var steps = (IEnumerable<int>)behavior.GetMethod("AllocateSiege", flags).Invoke(command, new object[] { home, 1100d });
                    int initialQuotes = changed == "army-leader" ? score.Calls : price.Quotes;
                    using (var sweep = steps.GetEnumerator())
                    {
                        while ((changed == "army-leader" ? score.Calls : price.Quotes) == initialQuotes && sweep.MoveNext()) { }
                        Check((changed == "army-leader" ? score.Calls : price.Quotes) > initialQuotes,
                            "the offensive sweep is suspended just after a native quote for " + changed);
                        if (changed == "army-leader")
                            Property(ready, "LeaderParty", null);
                        else
                            Field((changed == "payer" ? leader : escort).LeaderHero, "_clan", null);
                        while (sweep.MoveNext()) { }
                        Check(behavior.GetField("_offensiveCommit", flags).GetValue(command) == null &&
                            leader.DefaultBehavior != AiBehavior.BesiegeSettlement && payer.Influence == 300,
                            "a native object invalidated between quotes is discarded without a crash, proposal or debit: " + changed);
                    }
                }

                World();
                Site("sliced-fault-home", 20);
                var target = Site("sliced-fault-enemy", 55, hostile);
                var payerLeader = Lord("sliced-fault-leader", payer, 400, 20, true);
                var candidate = Lord("sliced-fault-escort", ClanFor(home, "sliced-fault-escort", 0), 350, 24);
                payer.Influence = 300;
                score.Minimum = 600;
                score.OverrideMinimum = _ => throw new NullReferenceException("fixture missing campaign data");
                int mark = log.Count;
                Search(1100);
                Check(payerLeader.Army == null && candidate.Army == null && payer.Influence == 300 && !SweepPending() &&
                    log.Skip(mark).Any(line => line.Contains("offensive sweep aborted on missing campaign data") &&
                        line.Contains("fixture missing campaign data")),
                    "a null fault in the read-only scan discards its proposal, logs evidence, and spends nothing");
                score.OverrideMinimum = null;
                Search(1108);
                Check(OwnsClaim(payerLeader.Army, target, "siege", ordered: false) &&
                    candidate.Army == payerLeader.Army && payer.Influence == 250,
                    "after a failed sweep the next valid search can still form and pay normally");
            }

            void TestTransientMuster()
            {
                foreach (bool recovery in new[] { true, false })
                {
                    World();
                    var site = Site("confirmation-home", 20);
                    var target = Site("confirmation-enemy", 55, hostile);
                    var leader = Lord("confirmation-leader", payer, 400, 19, true);
                    var escort = Lord("confirmation-paid", ClanFor(home, "confirmation-member", 0), 300, 24);
                    score.Minimum = 625;
                    Search(1100);
                    var paid = leader.Army;
                    NoNewSearch();
                    score.Minimum = 876;
                    Tick(1100.25);
                    Check(paid != null && leader.Army == paid && escort.Army == paid && payer.Influence == 200 &&
                        OwnsClaim(paid, target, "siege", ordered: false) && target.SiegeEvent == null,
                        "unchanged traveling paid force survives one failed projection without a new order, upkeep or summons");
                    if (recovery) score.Minimum = 700;
                    Tick(1100.5);
                    Check((recovery ? leader.Army == paid && escort.Army == paid : Idle(leader) && escort.Army == paid) &&
                        payer.Influence == 200 && Retries().Count == 0 && target.SiegeEvent == null,
                        "next scheduled check either retains the recovered army or closes a sustained failure without repulse");
                    if (recovery)
                    {
                        Enter(leader, site); Enter(escort, site);
                        score.Minimum = 625;
                        Tick(1100.75);
                        Check(OwnsClaim(paid, target, "siege") && payer.Influence == 200,
                            "recovered paid army still attaches and passes the live native check before departure");
                    }
                }
            }

            void TestCoordinationObserver(string caseName)
            {
                World();
                var ally = NewKingdom("observer-formal-ally");
                War(ally.Kingdom, enemy, true);
                Alliance(home, ally.Kingdom, true);
                var castle = Site("observer-home-castle", 40, castle: true);
                bool understrength = caseName.StartsWith("understrength-", StringComparison.Ordinal);
                var town = Site("observer-allied-town", 80, understrength ? payer : ally, castle: understrength);
                var first = Lord("observed-home-routed-army", payer, 400, 15, true);
                var second = Lord("other-observed-army", caseName == "different-kingdom" ? ally : payer, 400, 38,
                    caseName == "different-kingdom");
                var firstArmy = Ready(first);
                var secondArmy = Ready(second);
                var third = understrength ? Lord("third-observed-army", payer, 750, 26) : null;
                var thirdArmy = understrength ? Ready(third) : null;
                if (!understrength) blockedFixtureRoute = (from, to) => from == 15 && to == 80;
                var temp = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maio-coordination-" + Guid.NewGuid().ToString("N")));
                string id = Guid.NewGuid().ToString("N");
                string Receipt(string kind) => System.IO.Path.Combine(temp.FullName, $"acceptance-coordination-v1.{id}.{kind}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(temp.FullName, "acceptance-coordination-v1.armed"), "coordination-v1 " + id);
                object Runner() => Activator.CreateInstance(acceptance, flags, null, new object[]{command, sessionLog, temp.FullName}, null);
                var runner = Runner();
                object Run(string method, params object[] args) => acceptance.GetMethod(method, flags).Invoke(runner, args);
                object Read(string field) => acceptance.GetField(field, flags).GetValue(runner);
                long wall = DateTime.UtcNow.Ticks;
                int popups = 0;
                void Popup(InquiryData data, bool pause, bool priority) { if (data.Text.Contains(id)) popups++; }
                InformationManager.OnShowInquiry += Popup;
                try
                {
                    Run("OnLoaded");
                    var store = new Store { IsSaving = true };
                    ((CampaignBehaviorBase)runner).SyncData(store);
                    if (caseName == "offense-only" || caseName == "diversion" || caseName == "joint-diversion")
                    {
                        var left = Site("observer-offense-left", 14, hostile);
                        var right = Site("observer-offense-right", 39, hostile);
                        Search(1100); Search(1100.25);
                        Check(OwnsClaim(firstArmy, left, "siege") && OwnsClaim(secondArmy, right, "siege"),
                            "observer sees two real same-kingdom offensive departures before any potential diversion");
                        if (caseName != "offense-only")
                        {
                            Move(first, 14); Move(second, 39);
                            Camp(left, first); Camp(right, second);
                            var defense = caseName == "diversion" ? town : castle;
                            Camp(defense, Lord("mid-offense-relief-camp", hostile, caseName == "joint-diversion" ? 600 : 300,
                                defense.GatePosition.X, true));
                            NoNewSearch(); Tick(1100.5);
                        }
                    }
                    else if (understrength)
                    {
                        // Higher is a town, lower a castle in this model: do
                        // not rely on the fake X-based settlement value.
                        Field(castle.Town, "_isCastle", false);
                        var lowerCamp = Camp(town, Lord("original-lower-home-camp", hostile, 1700, 80, true));
                        NoNewSearch();
                        if (caseName != "understrength-fresh")
                        {
                            Tick(1100);
                            Check(new[]{firstArmy, secondArmy, thirdArmy}.All(army => OwnsClaim(army, town, "home")),
                                "all three native orders cover the original home siege before the transfer");
                        }
                        if (caseName == "understrength-lost-route") blockedFixtureRoute = (from, to) => to == 80;
                        Camp(castle, Lord("new-higher-home-camp", hostile, 1700, 40));
                        Tick(1100.25);
                        Check(new[]{firstArmy, secondArmy, thirdArmy}.All(army => OwnsClaim(army, castle, "home")) &&
                            town.SiegeEvent == lowerCamp && DuplicateClaims().Length == 0,
                            "all three understrength armies have contemporaneous native orders at the higher defense, and the lower siege remains open");
                    }
                    else
                    {
                        Camp(castle, Lord("observer-home-camp", hostile, caseName == "joint" ? 600 : 300, 40, true));
                        Camp(town, Lord("observer-ally-camp", hostile, 300, 80));
                        NoNewSearch();
                        Tick(1100);
                        if (caseName == "non-overlapping")
                        {
                            Check(OwnsClaim(firstArmy, castle, "home") && OwnsClaim(secondArmy, town, "allied"),
                                "non-overlap fixture gets real orders before sampling");
                            Lift(castle); Tick(1100.25);
                        }
                    }
                    Run("Tick", 1100.5d, wall);
                    bool pass = caseName == "split" || caseName == "joint" || caseName == "diversion" ||
                        caseName == "joint-diversion" || caseName == "understrength-transfer";
                    if (!pass)
                    {
                        Check(Read("_result") == null, $"observer rejects {caseName} evidence even when production orders exist");
                        Run("Tick", 1460d, wall);
                    }
                    string result = (string)Read("_result");
                    Check(result != null && result.Contains(pass ? "PASS:" : "INCONCLUSIVE:") &&
                        result.Contains("production orders left intact") && DuplicateClaims().Length == 0 &&
                        (caseName == "non-overlapping" || first.Army == firstArmy) && second.Army == secondArmy,
                        $"read-only observer returns the expected bounded verdict for {caseName} without removing production orders");
                    Check(System.IO.File.Exists(Receipt("claimed")) && System.IO.File.Exists(Receipt("result")),
                        "coordination run has a unique claim and terminal receipt");
                    if (caseName == "split")
                    {
                        Run("Notify");
                        store.IsSaving = false;
                        runner = Runner();
                        ((CampaignBehaviorBase)runner).SyncData(store);
                        Run("OnLoaded"); Run("Notify");
                        Check(popups == 1 && System.IO.File.Exists(Receipt("shown")) &&
                            OwnsClaim(firstArmy, castle, "home") && OwnsClaim(secondArmy, town, "allied"),
                            "stale observer reload cannot replay the run or its popup or alter the split");
                    }
                }
                finally
                {
                    InformationManager.OnShowInquiry -= Popup;
                    foreach (var file in temp.GetFiles()) file.Delete();
                    temp.Delete();
                }
            }
        }

        void TestReliefAcceptance(bool generateCamp = false, bool allied = false, bool ineligible = false, bool unreachable = false)
        {
            World();
            var controlled = (CampaignBehaviorBase)Activator.CreateInstance(behavior, flags, null, new[]{sessionLog, (object)true}, null);
            campaign.AddCampaignBehaviorManager(new CampaignBehaviorManager(new CampaignBehaviorBase[]{command, controlled, alliances}));
            controlled.RegisterEvents();
            Field(controlled, "_ready", true);
            var muster = Site("relief-observer-muster", 0);
            var owner = allied ? NewKingdom("acceptance-actual-owner") : payer;
            if (allied)
            {
                War(owner.Kingdom, enemy, true);
                if (!ineligible) Alliance(home, owner.Kingdom, true);
            }
            var target = Site("relief-observer-town", 40, owner);
            var attacker = Lord("observed-attacker", hostile, 600, generateCamp ? 50 : 40, true);
            var defender = Lord("production-defender", payer, 400, 5, true);
            Army response = null;
            if (generateCamp)
            {
                var frontier = Site("hostile-frontier-guard", 50, hostile);
                graph.Links.Clear();
                graph.Link(frontier, target);
                // The relief observer's idle-cleanup case needs a neighboring fort
                // too strong for the relieved army to take on the return leg.
                score.OverrideMinimum = site => site == frontier ? 1000 : score.Minimum;
                Enter(attacker, frontier);
                Enter(defender, muster);
                if (unreachable) blockedFixtureRoute = (from, to) => from == 0 && to == 40;
            }
            else
            {
                Camp(target, attacker);
                response = Ready(defender);
            }
            var homeOnly = allied ? Lord("owner-response-not-allied-evidence", owner, 80, 32, true) : null;
            if (homeOnly != null) Ready(homeOnly);
            var map = Blank<MapState>();
            var games = Blank<GameStateManager>();
            Field(games, "_gameStates", new MBList<GameState>(new[]{map}));
            var game = Blank<Game>();
            Property(game, "GameStateManager", games);
            var playerCharacter = Blank<CharacterObject>();
            Property(playerCharacter, "HeroObject", player.LeaderHero);
            game.PlayerTroop = playerCharacter;
            gameFrame.SetValue(null, game);
            var temp = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maio-relief-" + Guid.NewGuid().ToString("N")));
            var id = Guid.NewGuid().ToString("N");
            string scenario = allied ? "allied-relief-v1" : "relief-v1";
            var arm = System.IO.Path.Combine(temp.FullName, $"acceptance-{scenario}.armed");
            string Receipt(string kind) => System.IO.Path.Combine(temp.FullName, $"acceptance-{scenario}.{id}.{kind}");
            System.IO.File.WriteAllText(arm, scenario + " " + id);
            object Runner() => Activator.CreateInstance(acceptance, flags, null, new object[]{command, sessionLog, temp.FullName, controlled}, null);
            void Run(object instance, string method, params object[] args) => acceptance.GetMethod(method, flags).Invoke(instance, args);
            var runner = Runner();
            object Read(object instance, string field) => acceptance.GetField(field, flags).GetValue(instance);
            var wall = DateTime.UtcNow.Ticks;
            void Step(double hour)
            {
                Tick(hour);
                behavior.GetMethod("OnTick", flags).Invoke(controlled, new object[]{0f});
                Run(runner, "Tick", hour, wall);
            }
            int popups = 0;
            void Popup(InquiryData data, bool pause, bool priority) { if (data.Text.Contains(id)) popups++; }
            var inquiry = InformationManager.IsAnyInquiryActiveInternal;
            InformationManager.IsAnyInquiryActiveInternal = () => false;
            InformationManager.OnShowInquiry += Popup;
            try
            {
                Run(runner, "OnLoaded");
                Step(1100);
                if (unreachable)
                {
                    Step(1460);
                    string result = (string)Read(runner, "_result");
                    Check(result.Contains("INCONCLUSIVE") && result.Contains("eligibleAllianceWarTriads=2") &&
                        result.Contains("noDefenderMusterOrLeader=") && (int)Read(runner, "_attempts") == 0 && attacker.Army == null,
                        "an eligible triad without a reachable reliever produces a diagnostic inconclusive without spending on a test attack");
                    return;
                }
                if (ineligible)
                {
                    Move(homeOnly, 42);
                    Step(1100.5);
                    Check(Defends(homeOnly, target) && defender.DefaultBehavior == AiBehavior.Hold &&
                        Read(runner, "_verdict") == null, "a physical home-kingdom response cannot satisfy the allied-only marker");
                    Step(1460);
                    Check(((string)Read(runner, "_result")).Contains("INCONCLUSIVE") &&
                        ((string)Read(runner, "_result")).Contains("eligibleAllianceWarTriads=0") && (int)Read(runner, "_attempts") == 0,
                        "no eligible triad yields a diagnostic one-shot inconclusive and no test attack");
                    Run(runner, "Frame", campaign);
                    Run(runner, "Frame", campaign);
                    Check(popups == 1 && Defends(homeOnly, target), "an inconclusive run shows once and leaves the real siege response alone");
                    return;
                }
                double contact = 1100.5;
                Army attackingArmy = null;
                if (generateCamp)
                {
                    attackingArmy = (Army)Read(runner, "_original");
                    Step(1100.25);
                    Check(attackingArmy != null && attackingArmy == attacker.Army && attacker.DefaultBehavior == AiBehavior.DefendSettlement &&
                        target.SiegeEvent == null && defender.Army == null && behavior.GetField("_army", flags).GetValue(command) == null,
                        "no-siege fallback uses the existing frontier guard and cannot invent a siege or a defender");
                    behavior.GetMethod("RequestReview", flags).Invoke(controlled, null);
                    Step(1101.25);
                    Check(attacker.Army == attackingArmy && attacker.DefaultBehavior == AiBehavior.BesiegeSettlement &&
                        target.SiegeEvent == null && Read(runner, "_verdict") == null,
                        "controlled siege order has no effect on relief until native camp contact");
                    Move(attacker, 40);
                    contact = 1102;
                    Camp(target, attacker);
                    Step(contact);
                    response = defender.Army;
                    Check((bool)Read(runner, "_campContact") && response != null && response != attackingArmy && Defends(defender, target) &&
                        Read(runner, "_verdict") == null,
                        "fallback camp starts independent production defense but is not itself a pass");
                }
                else
                {
                    Step(contact);
                    Check(Defends(defender, target) && Read(runner, "_result") == null &&
                        (int)Read(runner, "_attempts") == 0 && System.IO.File.Exists(Receipt("claimed")),
                        "armed relief observes a pre-existing native siege without a test attack or credit for the defend order alone");
                }
                Move(defender, 42);
                typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(defender, new object[]{AiBehavior.Hold, null});
                var state = new Store { IsSaving = true };
                ((CampaignBehaviorBase)runner).SyncData(state);
                Step(contact + .5);
                string verdict = (string)(Read(runner, "_result") ?? Read(runner, "_verdict"));
                Check(verdict.Contains("PASS: production army") && verdict.Contains("settlement saved was not established") &&
                    defender.Army == response && Defends(defender, target) && target.SiegeEvent != null &&
                    (!allied || (verdict.Contains("defended allied") && verdict.Contains("owned by acceptance-actual-owner"))),
                    "live-mode acceptance requires physical production relief and never cancels a natural siege or the defenders");
                if (generateCamp)
                {
                    Step(contact + .75);
                    Check(attacker.Army == null && target.SiegeEvent == null && ((string)Read(runner, "_result")).Contains("cleanup=no owned assignment left"),
                        "automatic acceptance cleanup withdraws the test attacker through native camp cleanup");
                    Step(contact + 1);
                    Check(Idle(defender) && defender.Army == response && log.Any(line => line.Contains("target=relief-observer-town") &&
                        line.Contains("reason=siege-ended-owner-retained")), "production preserves the now-idle defender after acceptance cleanup");
                }
                Run(runner, "Frame", campaign);
                state.IsSaving = false;
                runner = Runner();
                ((CampaignBehaviorBase)runner).SyncData(state);
                Run(runner, "OnLoaded");
                Run(runner, "Frame", campaign);
                Check(popups == 1 && System.IO.File.Exists(Receipt("shown")),
                    "relief receipts survive reload and the single popup is not repeated");
            }
            finally
            {
                InformationManager.OnShowInquiry -= Popup;
                InformationManager.IsAnyInquiryActiveInternal = inquiry;
                foreach (var file in temp.GetFiles()) file.Delete();
                temp.Delete();
            }
        }
    }
}

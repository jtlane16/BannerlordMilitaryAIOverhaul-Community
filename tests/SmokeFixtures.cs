using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

public static partial class SmokeFixtures
{
    private static readonly BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static T Blank<T>() { return (T)FormatterServices.GetUninitializedObject(typeof(T)); }
    private static void Field(object obj, string name, object value) { obj.GetType().GetField(name, flags).SetValue(obj, value); }
    private static void Property(object obj, string name, object value) { obj.GetType().GetProperty(name, flags).SetValue(obj, value, null); }
    private static void Check(bool ok, string purpose) { if (!ok) throw new Exception(purpose); Console.WriteLine("PASS: " + purpose); }
    private static readonly List<string> log = new List<string>();
    private static readonly List<Army.ArmyDispersionReason> nativeDispersals = new List<Army.ArmyDispersionReason>();
    private static readonly Dictionary<PartyBase, float> forcePowers = new Dictionary<PartyBase, float>();
    private static int powerReads;
    private static int rolls;
    private static bool CaptureLog(string __0) { log.Add(__0); return false; }
    private static bool CaptureBatch(string __0)
    {
        log.AddRange(__0.Split(new[]{Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries));
        return false;
    }
    private static bool ReadFixturePower(PartyBase __instance, BattleSideEnum __0, MapEvent.PowerCalculationContext __1, ref float __result)
    {
        if (__0 == BattleSideEnum.Defender && __1 == MapEvent.PowerCalculationContext.Estimated)
            ++powerReads;
        else if (!reliefEnabled || __1 != MapEvent.PowerCalculationContext.PlainBattle)
            throw new Exception("Force inventory requested the wrong military power context.");
        __result = forcePowers[__instance];
        return false;
    }
    private static bool CaptureDisperse(Army.ArmyDispersionReason __0) { nativeDispersals.Add(__0); return false; }
    private static float FixedRoll() { ++rolls; return 0f; }
    private static IEnumerable<CodeInstruction> ForceDispersionRoll(IEnumerable<CodeInstruction> il)
    {
        var code = il.ToList();
        var call = code.Single(i => i.Calls(AccessTools.PropertyGetter(typeof(MBRandom), "RandomFloat")));
        call.opcode = OpCodes.Call;
        call.operand = typeof(SmokeFixtures).GetMethod("FixedRoll", flags);
        return code;
    }

    private sealed class Store : IDataStore
    {
        private readonly Dictionary<string, object> records = new Dictionary<string, object>();
        public int Count { get { return records.Count; } }
        public bool IsSaving { get; set; }
        public bool IsLoading { get { return !IsSaving; } }
        public bool SyncData<T>(string key, ref T data)
        {
            if (IsSaving) { records.Add(key, data); return true; }
            object value;
            if (!records.TryGetValue(key, out value)) return false;
            data = (T)value;
            return true;
        }
    }

    private sealed class EligibilityReceiver : CampaignEventReceiver
    {
        public Hero IneligibleLeader { get; set; }

        public override void CanHeroLeadParty(Hero hero, ref bool result)
        {
            if (hero == IneligibleLeader)
                result = false;
        }
    }

    private static MobileParty Party(string id)
    {
        var p = Blank<MobileParty>();
        p.StringId = id;
        p.IsActive = true;
        Field(p, "_attachedParties", new MBList<MobileParty>());
        Property(p, "Party", new PartyBase(p));
        return p;
    }
    private static Army Formation(MobileParty lead, params MobileParty[] members)
    {
        var a = Blank<Army>();
        Property(a, "LeaderParty", lead);
        a.Cohesion = 25;
        Field(a, "_parties", new MBList<MobileParty>(new[]{lead}.Concat(members)));
        foreach (var p in a.Parties) Field(p, "_army", a);
        return a;
    }

    private static Kingdom FixtureKingdom(string id)
    {
        var kingdom = Blank<Kingdom>();
        kingdom.StringId = id;
        Property(kingdom, "Name", new TextObject("{=!}" + id));
        Field(kingdom, "_armies", new MBList<Army>());
        Field(kingdom, "_alliedKingdoms", new MBList<Kingdom>());
        Field(kingdom, "_warPartyComponentsCache", new MBList<WarPartyComponent>());
        Field(kingdom, "_clans", new MBList<Clan>());
        return kingdom;
    }

    private static void FixtureLord(MobileParty party, Clan clan, float power, bool list = true)
    {
        var hero = Blank<Hero>();
        Field(hero, "_heroState", Hero.CharacterStates.Active);
        Field(hero, "_clan", clan);
        var component = Blank<LordPartyComponent>();
        Field(component, "_leader", hero);
        Field(component, "_cachedName", new TextObject("{=!}" + party.StringId));
        typeof(PartyComponent).GetProperty("MobileParty", flags).SetValue(component, party, null);
        Field(party, "_partyComponent", component);
        Property(party, "IsLordParty", true);
        Property(party, "Ai", Blank<MobilePartyAi>());
        party.ActualClan = clan;
        if (list)
            ((IList)clan.Kingdom.WarPartyComponents).Add(component);
        forcePowers.Add(party.Party, power);
    }

    private static void TestKingdomForces(Assembly mod, Type logType, object sessionLog, Harmony harmony, Campaign campaign, object tracker, MobileParty player)
    {
        var fixture = typeof(SmokeFixtures);
        harmony.Patch(typeof(PartyBase).GetMethod("GetCustomStrength", flags),
            prefix: new HarmonyMethod(fixture.GetMethod("ReadFixturePower", flags)));

        var mixed = FixtureKingdom("inventory-mixed");
        var empty = FixtureKingdom("inventory-empty");
        var other = FixtureKingdom("inventory-other");
        var objects = Blank<CampaignObjectManager>();
        Field(objects, "_kingdoms", new MBList<Kingdom>(new[]{mixed, empty, other}));
        Property(campaign, "CampaignObjectManager", objects);
        var events = new CampaignEvents();
        Property(campaign, "CampaignEvents", events);
        campaign.AddCampaignBehaviorManager(new CampaignBehaviorManager(new CampaignBehaviorBase[0]));
        var eligibility = new EligibilityReceiver();
        var dispatcher = (CampaignEventDispatcher)Activator.CreateInstance(typeof(CampaignEventDispatcher), flags, null,
            new object[]{new CampaignEventReceiver[]{events, eligibility}}, null);
        Property(campaign, "CampaignEventDispatcher", dispatcher);
        var playerClan = Blank<Clan>();
        Field(playerClan, "_kingdom", mixed);
        Property(campaign, "PlayerDefaultFaction", playerClan);
        var clan = Blank<Clan>();
        Field(clan, "_kingdom", mixed);
        var otherClan = Blank<Clan>();
        Field(otherClan, "_kingdom", other);
        FixtureLord(player, playerClan, 150, list: false);

        var free = Party("inventory-free");
        var leader = Party("inventory-leader");
        var attached = Party("inventory-attached");
        var traveler = Party("inventory-traveler");
        var playerMember = Party("inventory-player-member");
        var veto = Party("inventory-veto");
        var vetoLeader = Party("inventory-veto-leader");
        var vetoFollower = Party("inventory-veto-follower");
        var busy = Party("inventory-busy");
        var zero = Party("inventory-zero");
        var disband = Party("inventory-disband");
        var foreign = Party("inventory-foreign");
        FixtureLord(free, clan, 40);
        FixtureLord(leader, clan, 100);
        FixtureLord(attached, clan, 30, list: false);
        FixtureLord(traveler, clan, 20);
        FixtureLord(playerMember, clan, 70);
        FixtureLord(veto, playerClan, 42);
        FixtureLord(vetoLeader, playerClan, 45);
        FixtureLord(vetoFollower, clan, 46);
        FixtureLord(busy, clan, 22);
        FixtureLord(zero, clan, 0);
        FixtureLord(disband, clan, 21);
        FixtureLord(foreign, otherClan, 14);
        veto.LeaderHero.CanJoinArmy = false;
        vetoLeader.LeaderHero.CanJoinArmy = false;
        disband.IsDisbanding = true;
        var battle = Blank<MapEvent>();
        var side = Blank<MapEventSide>();
        Field(side, "_mapEvent", battle);
        Field(busy.Party, "_mapEventSide", side);
        var army = Formation(leader, attached, traveler, foreign);
        var playerArmy = Formation(player, playerMember);
        var vetoArmy = Formation(vetoLeader, vetoFollower);
        var mixedArmies = (IList)mixed.Armies;
        foreach (var item in new[]{army, playerArmy, vetoArmy})
        {
            mixedArmies.Add(item);
            Field(item, "_kingdom", mixed);
        }
        Field(attached, "_attachedTo", leader);
        ((IList)leader.AttachedParties).Add(attached);
        ((IList)mixed.WarPartyComponents).Add(traveler.WarPartyComponent);
        var city = Blank<Settlement>();
        city.StringId = "inventory-town";
        Field(free, "_currentSettlement", city);
        Field(free, "_position", new CampaignVec2(new Vec2(10, 20), true));
        clan.Influence = 8;
        var beforeArmy = army.Parties.ToArray();

        var reportingType = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.MilitaryCommandBehavior", true);
        var reporting = (CampaignBehaviorBase)Activator.CreateInstance(reportingType, flags, null, new[]{sessionLog}, null);
        reporting.RegisterEvents();
        const long ticksPerHour = 1000;
        typeof(CampaignTime).GetField("TimeTicksPerHour", flags).SetValue(null, ticksPerHour);
        int firstLog = log.Count;
        dispatcher.Tick(0f);
        Check(log.Count == firstLog, "force inventory does not run before campaign launch");
        dispatcher.OnAfterSessionStart(null);
        dispatcher.Tick(0f);
        var first = log.Skip(firstLog).ToArray();
        Func<string[], string, string> row = (lines, id) => lines.Single(line => line.Contains("Force party") && line.Contains(" party=" + id + " "));
        var summary = first.Single(line => line.Contains("Force inventory trigger=initial kingdom=inventory-mixed"));
        Check(first.Count(line => line.Contains("Force inventory trigger=initial")) == 3 && powerReads == 13 &&
            first.Count(line => line.Contains("Force party")) == 13 &&
            summary.Contains("armies=3 parties=13 available=1 availableStrength=40 committed=3 committedPresentStrength=130 committedTravelingStrength=20") &&
            summary.Contains("excluded=9 excludedStrength=410 observedStrength=600 assignableStrength=190"),
            "every kingdom is reported and each party contributes to exactly one kingdom/category total");
        Check(first.Any(line => line.Contains("kingdom=inventory-empty") && line.Contains("armies=0 parties=0") && line.Contains("observedStrength=0")) &&
            first.Any(line => line.Contains("kingdom=inventory-other") && line.Contains("parties=0")),
            "empty kingdom and kingdom whose only party is in another registered army receive explicit zero totals");
        Check(row(first, leader.StringId).Contains("role=Leader availability=committed") &&
            row(first, attached.StringId).Contains("role=Attached availability=committed") &&
            row(first, traveler.StringId).Contains("role=Traveling availability=committed") &&
            first.Any(line => line.Contains("Force army") && line.Contains("leader=inventory-leader") &&
                line.Contains("present=2 presentStrength=130 traveling=2 travelingStrength=34 assignablePresentStrength=130 assignableTravelingStrength=20")),
            "leader, attached party, summons and excluded foreign member have distinct physical and assignable strengths");
        Check(row(first, player.StringId).Contains("reason=player-main-party") && row(first, player.StringId).Contains("assignableStrength=0") &&
            row(first, playerMember.StringId).Contains("reason=player-led-army") && row(first, playerMember.StringId).Contains("assignableStrength=0") &&
            first.Any(line => line.Contains("Force army") && line.Contains("leader=player") && line.Contains("assignablePresentStrength=0 assignableTravelingStrength=0")),
            "player main party and all player-led army members provide zero assignable power");
        Check(row(first, veto.StringId).Contains("reason=player-clan-opt-out") && row(first, vetoLeader.StringId).Contains("reason=player-clan-opt-out") &&
            row(first, vetoFollower.StringId).Contains("reason=army-leader-unavailable") &&
            row(first, busy.StringId).Contains("reason=in-map-event") && row(first, disband.StringId).Contains("reason=disbanding") &&
            row(first, zero.StringId).Contains("reason=no-fighting-strength"),
            "opt-out is a hard veto including for the leader, and fighting/disbanding/zero parties are explained");
        Check(row(first, free.StringId).Contains("position=(10,20,land) settlement=inventory-town") &&
            row(first, free.StringId).Contains("availability=available") && clan.Influence == 8f &&
            army.Parties.SequenceEqual(beforeArmy) && traveler.Army == army && traveler.AttachedTo == null &&
            attached.AttachedTo == leader && free.Army == null && free.DefaultBehavior == AiBehavior.Hold &&
            veto.LeaderHero.CanJoinArmy == false && army.Cohesion == 25f,
            "reporting reads positions without changing influence, orders, cohesion, toggles, or army membership");

        int afterInitial = log.Count;
        Field(free, "_position", new CampaignVec2(new Vec2(12, 23), false));
        forcePowers[free.Party] = 43;
        veto.LeaderHero.CanJoinArmy = true;
        Field(traveler, "_attachedTo", leader);
        ((IList)leader.AttachedParties).Add(traveler);
        Field(tracker, "_numTicks", 23L * ticksPerHour);
        dispatcher.Tick(0f);
        dispatcher.HourlyTick();
        Check(log.Count == afterInitial && powerReads == 13, "position and roster changes do not trigger unscheduled reports");
        Field(tracker, "_numTicks", 24L * ticksPerHour);
        dispatcher.HourlyTick();
        dispatcher.HourlyTick();
        var refresh = log.Skip(afterInitial).ToArray();
        Check(refresh.Count(line => line.Contains("Force inventory trigger=refresh")) == 3 && powerReads == 26 &&
            row(refresh, free.StringId).Contains("position=(12,23,sea)") && row(refresh, free.StringId).Contains("strength=43") &&
            row(refresh, traveler.StringId).Contains("role=Attached availability=committed") &&
            row(refresh, veto.StringId).Contains("availability=available") &&
            refresh.Any(line => line.Contains("committedPresentStrength=150 committedTravelingStrength=0")),
            "bounded refresh rereads native strength, movement, opt-out, and arrival, once at the interval");
        int afterRefresh = log.Count;
        Field(attached, "_actualClan", playerClan);
        Field(attached.LeaderHero, "_clan", playerClan);
        attached.LeaderHero.CanJoinArmy = false;
        var siege = Blank<SiegeEvent>();
        Field(siege, "BesiegedSettlement", city);
        Property(city, "SiegeEvent", siege);
        Field(tracker, "_numTicks", 48L * ticksPerHour);
        dispatcher.HourlyTick();
        var underSiege = log.Skip(afterRefresh).ToArray();
        Check(row(underSiege, attached.StringId).Contains("role=Attached availability=excluded reason=player-clan-opt-out") &&
            row(underSiege, attached.StringId).Contains("strength=30 assignableStrength=0") &&
            row(underSiege, free.StringId).Contains("availability=excluded reason=inside-besieged-settlement") &&
            underSiege.Any(line => line.Contains("present=3 presentStrength=150 traveling=1 travelingStrength=14 assignablePresentStrength=120")),
            "opted-out attached troops stay physically present but contribute no assignable power, and sieged parties are unavailable");

        // Native preparation has a SiegeEvent/BesiegerCamp, no map event;
        // joining the actual fight adds a distinct MapEvent to the party.
        var camp = new BesiegerCamp(siege, mixed);
        Field(camp, "_leaderParty", leader);
        Field(siege, "BesiegerCamp", camp);
        foreach (var party in new[]{leader, attached, traveler, veto, player, playerMember})
            Field(party, "_besiegerCamp", camp);
        attached.LeaderHero.CanJoinArmy = true;
        eligibility.IneligibleLeader = leader.LeaderHero;

        string[] AtHour(int hour)
        {
            int offset = log.Count;
            Field(tracker, "_numTicks", hour * ticksPerHour);
            dispatcher.HourlyTick();
            return log.Skip(offset).ToArray();
        }
        string AiArmyLine(string[] lines) =>
            lines.Single(line => line.Contains("Force army") && line.Contains("leader=inventory-leader "));

        var campReport = AtHour(72);
        Check(!leader.LeaderHero.CanLeadParty() && row(campReport, leader.StringId).Contains("role=Leader availability=committed reason=attacking-siege-camp") &&
            row(campReport, attached.StringId).Contains("role=Attached availability=committed reason=attacking-siege-camp") &&
            AiArmyLine(campReport).Contains("present=3 presentStrength=150 traveling=1 travelingStrength=14 assignablePresentStrength=150 assignableTravelingStrength=0") &&
            AiArmyLine(campReport).Contains("siegeSide=attacker siegeTarget=inventory-town mapEvent=-"),
            "attacking camp commits the AI leader and attached power once without re-running muster eligibility");
        Check(row(campReport, free.StringId).Contains("reason=inside-besieged-settlement") &&
            row(campReport, free.StringId).Contains("assignableStrength=0 siegeSide=defender siegeTarget=inventory-town") &&
            row(campReport, veto.StringId).Contains("reason=other-siege-commitment") &&
            row(campReport, player.StringId).Contains("reason=player-main-party") &&
            row(campReport, playerMember.StringId).Contains("reason=player-led-army"),
            "trapped defender and independent besieger are unavailable; player-led attackers remain excluded");
        var takeoverType = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.ArmyTakeoverBehavior", true);
        Check(((string)takeoverType.GetMethod("ReleaseBlockers", flags).Invoke(null, new object[]{army})).Contains("siege(inventory-town)"),
            "an assignable attacking camp remains blocked from Stage 0 takeover release");

        var assault = Blank<MapEvent>();
        var assaultComponent = Blank<SiegeAssaultEventComponent>();
        Field(assaultComponent, "_eventType", MapEvent.BattleTypes.Siege);
        Property(assault, "Component", assaultComponent);
        Field(assault, "_state", MapEventState.Wait);
        var siegeSide = Blank<MapEventSide>();
        Field(siegeSide, "_mapEvent", assault);
        foreach (var party in new[]{leader, attached, traveler})
            Field(party.Party, "_mapEventSide", siegeSide);
        var assaultReport = AtHour(96);
        Check(row(assaultReport, leader.StringId).Contains("availability=excluded reason=in-map-event") &&
            row(assaultReport, attached.StringId).Contains("assignableStrength=0 siegeSide=attacker siegeTarget=inventory-town mapEvent=Siege") &&
            AiArmyLine(assaultReport).Contains("presentStrength=150") && AiArmyLine(assaultReport).Contains("assignablePresentStrength=0"),
            "a siege assault is a tactical lock even while the native battle event is in Wait state");
        Field(assault, "_state", MapEventState.WaitingRemoval);
        var settled = AiArmyLine(AtHour(120));
        Check(settled.Contains("assignablePresentStrength=150") && settled.Contains("siegeSide=attacker siegeTarget=inventory-town mapEvent=-"),
            "a finalized battle reference no longer locks a surviving army still in camp");

        var fieldBattle = Blank<MapEvent>();
        Property(fieldBattle, "Component", Blank<FieldBattleEventComponent>());
        Field(fieldBattle, "_state", MapEventState.Wait);
        Field(siegeSide, "_mapEvent", fieldBattle);
        Field(leader.Party, "_mapEventSide", null);
        Field(traveler.Party, "_mapEventSide", null);
        var battleReport = AtHour(144);
        Check(row(battleReport, attached.StringId).Contains("assignableStrength=0 siegeSide=attacker siegeTarget=inventory-town mapEvent=FieldBattle") &&
            row(battleReport, leader.StringId).Contains("reason=army-tactically-locked") &&
            AiArmyLine(battleReport).Contains("assignablePresentStrength=0 assignableTravelingStrength=0") &&
            ReferenceEquals(army.LeaderParty.BesiegerCamp, camp) && army.Parties.SequenceEqual(beforeArmy) && clan.Influence == 8f,
            "an attached member in a field battle locks the intact army without changing camp, membership or influence");

        Field(attached.Party, "_mapEventSide", null);
        foreach (var party in new[]{leader, attached, traveler})
        {
            Field(party, "_besiegerCamp", null);
            Field(party, "_currentSettlement", city);
        }
        var trappedReport = AtHour(168);
        Check(row(trappedReport, leader.StringId).Contains("reason=inside-besieged-settlement") &&
            AiArmyLine(trappedReport).Contains("assignablePresentStrength=0 assignableTravelingStrength=0 siegeSide=defender siegeTarget=inventory-town mapEvent=-"),
            "an army trapped inside a besieged settlement is locked even without an assault MapEvent");
        var saved = new Store { IsSaving = true };
        reporting.SyncData(saved);
        Check(saved.Count == 0, "force accounting saves no roster or timer data");
    }

    private static void TestDispersalReconciliation(Type stageType, CampaignBehaviorBase stage, IList pending)
    {
        var returned = stageType.GetMethod("OnDispersalReturned", flags);
        var left = stageType.GetMethod("OnPartyLeftArmy", flags);
        var nextTick = stageType.GetMethod("OnTick", flags);
        int start = log.Count;
        var leader = Party("recursive-leader");
        var army = Formation(leader);
        pending.Add(army);
        // At the nested DisperseInternal postfix, the old army list and kingdom
        // are cleared, but the outer MobileParty.Army setter has not assigned yet.
        ((IList)army.Parties).Clear();
        returned.Invoke(stage, new object[]{army, Army.ArmyDispersionReason.LeaderPartyRemoved, "recursive-army", "recursive-leader"});
        Check(log.Count == start && pending.Contains(army), "mid-setter callback does not report a premature outcome");
        Field(leader, "_army", null);
        left.Invoke(stage, new object[]{leader, army});
        nextTick.Invoke(stage, new object[]{1f});
        Check(!pending.Contains(army) && log.Count == start + 1 &&
            log.Last().Contains("outcome=complete source=native reason=LeaderPartyRemoved pendingTakeover=True") &&
            log.Last().Contains("registered=False kingdomCleared=True leaderStillMember=False"),
            "native leave callback verifies terminal state and attributes the native loss once before the takeover scan");

        start = log.Count;
        leader = Party("transferred-leader");
        army = Formation(leader);
        pending.Add(army);
        ((IList)army.Parties).Clear();
        returned.Invoke(stage, new object[]{army, Army.ArmyDispersionReason.LeaderPartyRemoved, "transferred-army", "transferred-leader"});
        var replacement = Formation(leader);
        nextTick.Invoke(stage, new object[]{1f});
        Check(log.Count == start + 1 && log.Last().Contains("outcome=complete source=native reason=LeaderPartyRemoved") &&
            !pending.Contains(army) && !pending.Contains(replacement) && leader.Army == replacement,
            "direct transfer without a leave event verifies on next tick without touching the replacement army");

        start = log.Count;
        leader = Party("save-before-tick-leader");
        army = Formation(leader);
        pending.Add(army);
        ((IList)army.Parties).Clear();
        returned.Invoke(stage, new object[]{army, Army.ArmyDispersionReason.LeaderPartyRemoved, "save-before-tick-army", "save-before-tick-leader"});
        Field(leader, "_army", null);
        var store = new Store { IsSaving = true };
        stage.SyncData(store);
        store.IsSaving = false;
        var savedPending = new List<Army>();
        store.SyncData("pendingArmies", ref savedPending);
        Check(!pending.Contains(army) && !savedPending.Contains(army) &&
            log.Skip(start).Count(line => line.Contains("outcome=complete source=native reason=LeaderPartyRemoved")) == 1,
            "saving before next tick reconciles the settled native loss before capturing pending references");

        start = log.Count;
        leader = Party("outstanding-leader");
        army = Formation(leader);
        pending.Add(army);
        ((IList)army.Parties).Clear();
        returned.Invoke(stage, new object[]{army, Army.ArmyDispersionReason.LeaderPartyRemoved, "outstanding-army", "outstanding-leader"});
        nextTick.Invoke(stage, new object[]{1f});
        stageType.GetMethod("CheckPending", flags).Invoke(stage, null);
        Check(log.Count == start + 1 && log.Last().Contains("outcome=incomplete source=native reason=LeaderPartyRemoved") &&
            log.Last().Contains("registered=False kingdomCleared=True leaderStillMember=True") &&
            !pending.Contains(army) && leader.Army == army,
            "genuine stale backlink is reported once without attempting to release an unregistered army");

        start = log.Count;
        leader = Party("registered-leader");
        army = Formation(leader);
        var kingdom = Blank<Kingdom>();
        Field(kingdom, "_armies", new MBList<Army>(new[]{army}));
        Field(army, "_kingdom", kingdom);
        pending.Add(army);
        returned.Invoke(stage, new object[]{army, Army.ArmyDispersionReason.LeaderPartyRemoved, "registered-army", "registered-leader"});
        Check(log.Count == start + 1 && log.Last().Contains("outcome=incomplete") &&
            log.Last().Contains("remaining=1 registered=True kingdomCleared=False leaderStillMember=True") && pending.Contains(army),
            "a genuinely outstanding registered formation remains pending and diagnostics show what is left");
        pending.Remove(army);
    }

    private sealed class FixtureCallPrice : DefaultArmyManagementCalculationModel
    {
        public int Cost = 50;
        public int Quotes;
        public Func<float, int> Upkeep;
        public Func<MobileParty, MobileParty, int> PriceOverride;
        public override float MaximumDistanceToCallToArmy => 200f;
        public override int CalculatePartyInfluenceCost(MobileParty leader, MobileParty party)
        {
            Quotes++;
            FixtureDelay(35);
            return leader.LeaderHero.Clan == party.LeaderHero.Clan ? 0 : PriceOverride?.Invoke(leader, party) ?? Cost;
        }
        public override int CalculateTotalInfluenceCost(Army army, float gain) => Upkeep(gain);
    }
    private sealed class FixtureNeighbors : DefaultMapDistanceModel
    {
        private static readonly MBList<Settlement> NoNeighbors = new MBList<Settlement>();
        public Dictionary<Settlement, MBList<Settlement>> Links;
        public void Link(Settlement a, Settlement b)
        {
            if (!Links.TryGetValue(a, out var from)) Links[a] = from = new MBList<Settlement>();
            if (!Links.TryGetValue(b, out var to)) Links[b] = to = new MBList<Settlement>();
            if (!from.Contains(b)) from.Add(b);
            if (!to.Contains(a)) to.Add(a);
        }
        public override MBReadOnlyList<Settlement> GetNeighborsOfFortification(Town town, MobileParty.NavigationType navigationType) =>
            Links.TryGetValue(town.Settlement, out var found) ? found : NoNeighbors;
        public override bool PathExistBetweenPoints(in CampaignVec2 fromPoint, in CampaignVec2 toPoint,
            MobileParty.NavigationType navigationType) => true;
    }
    private sealed class FixtureSiegeScore : DefaultTargetScoreCalculatingModel
    {
        public float Minimum;
        public float LastPresent;
        public int Calls;
        public Func<Settlement, float> OverrideMinimum;
        public override float GetTargetScoreForFaction(Settlement target, Army.ArmyTypes type, MobileParty armyLeader, float power)
        {
            if (type != Army.ArmyTypes.Besieger) throw new Exception("Used non-siege native score.");
            LastPresent = power;
            Calls++;
            FixtureDelay(220);
            return power >= (OverrideMinimum?.Invoke(target) ?? Minimum) ? 1f : 0f;
        }
    }
    private sealed class FixtureDiplomacy : DefaultDiplomacyModel
    {
        public override bool IsAtConstantWar(IFaction faction1, IFaction faction2) => false;
        public override DiplomacyStance? GetShallowDiplomaticStance(IFaction faction1, IFaction faction2) =>
            faction1 is Kingdom && faction2 is Kingdom && faction1 != faction2 ? DiplomacyStance.War : DiplomacyStance.Neutral;
    }
    private sealed class FixtureCampPower : DefaultMilitaryPowerModel
    {
        public override float GetPowerOfParty(PartyBase party, BattleSideEnum side, MapEvent.PowerCalculationContext context) => forcePowers[party];
    }
    private static bool FixtureRatio(ref float __result) { __result = 1f; return false; }
    private static float movementSpeed = 5f;
    private static bool FixtureSpeed(ref float __result) { __result = movementSpeed; return false; }
    private static bool FixtureFood(ref int __result) { __result = 20; return false; }
    private static bool FixtureWall(ref int __result) { __result = 1; return false; }
    private static bool SkipInfluenceSkillXp() => false;
    private static bool visitPathAvailable = true;
    private static int fixturePathTrials;
    private static bool FixtureVisitPath(ref bool __result)
    {
        fixturePathTrials++;
        FixtureDelay(135);
        __result = visitPathAvailable;
        return false;
    }
    private static bool expensiveSiegeFixture;
    private static void FixtureDelay(int microseconds)
    {
        if (!expensiveSiegeFixture) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long ticks = System.Diagnostics.Stopwatch.Frequency * microseconds / 1000000;
        while (System.Diagnostics.Stopwatch.GetTimestamp() - start < ticks) { }
    }
    private static Func<float, float, bool> blockedFixtureRoute;
    private static Func<MobileParty, Settlement, bool> blockedFixtureVisit;
    private static bool FixtureSettlementVisit(MobileParty __0, Settlement __1, ref bool __result)
    {
        if (blockedFixtureVisit?.Invoke(__0, __1) != true)
            return FixtureVisitPath(ref __result);
        __result = false;
        return false;
    }
    private static Func<float, float, double> fixtureRouteDistance;
    private static int fixtureDistanceCalls;
    private static int fixtureNextDistanceDelay;
    private static int fixtureObjectiveCalls;
    private static void CountFixtureObjective() => fixtureObjectiveCalls++;
    private static bool FixtureDistance(CampaignVec2 __0, CampaignVec2 __1, ref double __result)
    {
        fixtureDistanceCalls++;
        FixtureDelay(fixtureNextDistanceDelay == 0 ? 100 : fixtureNextDistanceDelay);
        fixtureNextDistanceDelay = 0;
        __result = __0.X == 999 || __1.X == 999 || blockedFixtureRoute?.Invoke(__0.X, __1.X) == true ?
            double.PositiveInfinity : fixtureRouteDistance?.Invoke(__0.X, __1.X) ?? Math.Abs(__0.X - __1.X);
        return false;
    }

    private static void TestMuster(Assembly mod, Harmony harmony, Campaign campaign, object tracker, MobileParty player, Type logType, Assembly previousMod)
    {
        var command = mod.GetType("BannerlordMilitaryAIOverhaul.Command.KingdomCommander", true);
        var behavior = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.MilitaryCommandBehavior", true);
        var orders = mod.GetType("BannerlordMilitaryAIOverhaul.Armies.ArmyOrders", true);
        var takeover = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.ArmyTakeoverBehavior", true);
        string Decision(float present, bool atMuster, double now, params (float, double)[] traveling) =>
            command.GetMethod("CheckMuster", flags).Invoke(null, new object[] {300f, present, atMuster, now, 30d, 8d, 0d, traveling}).ToString();
        Check(Decision(310, true, 18, (40f, 6d)) == "Depart", "310 present at H18 departs despite outstanding traveling strength");
        Check(Decision(240, true, 18, (60f, 6d)) == "Cancel", "240 present and H24 reinforcement fails the H22 departure boundary");
        Check(Decision(240, true, 18, (60f, 4d)) == "Wait" && Decision(300, true, 22) == "Depart" &&
            Decision(300, true, 22.001) == "Cancel", "exact strength and ETA boundaries are inclusive");
        Check(Decision(240, true, 22, (60f, 0d)) == "Cancel",
            "a zero-ETA traveler that has not attached cannot extend the departure boundary");
        Check(Decision(310, false, 22) == "Cancel",
            "a zero-ETA leader not physically at muster cannot extend the departure boundary");
        Check(Decision(310, false, 18) == "Wait" && Decision(240, true, 18, (100f, double.PositiveInfinity)) == "Cancel",
            "meeting strength before muster does not depart and unreachable reinforcement never counts");
        var afford = orders.GetMethod("Affordable", flags);
        bool Affords(float balance, float cost) => (bool)afford.Invoke(null, new object[] {balance, cost});
        Check(Affords(250, 50) && !Affords(250, 51) && !Affords(250, 30 + 21) && !Affords(200, 1) && Affords(12, 0),
            "combined and per-payment reserve is exact and free summons below reserve are legal");

        // Headless providers: isolate map pathfinding, party size, food and speed.
        // Keep real construction, native army/party setters, movement actions,
        // native attachment, event dispatcher, pricing hook and dispersal.
        void Prefix(MethodBase method, string prefix) =>
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(SmokeFixtures).GetMethod(prefix, flags)));
        Prefix(AccessTools.PropertyGetter(typeof(MobileParty), "PartySizeRatio"), nameof(FixtureRatio));
        Prefix(AccessTools.PropertyGetter(typeof(MobileParty), "Speed"), nameof(FixtureSpeed));
        Prefix(typeof(MobileParty).GetMethod("GetNumDaysForFoodToLast", flags), nameof(FixtureFood));
        Prefix(typeof(Town).GetMethod("GetWallLevel", flags), nameof(FixtureWall));
        Prefix(command.GetMethod("LandDistance", flags), nameof(FixtureDistance));
        Prefix(orders.GetMethod("CanPathVisit", flags), nameof(FixtureSettlementVisit));
        Prefix(orders.GetMethod("CanPathLand", flags), nameof(FixtureVisitPath));
        Prefix(behavior.GetMethod("TickRelief", flags), nameof(EnableReliefFixture));
        Prefix(behavior.GetMethod("TickSieges", flags), nameof(EnableSiegeFixture));
        // Earlier branch tests intercept dispersal to count reasons. Now execute it.
        harmony.Unpatch(typeof(Army).GetMethod("DisperseInternal", flags), typeof(SmokeFixtures).GetMethod("CaptureDisperse", flags));

        var kingdom = FixtureKingdom("march");
        var objects = Blank<CampaignObjectManager>();
        Field(objects, "_kingdoms", new MBList<Kingdom>(new[]{kingdom}));
        Property(campaign, "CampaignObjectManager", objects);
        var events = new CampaignEvents();
        Property(campaign, "CampaignEvents", events);
        var dispatcher = (CampaignEventDispatcher)Activator.CreateInstance(typeof(CampaignEventDispatcher), flags, null,
            new object[]{new CampaignEventReceiver[]{events}}, null);
        Property(campaign, "CampaignEventDispatcher", dispatcher);
        Property(campaign, "CustomPeriodicCampaignEvents", new List<MBCampaignEvent>());
        campaign.SkillLevelingManager = new DefaultSkillLevelingManager();
        var price = Blank<FixtureCallPrice>();
        price.Cost = 50;
        price.Upkeep = gain => (int)Math.Ceiling(gain * 0.75);
        var models = Blank<GameModels>();
        Property(models, "ArmyManagementCalculationModel", price);
        Property(models, "MobilePartyAIModel", Blank<DefaultMobilePartyAIModel>());
        Property(models, "EncounterModel", Blank<DefaultEncounterModel>());
        Field(campaign, "_gameModels", models);
        Field(player, "_army", null);

        Clan ClanFor(string id, float influence)
        {
            var clan = Blank<Clan>();
            clan.StringId = id;
            Field(clan, "_kingdom", kingdom);
            clan.Influence = influence;
            return clan;
        }
        var payer = ClanFor("payer", 250);
        var ally = ClanFor("ally", 0);
        var playerClan = ClanFor("test-player-clan", 500);
        Property(campaign, "PlayerDefaultFaction", playerClan);
        Field(player, "_actualClan", playerClan);
        Field(player.LeaderHero, "_clan", playerClan);
        kingdom.RulingClan = payer;
        MobileParty Lord(string id, Clan clan, float power, float x)
        {
            var party = Party(id);
            FixtureLord(party, clan, power);
            Property(party, "HasLandNavigationCapability", true);
            Field(party.Ai, "_mobileParty", party);
            Field(party, "_position", new CampaignVec2(new Vec2(x, 0), true));
            Field(party.LeaderHero, "_name", new TextObject("{=!}" + id));
            return party;
        }
        var leader = Lord("muster-leader", payer, 240, -5);
        var member = Lord("muster-member", ally, 70, -15);
        var optOut = Lord("muster-veto", playerClan, 900, 0);
        optOut.LeaderHero.CanJoinArmy = false;
        var playerMember = Lord("muster-player-member", ally, 900, 0);
        var playerArmy = Formation(player, playerMember);
        playerArmy.Kingdom = kingdom;
        Field(payer, "_leader", leader.LeaderHero);
        Field(ally, "_leader", member.LeaderHero);
        Field(playerClan, "_leader", optOut.LeaderHero);

        Settlement Site(string id, float x, Clan owner = null)
        {
            var site = Blank<Settlement>();
            site.StringId = id;
            Property(site, "GatePosition", new CampaignVec2(new Vec2(x, 0), true));
            var town = Blank<Town>();
            var baseParty = new PartyBase(site);
            typeof(SettlementComponent).GetField("_owner", flags).SetValue(town, baseParty);
            Property(site, "Party", baseParty);
            Property(site, "SettlementComponent", town);
            Field(site, "Town", town);
            Field(town, "_ownerClan", owner ?? payer);
            Field(site, "_partiesCache", new MBList<MobileParty>());
            return site;
        }
        var muster = Site("muster", 0);
        var target = Site("destination", 40);
        var unreachable = Site("unreachable", 999);
        Field(kingdom, "_fiefsCache", new MBList<Town>(new[]{muster.Town, target.Town, unreachable.Town}));
        Field(tracker, "_numTicks", 10000L);
        void At(double hour) => Field(tracker, "_numTicks", (long)(hour * 1000));
        var sessionLog = logType.GetMethod("Open", flags).Invoke(null, new object[]{campaign});
        var inherited = (CampaignBehaviorBase)Activator.CreateInstance(takeover, flags, null, new[]{campaign, sessionLog}, null);
        Property(inherited, "InitialCheckComplete", true);
        CampaignBehaviorBase NewBehavior() => (CampaignBehaviorBase)Activator.CreateInstance(behavior, flags, null, new[]{sessionLog}, null);
        var reporting = NewBehavior();
        void Activate(CampaignBehaviorBase b)
        {
            campaign.AddCampaignBehaviorManager(new CampaignBehaviorManager(new[]{b}));
            b.RegisterEvents();
            Field(b, "_ready", true);
            Field(b, "_nextReportHour", double.MaxValue);
        }
        Activate(reporting);
        const string arrived = "arrived-by-deadline";
        string Control(params string[] args) => (string)behavior.GetMethod("Control", flags).Invoke(reporting, new object[]{args});
        void Tick() => behavior.GetMethod("OnTick", flags).Invoke(reporting, new object[]{0f});
        void Review() { behavior.GetMethod("RequestReview", flags).Invoke(reporting, null); Tick(); }
        void Resume(bool hasPendingSurplus = false, bool legacy = false, int extraKeys = 0)
        {
            int before = log.Count;
            var storeType = typeof(Campaign).Assembly.GetType("TaleWorlds.CampaignSystem.CampaignBehaviorDataStore", true);
            var store = Activator.CreateInstance(storeType, true);
            CampaignBehaviorBase writer = reporting;
            var previousBehavior = legacy ? previousMod?.GetType(behavior.FullName) : null;
            if (previousBehavior?.GetField("_surplus", flags) != null)
            {
                var previousLog = previousMod.GetType(logType.FullName, true).GetMethod("Open", flags).Invoke(null, new object[]{campaign});
                writer = (CampaignBehaviorBase)Activator.CreateInstance(previousBehavior, flags, null, new[]{previousLog}, null);
                foreach (var name in new[]{"_phase", "_army", "_kingdomId", "_muster", "_target", "_required", "_deadline", "_outcome", "_surplus",
                    "_visitRetries", "_visitProgressAt", "_visitRetryAfter", "_visitBestRoute"})
                    previousBehavior.GetField(name, flags)?.SetValue(writer, behavior.GetField(name, flags).GetValue(reporting));
            }
            storeType.GetMethod("SaveBehaviorData", flags).Invoke(store, new object[]{writer});
            var dict = (IDictionary)storeType.GetField("_behaviorDict", flags).GetValue(store);
            var saved = dict[reporting.StringId];
            var records = (IDictionary)saved.GetType().GetField("_records", flags).GetValue(saved);
            var loaded = Activator.CreateInstance(saved.GetType(), flags, null, new object[]{false}, null);
            Field(loaded, "_records", records);
            dict[reporting.StringId] = loaded;
            events.RemoveListeners(reporting);
            var restored = NewBehavior();
            bool savedVisit = writer.GetType().GetField("_visitRetries", flags) != null &&
                new[]{1, 2}.Contains((int)behavior.GetField("_phase", flags).GetValue(reporting));
            storeType.GetMethod("LoadBehaviorData", flags).Invoke(store, new object[]{restored});
            reporting = restored;
            Activate(reporting);
            Tick();
            Check(records.Count == (hasPendingSurplus ? 9 : 8) + extraKeys +
                (savedVisit ? 4 : 0) &&
                records.Contains("fixtureSurplusReleases") == hasPendingSurplus &&
                log.Skip(before).Any(line => line.Contains("Muster loaded:")) &&
                !log.Skip(before).Any(line => line.Contains("Muster debit") || line.Contains("Muster summons") ||
                    line.Contains("Muster formed") || line.Contains("Muster departure:")),
                "resume uses the persisted operation and never replays summons or payments");
            if (writer != null && !ReferenceEquals(writer, restored) && previousBehavior?.GetField("_surplus", flags) != null)
                Check(writer.GetType() == previousBehavior && restored.GetType() == behavior, "installed Stage 2 writer loads into sustained commander without replay");
        }
        int preflightStart = log.Count;
        // Exercise real attribute discovery and the installed console registry's
        // string-argv overload without opening a native game or console window.
        var games = Blank<GameStateManager>();
        Field(games, "_gameStates", new List<GameState>{Blank<MapState>()});
        var game = Blank<Game>();
        Property(game, "GameStateManager", games);
        var playerCharacter = Blank<CharacterObject>();
        Property(playerCharacter, "HeroObject", player.LeaderHero);
        game.PlayerTroop = playerCharacter;
        Check(CommandLineFunctionality.CollectCommandLineFunctions().Contains("maio.muster") &&
            CommandLineFunctionality.HasFunctionForCommand("maio.muster"), "native console discovers the private attributed maio.muster command");
        var currentGame = typeof(Game).GetField("_current", flags);
        var priorGame = currentGame.GetValue(null);
        bool found;
        string preview;
        try
        {
            currentGame.SetValue(null, game);
            preview = CommandLineFunctionality.CallFunction("maio.muster", "preview march destination 300 30", out found);
            Check(found && CommandLineFunctionality.CallFunction("maio.muster", "status", out found).Contains("phase=idle") && found,
                "native console dispatch accepts the documented space-separated argv without the command name");
        }
        finally { currentGame.SetValue(null, priorGame); }
        if (!preview.Contains("calls=1 cost=50"))
            throw new Exception(preview + Environment.NewLine + string.Join(Environment.NewLine, log.Skip(preflightStart)));
        Check(
            payer.Influence == 250 && leader.Army == null && member.Army == null,
            "preflight selects one legal summon and checks combined cost without mutating orders or balance");
        Check(log.Any(line => line.Contains("party=muster-veto reason=player-clan-opt-out")) &&
            log.Any(line => line.Contains("party=muster-player-member reason=player-led-army")), "preflight explains player and opt-out exclusions");
        price.Cost = 51;
        Control("start", "march", "destination", "300", "30");
        Tick();
        Check(Control("status").Contains("no-feasible-plan") && leader.Army == null && payer.Influence == 250,
            "one influence over reserve rejects the plan without a partial formation");
        price.Cost = 50;
        Control("start", "march", "destination", "300", "30");
        Tick();
        var army = leader.Army;
        Check(army != null && kingdom.Armies.Contains(army) && member.Army == army && member.AttachedTo == null &&
            leader.TargetSettlement == muster && member.TargetParty == leader && payer.Influence == 200 &&
            log.Any(line => line.Contains("nativeCost=50 accepted=True actualDebit=50 balanceAfter=200")),
            "native membership charges the leader's clan exactly once, sets escort, and does not fake arrival");
        Check(Control("status").Contains("gathering"), "transit to muster is not physical presence");
        Resume(legacy: true);
        Check(leader.Army == army && member.AttachedTo == null && payer.Influence == 200, "muster reload preserves the original army, summons and balance");
        At(11);
        Field(leader, "_position", muster.GatePosition);
        typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(leader, new object[]{AiBehavior.GoToSettlement, muster.Party});
        try
        {
            currentGame.SetValue(null, game);
            EncounterManager.StartSettlementEncounter(leader, muster);
        }
        finally { currentGame.SetValue(null, priorGame); }
        Tick();
        Check(leader.CurrentSettlement == muster && leader.DefaultBehavior == AiBehavior.GoToSettlement &&
            leader.TargetSettlement == muster && army.AiBehaviorObject == muster && Control("status").Contains("gathering") &&
            log.Any(line => line.Contains("Muster settlement entry:") && line.Contains("nativeTarget=muster")),
            "installed friendly fort encounter enters the muster without clearing the native visit order while calls are out");
        At(13);
        // Drive the installed Army.Tick proximity path with actual short-term
        // escort target and an adjacent position; no commander arrival flag.
        Field(member, "_position", new CampaignVec2(new Vec2(0.5f, 0), true));
        member.Ai.AiBehaviorInteractable = leader.Party;
        typeof(Army).GetMethod("Tick", flags).Invoke(army, new object[]{null, null});
        Tick();
        Check(member.AttachedTo == leader && Control("status").Contains("marching") && leader.TargetSettlement == target &&
            payer.Influence == 200 && log.Any(line => line.Contains("Muster attachment:") && line.Contains("nativeAttached=True")) &&
            log.Any(line => line.Contains("Muster departure:") && line.Contains("present=310")),
            "native Army.Tick attaches the member and triggers departure with 310 physically present");
        Resume(legacy: true);
        Check(leader.Army == army && leader.TargetSettlement == target, "march reload keeps the original target and army");
        At(21);
        // Settlement state is simulated in headless mode; run the same native
        // dispatcher callback emitted after the actual map settlement entry.
        ((IList)muster.Parties).Clear();
        foreach (var party in new[]{leader, member})
        {
            Field(party, "_currentSettlement", target);
            ((IList)target.Parties).Add(party);
        }
        dispatcher.OnAfterSettlementEntered(leader, target, leader.LeaderHero);
        Tick();
        Check(Control("status").Contains(arrived) && leader.Army == null && member.Army == null && member.AttachedTo == null &&
            !kingdom.Armies.Contains(army) && member.Ai.RethinkAtNextHourlyTick && playerMember.Army == playerArmy &&
            log.Any(line => line.Contains("Muster release:") && line.Contains("complete=True")),
            "arrival closes once through native dispersal and resumes vanilla without touching the player army");
        Resume();
        Check(Control("status").Contains(arrived) && leader.Army == null && payer.Influence == 200, "terminal reload does not restart the fixture");

        foreach (var party in new[]{leader, member})
            Field(party, "_currentSettlement", null);
        ((IList)target.Parties).Clear();
        Field(leader, "_currentSettlement", muster);
        ((IList)muster.Parties).Add(leader);
        Field(member, "_position", new CampaignVec2(new Vec2(-10, 0), true));
        payer.Influence = 250;
        Control("start", "march", "destination", "300", "15");
        Tick();
        Check(Control("status").Contains("gathering") && member.Army == leader.Army, "explicit new run can begin after completion");
        Field(member, "_position", new CampaignVec2(new Vec2(-100, 0), true));
        At(22);
        Review();
        Check(Control("status").Contains("no-feasible-muster-by-latest-departure") && leader.Army == null &&
            member.Army == null && member.TargetParty != leader && payer.Influence == 200,
            "late eligible reinforcement cancels promptly and releases travel without repayment or stale escort");

        Field(member, "_position", new CampaignVec2(new Vec2(-10, 0), true));
        payer.Influence = 250;
        void LowerBalanceOnJoin(MobileParty joining)
        {
            if (joining == member)
                payer.Influence = 249;
        }
        var listener = new object();
        CampaignEvents.OnPartyJoinedArmyEvent.AddNonSerializedListener(listener, LowerBalanceOnJoin);
        Control("start", "march", "destination", "300", "30");
        Tick();
        events.RemoveListeners(listener);
        Check(payer.Influence == 249 && leader.Army == null && member.Army == null &&
            log.Any(line => line.Contains("nativeCost=50 accepted=False actualDebit=0 balanceAfter=249")),
            "the actual native debit rechecks the balance after the join callback and refuses a now-unaffordable charge");

        payer.Influence = 250;
        Control("start", "march", "destination", "300", "30");
        Tick();
        army = leader.Army;
        var battle = Blank<MapEvent>();
        var battleSide = Blank<MapEventSide>();
        Field(battleSide, "_mapEvent", battle);
        Field(member.Party, "_mapEventSide", battleSide);
        Control("cancel");
        Tick();
        Check(leader.Army == army && member.Army == army && member.MapEvent == battle &&
            Control("status").Contains("phase=closing"), "explicit cancellation cannot pull a traveling party out of its battle");
        Resume();
        Check(leader.Army == army && member.Army == army && payer.Influence == 200, "pending safe release survives reload without a charge");
        Field(member.Party, "_mapEventSide", null);
        Review();
        Check(Control("status").Contains("cancelled-by-player") && leader.Army == null && member.Army == null,
            "closing after the native lock clears releases exactly once");

        // A second planned summon stops being necessary after the first attaches
        // stronger than the estimate. A third party is not synthesized by the mod.
        var spare = Lord("muster-spare", payer, 50, -20);
        forcePowers[member.Party] = 20;
        payer.Influence = 250;
        Control("start", "march", "destination", "300", "30");
        Tick();
        army = leader.Army;
        Check(member.Army == army && spare.Army == army && spare.AttachedTo == null && Control("status").Contains("gathering"),
            "two calls are made only when needed by the starting strength estimate");
        forcePowers[member.Party] = 70;
        Field(member, "_position", new CampaignVec2(new Vec2(0.5f, 0), true));
        member.Ai.AiBehaviorInteractable = leader.Party;
        typeof(Army).GetMethod("Tick", flags).Invoke(army, new object[]{null, null});
        Tick();
        Check(Control("status").Contains("marching") && spare.Army == null && spare.TargetParty != leader &&
            member.AttachedTo == leader && payer.Influence == 200,
            "actual surplus present strength releases the unneeded traveling summons before departing");
        Control("cancel");
        Tick();

        // Repeat at exactly the last useful departure; the unused invitee is
        // independently fighting. A later native attachment must not turn it
        // into required strength or make us detach any participant in combat.
        forcePowers[member.Party] = 20;
        payer.Influence = 250;
        Control("start", "march", "destination", "300", "12");
        Tick();
        army = leader.Army;
        Check(Control("status").Contains("gathering") && spare.Army == army && spare.AttachedTo == null,
            "battle-held surplus test begins with a genuine traveling summons");
        Field(spare.Party, "_mapEventSide", battleSide);
        forcePowers[member.Party] = 70;
        Field(member, "_position", new CampaignVec2(new Vec2(0.5f, 0), true));
        member.Ai.AiBehaviorInteractable = leader.Party;
        At(26); // H22 start +12 to arrive -8 to march.
        typeof(Army).GetMethod("Tick", flags).Invoke(army, new object[]{null, null});
        int beforeDeparture = log.Count;
        Tick();
        Check(Control("status").Contains("phase=marching") && leader.TargetSettlement == target &&
            spare.MapEvent == battle && spare.Army == army && spare.AttachedTo == null &&
            log.Skip(beforeDeparture).Any(line => line.Contains("surplus cancellation deferred") && line.Contains("countedForMarch=false")) &&
            log.Skip(beforeDeparture).Any(line => line.Contains("Muster departure:") && line.Contains("present=310") && line.Contains("deferredSurplus=1")),
            "ready army departs exactly on time without detaching a surplus traveler fighting independently");
        Resume(hasPendingSurplus: true, legacy: true);
        Check(leader.TargetSettlement == target && spare.Army == army && spare.MapEvent == battle && payer.Influence == 200,
            "battle-held surplus remains pending across reload; march order and charge are unchanged");

        // Emulate native map progress and a battle ending. The real listener
        // queues review; safe cancellation runs only after the setter settles.
        Field(leader, "_currentSettlement", null);
        ((IList)muster.Parties).Remove(leader);
        Field(leader, "_position", new CampaignVec2(new Vec2(5, 0), true));
        At(27);
        int beforeRelease = log.Count;
        dispatcher.OnMapEventEnded(battle);
        Check(spare.Army == army && spare.MapEvent == battle, "battle-ended callback itself does not detach inside the native event");
        Field(spare.Party, "_mapEventSide", null);
        Tick();
        Review();
        Check(spare.Army == null && spare.TargetParty != leader && spare.Ai.RethinkAtNextHourlyTick && leader.Army == army &&
            Control("status").Contains("phase=marching") && payer.Influence == 200 &&
            log.Skip(beforeRelease).Count(line => line.Contains("Muster member released") && line.Contains("party=muster-spare ")) == 1,
            "first safe review releases the summons once during transit, clears its escort and keeps the original march");
        // Avoid native flank-position queries on closing this headless march.
        Field(leader, "_currentSettlement", muster);
        ((IList)muster.Parties).Add(leader);
        Control("cancel");
        Tick();

        Field(member, "_position", new CampaignVec2(new Vec2(-10, 0), true));
        forcePowers[member.Party] = 20;
        payer.Influence = 250;
        Control("start", "march", "destination", "300", "30");
        Tick();
        army = leader.Army;
        Field(spare.Party, "_mapEventSide", battleSide);
        forcePowers[member.Party] = 70;
        Field(member, "_position", new CampaignVec2(new Vec2(0.5f, 0), true));
        member.Ai.AiBehaviorInteractable = leader.Party;
        typeof(Army).GetMethod("Tick", flags).Invoke(army, new object[]{null, null});
        Tick();
        Check(Control("status").Contains("phase=marching") && spare.Army == army && spare.AttachedTo == null,
            "the surplus can still be a native member after the march order");
        Field(spare.Party, "_mapEventSide", null);
        // Simulate the party catching up between commander reviews, then the
        // formation entering a battle before the next review.
        army.AddPartyToMergedParties(spare);
        foreach (var party in new[]{leader, member, spare})
            Field(party.Party, "_mapEventSide", battleSide);
        Resume(hasPendingSurplus: true);
        Check(spare.AttachedTo == leader && spare.Army == army && spare.MapEvent == battle &&
            leader.MapEvent == battle && member.MapEvent == battle && leader.TargetSettlement == target,
            "a formerly surplus summon that attached is not pulled out of the formation's battle, including on reload");
        foreach (var party in new[]{leader, member, spare})
            Field(party.Party, "_mapEventSide", null);
        forcePowers[member.Party] = 20; // 260 original force; the late 50 must not help.
        dispatcher.OnMapEventEnded(battle);
        Tick();
        Check(spare.Army == null && spare.AttachedTo == null && spare.TargetParty != leader &&
            Control("status").Contains("march-no-longer-feasible(present=260)") && payer.Influence == 200,
            "once safe, a newly attached surplus is released and never makes an understrength traveling army viable");

        // Native membership outside the commander context still makes its original
        // debit. The test uses a plain native army and does not call ArmyOrders.
        payer.Influence = 205;
        army = new Army(kingdom, leader, Army.ArmyTypes.Defender);
        member.Army = army;
        Check(payer.Influence == 155 && member.Army == army, "native membership outside commander scope is not subject to its reserve");
        DisbandArmyAction.ApplyByUnknownReason(army);

        Property(models, "DiplomacyModel", Blank<FixtureDiplomacy>());
        var enemy = FixtureKingdom("enemy");
        var hostile = ClanFor("hostile", 0);
        Field(hostile, "_kingdom", enemy);
        var adjacent = Site("adjacent", 55, hostile);
        var distant = Site("distant", 150, hostile);
        var impassable = Site("impassable", 999, hostile);
        Property(objects, "Settlements", new MBList<Settlement>(new[]{muster, target, adjacent, distant, impassable}));
        Field(objects, "_kingdoms", new MBList<Kingdom>(new[]{kingdom, enemy}));
        var graph = Blank<FixtureNeighbors>();
        graph.Links = new Dictionary<Settlement, MBList<Settlement>>();
        graph.Link(target, adjacent);
        graph.Link(target, impassable);
        var nativeScore = Blank<FixtureSiegeScore>();
        nativeScore.Minimum = 200;
        Property(models, "MapDistanceModel", graph);
        Property(models, "TargetScoreCalculatingModel", nativeScore);
        double clock = 100;

        Army Guard(string followOn = null)
        {
            clock += 100;
            At(clock);
            if (leader.CurrentSettlement != null) ((IList)leader.CurrentSettlement.Parties).Remove(leader);
            Field(leader, "_currentSettlement", muster);
            ((IList)muster.Parties).Add(leader);
            var args = new List<string>{"start", "march", "destination", "200", "30", "24"};
            if (followOn != null) args.Add(followOn);
            if (!Control(args.ToArray()).StartsWith("Queued")) throw new Exception(Control("status"));
            Tick();
            var result = leader.Army;
            At(clock + 8);
            ((IList)muster.Parties).Remove(leader);
            Field(leader, "_currentSettlement", target);
            ((IList)target.Parties).Add(leader);
            dispatcher.OnAfterSettlementEntered(leader, target, leader.LeaderHero);
            Tick();
            if (leader.DefaultBehavior != AiBehavior.DefendSettlement || !Control("status").Contains("phase=guarding"))
                throw new Exception(Control("status"));
            return result;
        }
        void GuardComplete() { At(clock + 32); Review(); }
        SiegeEvent SiegeAt(Settlement site, Kingdom attacking, MobileParty campLeader)
        {
            var siege = Blank<SiegeEvent>();
            Field(siege, "BesiegedSettlement", site);
            var camp = new BesiegerCamp(siege, attacking);
            Field(camp, "_leaderParty", campLeader);
            Field(siege, "BesiegerCamp", camp);
            Property(site, "SiegeEvent", siege);
            return siege;
        }

        Check(Control("neighbors", "destination").Contains("adjacent (enemy)") && !Control("neighbors", "destination").Contains("distant"),
            "console reports actual modeled one-ring IDs without choosing a follow-on");
        army = Guard();
        payer.Influence = 260;
        army.Cohesion = 50;
        Review();
        Check(payer.Influence == 260 && army.Cohesion == 50, "active native defend order receives no refill at exactly 50");
        army.Cohesion = 40;
        var opposingLeader = Lord("enemy-camp", hostile, 900, 40);
        SiegeAt(target, enemy, opposingLeader);
        forcePowers[leader.Party] = 40;
        Review();
        Check(payer.Influence == 215 && army.Cohesion == 100 && leader.DefaultBehavior == AiBehavior.DefendSettlement &&
            log.Any(line => line.Contains("Upkeep full:") && line.Contains("nativeCost=45") && line.Contains("actualDebit=45")),
            "outmatched native defend assignment gets one full native refill, no strategic order change");
        Resume(extraKeys: 6);
        Check(leader.Army == army && payer.Influence == 215 && army.Cohesion == 100, "resume preserves the paid full boost, army and guard order");
        army.Cohesion = 40;
        Property(target, "SiegeEvent", null);
        forcePowers[leader.Party] = 240;
        GuardComplete();
        Check(leader.Army == null && payer.Influence == 215 && Control("status").Contains("no-nominated-follow-on"),
            "guard completion releases by default before considering low cohesion");

        army = Guard("adjacent");
        payer.Influence = 230;
        army.Cohesion = 40;
        Review();
        Check(payer.Influence == 200 && Math.Abs(army.Cohesion - 80) < 0.001f &&
            log.Any(line => line.Contains("Upkeep partial:") && line.Contains("fullCost=45 nativeCost=30")),
            "230/45 fixture buys the largest native-priced partial gain and keeps 200 influence");
        GuardComplete();
        Check(ReferenceEquals(army, leader.Army) && leader.DefaultBehavior == AiBehavior.BesiegeSettlement &&
            leader.TargetSettlement == adjacent && nativeScore.LastPresent == 240 && leader.BesiegedSettlement == null &&
            log.Any(line => line.Contains("decision=reuse") && line.Contains("followOn=adjacent")),
            "feasible one-ring follow-on keeps the exact army and orders a native siege without creating a camp remotely");
        Resume(extraKeys: 6);
        Check(leader.Army == army && leader.TargetSettlement == adjacent && payer.Influence == 200 &&
            log.Count(line => line.Contains("Assignment follow-on issued:")) == 1, "follow-on reload does not reassign or spend again");
        Field(adjacent.Town, "_ownerClan", payer); // observe a native ownership outcome without simulating combat.
        Review();
        Check(leader.Army == null && Control("status").Contains("follow-on-fortification-taken"),
            "observed follow-on completion releases the reused formation");
        Field(adjacent.Town, "_ownerClan", hostile);

        army = Guard("adjacent");
        var retryDates = (IDictionary)behavior.GetField("_siegeRetry", flags).GetValue(reporting);
        string retryKey = kingdom.StringId + "/" + adjacent.StringId;
        retryDates[retryKey] = clock + 33d;
        GuardComplete();
        Check(leader.Army == null && Control("status").Contains("repulse-cooldown"),
            "the fixed named neighboring follow-on cannot bypass the kingdom's offensive repulse cooldown");
        retryDates.Remove(retryKey);

        foreach (var item in new[]{(target: "distant", cause: "not-an-immediate-fortification-neighbor", min: 0f),
            (target: "impassable", cause: "no-land-route", min: 0f),
            (target: "adjacent", cause: "native-new-siege-strength-check", min: 300f)})
        {
            nativeScore.Minimum = item.min;
            army = Guard(item.target);
            GuardComplete();
            Check(leader.Army == null && Control("status").Contains(item.cause),
                $"rejected follow-on ({item.cause}) releases the same formation without another summons");
        }

        nativeScore.Minimum = 200;
        army = Guard("adjacent");
        SiegeAt(muster, enemy, opposingLeader);
        GuardComplete();
        Check(leader.Army == null && Control("status").Contains("higher-priority-defense(muster)"),
            "a qualifying home siege blocks the nominated offense without selecting a relief assignment");
        Property(muster, "SiegeEvent", null);

        army = Guard("adjacent");
        GuardComplete();
        var ownSiege = SiegeAt(adjacent, kingdom, leader);
        Field(leader, "_besiegerCamp", ownSiege.BesiegerCamp);
        nativeScore.Minimum = 1000;
        nativeScore.LastPresent = -1f;
        Review();
        Check(leader.Army == army && nativeScore.LastPresent == -1f &&
            log.Any(line => line.Contains("Assignment follow-on observed:") && line.Contains("native=camp:")),
            "observed attacking camp remains assigned without rerunning the new-siege score");
        Resume(extraKeys: 4);
        Field(leader, "_besiegerCamp", null);
        Property(adjacent, "SiegeEvent", null);
        Review();
        Check(leader.Army == null && Control("status").Contains("follow-on-camp-ended-without-capture"),
            "lost camp after reload releases the assignment rather than retrying the siege");

        army = Guard();
        payer.Influence = 199;
        army.Cohesion = 40;
        price.Upkeep = _ => 0;
        Review();
        Check(army.Cohesion == 100 && payer.Influence == 199, "a genuinely free full native refill is legal below reserve");
        price.Upkeep = gain => gain >= 60 ? 2 : 0;
        army.Cohesion = 40;
        payer.Influence = 201;
        Resume(extraKeys: 6);
        Check(army.Cohesion == 40 && payer.Influence == 201, "same-hour reload does not replay even with new low cohesion");
        At(clock + 10);
        Review();
        Check(army.Cohesion == 40 && payer.Influence == 201, "rounded-zero partial gains are not used as free upkeep");
        At(clock + 11);
        payer.Influence = 199;
        var quoted = new List<float>();
        price.Upkeep = gain =>
        {
            quoted.Add(gain);
            return (int)(Math.Ceiling(gain * 4 / 100) * 0.25); // native ceiling, then AI quarter/truncation
        };
        Review();
        Check(army.Cohesion == 100 && payer.Influence == 199 && quoted.SequenceEqual(new[]{60f}),
            "native rounded zero for the actual 60-point refill is legal below reserve even though 100 points would cost one");
        At(clock + 12);
        army.Cohesion = 40;
        payer.Influence = 245;
        price.Upkeep = gain =>
        {
            payer.Influence = 244; // simulates a changed balance between quote and native debit
            return (int)Math.Ceiling(gain * 0.75);
        };
        Review();
        Check(army.Cohesion == 40 && payer.Influence == 244, "the live debit guard rejects a stale quote that would breach reserve");
        payer.Influence = 201;
        var boost = orders.GetMethod("Sustain", flags);
        playerArmy.Cohesion = 30;
        Check((string)boost.Invoke(null, new object[]{playerArmy, "fixture", sessionLog}) == "ineligible" && playerArmy.Cohesion == 30,
            "direct action also refuses player-led boosts");
        army.Cohesion = 0;
        DisbandArmyAction.ApplyByCohesionDepleted(army);
        Tick();
        Check(leader.Army == null && payer.Influence == 201 &&
            Control("status").Contains("native-dispersal(CohesionDepleted)") &&
            log.Any(line => line.Contains("Assignment native dispersal:") && line.Contains("lastUpkeep=no-affordable-positive-refill")),
            "native cohesion dispersal remains terminal and never triggers a boost or second release");

        // Seed only the starting native camp state. Use the real camp setter,
        // native leader transfer/finalization, army release and console cancel.
        Property(models, "PartyImpairmentModel", Blank<DefaultPartyImpairmentModel>());
        Property(models, "PartyNavigationModel", Blank<DefaultPartyNavigationModel>());
        Property(campaign, "DefaultSiegeStrategies", Blank<DefaultSiegeStrategies>());
        var independent = Lord("other-besieger", ally, 50, 55);
        Field(independent.LeaderHero, "_partyBelongedTo", independent);
        Field(leader.LeaderHero, "_partyBelongedTo", leader);
        nativeScore.Minimum = 200;
        BesiegerCamp FollowOnCamp(params MobileParty[] others)
        {
            var siege = SiegeAt(adjacent, kingdom, leader);
            var camp = siege.BesiegerCamp;
            camp.SetSiegeStrategy(new SiegeStrategy("fixture-already-chosen"));
            var roster = (IList)typeof(BesiegerCamp).GetField("_besiegerParties", flags).GetValue(camp);
            foreach (var party in new[]{leader}.Concat(others))
            {
                roster.Add(party);
                Field(party, "_besiegerCamp", camp);
            }
            return camp;
        }
        army = Guard("adjacent");
        GuardComplete();
        price.Cost = 0;
        Field(leader.LeaderHero, "_partyBelongedTo", null);
        member.Army = army;
        Field(leader.LeaderHero, "_partyBelongedTo", leader);
        Field(member, "_position", target.GatePosition);
        army.AddPartyToMergedParties(member);
        var campWithOther = FollowOnCamp(member, independent);
        independent.SetMoveBesiegeSettlement(adjacent, MobileParty.NavigationType.Default);
        var prepStart = log.Count;
        Control("cancel");
        Tick();
        Review();
        Check(leader.Army == null && member.Army == null && leader.BesiegerCamp == null && member.BesiegerCamp == null &&
            independent.BesiegerCamp == campWithOther && campWithOther.LeaderParty == independent &&
            adjacent.SiegeEvent == campWithOther.SiegeEvent && independent.TargetSettlement == adjacent &&
            log.Skip(prepStart).Count(line => line.Contains("Assignment withdrawal:")) == 1,
            "cancel in preparation releases the attached formation and leaves the independent camp participant and siege intact");
        Check(leader.DefaultBehavior == AiBehavior.Hold && leader.ShortTermBehavior == AiBehavior.Hold &&
            leader.TargetSettlement == null && army.AiBehaviorObject == null && Control("status").Contains("cancelled-by-player"),
            "withdrawal clears both native siege orders, preventing immediate re-camp");
        independent.BesiegerCamp = null;
        Check(adjacent.SiegeEvent == null, "remaining camp ends natively only when its last participant leaves");

        army = Guard("adjacent");
        GuardComplete();
        var campDuringBattle = FollowOnCamp();
        var assault = Blank<MapEvent>();
        Property(assault, "Component", Blank<SiegeAssaultEventComponent>());
        var assaultSide = Blank<MapEventSide>();
        Field(assaultSide, "_mapEvent", assault);
        Field(adjacent.Party, "_mapEventSide", assaultSide);
        // The target-side event alone is sufficient to prevent finalizing its camp.
        army.Cohesion = 40;
        payer.Influence = 300;
        var pendingStart = log.Count;
        Control("cancel");
        Tick();
        Resume(extraKeys: 5);
        Check(leader.Army == army && leader.BesiegerCamp == campDuringBattle &&
            adjacent.Party.MapEvent == assault && leader.TargetSettlement == adjacent && army.Cohesion == 40 &&
            payer.Influence == 300 && Control("status").Contains("phase=closing") &&
            log.Skip(pendingStart).Any(line => line.Contains("withdrawal deferred:") && line.Contains("blocker=native-battle")),
            "battle-held cancellation persists without touching the native event, its order or influence");
        dispatcher.OnMapEventEnded(assault);
        Field(adjacent.Party, "_mapEventSide", null);
        // A field event on the commander side still blocks the same request.
        Field(leader.Party, "_mapEventSide", battleSide);
        Tick();
        Check(leader.Army == army && leader.BesiegerCamp == campDuringBattle && leader.MapEvent == battle &&
            army.Cohesion == 40 && payer.Influence == 300, "a commander field battle also defers camp withdrawal");
        dispatcher.OnMapEventEnded(battle);
        Field(leader.Party, "_mapEventSide", null);
        Tick();
        Review();
        Check(leader.Army == null && leader.BesiegerCamp == null && adjacent.SiegeEvent == null &&
            leader.TargetSettlement == null && payer.Influence == 300 &&
            log.Skip(pendingStart).Count(line => line.Contains("Assignment withdrawal:")) == 1 &&
            log.Skip(pendingStart).Count(line => line.Contains("Muster release:")) == 1 &&
            log.Skip(pendingStart).All(line => !line.Contains("Assignment follow-on issued:")),
            "after the assault unlocks, removes the sole formation, lets native finalize, and never retries or charges");

        army = Guard("adjacent");
        GuardComplete();
        var protectedCamp = FollowOnCamp(player);
        Control("cancel");
        Tick();
        Check(leader.Army == army && player.BesiegerCamp == protectedCamp && adjacent.SiegeEvent == protectedCamp.SiegeEvent &&
            log.Any(line => line.Contains("withdrawal deferred:") && line.Contains("blocker=player-protection")),
            "explicit cancellation does not change a player-involved camp");
        // Model the player leaving under their own control.
        ((IList)typeof(BesiegerCamp).GetField("_besiegerParties", flags).GetValue(protectedCamp)).Remove(player);
        Field(player, "_besiegerCamp", null);
        Review();
        Check(leader.Army == null && adjacent.SiegeEvent == null, "withdrawal resumes when the player is no longer involved");

        army = Guard();
        var simpleStart = log.Count;
        Control("cancel");
        Tick();
        Check(leader.Army == null && log.Skip(simpleStart).Any(line => line.Contains("Muster release:")) &&
            log.Skip(simpleStart).All(line => !line.Contains("Assignment withdrawal")),
            "cancelling an ordinary guard uses the existing simple release path");

        nativeScore.Minimum = 200;
        forcePowers[member.Party] = 70;
        double visitH = clock + 40;
        void StartVisit(bool withCall, float x = -20f, string deadline = "30")
        {
            if (leader.CurrentSettlement != null) ((IList)leader.CurrentSettlement.Parties).Remove(leader);
            if (member.CurrentSettlement != null) ((IList)member.CurrentSettlement.Parties).Remove(member);
            Field(leader, "_currentSettlement", null);
            Field(member, "_currentSettlement", null);
            Field(leader, "_position", new CampaignVec2(new Vec2(x, 0), true));
            Field(member, "_position", new CampaignVec2(new Vec2(-25, 0), true));
            visitH += 40;
            At(visitH);
            Control("start", "march", "destination", withCall ? "300" : "200", deadline);
            Tick();
        }
        void ResetNativeOrder()
        {
            leader.SetMoveModeHold();
            typeof(MobileParty).GetMethod("SetNavigationModeHold", flags).Invoke(leader, null);
        }
        int rejectedBefore = log.Count;
        visitPathAvailable = false;
        StartVisit(false);
        visitPathAvailable = true;
        Check(leader.Army == null && log.Skip(rejectedBefore).Any(line => line.Contains("native-muster-path-unreachable")) &&
            !log.Skip(rejectedBefore).Any(line => line.Contains("Muster debit")),
            "a distance estimate alone does not admit a muster with no native movement path");

        int recoveryLog = log.Count;
        // The headless world has no native skill catalog. Keep the actual
        // membership charge and skip only its XP side effect.
        Prefix(typeof(DefaultSkillLevelingManager).GetMethod("OnInfluenceSpent", flags), nameof(SkipInfluenceSkillXp));
        price.Cost = 50;
        float balanceBefore = payer.Influence;
        StartVisit(true);
        army = leader.Army;
        float balanceAfter = payer.Influence;
        int charges = log.Skip(recoveryLog).Count(line => line.Contains("Muster debit"));
        int summons = log.Skip(recoveryLog).Count(line => line.Contains("Muster summons"));
        ResetNativeOrder(); // native RemoveParty can reset both layers, without clearing Army.AiBehaviorObject
        At(visitH + 1);
        Review();
        Check(army != null && leader.Army == army && member.Army == army && leader.TargetSettlement == muster &&
            leader.DefaultBehavior == AiBehavior.GoToSettlement && balanceAfter == balanceBefore - 50 && payer.Influence == balanceAfter &&
            summons > 0 && charges == summons + 1 && log.Skip(recoveryLog).Count(line => line.Contains("Muster debit")) == charges &&
            log.Skip(recoveryLog).Count(line => line.Contains("Muster summons")) == summons &&
            log.Skip(recoveryLog).Any(line => line.Contains("actual=Hold:-") && line.Contains("armyObjective=muster") && line.Contains("reissue=1")),
            "native Hold is reissued on the original muster without a second membership debit: " +
            $"{balanceBefore}/{balanceAfter}/{payer.Influence} charges={charges} {Control("status")}\n" +
            string.Join("\n", log.Skip(recoveryLog).Where(line => line.Contains("Muster visit") || line.Contains("Muster debit") || line.Contains("Muster summons"))));
        Resume();
        ResetNativeOrder();
        At(visitH + 1.25);
        Review();
        Check(leader.DefaultBehavior == AiBehavior.Hold && leader.Army == army,
            "reload retains the recovery cooldown rather than rapidly reissuing");
        At(visitH + 2);
        Review();
        ResetNativeOrder();
        At(visitH + 3);
        Review();
        Check(leader.Army == null && member.Army == null && payer.Influence == balanceAfter &&
            Control("status").Contains("commander-visit-retry-exhausted") &&
            log.Skip(recoveryLog).Count(line => line.Contains("Muster visit recovery before:")) == 2 &&
            log.Skip(recoveryLog).Any(line => line.Contains("before cancellation:") && line.Contains("actual=Hold:-")),
            "persistent native Hold stops after two reissues and reports the pre-dispersal state");
        price.Cost = 0;
        payer.Influence = balanceBefore;

        StartVisit(false);
        army = leader.Army;
        leader.SetMoveModeHold();
        Field(leader.Party, "_mapEventSide", battleSide);
        At(visitH + 1);
        Review();
        Check(leader.Army == army && leader.DefaultBehavior == AiBehavior.Hold &&
            leader.MapEvent == battle, "battle safety holds the native order without recovery or dispersal");
        Field(leader.Party, "_mapEventSide", null);
        visitPathAvailable = false;
        At(visitH + 2);
        Review();
        visitPathAvailable = true;
        Check(leader.Army == null && Control("status").Contains("native-visit-path-unreachable"),
            "an apparent ETA cannot override a failed native movement-path probe");

        StartVisit(false);
        At(visitH + 6);
        Review();
        Check(leader.Army == null && Control("status").Contains("commander-visit-no-route-progress"),
            "a standing GoToSettlement with no route progress still terminates");

        StartVisit(false, -350f, "100");
        army = leader.Army;
        ResetNativeOrder();
        leader.StartTransitionNextFrameToExitFromPort = true;
        At(visitH + 1);
        Review();
        Check(army != null && leader.Army == army && leader.DefaultBehavior == AiBehavior.Hold &&
            (int)behavior.GetField("_visitRetries", flags).GetValue(reporting) == 0,
            "a scheduled native map transition is not interrupted by recovery");
        leader.StartTransitionNextFrameToExitFromPort = false;
        At(visitH + 2);
        Review();
        Field(leader, "_position", new CampaignVec2(new Vec2(-320, 0), true));
        At(visitH + 8);
        Review();
        Check(leader.Army == army && leader.DefaultBehavior == AiBehavior.GoToSettlement &&
            leader.TargetSettlement == muster && Control("status").Contains("phase=gathering"),
            "native Hold on a long ground leg recovers after transition, and measured progress keeps the same muster alive");
        leader.Ai.SetDoNotMakeNewDecisions(true);
        ResetNativeOrder();
        At(visitH + 9);
        Review();
        Check(leader.Army == army && leader.DefaultBehavior == AiBehavior.Hold && Control("status").Contains("phase=closing") &&
            Control("status").Contains("party-control-lock"), "loss of party control closes without reissue or disband while locked");
        leader.Ai.SetDoNotMakeNewDecisions(false);
        Review();
        Check(leader.Army == null && Control("status").Contains("phase=finished"),
            "the same owned army releases once its party-control lock clears");

        StartVisit(false);
        leader.SetMoveModeHold();
        leader.Army.AiBehaviorObject = target;
        At(visitH + 1);
        Review();
        Check(leader.Army == null && Control("status").Contains("commander-move-no-longer-active"),
            "a different army objective is lost authority, never a recoverable Hold");

        StartVisit(false);
        ResetNativeOrder();
        int invalidLog = log.Count;
        Field(muster.Town, "_ownerClan", hostile);
        At(visitH + 1);
        Review();
        Field(muster.Town, "_ownerClan", payer);
        Check(leader.Army == null && Control("status").Contains("target-or-muster-lost-or-besieged") &&
            !log.Skip(invalidLog).Any(line => line.Contains("Muster visit recovery")),
            "loss of the muster fort invalidates even a cleared Hold on the original objective");

        StartVisit(false);
        army = leader.Army;
        // Native current settlement observed, then a native reset during the
        // second leg. Retain the same army and do not repeat muster.
        Field(leader, "_currentSettlement", muster);
        ((IList)muster.Parties).Add(leader);
        Review();
        leader.SetMoveModeHold();
        At(visitH + 1);
        Review();
        Check(leader.Army == army && leader.TargetSettlement == target && Control("status").Contains("phase=marching"),
            "march recovery restores only its own leg rather than replaying muster");
        Control("cancel");
        Tick();

        clock = visitH + 1;
        var acceptance = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.CampaignAcceptanceBehavior");
        if (acceptance == null)
        {
            Check(mod.GetType("BannerlordMilitaryAIOverhaul.SubModule").GetField("_acceptance", flags) == null,
                "ordinary build contains no acceptance runner");
            return;
        }
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maio-smoke-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            string Arm()
            {
                string markerId = Guid.NewGuid().ToString("N");
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "acceptance-reuse-v1.armed"), "reuse-v1 " + markerId);
                return markerId;
            }
            CampaignBehaviorBase Runner() => (CampaignBehaviorBase)Activator.CreateInstance(acceptance, flags, null,
                new object[]{reporting, sessionLog, directory}, null);
            void Run(object instance, string method, params object[] args) => acceptance.GetMethod(method, flags).Invoke(instance, args);
            object Read(object instance, string field) => acceptance.GetField(field, flags).GetValue(instance);
            long wall = DateTime.UtcNow.Ticks;
            void Step(object instance, double h) => Run(instance, "Tick", h, wall);
            var unarmed = Runner();
            Run(unarmed, "OnLoaded");
            Step(unarmed, clock + 32);
            Check(Read(unarmed, "_id") == null && leader.Army == null, "unarmed campaign never runs the acceptance fixture");

            var manual = Guard();
            double now = clock + 8;
            var runner = Runner();
            string id = Arm();
            Run(runner, "OnLoaded");
            var fromEarlierSave = Runner();
            Run(fromEarlierSave, "OnLoaded");
            Check((string)Read(runner, "_id") == id && Read(fromEarlierSave, "_id") == null,
                "arming is consumed once, even if an earlier save is loaded again");
            Run(runner, "Tick", now, wall);
            Check(leader.Army == manual && (int)Read(runner, "_attempts") == 0, "non-map state cannot take over or search");
            currentGame.SetValue(null, game);
            Step(runner, now);
            Check(leader.Army == manual && Control("status").Contains("superseded-by-armed-acceptance"),
                "armed runner requests safe old manual release rather than overlapping");
            Tick();
            Check(leader.Army == null && player.Army == playerArmy, "safe old fixture release does not affect the player formation");

            Field(kingdom, "_factionsAtWarWith", new MBList<IFaction>(new[]{enemy}));
            Field(enemy, "_factionsAtWarWith", new MBList<IFaction>(new[]{kingdom}));
            Field(enemy, "_fiefsCache", new MBList<Town>(new[]{adjacent.Town}));
            void PositionAt(MobileParty party, Settlement at)
            {
                if (party.CurrentSettlement != null) ((IList)party.CurrentSettlement.Parties).Remove(party);
                Field(party, "_currentSettlement", at);
                if (at != null)
                {
                    Field(party, "_position", at.GatePosition);
                    ((IList)at.Parties).Add(party);
                }
            }
            PositionAt(leader, null);
            Field(leader, "_position", new CampaignVec2(new Vec2(20, 0), true));
            nativeScore.Minimum = 100000;
            now += 2;
            At(now);
            Step(runner, now);
            Check((int)Read(runner, "_attempts") == 0 && leader.Army == null,
                "unviable siege preview creates no army and does not exhaust an attempt");
            nativeScore.Minimum = 200;
            now += 2;
            At(now);
            Step(runner, now);
            var original = (Army)Read(runner, "_original");
            var selectedGuard = (Settlement)Read(runner, "_guard");
            if (original == null) throw new Exception(Control("status") + " " + string.Join("\n", log.Skip(log.Count - 12)));
            Check(original == leader.Army && original != null && (int)Read(runner, "_attempts") == 1,
                "retry after changed strength starts exactly one real commander muster");

            // Reach the gate, then exercise the installed settlement encounter.
            var actualMuster = (Settlement)behavior.GetField("_muster", flags).GetValue(reporting);
            void EnterGuard(Settlement at)
            {
                Field(leader, "_position", at.GatePosition);
                typeof(MobileParty).GetMethod("SetShortTermBehavior", flags).Invoke(leader, new object[]{AiBehavior.GoToSettlement, at.Party});
                EncounterManager.StartSettlementEncounter(leader, at);
            }
            At(now + 5);
            Field(leader, "_position", selectedGuard.GatePosition);
            Review();
            Check(actualMuster == selectedGuard && leader.CurrentSettlement == null && Control("status").Contains("phase=gathering"),
                "zero ETA at the frontier gate is not physical arrival or a completed muster");
            int guardLog = log.Count;
            EnterGuard(selectedGuard);
            Tick();
            Step(runner, now + 5);
            Check((bool)Read(runner, "_guardSeen") && leader.Army == original && leader.CurrentSettlement == selectedGuard &&
                leader.DefaultBehavior == AiBehavior.DefendSettlement &&
                log.Skip(guardLog).Any(line => line.Contains("Muster settlement entry:")) &&
                !log.Skip(guardLog).Any(line => line.Contains("Muster departure:")),
                "native entry into the shared frontier muster issues and observes a real guard without a synthetic march");
            At(now + 5.75);
            Review();
            Step(runner, now + 5.75);
            Check(leader.DefaultBehavior == AiBehavior.DefendSettlement && !(bool)Read(runner, "_handoff"),
                "frontier guard does not hand off before completing its full duration");
            At(now + 6);
            Review();
            Step(runner, now + 6);
            Check((bool)Read(runner, "_handoff") && Read(runner, "_verdict") == null && leader.TargetSettlement == adjacent,
                "receiving an actual siege order is not native contact or a pass");
            var state = new Store { IsSaving = true };
            runner.SyncData(state);
            state.IsSaving = false;
            runner = Runner();
            runner.SyncData(state);
            Run(runner, "OnLoaded");
            Check(ReferenceEquals(Read(runner, "_original"), original) && (bool)Read(runner, "_handoff"),
                "in-flight runner reload retains army identity and order evidence");
            var acceptanceCamp = FollowOnCamp(independent);
            independent.SetMoveBesiegeSettlement(adjacent, MobileParty.NavigationType.Default);
            var roster = (IList)typeof(BesiegerCamp).GetField("_besiegerParties", flags).GetValue(acceptanceCamp);
            roster.Remove(leader);
            At(now + 7);
            Step(runner, now + 7);
            Check(Read(runner, "_verdict") == null, "camp at the right target without original-leader roster membership cannot pass");
            Property(models, "MilitaryPowerModel", new FixtureCampPower());
            typeof(BesiegerCamp).GetMethod("AddSiegePartyInternal", flags).Invoke(acceptanceCamp, new object[]{leader});
            Step(runner, now + 7);
            Check(((string)Read(runner, "_verdict")).StartsWith("PASS") && leader.Army == original,
                "actual camp membership proves contact and queues safe commander cleanup");
            Tick();
            Step(runner, now + 7);
            Check(leader.Army == null && independent.BesiegerCamp == acceptanceCamp && ((string)Read(runner, "_result")).Contains(id),
                "cleanup leaves independent camp intact and commits a stable-ID result");
            independent.BesiegerCamp = null;

            int popups = 0;
            string text = null;
            void CaptureInquiry(InquiryData inquiry, bool pause, bool priority) { popups++; text = inquiry.Text; }
            InformationManager.OnShowInquiry += CaptureInquiry;
            var inquiryActive = InformationManager.IsAnyInquiryActiveInternal;
            try
            {
                InformationManager.IsAnyInquiryActiveInternal = () => true;
                Run(runner, "Frame", campaign);
                Check(popups == 0, "completion does not replace an existing native inquiry");
                InformationManager.IsAnyInquiryActiveInternal = () => false;
                Run(runner, "Frame", campaign);
                Run(runner, "Frame", campaign);
                runner = Runner();
                runner.SyncData(state); // stale in-flight save from before notification
                Run(runner, "OnLoaded");
                Run(runner, "Frame", campaign);
                Check(popups == 1 && text.Contains("Return to the coordinator") && text.Contains(id),
                    "native completion inquiry is sent once even after reloading the stale in-flight save");

                army = Guard();
                var timeout = Runner();
                Arm();
                Run(timeout, "OnLoaded");
                Run(timeout, "Tick", (double)Read(timeout, "_deadline"), wall);
                Check(((string)Read(timeout, "_result")).Contains("INCONCLUSIVE") && leader.Army == army,
                    "terminal timeout reports inconclusive and cannot cancel somebody else's assignment");
                Control("cancel");
                Tick();
                var paused = Runner();
                Arm();
                Run(paused, "OnLoaded");
                Field(paused, "_wallDeadline", DateTime.UtcNow.AddSeconds(-1).Ticks);
                currentGame.SetValue(null, priorGame);
                Run(paused, "Frame", campaign);
                Check(((string)Read(paused, "_result")).Contains("wall-clock limit") && popups == 1,
                    "wall-clock watchdog records an inconclusive result even if the map never progresses");
                currentGame.SetValue(null, game);
                Run(paused, "Frame", campaign);
                Run(paused, "Frame", campaign);
                Check(popups == 2, "watchdog shows its one completion on return to safe map UI");

                // In the old completed save, cleanup can still belong to the
                // previous run. A fresh arm must wait, not bypass that ownership.
                army = Guard();
                Field(reporting, "_acceptanceOwner", id);
                Field(leader.Party, "_mapEventSide", battleSide);
                var nextId = Arm();
                var rearmed = Runner();
                rearmed.SyncData(state);
                Run(rearmed, "OnLoaded");
                Run(rearmed, "Frame", campaign);
                Tick();
                Check((string)Read(rearmed, "_id") == id && leader.Army == army &&
                    System.IO.File.Exists(System.IO.Path.Combine(directory, "acceptance-reuse-v1.armed")),
                    "a new arm is not claimed over unfinished owned cleanup in a previously completed save");
                Field(leader.Party, "_mapEventSide", null);
                Review();
                Run(rearmed, "Frame", campaign);
                Check((string)Read(rearmed, "_id") == nextId && (int)Read(rearmed, "_attempts") == 0 &&
                    Read(rearmed, "_result") == null && popups == 2 &&
                    System.IO.File.Exists(System.IO.Path.Combine(directory, $"acceptance-reuse-v1.{id}.shown")),
                    "the same disposable save can claim a fresh run after cleanup without replaying the preceding popup");

                // Replay the logged shape: a 17.6h solo siege quote, dwindling
                // speed, and 8.37h still to the friendly gate at about H+100.
                // The nearest 16 free parties are recruits, not legal leaders.
                Property(target, "GatePosition", new CampaignVec2(new Vec2(240, 0), true));
                Property(adjacent, "GatePosition", new CampaignVec2(new Vec2(303.47f, 0), true));
                PositionAt(leader, null);
                Field(leader, "_position", new CampaignVec2(new Vec2(54, 0), true));
                movementSpeed = 3.61f;
                foreach (var party in new[]{member, spare, independent}) forcePowers[party.Party] = 0;
                var localRecruits = new List<MobileParty>();
                currentGame.SetValue(null, priorGame);
                for (int i = 1; i <= 16; ++i)
                    localRecruits.Add(Lord($"nearby-recruit-{i}", payer, 60, 240 - i));
                currentGame.SetValue(null, game);
                var brittle = Runner();
                Arm();
                Run(brittle, "OnLoaded");
                Field(brittle, "_deadline", clock + 119d);
                nativeScore.Minimum = 700;
                At(clock + 9);
                float beforeBrittle = payer.Influence;
                Step(brittle, clock + 9);
                Check(leader.Army == null && (int)Read(brittle, "_attempts") == 0 && payer.Influence == beforeBrittle &&
                    ((string)Read(brittle, "_searchSummary")).Contains("leaderFrontierTimeOrRoute="),
                    "a 110h remainder rejects the optimistic dossier before paying when slow travel does not fit");
                nativeScore.Minimum = 100000;
                At(clock + 9);
                Step(rearmed, clock + 9);
                Check(leader.Army == null && (int)Read(rearmed, "_attempts") == 0 &&
                    ((string)Read(rearmed, "_searchSummary")).Contains("nativeStrength=") &&
                    ((string)Read(rearmed, "_searchSummary")).Contains("eligibleLeaderChecks="),
                    "aggregate discovery identifies a genuinely infeasible strength gate without creating an army");
                nativeScore.Minimum = 700;
                double started = clock + 11;
                for (; started <= clock + 19; started += 2)
                {
                    At(started);
                    Step(rearmed, started);
                    if (leader.Army != null)
                        break;
                }
                Check(leader.Army != null && leader.Army.Parties.Count > 9 &&
                    (Settlement)Read(rearmed, "_target") == adjacent &&
                    log.Any(line => line.Contains($"Acceptance run={nextId}") && line.Contains("siegeETA=17.58")),
                    "full remaining budget admits the same profile with local real recruits and padded siege transit");
                var slowArmy = leader.Army;
                var called = localRecruits.Where(recruit => recruit.Army == slowArmy).ToArray();
                foreach (var recruit in called.Take(called.Length - 2))
                    PositionAt(recruit, target);
                Review();
                for (int elapsed = 4; elapsed <= 96; elapsed += 4)
                {
                    movementSpeed = 3.61f - 2.47f * elapsed / 96;
                    Field(leader, "_position", new CampaignVec2(new Vec2(54f + 173f * elapsed / 96, 0), true));
                    At(started + elapsed);
                    Review();
                }
                Field(leader, "_position", new CampaignVec2(new Vec2(228.82f, 0), true));
                movementSpeed = 1.114f;
                At(started + 96.93);
                Review();
                Field(leader, "_position", new CampaignVec2(new Vec2(231.4f, 0), true));
                movementSpeed = 1.0275f;
                At(started + 99.96);
                Review();
                var remainingEta = (double)command.GetMethod("HoursTo", flags).Invoke(null, new object[] {leader, target});
                Check(leader.Army == slowArmy && Control("status").Contains("phase=gathering") &&
                    Math.Abs(remainingEta - 8.37d) < 0.02d && remainingEta > 8.04d,
                    "8.37h to the gate at H+99.96 would miss the old 108h window but does not cancel frontier setup");
                int slowLog = log.Count;
                At(started + 108.5);
                EnterGuard(target);
                Tick();
                Step(rearmed, started + 108.5);
                Check(leader.CurrentSettlement == target && leader.Army == slowArmy && !(bool)Read(rearmed, "_guardSeen") &&
                    Control("status").Contains("phase=gathering"), "native arrival with unmerged strength waits for physical recruits");
                foreach (var recruit in called.Skip(called.Length - 2))
                    PositionAt(recruit, target);
                At(started + 109);
                Review();
                Step(rearmed, started + 109);
                Check(leader.Army == slowArmy && (bool)Read(rearmed, "_guardSeen") && leader.CurrentSettlement == target &&
                    log.Skip(slowLog).Any(line => line.Contains("Assignment guard issued:")) &&
                    !log.Skip(slowLog).Any(line => line.Contains("Muster departure:")),
                    "physical frontier arrival after the old deadline reaches guard with the original assembled strength");
                Control("cancel");
                Tick();
                movementSpeed = 5f;
                var seated = Runner();
                Arm();
                Run(seated, "OnLoaded");
                nativeScore.Minimum = 200;
                At(started + 110);
                Step(seated, started + 110);
                Tick();
                Step(seated, started + 110);
                Check(leader.CurrentSettlement == target && (bool)Read(seated, "_guardSeen") &&
                    leader.DefaultBehavior == AiBehavior.DefendSettlement,
                    "a leader already in the frontier fort starts the real guard without another entry event");
                Control("cancel");
                Tick();
                var none = Runner();
                Arm();
                Run(none, "OnLoaded");
                nativeScore.Minimum = 100000;
                Step(none, started + 112);
                Step(none, (double)Read(none, "_deadline"));
                var noResult = (string)Read(none, "_result");
                Check(noResult.Contains("no feasible army and border handoff") && noResult.Contains("attempts=0") &&
                    noResult.Contains("nativeStrength=") && !noResult.Contains("cancelled-by-player") &&
                    noResult.Contains("borderPairsRemaining="), "zero-attempt terminal report identifies search blockers rather than the previous fixture");
            }
            finally
            {
                InformationManager.OnShowInquiry -= CaptureInquiry;
                InformationManager.IsAnyInquiryActiveInternal = inquiryActive;
            }
        }
        finally
        {
            movementSpeed = 5f;
            currentGame.SetValue(null, priorGame);
            foreach (var path in System.IO.Directory.GetFiles(directory)) System.IO.File.Delete(path);
            System.IO.Directory.Delete(directory);
        }
    }

    private static void TestActualDispersionBranch(Type stageType, CampaignBehaviorBase stage, Type logType, Harmony harmony, Army army, Army playerArmy, MobileParty traveler, MapEventSide battleSide)
    {
        var check = typeof(Army).GetMethod("CheckArmyDispersion", flags);
        harmony.Patch(check, transpiler: new HarmonyMethod(typeof(SmokeFixtures).GetMethod("ForceDispersionRoll", flags)));
        var capture = new HarmonyMethod(typeof(SmokeFixtures).GetMethod("CaptureDisperse", flags)) { priority = Priority.First };
        harmony.Patch(typeof(Army).GetMethod("DisperseInternal", flags), prefix: capture);
        var kingdom = Blank<Kingdom>();
        var clan = Blank<Clan>();
        var enemies = new MBList<IFaction>();
        Field(kingdom, "_factionsAtWarWith", enemies);
        Field(kingdom, "_armies", new MBList<Army>(new[]{army}));
        Field(clan, "_kingdom", kingdom);
        Field(army, "_kingdom", kingdom);
        army.LeaderParty.ActualClan = clan;
        army.Cohesion = 80f;
        var pending = (IList)stageType.GetField("_pending", flags).GetValue(stage);
        pending.Add(army);
        Field(traveler.Party, "_mapEventSide", battleSide);
        var blockers = stageType.GetMethod("ReleaseBlockers", flags);
        Check(((string)blockers.Invoke(null, new object[]{army})).Contains("travel:map-event"),
            "the native dispersion fixture is also blocked from Stage 0 takeover by the traveling member's battle");

        check.Invoke(army, null);
        Check(rolls == 1 && nativeDispersals.Count == 0 && pending.Contains(army)
            && log.Any(line => line.Contains("reason=no-active-war")), "patched native peace-roll branch leaves a viable pending army intact");

        var landlessEnemy = Blank<Kingdom>();
        Field(landlessEnemy, "_fiefsCache", new MBList<Town>());
        enemies.Add(landlessEnemy);
        check.Invoke(army, null);
        Check(rolls == 2 && nativeDispersals.Count == 0 && pending.Contains(army)
            && log.Count(line => line.Contains("reason=no-active-war")) == 1,
            "landless enemy triggers the native roll, blocked while an unattached member is in battle and deduped");
        Field(traveler.Party, "_mapEventSide", null);

        army.Cohesion = 0f;
        check.Invoke(army, null);
        Check(rolls == 3 && nativeDispersals.SequenceEqual(new[]{Army.ArmyDispersionReason.CohesionDepleted}),
            "AI cohesion dispersal downstream of the blocked roll still executes");
        nativeDispersals.Clear();

        army.Cohesion = 80f;
        army.LeaderParty.IsActive = false;
        check.Invoke(army, null);
        Check(rolls == 4 && nativeDispersals.SequenceEqual(new[]{Army.ArmyDispersionReason.Unknown}),
            "inactive AI leader cleanup downstream of the blocked roll still executes");
        nativeDispersals.Clear();
        army.LeaderParty.IsActive = true;

        Field(army.LeaderParty.Party, "_remainingFoodPercentage", -1);
        check.Invoke(army, null);
        Check(rolls == 4 && nativeDispersals.SequenceEqual(new[]{Army.ArmyDispersionReason.FoodProblem}),
            "starvation cleanup before the peace roll still executes");
        nativeDispersals.Clear();
        Field(army.LeaderParty.Party, "_remainingFoodPercentage", 0);

        playerArmy.Cohesion = 80f;
        check.Invoke(playerArmy, null);
        playerArmy.Cohesion = 0f;
        check.Invoke(playerArmy, null);
        Check(rolls == 4 && nativeDispersals.SequenceEqual(new[]{Army.ArmyDispersionReason.CohesionDepleted}),
            "player still skips the AI-only peace roll and disperses naturally at zero cohesion");
        nativeDispersals.Clear();

        stageType.GetMethod("Close", flags).Invoke(stage, null);
        logType.GetMethod("Close", flags).Invoke(null, new object[]{Campaign.Current});
        check.Invoke(army, null);
        Check(rolls == 5 && nativeDispersals.SequenceEqual(new[]{Army.ArmyDispersionReason.NoActiveWar}),
            "outside the owning Stage 0 campaign the original no-active-war action still runs");
        nativeDispersals.Clear();
    }
    public static void Run(Assembly mod, Harmony harmony, Assembly previousMod)
    {
        var stageType = mod.GetType("BannerlordMilitaryAIOverhaul.Campaign.ArmyTakeoverBehavior", true);
        var selectionType = mod.GetType("BannerlordMilitaryAIOverhaul.Patches.PartyOrderSelectionPatch", true);
        var nativeControlType = mod.GetType("BannerlordMilitaryAIOverhaul.Patches.NativeArmyControlPatches", true);
        var logType = mod.GetType("BannerlordMilitaryAIOverhaul.Diagnostics.CampaignLog", true);
        var previousStageType = previousMod == null ? stageType :
            previousMod.GetType(stageType.FullName) ?? previousMod.GetType("BannerlordMilitaryAIOverhaul.Stage0Takeover", true);
        Check(mod.GetType("SmokeFixtures") == null && !mod.GetReferencedAssemblies().Any(a => a.Name == "SmokeFixtures"),
            "the shipped assembly neither contains nor references the fixture");
        var untouched = new[]{
            typeof(Army).GetMethod("HourlyTick", flags), typeof(Army).GetMethod("FinishArmyObjective", flags),
            typeof(Army).GetMethod("OnRemovePartyInternal", flags), typeof(Army).GetMethod("BoostCohesionWithInfluence", flags),
            typeof(Kingdom).GetMethod("CreateArmy", flags), typeof(DisbandArmyAction).GetMethod("ApplyByNoActiveWar", flags)
        };
        Check(untouched.All(method => method != null && Harmony.GetPatchInfo(method) == null),
            "hourly execution, objective invalidation, leader removal, and shared/manual creation, boost, and disband APIs are untouched");
        void IsolateLog(Type type)
        {
            // A release JIT can inline Write. Intercept the file sink before
            // invoking any current or compatibility behavior.
            var append = type.GetMethod("Append", flags);
            if (append != null)
                harmony.Patch(append, prefix: new HarmonyMethod(typeof(SmokeFixtures).GetMethod("CaptureBatch", flags)));
            harmony.Patch(type.GetMethod("Write", flags), prefix: new HarmonyMethod(typeof(SmokeFixtures).GetMethod("CaptureLog", flags)));
        }
        IsolateLog(logType);
        if (previousMod != null)
        {
            Check(mod.ManifestModule.ModuleVersionId != previousMod.ManifestModule.ModuleVersionId, "old and new builds are both loaded as distinct assemblies");
            var previousLogType = previousMod.GetType(logType.FullName) ?? previousMod.GetType("BannerlordMilitaryAIOverhaul.Stage0Log", true);
            IsolateLog(previousLogType);
        }
        var campaign = Blank<Campaign>();
        typeof(Campaign).GetProperty("Current", flags).SetValue(null, campaign, null);
        var tracker = FormatterServices.GetUninitializedObject(typeof(Campaign).Assembly.GetType("TaleWorlds.CampaignSystem.MapTimeTracker", true));
        Property(campaign, "MapTimeTracker", tracker);
        var player = Party("player");
        Property(campaign, "MainParty", player);
        var sessionLog = logType.GetMethod("Open", flags).Invoke(null, new object[]{campaign});
        logType.GetMethod("Event", flags).Invoke(sessionLog, new object[]{"Standalone campaign log fixture"});
        Check(ReferenceEquals(logType.GetProperty("Current", flags).GetValue(null, null), sessionLog) &&
            log.Last().Contains("Standalone campaign log fixture"), "campaign diagnostics work before a takeover behavior exists");
        TestPerformance(mod, harmony, sessionLog);
        var stage = (CampaignBehaviorBase)Activator.CreateInstance(stageType, flags, null, new[]{campaign,sessionLog}, null);
        var lead = Party("ai-leader");
        var other = Party("travel");
        var think = new PartyThinkParams(lead);
        var form = new AIBehaviorData((Settlement)null, AiBehavior.BesiegeSettlement, MobileParty.NavigationType.Default, true, false, false);
        var visit = new AIBehaviorData((Settlement)null, AiBehavior.GoToSettlement, MobileParty.NavigationType.Default, false, false, false);
        think.AddBehaviorScore(new ValueTuple<AIBehaviorData, float>(form, 2));
        think.AddBehaviorScore(new ValueTuple<AIBehaviorData, float>(visit, 1));
        var select = selectionType.GetMethod("SelectableScores", flags);
        Func<PartyThinkParams, MBReadOnlyList<ValueTuple<AIBehaviorData, float>>> filtered =
            p => (MBReadOnlyList<ValueTuple<AIBehaviorData, float>>)select.Invoke(null, new object[]{p});
        Check(filtered(think).Count == 1 && filtered(think)[0].Item1.AiBehavior == AiBehavior.GoToSettlement,
            "formation proposal removed; vanilla standalone score intact");
        Check(think.AIBehaviorScores.Count == 2, "shared input score cache was not mutated");
        Check(log.Any(line => line.Contains("rank=would-win-scoring") && line.Contains("topIndependentCandidate=GoToSettlement")
            && line.Contains("not an issued order")), "winning formation is logged as pre-commit with independent candidate");
        think.Reset(lead);
        think.AddBehaviorScore(new ValueTuple<AIBehaviorData, float>(form, 0.3f));
        think.AddBehaviorScore(new ValueTuple<AIBehaviorData, float>(visit, 1));
        Check(filtered(think).Count == 1 && log.Any(line => line.Contains("rank=not-score-winner")),
            "losing formation is filtered and logged, even if it never wins");
        for (int tick = 0; tick < 5; ++tick)
        {
            think.Reset(lead);
            think.AddBehaviorScore(new ValueTuple<AIBehaviorData, float>(form, tick % 2 == 0 ? 2 : 0.5f));
            think.AddBehaviorScore(new ValueTuple<AIBehaviorData, float>(visit, 1));
            filtered(think);
        }
        Check(log.Count(line => line.Contains("Filtered vanilla formation")) == 2
            && log.All(line => !line.Contains("Army created:")), "alternating ranks are deduped and not logged as actual creation");
        var army = Formation(lead, other);
        Check(filtered(think).Count == 0, "AI army leader receives no new strategic selection");
        think.MobilePartyOf = other;
        Check(Object.ReferenceEquals(filtered(think), think.AIBehaviorScores), "existing summoned member continues through vanilla selection");
        var playerArmy = Formation(player, lead);
        think.MobilePartyOf = lead;
        Check(Object.ReferenceEquals(filtered(think), think.AIBehaviorScores), "player-led army member continues through vanilla selection");
        Field(lead, "_army", army);
        nativeControlType.GetMethod("BlockBoost", flags).Invoke(null, new object[]{army, 50f, 10});
        Check(army.Cohesion == 25, "autonomous boost did not alter cohesion or enter native payment");

        var blockers = stageType.GetMethod("ReleaseBlockers", flags);
        Func<Army,string> blocked = a => (string)blockers.Invoke(null, new object[]{a});
        Check(blocked(army) == "", "event-free formation can release");
        var battle = Blank<MapEvent>();
        Property(battle, "Component", Blank<FieldBattleEventComponent>());
        var side = Blank<MapEventSide>();
        Field(side, "_mapEvent", battle);
        Field(other.Party, "_mapEventSide", side);
        Check(blocked(army).Contains("travel:map-event"), "battle of unattached traveling member defers release");
        Field(other.Party, "_mapEventSide", null);
        var siege = Blank<SiegeEvent>();
        var settlement = Blank<Settlement>();
        settlement.StringId = "fixture-town";
        Property(settlement, "SiegeEvent", siege);
        Field(siege, "BesiegedSettlement", settlement);
        Field(other, "_currentSettlement", settlement);
        Check(blocked(army).Contains("travel:siege(fixture-town)"), "settlement siege of traveling member defers release");
        Field(other, "_currentSettlement", null);
        Check(blocked(army) == "", "cleared native event removes release blocker");

        // Use Bannerlord's behavior data store, not a hand-copied dictionary.
        // This exercises its real StringId lookup after the CLR type is renamed.
        const string takeoverId = "BannerlordMilitaryAIOverhaul.Stage0Takeover";
        var fields = stageType.GetField("_pending", flags);
        var writer = stage;
        if (previousMod != null)
        {
            // The pre-refactor writer owned its own logging; the Stage 1
            // writer takes a session log from its own assembly instead.
            var previousLogType = previousMod.GetType(logType.FullName);
            var args = previousLogType == null ? new object[]{campaign} : new object[]{campaign,
                previousLogType.GetMethod("Open", flags).Invoke(null, new object[]{campaign})};
            writer = (CampaignBehaviorBase)Activator.CreateInstance(previousStageType, flags, null, args, null);
        }
        var writerPending = (IList)previousStageType.GetField("_pending", flags).GetValue(writer);
        writerPending.Add(army);
        Field(writer, "_captured", true);
        var storeType = typeof(Campaign).Assembly.GetType("TaleWorlds.CampaignSystem.CampaignBehaviorDataStore", true);
        var store = Activator.CreateInstance(storeType, true);
        storeType.GetMethod("SaveBehaviorData", flags).Invoke(store, new object[]{writer});
        var behaviorRecords = (IDictionary)storeType.GetField("_behaviorDict", flags).GetValue(store);
        Check(writer.StringId == takeoverId && behaviorRecords.Count == 1 && behaviorRecords.Contains(takeoverId),
            "native data store keys takeover by the original behavior ID");
        var saved = behaviorRecords[takeoverId];
        var records = (IDictionary)saved.GetType().GetField("_records", flags).GetValue(saved);
        Check(records.Count == 2 && records["captured"] is bool && (bool)records["captured"] &&
            records["pendingArmies"].GetType() == typeof(List<Army>) && !ReferenceEquals(records["pendingArmies"], writerPending),
            "original captured and pendingArmies keys use bool and a snapshot of native List<Army>");
        writerPending.Clear();
        // SaveSystem restores the records, not BehaviorSaveData's runtime IsSaving
        // flag. Emulate that boundary without attempting to serialize a campaign.
        var loaded = Activator.CreateInstance(saved.GetType(), flags, null, new object[]{false}, null);
        Field(loaded, "_records", records);
        behaviorRecords[takeoverId] = loaded;
        var reloaded = (CampaignBehaviorBase)Activator.CreateInstance(stageType, flags, null, new[]{campaign,sessionLog}, null);
        storeType.GetMethod("LoadBehaviorData", flags).Invoke(store, new object[]{reloaded});
        var restored = (IList)fields.GetValue(reloaded);
        Check(reloaded.StringId == takeoverId && (bool)stageType.GetField("_captured", flags).GetValue(reloaded) &&
            restored.Count == 1 && ReferenceEquals(army, restored[0]),
            "renamed takeover reads captured flag and exact pending reference from the native data store");
        var nextArmy = Formation(lead);
        int loadedLog = log.Count;
        stageType.GetMethod("OnSessionLaunched", flags).Invoke(reloaded, new object[]{null});
        Check(restored.Count == 1 && ReferenceEquals(restored[0], army) && !restored.Contains(nextArmy) &&
            log.Skip(loadedLog).Any(line => line.Contains("no new snapshot")) &&
            log.Skip(loadedLog).All(line => !line.Contains("Takeover captured")),
            "session start resumes pending takeover without a second snapshot or adding a successor with the same leader");
        ((IList)army.Parties).Clear();
        stageType.GetMethod("OnDispersalReturned", flags).Invoke(reloaded, new object[]{army, Army.ArmyDispersionReason.LeaderPartyRemoved, "fixture", "fixture"});
        Check(restored.Count == 0 && lead.Army == nextArmy, "completed native removal clears pending without touching a replacement army");
        TestDispersalReconciliation(stageType, reloaded, restored);
        stageType.GetMethod("OnArmyCreated", flags).Invoke(reloaded, new object[]{playerArmy});
        Check(log.Any(line => line.Contains("Army created: leaderControl=player") && line.Contains("army=player@")),
            "native army-created callback logs actual player creation separately from proposals");
        TestActualDispersionBranch(stageType, reloaded, logType, harmony, Formation(Party("deferred-leader"), other), playerArmy, other, side);
        TestKingdomForces(mod, logType, sessionLog, harmony, campaign, tracker, player);
        TestMuster(mod, harmony, campaign, tracker, player, logType, previousMod);
        TestRelief(mod, harmony, campaign, tracker, player, logType);
    }
}

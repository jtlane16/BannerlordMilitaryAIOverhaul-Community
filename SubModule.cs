using System;
using System.Diagnostics;
using BannerlordMilitaryAIOverhaul.Campaign;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using NativeCampaign = TaleWorlds.CampaignSystem.Campaign;

namespace BannerlordMilitaryAIOverhaul
{
    public sealed class SubModule : MBSubModuleBase
    {
        private const string PatchId = "BannerlordMilitaryAIOverhaul.Stage0";
        private bool _patchesInstalled;
        private ArmyTakeoverBehavior? _takeover;
        private MilitaryCommandBehavior? _commander;
#if CAMPAIGN_ACCEPTANCE
        private CampaignAcceptanceBehavior? _acceptance;
#endif

        protected override void OnApplicationTick(float dt)
        {
            long stamp = CampaignPerformanceCapture.Active != null ? Stopwatch.GetTimestamp() : 0;
            var campaign = NativeCampaign.Current;
            bool playable = campaign != null && _takeover?.InitialCheckComplete == true &&
                Game.Current?.GameStateManager?.ActiveState is MapState { AtMenu: false, MapConversationActive: false, IsSimulationActive: false } &&
                campaign.PlayerEncounter == null && campaign.SaveHandler?.IsSaving != true;
            if (CampaignPerformanceCapture.Active is { } capture)
            {
                string mode = campaign?.SaveHandler?.IsSaving == true ? "save" :
                    !playable ? "excluded" : campaign!.GetSimplifiedTimeControlMode() switch
                    {
                        CampaignTimeControlMode.StoppablePlay or CampaignTimeControlMode.UnstoppablePlay => "normal",
                        CampaignTimeControlMode.StoppableFastForward or CampaignTimeControlMode.UnstoppableFastForward => "fast",
                        _ => "paused"
                    };
                capture.Boundary(stamp, campaign == null ? 0d : CampaignTime.Now.ToHours, mode);
            }
            base.OnApplicationTick(dt);
            if (playable && campaign!.GetSimplifiedTimeControlMode() != CampaignTimeControlMode.Stop)
                _commander?.OffensiveFrame(CampaignTime.Now.ToHours);
#if CAMPAIGN_ACCEPTANCE
            if (_acceptance != null && NativeCampaign.Current is NativeCampaign acceptanceCampaign)
                _acceptance.Frame(acceptanceCampaign);
#endif
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            var harmony = new Harmony(PatchId);
            try
            {
                harmony.PatchAll(typeof(SubModule).Assembly);
                _patchesInstalled = true;
                CampaignLog.Write($"Army control hooks installed; CampaignSystem MVID={typeof(Army).Module.ModuleVersionId}.");
            }
            catch (Exception exception)
            {
                CampaignLog.Write($"Military AI disabled: patch installation failed. {exception}");
                harmony.UnpatchAll(PatchId);
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);
            if (_patchesInstalled && game.GameType is NativeCampaign campaign && gameStarter is CampaignGameStarter starter)
            {
                var log = CampaignLog.Open(campaign);
                CampaignPerformanceCapture.Open();
                _takeover = new ArmyTakeoverBehavior(campaign, log);
                starter.AddBehavior(_takeover);
                var commander = new MilitaryCommandBehavior(log);
                _commander = commander;
                starter.AddBehavior(commander);
                SiegeOrderTrace.Open(commander, log);
#if CAMPAIGN_ACCEPTANCE
                var bin = System.IO.Path.GetDirectoryName(typeof(SubModule).Assembly.Location)!;
                var module = System.IO.Directory.GetParent(bin)?.Parent?.FullName ?? bin;
                var controlledAttack = new MilitaryCommandBehavior(log, fixtureOnly: true);
                starter.AddBehavior(controlledAttack);
                _acceptance = new CampaignAcceptanceBehavior(commander, log, module, controlledAttack);
                starter.AddBehavior(_acceptance);
#endif
            }
        }

        public override void OnGameEnd(Game game)
        {
            if (_takeover != null && _takeover.Campaign == game.GameType)
            {
#if CAMPAIGN_ACCEPTANCE
                _acceptance = null;
#endif
                SiegeOrderTrace.Close();
                CampaignPerformanceCapture.Close();
                _commander = null;
                _takeover.Close();
                CampaignLog.Close(_takeover.Campaign);
                _takeover = null;
            }
            base.OnGameEnd(game);
        }
    }
}

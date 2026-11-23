using System;
using BannerlordMilitaryAIOverhaul.Armies;
using BannerlordMilitaryAIOverhaul.Diagnostics;
using TaleWorlds.CampaignSystem;

namespace BannerlordMilitaryAIOverhaul.Campaign
{
    internal sealed partial class MilitaryCommandBehavior
    {
        private const int SiegeReissueLimit = 2;
        private const double SiegeReissueProgressDistance = 1d, SiegeReissueStallHours = 24d;
        private const float SiegeReissuePhysicalStep = 0.25f;

        private static bool HasSiegeRecovery(SiegeState state) => state.Reissues > 0 || state.ReissueProgressAt >= 0d;

        private static bool SiegeRecoveryStalled(SiegeState state, double now) =>
            state.ReissueProgressAt >= 0d && now - state.ReissueProgressAt >= SiegeReissueStallHours;

        private static void RecordSiegeRecoveryCheckpoint(SiegeState state, CampaignVec2 position, double distance)
        {
            state.ReissueBestDistance = distance;
            state.ReissueBestX = position.X;
            state.ReissueBestY = position.Y;
        }

        private void ObserveSiegeRecoveryProgress(Army army, SiegeAssignment job, double now, double distance, bool orderPresent)
        {
            var state = job.State;
            // A later readback of the correct order can confirm it. A Hold
            // alone, including an old save already on Hold, cannot do so.
            bool newlyConfirmed = orderPresent && !state.ReissueOrderConfirmed;
            if (newlyConfirmed)
            {
                state.ReissueOrderConfirmed = true;
                if (state.ReissueProgressAt < 0d)
                    state.ReissueProgressAt = now;
            }
            var position = army.LeaderParty.Position;
            if (!state.ReissueOrderConfirmed || !position.IsOnLand || double.IsInfinity(distance) || double.IsNaN(distance))
                return;
            if (state.ReissueBestDistance < 0d || newlyConfirmed)
            {
                // Never move the baseline backwards, and don't credit
                // movement that occurred before we confirmed restoration.
                if (state.ReissueBestDistance < 0d || distance < state.ReissueBestDistance)
                    RecordSiegeRecoveryCheckpoint(state, position, distance);
                return;
            }
            double gained = state.ReissueBestDistance - distance;
            float dx = position.X - state.ReissueBestX, dy = position.Y - state.ReissueBestY;
            if (gained < SiegeReissueProgressDistance || dx * dx + dy * dy < SiegeReissuePhysicalStep * SiegeReissuePhysicalStep)
                return;

            int used = state.Reissues;
            RecordSiegeRecoveryCheckpoint(state, position, distance);
            state.ReissueProgressAt = now;
            state.Reissues = 0;
            if (used > 0)
                _log.Event($"Siege order recovery progress: {SiegeId(army, job)} phase={(state.Issued ? "march" : "muster")} " +
                    $"routeAdvanced={CampaignLog.Number(gained)} remainingRoute={CampaignLog.Number(distance)} previousReissues={used}; budget restored.");
        }

        private void ReissueSiegeTravel(Army army, SiegeAssignment job, double now, double distance)
        {
            var state = job.State;
            if (state.ReissueProgressAt < 0d)
                state.ReissueProgressAt = now;
            state.Reissues++;
            state.ReissueAfter = now + 1d;
            if (state.Issued)
                ArmyOrders.Besiege(army, job.Target);
            else
                ArmyOrders.Visit(army, job.Muster!);

            bool restored = HasAssignedSiegeOrder(army, job);
            if (restored)
                ObserveSiegeRecoveryProgress(army, job, now, distance, orderPresent: true);
            else
                state.ReissueOrderConfirmed = false;
            string label = state.Issued ? "Siege native order reissued" : "Siege native muster order reissued";
            _log.Event($"{label}: {SiegeId(army, job)} retry={state.Reissues} restored={restored}.");
        }

        private void LogStalledSiegeRecovery(Army army, SiegeAssignment job) =>
            _log.Event($"Siege order recovery stalled: {SiegeId(army, job)} phase={(job.State.Issued ? "march" : "muster")} " +
                $"reissues={job.State.Reissues} no-progress-hours={SiegeReissueStallHours}.");
    }
}

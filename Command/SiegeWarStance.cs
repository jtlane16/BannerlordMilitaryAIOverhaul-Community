using System;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace BannerlordMilitaryAIOverhaul.Command
{
    internal static class SiegeWarStance
    {
        private const int Defensive = 1; // native diplomacy selector and CAI's V1 contract
        private static readonly Func<Kingdom, Kingdom, int>? AiPosture = BindAiPosture();

        internal static bool BlocksNewTarget(Kingdom attacker, IFaction? owner)
        {
            if (owner is not Kingdom enemy || enemy == attacker)
                return false;
            // Vanilla stores one shared link, but the menu choice belongs only to the player ruler.
            if (attacker.Leader is { } ruler && ruler == (Game.Current?.PlayerTroop as CharacterObject)?.HeroObject)
                return attacker.GetStanceWith(enemy)?.BehaviorPriority == Defensive;
            return AiPosture?.Invoke(attacker, enemy) == Defensive;
        }

        private static Func<Kingdom, Kingdom, int>? BindAiPosture()
        {
            // Read an already-loaded CAI; standalone campaigns have no AI stance provider.
            var method = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "BannerlordCAIOverhaul")
                ?.GetType("BannerlordCAIOverhaul.Host.HarnessRuntime")?.GetMethod("GetMilitaryWarPostureV1",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Kingdom), typeof(Kingdom) }, null);
            return method?.ReturnType == typeof(int)
                ? (Func<Kingdom, Kingdom, int>)method.CreateDelegate(typeof(Func<Kingdom, Kingdom, int>)) : null;
        }
    }
}

namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// A destructible lighthouse. Unlike every other loot type this one is not scored where it is
/// caught: the reward only exists once enough of them are destroyed across the whole path, so the
/// planner counts them during the sweep and settles the value once at the end.
/// </summary>
public class Lighthouse : IExpeditionLoot
{
}

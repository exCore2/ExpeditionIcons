namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// Monsters that propagated runes scale. Implemented by both <see cref="RunicMonster"/>
/// (the real ones from monster markers) and <see cref="RunestoneMonster"/> (the ones a
/// runestone spawns when detonated). <see cref="NormalMonster"/> deliberately stays out.
/// </summary>
public interface IRunicMonster : IMonster
{
}

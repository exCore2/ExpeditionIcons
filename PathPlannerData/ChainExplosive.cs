using System.Numerics;

namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// An object that produces its own blast when caught by one of your explosions. Worth nothing
/// itself - its value is entirely the loot its blast covers, plus the radius growth for wells.
/// <para>
/// These do NOT detonate each other: an oil well going off beside a Faridun explosive leaves it
/// intact. Only a placed explosive triggers them, so the effect is exactly one level deep.
/// </para>
/// </summary>
/// <param name="Radius">Static. Not scaled by our radius bonus, by other blasts, or by anything else.</param>
/// <param name="GrantsRadiusBonus">True for the OilWell, which enlarges our later explosions.</param>
public record ChainExplosive(Vector2 Position, float Radius, bool GrantsRadiusBonus);

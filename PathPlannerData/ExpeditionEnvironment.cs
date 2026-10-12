using System;
using System.Collections.Generic;
using System.Numerics;

namespace ExpeditionIcons.PathPlannerData;

public record ExpeditionEnvironment(
    List<(Vector2, IExpeditionRelic)> Relics,
    List<(Vector2, IExpeditionLoot)> Loot,
    float ExplosionRange,
    float ExplosionRadius,
    //ExplosionRadius before the map's explosion radius mod. The oil well bonus is added to the
    //same increase pool as that mod, so the well maths needs the unmodded value.
    float BaseExplosionRadius,
    int MaxExplosions,
    Vector2 StartingPoint,
    Func<Vector2, bool> IsValidPlacement,
    (Vector2 Min, Vector2 Max) ExclusionArea,
    bool IsLogbook,
    //Indexed by rune bit. Built once per environment so scoring never touches strings.
    double[] RuneMultipliers,
    int RunestoneCount,
    //Rune sources, indexed by RuneSource.Index, so the planner can size its charge counters once.
    int RuneSourceCount,
    //Objects that detonate when caught and produce their own blast. Kept out of Loot: they
    //score nothing, and a type check for them in the innermost loop would cost every iteration.
    List<ChainExplosive> ChainExplosives,
    //Price of one Expedition Logbook, via NinjaPricer. Zero when that plugin is not loaded, which
    //makes the lighthouse reward worth nothing rather than guessing at a value.
    double LogbookValue,
    //Chained blasts currently fail to set off runestones with any consistency, so while this is set a
    //runestone whose chosen recipe is above the value threshold only counts when the placed explosive
    //itself reaches it. Carried on the environment rather than read from settings so a search keeps
    //the rule it started under.
    bool IgnoreSecondaryBlastsForValuableRunestones,
    //The geodesic placement rule, or null to keep the original straight-line sampling. Carried on
    //the environment so a running search keeps the rule it started under, and swapped in by the
    //runner once the area's bound model has finished building.
    GeodesicPlacementValidator PlacementValidator = null);

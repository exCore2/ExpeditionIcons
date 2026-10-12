using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ExileCore2.Shared.Helpers;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public class PathPlanner
{
    public record PerPointLootScore(Vector2 Point, double ScoreDiff, int NewRelics, int Loot, float Radius, List<(Vector2 Pos, float Radius)> Blasts);

    public record DetailedLootScore(List<PerPointLootScore> PerPointScore, double TotalScore, ExpeditionEnvironment Environment, PathCandidate Candidate);

    //Increased area of effect granted by each detonated oil well, added rather than compounded.
    //See RadiusAfterWells for how this was measured.
    private const float OilWellAreaIncreasePerWell = 0.6f;

    //Lighthouses pay out only once this many are destroyed on a single path, and the reward does
    //not grow past that. All-or-nothing, so the score is flat at one and two and jumps at three.
    private const int LighthousesRequired = 3;

    //A dead end runs out well before this; the cap only stops a pathological case from stalling
    //the search thread while measuring.
    private const int GeodesicNodeLimit = 200_000;

    //What a path that touches a warning relic scores. Finite rather than negative infinity so the
    //number still prints and compares like any other, and far below any real path - the worst a
    //genuine one can do is collect a handful of below-threshold runestones at their flat penalty.
    private const double InvalidPathScore = -1e9;

    private readonly Dictionary<object, double> _lootValueTable = new(ReferenceEqualityComparer.Instance);
    private readonly PlannerSettings _settings;
    private readonly int _validatedPoints;
    private RuneEncounter[] _runestones = [];
    //Where a freshly built path is aimed, and this thread's scratch for which of them one seed has
    //already been to. Sized once in Init, so building a seed allocates nothing beyond the path.
    private Vector2[] _seedTargets = [];
    private bool[] _seedVisited = [];
    //Runestone index per seed target, or -1 for a target that is not a runestone. Reach mutation
    //tests these against a candidate's covered mask to find a stone the path is missing.
    private int[] _seedTargetRunestones = [];
    //Somewhere to build a beeline before committing to it, so a leg that turns out to be unbuildable
    //costs nothing rather than leaving a path with its tail already cut off.
    private readonly List<Vector2> _seedScratch = [];
    //Positions no blast may reach. Checked on every placement, so it is a flat array rather than a
    //filter over the relic list.
    private Vector2[] _warningRelics = [];
    private double[] _runeMultipliers = [];

    //Reused across calls so the flood fill allocates nothing. Safe because each search thread
    //constructs its own PathPlanner.
    private readonly List<(Vector2 Pos, float Radius)> _blasts = [];

    //Generation stamps instead of a HashSet: dedupe becomes an integer compare and the hot
    //loop gains no third per-call allocation.
    private int[] _chainTriggered = [];
    private int _generation;

    //Per-path state for Sentinel-style rune sources: remaining charges and the precomputed rune
    //product for each. Sized once in Init and cleared per candidate, so scoring allocates nothing.
    private int[] _runeSourceCharges = [];
    private double[] _runeSourceProducts = [];

    //Sources caught during the current explosion point. They activate only once the point is done,
    //so a source never buffs the blast that consumed it.
    private readonly List<int> _pendingSources = [];

    private int _diagnosticSegmentCounter;
    private PathBoundModel _boundModel;
    private PathBoundModel.Workspace _boundWorkspace;
    private GeodesicPlacementValidator _validator;
    private PathBoundModel.Workspace _validatorWorkspace;

    public PathPlanner(PlannerSettings settings)
    {
        _settings = settings;
        _validatedPoints = _settings.ValidatedIntermediatePoints + 1;
    }

    public double GetScore(PathCandidate candidate, ExpeditionEnvironment environment)
    {
        return Score(candidate, environment, null);
    }

    public DetailedLootScore GetDetailedScore(PathCandidate candidate, ExpeditionEnvironment environment)
    {
        var scorePerPoint = new List<PerPointLootScore>();
        return new DetailedLootScore(scorePerPoint, Score(candidate, environment, scorePerPoint), environment, candidate);
    }

    /// <summary>
    /// Scores one path. Pass a list to also collect the per-point breakdown the UI shows; pass null
    /// for the search, which runs this millions of times and wants no allocation.
    /// <para>
    /// One method rather than two: every mechanic here - chained blasts, well radius, rune
    /// propagation, source charges, lighthouses - has to agree exactly between the number the search
    /// optimises and the number the UI explains, and a second copy only agrees until someone edits one.
    /// </para>
    /// </summary>
    private double Score(PathCandidate candidate, ExpeditionEnvironment environment, List<PerPointLootScore> scorePerPoint)
    {
        var relics = new HashSet<IExpeditionRelic>();
        var lootList = new HashSet<IExpeditionLoot>();
        var choices = candidate.Choices;
        var chain = environment.ChainExplosives;
        var skipChainedRunestones = environment.IgnoreSecondaryBlastsForValuableRunestones;
        var score = 0.0;
        ulong accumulated = 0;
        ulong covered = 0;
        var runeMult = 1.0;
        var wellsTriggered = 0;
        var lighthouses = 0;
        var sourceMult = 1.0;
        var activeSources = 0;
        var currentRadius = environment.ExplosionRadius;
        var touchedWarningRelic = false;
        Array.Clear(_runeSourceCharges);
        _pendingSources.Clear();
        _generation++;

        foreach (var explosionPoint in candidate.Points)
        {
            var pointRadius = currentRadius;
            var blastCount = CollectBlasts(explosionPoint, currentRadius, chain, out var wellsThisPoint);
            //_blasts is scratch that the next point overwrites, so the breakdown needs its own copy.
            var blastsForPoint = scorePerPoint == null ? null : new List<(Vector2 Pos, float Radius)>(_blasts);

            //Relics for the whole point before any loot. Interleaved, a relic reached by the
            //third blast would not apply to loot already scored by the first, which would make
            //the result depend on flood-fill order.
            var newRelics = 0;
            for (var b = 0; b < blastCount; b++)
            {
                var (blastPos, blastRadius) = _blasts[b];
                foreach (var (relicPos, relic) in environment.Relics)
                {
                    if (relicPos.DistanceLessThanOrEqual(blastPos, blastRadius) && relics.Add(relic))
                    {
                        newRelics++;
                        //Caught here rather than at placement because this is the pass that sees
                        //chained blasts and the widened radius - a well can push a legal placement
                        //onto a relic the placement rule cleared.
                        touchedWarningRelic |= relic is WarningRelic;
                    }
                }
            }

            ulong pending = 0;
            var newLoot = 0;
            var localScore = 0.0;
            for (var b = 0; b < blastCount; b++)
            {
                var (blastPos, blastRadius) = _blasts[b];
                foreach (var (lootPos, loot) in environment.Loot)
                {
                    if (!lootPos.DistanceLessThanOrEqual(blastPos, blastRadius))
                    {
                        continue;
                    }

                    //Left unconsumed on purpose: a later explosion whose own placement reaches this
                    //runestone still claims it normally.
                    if (b > 0 && skipChainedRunestones && IsValuableRunestone(loot, choices))
                    {
                        continue;
                    }

                    if (!lootList.Add(loot))
                    {
                        continue;
                    }

                    newLoot++;
                    //The static drop is relic-immune and rune-immune, so it skips the aggregate entirely.
                    if (loot is RuneEncounter runestone)
                    {
                        if ((uint)runestone.RunestoneIndex >= (uint)choices.Length)
                        {
                            continue;
                        }

                        var picked = runestone.GetCandidate(choices[runestone.RunestoneIndex]);
                        //A shift count is taken mod 64, so past 64 runestones this would mark an
                        //unrelated slot as covered and the recipe mutation would edit the wrong one.
                        if (runestone.RunestoneIndex < 64)
                        {
                            covered |= 1UL << runestone.RunestoneIndex;
                        }

                        pending |= picked.PassedOnMask;
                        localScore += GetRuneWeight(picked.Price);
                        continue;
                    }

                    if (loot is RuneSource runeSource)
                    {
                        _pendingSources.Add(runeSource.Index);
                        continue;
                    }

                    if (loot is Lighthouse)
                    {
                        lighthouses++;
                        continue;
                    }

                    var (multiplier, sum) = relics.Select(x => x.GetScoreMultiplier(loot)).Aggregate((mult: 1.0, sum: 0.0), (a, r) => (a.mult * r.Item1, a.sum + r.Item2));
                    var value = _lootValueTable[loot] * multiplier * (1 + sum);

                    if (loot is RunestoneMonster spawned)
                    {
                        var spawnIndex = spawned.Runestone.RunestoneIndex;
                        if ((uint)spawnIndex < (uint)choices.Length)
                        {
                            var pickedSpawn = spawned.Runestone.GetCandidate(choices[spawnIndex]);
                            //Quantity, on top of the rune scaling: the weight is what one consumed rune is
                            //worth, so a longer recipe is linearly more monsters. Deliberately independent of
                            //the static drop scored above - a rich five-rune recipe and a cheap seven-rune one
                            //are meant to trade off against each other through the two terms.
                            value *= pickedSpawn.MonsterRuneCount * runeMult * MaskProduct(pickedSpawn.RecipeRuneMask & ~accumulated);
                        }
                    }
                    else if (loot is IRunicMonster)
                    {
                        value *= runeMult;
                    }

                    //Both kinds of runic monster spend a charge from every active source. Which
                    //ones fall inside a source's window depends on the order Loot happens to be
                    //in when a single blast catches more monsters than there are charges left.
                    if (activeSources > 0 && loot is IRunicMonster)
                    {
                        value *= sourceMult;
                        SpendSourceCharges(ref sourceMult, ref activeSources);
                    }

                    localScore += value;
                }
            }

            scorePerPoint?.Add(new PerPointLootScore(explosionPoint, localScore, newRelics, newLoot, pointRadius, blastsForPoint));
            score += localScore;

            //Deferred: propagation reaches later explosions only, never this one.
            if (pending != 0)
            {
                var newBits = pending & ~accumulated;
                if (newBits != 0)
                {
                    accumulated |= newBits;
                    runeMult *= MaskProduct(newBits);
                }
            }

            //Same deferral again: a source never buffs the explosion that consumed it.
            if (_pendingSources.Count > 0)
            {
                ActivateSources(ref sourceMult, ref activeSources);
            }

            //Same deferral for the radius: a well never enlarges the blast that set it off.
            if (wellsThisPoint > 0)
            {
                wellsTriggered += wellsThisPoint;
                currentRadius = RadiusAfterWells(environment, wellsTriggered);
            }
        }

        score += LighthouseReward(lighthouses, environment);

        //Both paths write it: the hand-edited path is only ever scored in detail, and the recipe
        //table reads the mask to decide which runestones the path covers.
        candidate.CoveredMask = covered;
        //Last, so the per-point breakdown is still filled in and the UI can show which explosion
        //touched the relic rather than just reporting the path as worthless.
        return touchedWarningRelic ? InvalidPathScore : score;
    }

    /// <summary>
    /// The blasts produced by one placement: the explosion itself, plus every explodable object
    /// it reaches. Results land in <see cref="_blasts"/>, with the placement always at index 0.
    /// <para>
    /// Exactly one level deep. These objects do NOT set each other off - an oil well detonated
    /// right beside a Faridun explosive leaves it intact - so a triggered blast is a leaf and is
    /// never scanned for further objects. Its blast still catches loot, relics and runestones.
    /// </para>
    /// Each object detonates at most once per path, tracked by generation stamp.
    /// </summary>
    private int CollectBlasts(Vector2 origin, float radius, List<ChainExplosive> chain, out int wellsTriggered)
    {
        wellsTriggered = 0;
        _blasts.Clear();
        _blasts.Add((origin, radius));
        if (chain == null || chain.Count == 0)
        {
            return _blasts.Count;
        }

        //Only the placement is tested, never the blasts it spawns.
        for (var c = 0; c < chain.Count && c < _chainTriggered.Length; c++)
        {
            if (_chainTriggered[c] == _generation)
            {
                continue;
            }

            var explosive = chain[c];
            if (!explosive.Position.DistanceLessThanOrEqual(origin, radius))
            {
                continue;
            }

            _chainTriggered[c] = _generation;
            _blasts.Add((explosive.Position, explosive.Radius));
            if (explosive.GrantsRadiusBonus)
            {
                wellsTriggered++;
            }
        }

        return _blasts.Count;
    }

    /// <summary>
    /// Radius after <paramref name="wells"/> oil wells. Each well grants a flat increase to area
    /// of effect and radius scales as the square root of area, so the growth curve flattens.
    /// <para>
    /// Measured: radii at 0/1/2/3 wells came out at 33.94 / 42.43 / 49.40 / 56.83 grid, read by
    /// placing explosives so their circles just touched and halving the entity distance. Sweeping
    /// every integer base against every 5% step of increase, base 34 with +60% is the best fit in
    /// the space - every reading within 1.5% except one that disagreed with its own duplicate by
    /// 3.4%. A flat radius increase and a compounding multiplier both fit the first well by
    /// construction and then diverge badly, predicting 59.4 and 66.3 at three wells.
    /// </para>
    /// <para>
    /// The well bonus is ADDED to the map's explosion radius mod rather than multiplied onto it,
    /// both increases landing in one area pool: r = base * sqrt((1 + mapMod)^2 + 0.6 * wells).
    /// Written against the squares, since ExplosionRadius is already base * (1 + mapMod). Both
    /// measurements are reproduced exactly - the well curve was read with the map mod at 0, the
    /// map mod was confirmed with no well triggered - and only their combination changes, which
    /// nothing had ever measured. Multiplying them, as this used to, made a single well in a
    /// +33% map (radius 37 grid off a base of 28) read 46.8 grid where the additive pool gives 42.9.
    /// </para>
    /// </summary>
    private float RadiusAfterWells(ExpeditionEnvironment environment, int wells)
    {
        if (wells <= 0)
        {
            return environment.ExplosionRadius;
        }

        var baseRadius = environment.BaseExplosionRadius;
        return MathF.Sqrt(environment.ExplosionRadius * environment.ExplosionRadius +
                          wells * OilWellAreaIncreasePerWell * baseRadius * baseRadius);
    }

    /// <summary>
    /// Product of the multipliers of every rune in the mask. Only ever called with the
    /// bits that are actually new, so the loop runs a handful of times at most.
    /// </summary>
    /// <summary>
    /// Value of the logbook the lighthouses yield, awarded once for the whole path rather than at
    /// any single explosion. The configured weight scales the logbook's market value, so 1.0 means
    /// "worth exactly what a logbook sells for" and 0 disables it.
    /// </summary>
    private double LighthouseReward(int lighthouses, ExpeditionEnvironment environment)
    {
        if (lighthouses < LighthousesRequired || environment.LogbookValue <= 0)
        {
            return 0;
        }

        return environment.LogbookValue *
               _settings.ChestSettingsMap.GetValueOrDefault(IconPickerIndex.Lighthouse, new ChestSettings()).Weight;
    }

    /// <summary>
    /// Turns every source caught during the point just finished into an active buff.
    /// </summary>
    private void ActivateSources(ref double mult, ref int active)
    {
        var charges = _settings.RuneScoring.SentinelRuneCharges;
        foreach (var index in _pendingSources)
        {
            //A source is caught at most once per path, so an already-active one means a duplicate
            //index rather than a second pickup. Skip rather than refresh.
            if (charges <= 0 || (uint)index >= (uint)_runeSourceCharges.Length || _runeSourceCharges[index] > 0)
            {
                continue;
            }

            _runeSourceCharges[index] = charges;
            mult *= _runeSourceProducts[index];
            active++;
        }

        _pendingSources.Clear();
    }

    /// <summary>
    /// Spends one charge from every active source. Sources stack: each keeps its own countdown and
    /// every runic monster costs all of them a charge. The product is rebuilt from scratch when one
    /// expires rather than divided out, which would drift over a long path.
    /// </summary>
    private void SpendSourceCharges(ref double mult, ref int active)
    {
        var expired = false;
        for (var i = 0; i < _runeSourceCharges.Length; i++)
        {
            if (_runeSourceCharges[i] > 0 && --_runeSourceCharges[i] == 0)
            {
                expired = true;
                active--;
            }
        }

        if (!expired)
        {
            return;
        }

        mult = 1.0;
        for (var i = 0; i < _runeSourceCharges.Length; i++)
        {
            if (_runeSourceCharges[i] > 0)
            {
                mult *= _runeSourceProducts[i];
            }
        }
    }

    private double MaskProduct(ulong mask)
    {
        var result = 1.0;
        while (mask != 0)
        {
            var bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            if (bit < _runeMultipliers.Length)
            {
                result *= _runeMultipliers[bit];
            }
        }

        return result;
    }

    /// <summary>
    /// A runestone above the value threshold, or the monsters one spawns - the pieces that are only
    /// there because the stone detonated. Chained blasts do not set runestones off reliably, so these
    /// are the entries dropped when a secondary blast is the only thing reaching them.
    /// </summary>
    private bool IsValuableRunestone(IExpeditionLoot loot, int[] choices)
    {
        var runestone = loot switch
        {
            RuneEncounter encounter => encounter,
            RunestoneMonster monster => monster.Runestone,
            _ => null,
        };

        if (runestone == null || (uint)runestone.RunestoneIndex >= (uint)choices.Length)
        {
            return false;
        }

        return runestone.GetCandidate(choices[runestone.RunestoneIndex]).Price >= _settings.RuneScoring.ValueThreshold;
    }

    /// <summary>
    /// The static drop's weight. Path-independent given a price: above the threshold it
    /// scales with price, below it collapses to a flat penalty plus a negligible price tiebreak.
    /// </summary>
    private double GetRuneWeight(double price)
    {
        var runeSettings = _settings.RuneScoring;
        return price >= runeSettings.ValueThreshold
            ? runeSettings.AboveThresholdWeight + (price - runeSettings.ValueThreshold) * runeSettings.AboveThresholdScale
            : runeSettings.BelowThresholdWeight + price * BelowThresholdPriceTiebreak;
    }

    /// <summary>
    /// Per-chaos nudge below the threshold. Small enough that a whole map of sub-threshold drops adds up
    /// to a fraction of one monster's worth, so it never outweighs a real difference in path, runes or
    /// monsters - it only decides between paths that would otherwise score identically, in favour of
    /// the pricier recipe.
    /// </summary>
    private const double BelowThresholdPriceTiebreak = 1e-6;

    private Vector2 GetNextPosition(Vector2 position, Vector2 previousPosition, float radius, ExpeditionEnvironment environment)
    {
        var positionEnumerable = Enumerable.Range(1, 1000).Select(i => GetNextMaybeInvalidPosition(position, radius * MathF.Pow(0.99f, i)));
        return positionEnumerable.FirstOrDefault(x => IsValidPlacement(previousPosition, environment, x), position);
    }

    private bool IsValidPlacement(Vector2 previousPosition, ExpeditionEnvironment environment, Vector2 position)
    {
        if (!previousPosition.DistanceLessThanOrEqual(position, environment.ExplosionRange))
        {
            if (PathfindingDiagnostics.Enabled)
            {
                PathfindingDiagnostics.RecordTrivialReject();
            }

            return false;
        }

        //Before the exclusion and terrain tests because it is the cheapest of the three and, unlike
        //them, it is the one that has to hold for every point the path ever contains. Measured at the
        //base radius: a placement that only reaches a relic once oil wells have widened the blast
        //gets past here and is caught by scoring instead, which knows the grown radius.
        foreach (var warningRelic in _warningRelics)
        {
            if (warningRelic.DistanceLessThanOrEqual(position, environment.ExplosionRadius))
            {
                return false;
            }
        }

        var outsideExclusion = Vector2.Clamp(position, environment.ExclusionArea.Min, environment.ExclusionArea.Max) != position;
        var accepted = outsideExclusion &&
                       (environment.PlacementValidator is { } validator
                           ? validator.IsSegmentValid(previousPosition, position, environment.ExplosionRange, GetValidatorWorkspace(validator))
                           : Enumerable.Range(1, _validatedPoints)
                               .Select(i => i / (float)_validatedPoints)
                               .Select(l => Vector2.Lerp(previousPosition, position, l))
                               .All(environment.IsValidPlacement));

        if (PathfindingDiagnostics.Enabled)
        {
            RecordPlacementDiagnostics(previousPosition, position, environment, accepted);
        }

        return accepted;
    }

    /// <summary>
    /// Measures what a geodesic rule would have done with this segment without changing what the
    /// search does with it. The exact line test runs on every in-range segment because it is cheap;
    /// the path search runs on a sample of the segments the line test rejects, which are the only
    /// ones a real implementation would spend a search on.
    /// </summary>
    private void RecordPlacementDiagnostics(Vector2 previousPosition, Vector2 position, ExpeditionEnvironment environment, bool accepted)
    {
        var start = Stopwatch.GetTimestamp();
        var lineClear = PathfindingDiagnostics.LineIsClear(previousPosition, position, environment.IsValidPlacement, out var cells);
        PathfindingDiagnostics.RecordSegment(accepted, lineClear, cells, Stopwatch.GetTimestamp() - start);

        var model = GetBoundModel();
        if (model != null)
        {
            //Every in-range segment, because this is the tier that would run on every in-range
            //segment for real. The delegate result is the reference it has to match.
            start = Stopwatch.GetTimestamp();
            var bitLineClear = model.LineIsClear(previousPosition, position);
            PathfindingDiagnostics.RecordBitboardLine(bitLineClear == lineClear, Stopwatch.GetTimestamp() - start);
        }

        if (lineClear)
        {
            return;
        }

        var budget = environment.ExplosionRange;
        var coarse = BoundVerdict.Inconclusive;
        var landmark = BoundVerdict.Inconclusive;
        var chainVerdict = BoundVerdict.Inconclusive;
        if (model != null)
        {
            start = Stopwatch.GetTimestamp();
            landmark = model.Landmark(previousPosition, position, budget);
            var landmarkTicks = Stopwatch.GetTimestamp() - start;
            PathfindingDiagnostics.RecordBound(false, landmark, landmarkTicks);

            start = Stopwatch.GetTimestamp();
            coarse = model.Coarse(previousPosition, position, budget, _boundWorkspace);
            var coarseTicks = Stopwatch.GetTimestamp() - start;
            PathfindingDiagnostics.RecordBound(true, coarse, coarseTicks);

            //Chained, so each segment is charged once: the coarse bound is only reached by what the
            //landmarks left open.
            chainVerdict = landmark == BoundVerdict.Inconclusive ? coarse : landmark;
            PathfindingDiagnostics.RecordChain(
                chainVerdict,
                landmark == BoundVerdict.Inconclusive ? landmarkTicks + coarseTicks : landmarkTicks);
        }

        var sampleRate = Math.Max(1, _settings.DiagnosticsGeodesicSampleRate.Value);
        if (++_diagnosticSegmentCounter % sampleRate != 0)
        {
            return;
        }

        start = Stopwatch.GetTimestamp();
        var result = PathfindingDiagnostics.GeodesicWithin(
            previousPosition, position, budget, environment.IsValidPlacement, GeodesicNodeLimit, out var expanded);
        PathfindingDiagnostics.RecordGeodesic(result, expanded, Stopwatch.GetTimestamp() - start);
        PathfindingDiagnostics.RecordGeodesicSplit(accepted, result);

        if (model == null)
        {
            return;
        }

        //A point can be walkable and still be somewhere the explosives can never reach. The
        //reference search does not model that, so those segments are counted and left out of the
        //soundness comparison rather than being scored as bound failures.
        if (!model.IsInComponent(previousPosition) || !model.IsInComponent(position))
        {
            PathfindingDiagnostics.RecordOutsideComponentSample();
            return;
        }

        start = Stopwatch.GetTimestamp();
        var fineResult = model.AStar(previousPosition, position, budget, _boundWorkspace, out var fineExpanded);
        PathfindingDiagnostics.RecordFineSearch(chainVerdict, fineResult, fineExpanded, Stopwatch.GetTimestamp() - start, fineResult == result);

        //The bitboard search is the reference the bounds are judged against: it covers only the
        //component, which is the same terrain the bounds were built from.
        PathfindingDiagnostics.RecordBoundCheck(true, coarse, fineResult);
        PathfindingDiagnostics.RecordBoundCheck(false, landmark, fineResult);
    }

    /// <summary>This thread's scratch for the validator, rebuilt if the model is ever swapped.</summary>
    private PathBoundModel.Workspace GetValidatorWorkspace(GeodesicPlacementValidator validator)
    {
        if (!ReferenceEquals(validator, _validator))
        {
            _validator = validator;
            _validatorWorkspace = validator.CreateWorkspace();
        }

        return _validatorWorkspace;
    }

    /// <summary>
    /// The shared model, with this thread's scratch rebuilt whenever a different model is published.
    /// </summary>
    private PathBoundModel GetBoundModel()
    {
        var model = PathfindingDiagnostics.BoundModel;
        if (model == null)
        {
            return null;
        }

        if (!ReferenceEquals(model, _boundModel))
        {
            _boundModel = model;
            _boundWorkspace = new PathBoundModel.Workspace(model, GeodesicNodeLimit);
        }

        return model;
    }

    private static Vector2 GetNextMaybeInvalidPosition(Vector2 position, float radius)
    {
        radius = Math.Max(1, radius);
        var length = Math.Max(1, Random.Shared.Next(2) == 0 ? radius : GetWeightedLength(radius));
        var angle = Random.Shared.NextSingle() * MathF.PI * 2;
        var (sin, cos) = MathF.SinCos(angle);
        var rawPoint = position + new Vector2(cos * length, sin * length);
        var roundedPoint = RoundPoint(rawPoint);
        while (!roundedPoint.DistanceLessThanOrEqual(position, radius))
        {
            var diff = roundedPoint - position;
            var maxDiffComponent = Math.Abs(diff.X) > Math.Abs(diff.Y)
                ? new Vector2(Math.Sign(diff.X), 0)
                : new Vector2(0, Math.Sign(diff.Y));
            roundedPoint -= maxDiffComponent;
        }

        return roundedPoint;
    }

    private static Vector2 RoundPoint(Vector2 rawPoint)
    {
        return new Vector2(MathF.Round(rawPoint.X), MathF.Round(rawPoint.Y));
    }

    private static float GetWeightedLength(float radius)
    {
        return Math.Max(Random.Shared.NextSingle(), Random.Shared.NextSingle()) * radius;
    }

    private PathCandidate MutatePath(Vector2 startingPoint, float radius, PathCandidate original, ExpeditionEnvironment environment)
    {
        var mutateTimes = Random.Shared.Next(1, 4);
        var newCandidate = original.Clone();
        var newPath = newCandidate.Points;
        for (var mutation = 0; mutation < mutateTimes; mutation++)
        {
            //Substitutive, matching how skip/swap/move already compete within one event.
            if (Random.Shared.NextDouble() < _settings.RecipeMutateChance && TryApplyRecipeMutation(newCandidate))
            {
                continue;
            }

            //Ahead of the local operators because it is the only one that can add a runestone the
            //path does not already reach; the others can only rearrange what it has.
            if (Random.Shared.NextDouble() < _settings.ReachMutateChance &&
                TryApplyReachMutation(newCandidate, newPath, startingPoint, radius, environment))
            {
                continue;
            }

            if (Random.Shared.Next(2) == 0 && TryApplySkipMutation(newPath, environment))
            {
                continue;
            }

            if (Random.Shared.Next(2) == 0 && TryApplySwapMutation(newPath, environment))
            {
                continue;
            }

            var changeIndex = Random.Shared.Next(newPath.Count);
            Vector2 changedPoint;
            var previousPoint = changeIndex == 0 ? startingPoint : newPath[changeIndex - 1];
            var changingPoint = newPath[changeIndex];
            var tries = 0;
            bool isValidChange;
            do
            {
                if (Random.Shared.Next(2) == 0)
                {
                    changedPoint = GetNextPosition(previousPoint, previousPoint, radius, environment);
                }
                else
                {
                    var allowedMoveRadius = Math.Max(radius - previousPoint.Distance(changingPoint), radius / 5);
                    changedPoint = GetNextPosition(changingPoint, previousPoint, allowedMoveRadius, environment);
                }

                isValidChange = previousPoint.DistanceLessThanOrEqual(changedPoint, radius) &&
                                (changeIndex == newPath.Count - 1 ||
                                 IsValidPlacement(changedPoint, environment, newPath[changeIndex + 1]));
            } while (!isValidChange && tries++ < 10);

            if (!isValidChange)
            {
                continue;
            }

            newPath[changeIndex] = changedPoint;
        }

        return newCandidate;
    }

    /// <summary>
    /// Switches one runestone to a different recipe. Draws from the runestones the path
    /// actually detonates: a choice on an uncovered runestone cannot change the score, so
    /// mutating it burns an evaluation and lets that slot drift to junk under zero selection
    /// pressure. A path that has never been scored has no coverage yet, so fall back to any.
    /// </summary>
    private bool TryApplyRecipeMutation(PathCandidate candidate)
    {
        if (_runestones.Length == 0)
        {
            return false;
        }

        var mask = candidate.CoveredMask;
        int index;
        if (mask != 0)
        {
            var setCount = System.Numerics.BitOperations.PopCount(mask);
            var pick = Random.Shared.Next(setCount);
            for (var i = 0; i < pick; i++)
            {
                mask &= mask - 1;
            }

            index = System.Numerics.BitOperations.TrailingZeroCount(mask);
        }
        else
        {
            index = Random.Shared.Next(_runestones.Length);
        }

        if ((uint)index >= (uint)_runestones.Length || index >= candidate.Choices.Length)
        {
            return false;
        }

        if (_runestones[index] is not { } runestone)
        {
            return false;
        }

        var options = runestone.Candidates.Length;
        if (options <= 1)
        {
            return false;
        }

        var current = Math.Clamp(candidate.Choices[index], 0, options - 1);
        var next = Random.Shared.Next(options - 1);
        if (next >= current)
        {
            next++;
        }

        candidate.Choices[index] = next;
        return true;
    }

    /// <summary>
    /// Points the tail of an evolved path at a seed target it does not currently reach: keeps the
    /// path up to a randomly chosen point, beelines from there to the target, and fills whatever is
    /// left back in at random.
    /// <para>
    /// This is the operator that gets the third runestone. Seeding only helps a path built from
    /// scratch, and by the time a thread has converged on a good two-stone route no local move can
    /// add a third - every intermediate step scores below staying put and the truncation cut kills
    /// it. Rewriting a whole suffix in one move skips that valley instead of trying to cross it.
    /// </para>
    /// <para>
    /// Built into scratch and committed only on arrival, so a target the explosives cannot reach
    /// from the chosen cut costs one failed attempt rather than a path with its tail thrown away.
    /// </para>
    /// </summary>
    private bool TryApplyReachMutation(PathCandidate candidate, List<Vector2> path, Vector2 startingPoint, float radius,
        ExpeditionEnvironment environment)
    {
        if (path.Count == 0)
        {
            return false;
        }

        var target = PickUncoveredTarget(candidate);
        if (target < 0)
        {
            return false;
        }

        //Where to cut. An early cut gives the beeline the room to reach a distant target and throws
        //away more of what the path already earned; a late cut is the reverse. Selection is a better
        //judge of that trade than any rule here, so it is drawn at random and left to compete.
        var budget = path.Count;
        var keep = Random.Shared.Next(budget);
        var from = keep == 0 ? startingPoint : path[keep - 1];

        _seedScratch.Clear();
        var targetPosition = _seedTargets[target];
        var end = Beeline(_seedScratch, from, targetPosition, budget - keep, environment);
        if (!end.DistanceLessThanOrEqual(targetPosition, environment.ExplosionRadius))
        {
            return false;
        }

        path.RemoveRange(keep, budget - keep);
        path.AddRange(_seedScratch);
        while (path.Count < budget)
        {
            path.Add(end = GetNextPosition(end, end, radius, environment));
        }

        return true;
    }

    /// <summary>
    /// A seed target whose runestone the path does not detonate, drawn uniformly from those, or -1
    /// when the path already reaches all of them. Targets past bit 63 are skipped for the same
    /// reason scoring stops recording them: the mask cannot represent them.
    /// </summary>
    private int PickUncoveredTarget(PathCandidate candidate)
    {
        var mask = candidate.CoveredMask;
        var uncovered = 0;
        for (var i = 0; i < _seedTargetRunestones.Length; i++)
        {
            if (IsUncovered(i, mask))
            {
                uncovered++;
            }
        }

        if (uncovered == 0)
        {
            return -1;
        }

        var skip = Random.Shared.Next(uncovered);
        for (var i = 0; i < _seedTargetRunestones.Length; i++)
        {
            if (IsUncovered(i, mask) && skip-- == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsUncovered(int target, ulong mask)
    {
        var runestone = _seedTargetRunestones[target];
        return (uint)runestone < 64 && (mask & (1UL << runestone)) == 0;
    }

    private bool TryApplySkipMutation(List<Vector2> path, ExpeditionEnvironment environment)
    {
        var pathCount = path.Count - 2;
        if (pathCount <= 0)
        {
            return false;
        }

        var searchStartOffset = Random.Shared.Next(0, pathCount + 1);
        if (searchStartOffset == pathCount)
        {
            var injectionIndex = Random.Shared.Next(0, pathCount);
            var midpoint = RoundPoint((path[injectionIndex] + path[injectionIndex + 1]) / 2);
            if (IsValidPlacement(path[injectionIndex], environment, midpoint) &&
                IsValidPlacement(midpoint, environment, path[injectionIndex + 1]))
            {
                path.RemoveAt(path.Count - 1);
                path.Insert(injectionIndex + 1, midpoint);
                return true;
            }

            searchStartOffset = 0;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var checkIndex = 1 + (i + searchStartOffset) % pathCount;
            if (IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]))
            {
                path.RemoveAt(checkIndex);
                path.Add(GetNextPosition(path.Last(), path.Last(), environment.ExplosionRange, environment));
                return true;
            }
        }

        return false;
    }

    private bool TryApplySwapMutation(List<Vector2> path, ExpeditionEnvironment environment)
    {
        var pathCount = path.Count - 3;
        if (pathCount <= 0)
        {
            return false;
        }

        var searchStartOffset = Random.Shared.Next(0, pathCount);
        for (int i = 0; i < pathCount; i++)
        {
            var checkIndex = 1 + (i + searchStartOffset) % pathCount;
            if (IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]) &&
                IsValidPlacement(path[checkIndex], environment, path[checkIndex + 2]))
            {
                (path[checkIndex + 1], path[checkIndex]) = (path[checkIndex], path[checkIndex + 1]);
                return true;
            }
        }

        return false;
    }

    public void Init(ExpeditionEnvironment environment)
    {
        _runeMultipliers = environment.RuneMultipliers ?? [];
        _warningRelics = environment.Relics.Where(x => x.Item2 is WarningRelic).Select(x => x.Item1).ToArray();
        _runestones = new RuneEncounter[environment.RunestoneCount];
        //Seed targets, best tier available: runestones whose top recipe clears the value threshold,
        //failing that any runestone, failing that the relics. Only the first tier is really worth
        //steering at - a qualifying recipe is worth hundreds where a monster is worth three - but an
        //area with nothing above the threshold should still open at something rather than at random.
        //Warning relics never qualify: their multiplier is zero, so a seed that walks into one
        //flattens the value of everything the path collects afterwards.
        var seedRunestones = environment.Loot.Where(x => x.Item2 is RuneEncounter).ToList();
        var seedThreshold = _settings.RuneScoring.ValueThreshold;
        var seedTier = seedRunestones
            .Where(x => ((RuneEncounter)x.Item2).Candidates is { Length: > 0 } candidates && candidates[0].Price >= seedThreshold)
            .ToList();
        if (seedTier.Count == 0)
        {
            seedTier = seedRunestones;
        }

        _seedTargets = seedTier.Select(x => x.Item1).ToArray();
        _seedTargetRunestones = seedTier.Select(x => ((RuneEncounter)x.Item2).RunestoneIndex).ToArray();
        if (_seedTargets.Length == 0)
        {
            _seedTargets = environment.Relics.Where(x => x.Item2 is not WarningRelic).Select(x => x.Item1).ToArray();
            //Relics are not tracked in the covered mask, so a relic-only tier is seed-only: reach
            //mutation has no way to tell whether a path already collected one and never fires.
            _seedTargetRunestones = new int[_seedTargets.Length];
            Array.Fill(_seedTargetRunestones, -1);
        }

        _seedVisited = new bool[_seedTargets.Length];
        _runeSourceCharges = new int[environment.RuneSourceCount];
        _runeSourceProducts = new double[environment.RuneSourceCount];
        _chainTriggered = new int[environment.ChainExplosives?.Count ?? 0];
        _lootValueTable.Clear();
        foreach (var (_, loot) in environment.Loot)
        {
            //The static drop's value depends on the recipe the genome picked, so it is
            //computed inline during scoring and deliberately kept out of the table.
            if (loot is RuneEncounter runestone)
            {
                if (runestone.RunestoneIndex >= 0 && runestone.RunestoneIndex < _runestones.Length)
                {
                    _runestones[runestone.RunestoneIndex] = runestone;
                }

                continue;
            }

            //Neither is valued per item: a rune source only buffs later explosions, and a
            //lighthouse only pays out once enough of them are on the same path.
            if (loot is RuneSource source)
            {
                if ((uint)source.Index < (uint)_runeSourceProducts.Length)
                {
                    _runeSourceProducts[source.Index] = MaskProduct(source.RuneMask);
                }

                continue;
            }

            if (loot is Lighthouse)
            {
                continue;
            }

            _lootValueTable[loot] = loot switch
            {
                RunicMonster => environment.IsLogbook ? _settings.RunicMonsterLogbookWeight : _settings.RunicMonsterWeight,
                //Logbook areas reuse the runic monster logbook weight rather than carrying a second
                //runestone-specific knob: one consumed rune is worth one runic monster either way.
                RunestoneMonster => environment.IsLogbook ? _settings.RunicMonsterLogbookWeight : _settings.RunestoneMonsterWeight,
                Chest { Type: var type } => _settings.ChestSettingsMap.GetValueOrDefault(type, new ChestSettings()).Weight,
                NormalMonster => _settings.NormalMonsterWeight,
            };
        }

        _lootValueTable.TrimExcess();
    }

    public IEnumerable<PathState> GetBestPathSeries(ExpeditionEnvironment environment)
    {
        if (environment.MaxExplosions <= 0)
        {
            yield return new PathState(NewCandidate(new List<Vector2>(), environment), 0);
            yield break;
        }

        var bestPath = NewCandidate(Enumerable.Repeat(Vector2.Zero, environment.MaxExplosions).ToList(), environment);
        var batch = Enumerable.Range(0, _settings.PathGenerationSize * 2).Select(_ => BuildPath(environment)).ToList();
        while (true)
        {
            var batchWithValues = batch
                .Select(x => (GetScore(x, environment), x))
                .OrderByDescending(x => x.Item1)
                .Take(_settings.PathGenerationSize)
                .ToList();
            var mixedAndMutated = batchWithValues
                .Concat(batchWithValues)
                .Select(i => i.x)
                .Select(x => Random.Shared.NextDouble() > _settings.PathMutateChance ? x : MutatePath(environment.StartingPoint, environment.ExplosionRange, x, environment));
            var newPaths = Enumerable.Range(0, (int)(_settings.PathGenerationSize * _settings.NewRandomPathInjectionRate)).Select(_ => BuildPath(environment));
            var newBatch = mixedAndMutated.Append(bestPath).Concat(newPaths).ToList();
            if (batchWithValues[0].Item1 > GetScore(bestPath, environment))
            {
                bestPath = batchWithValues[0].x;
            }

            yield return new PathState(bestPath, GetScore(bestPath, environment));
            batch = newBatch;
        }
    }

    private PathCandidate NewCandidate(List<Vector2> points, ExpeditionEnvironment environment)
    {
        //Choices default to 0, which pruning guarantees is the highest-priced candidate.
        return new PathCandidate(points, new int[Math.Max(environment.RunestoneCount, 0)]);
    }

    private PathCandidate BuildPath(ExpeditionEnvironment environment)
    {
        var path = new List<Vector2>(environment.MaxExplosions);
        var point = Random.Shared.Next(2) != 0 ? BuildSeed(path, environment) : environment.StartingPoint;
        while (path.Count < environment.MaxExplosions)
        {
            path.Add(point = GetNextPosition(point, point, environment.ExplosionRange, environment));
        }

        return NewCandidate(path, environment);
    }

    /// <summary>
    /// Aims the opening of a fresh path at as many seed targets as its explosives will reach, taken
    /// as a nearest-neighbour tour. Whatever budget is left over the caller fills in randomly, which
    /// is what sweeps up the monsters and chests lying between the stops. Returns where it finished.
    /// <para>
    /// A tour rather than one or two stops because that is the shape the search cannot build for
    /// itself: mutation can never walk a path off one runestone and onto another, since every step
    /// of that journey scores below staying put and the truncation cut kills it. Seeding the whole
    /// route hands the search the multi-stone candidate directly, and it only has to polish it.
    /// </para>
    /// </summary>
    private Vector2 BuildSeed(List<Vector2> path, ExpeditionEnvironment environment)
    {
        var current = environment.StartingPoint;
        var remaining = _seedTargets.Length;
        if (remaining == 0)
        {
            return current;
        }

        Array.Clear(_seedVisited);
        //The first stop is random and the rest are nearest-first. Starting from the nearest as well
        //would make every seed in the batch the same tour, and a hundred copies of one path are worth
        //no more to a population search than one.
        var index = Random.Shared.Next(remaining);
        while (index >= 0 && path.Count < environment.MaxExplosions)
        {
            _seedVisited[index] = true;
            remaining--;
            //A target the explosives cannot reach leaves the path untouched and is simply dropped -
            //the tour carries on from wherever it actually got to.
            current = Beeline(path, current, _seedTargets[index], environment.MaxExplosions, environment);
            index = remaining > 0 ? PickNextTarget(current, remaining) : -1;
        }

        return current;
    }

    /// <summary>
    /// The next stop: the nearest target this seed has not been to, or one in four times a random
    /// one, which is what keeps a batch of seeds from collapsing onto a single tour order. Distance
    /// is straight-line on purpose - the seed only has to be a plausible route, and every hop it
    /// produces is validated as it is laid down.
    /// </summary>
    private int PickNextTarget(Vector2 from, int remaining)
    {
        if (Random.Shared.Next(4) == 0)
        {
            var skip = Random.Shared.Next(remaining);
            for (var i = 0; i < _seedTargets.Length; i++)
            {
                if (!_seedVisited[i] && skip-- == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < _seedTargets.Length; i++)
        {
            if (_seedVisited[i])
            {
                continue;
            }

            var distance = Vector2.DistanceSquared(from, _seedTargets[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Extends the path from <paramref name="start"/> toward <paramref name="target"/> in hops of
    /// nine tenths of the explosion range, stopping once the target is inside a blast, the explosion
    /// budget runs out, or a hop turns out to be unbuildable. Returns where it finished, which is
    /// where the following leg starts - compare that against the target to tell arrival from a leg
    /// that gave up. <paramref name="start"/> is the point each hop is validated against, so the
    /// path passed in may be empty scratch rather than the path the hops will end up on.
    /// </summary>
    private Vector2 Beeline(List<Vector2> path, Vector2 start, Vector2 target, int budget, ExpeditionEnvironment environment)
    {
        var step = environment.ExplosionRange * 0.9f;
        var current = start;
        while (path.Count < budget &&
               !current.DistanceLessThanOrEqual(target, environment.ExplosionRadius))
        {
            var diff = target - current;
            var length = diff.Length();
            //The arrival hop lands on the target itself. Advancing current to the rounded point that
            //was actually appended is what ends the walk: measuring the exit condition against a
            //position the path never contained leaves the target permanently one hop away, and the
            //same point gets appended until the explosion budget is gone.
            var next = RoundPoint(length <= step ? target : current + diff * (step / length));
            if (!IsValidPlacement(current, environment, next))
            {
                break;
            }

            path.Add(next);
            current = next;
        }

        return current;
    }
}

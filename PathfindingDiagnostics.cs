using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;

namespace ExpeditionIcons;

/// <summary>
/// Temporary instrumentation for the geodesic-pathfinding decision. Measures, on real areas and a
/// real search, the four numbers the design depends on:
/// <list type="bullet">
/// <item>how often the current sampled straight-line rule accepts a segment that actually crosses a
/// wall (the bug being chased),</item>
/// <item>how often a segment test would fall through an exact line test into a real path search
/// (the fraction that would pay for the expensive tier),</item>
/// <item>whether the anchor (previous detonator) repeats enough for a per-anchor distance field
/// cache to pay for its warmup, simulated as an LRU of a configurable size,</item>
/// <item>what a bounded A* over the same walkability actually costs, sampled so the measurement
/// does not dominate the thing being measured.</item>
/// </list>
/// Everything here is off unless <see cref="Enabled"/> is set, and nothing here feeds the planner's
/// decisions - it only counts.
/// </summary>
public static class PathfindingDiagnostics
{
    private const float DiagonalCost = 1.41421356f;

    public static volatile bool Enabled;

    /// <summary>
    /// The per-area bound structures, published once built. Null until then, and null when
    /// collection is off - every reader treats null as "skip the bound measurements".
    /// </summary>
    public static volatile PathBoundModel BoundModel;

    public static volatile string BoundModelStatus = "not built";

    private static long _searchRuns;
    private static long _searchRunsWithCollection;
    private static long _trivialRejects;
    private static long _segmentTests;
    private static long _sampledAccepts;
    private static long _rayClear;
    private static long _acceptedButRayBlocked;
    private static long _rejectedButRayClear;
    private static long _rayCells;
    private static long _rayTicks;
    private static long _geodesicSamples;
    private static long _geodesicReachable;
    private static long _geodesicUnreachable;
    private static long _geodesicAborted;
    private static long _geodesicExpanded;
    private static long _geodesicTicks;
    private static long _geodesicMaxTicks;
    private static long _geodesicOnAccepted;
    private static long _geodesicOnAcceptedReachable;
    private static long _geodesicOnRejected;
    private static long _geodesicOnRejectedReachable;
    private static long _bitLineTests;
    private static long _bitLineTicks;
    private static long _bitLineDisagreements;
    private static long _fineSamples;
    private static long _fineTicks;
    private static long _fineMaxTicks;
    private static long _fineExpanded;
    private static long _fineDisagreements;
    private static readonly long[] CoarseVerdicts = new long[4];
    private static readonly long[] LandmarkVerdicts = new long[4];
    private static readonly long[] ChainVerdicts = new long[4];

    //The fine search, split by what the cheap pipeline said about the same segment. The overall
    //average is misleading: it is dominated by the unreachable segments, which are the expensive
    //kind and which the bounds now settle for free. Only the inconclusive column is traffic the
    //fine tier would actually see.
    private static readonly long[] FineSamplesBy = new long[4];
    private static readonly long[] FineTicksBy = new long[4];
    private static readonly long[] FineExpandedBy = new long[4];
    private static readonly long[] FineReachableBy = new long[4];

    //Nodes expanded before the answer came back, for the inconclusive bucket only, so a node cap
    //can be read straight off: a reachable query resolved in N nodes is one a cap above N keeps.
    private static readonly int[] NodeBuckets = [25, 50, 100, 200, 400, 800, 1600, int.MaxValue];
    private static readonly long[] FineNodesReachable = new long[8];
    private static readonly long[] FineNodesUnreachable = new long[8];
    private static long _chainTicks;
    private static long _coarseTicks;
    private static long _landmarkTicks;
    private static long _coarseUnsoundAccepts;
    private static long _coarseUnsoundRejects;
    private static long _coarseChecked;
    private static long _landmarkUnsoundAccepts;
    private static long _landmarkUnsoundRejects;
    private static long _landmarkChecked;
    private static long _outsideComponentSamples;

    public static long SegmentTests => Interlocked.Read(ref _segmentTests);
    public static long SearchRuns => Interlocked.Read(ref _searchRuns);
    public static long SearchRunsWithCollection => Interlocked.Read(ref _searchRunsWithCollection);

    public static void Reset()
    {
        Interlocked.Exchange(ref _searchRuns, 0);
        Interlocked.Exchange(ref _searchRunsWithCollection, 0);
        Interlocked.Exchange(ref _trivialRejects, 0);
        Interlocked.Exchange(ref _segmentTests, 0);
        Interlocked.Exchange(ref _sampledAccepts, 0);
        Interlocked.Exchange(ref _rayClear, 0);
        Interlocked.Exchange(ref _acceptedButRayBlocked, 0);
        Interlocked.Exchange(ref _rejectedButRayClear, 0);
        Interlocked.Exchange(ref _rayCells, 0);
        Interlocked.Exchange(ref _rayTicks, 0);
        Interlocked.Exchange(ref _geodesicSamples, 0);
        Interlocked.Exchange(ref _geodesicReachable, 0);
        Interlocked.Exchange(ref _geodesicUnreachable, 0);
        Interlocked.Exchange(ref _geodesicAborted, 0);
        Interlocked.Exchange(ref _geodesicExpanded, 0);
        Interlocked.Exchange(ref _geodesicTicks, 0);
        Interlocked.Exchange(ref _geodesicMaxTicks, 0);
        Interlocked.Exchange(ref _geodesicOnAccepted, 0);
        Interlocked.Exchange(ref _geodesicOnAcceptedReachable, 0);
        Interlocked.Exchange(ref _geodesicOnRejected, 0);
        Interlocked.Exchange(ref _geodesicOnRejectedReachable, 0);
        Interlocked.Exchange(ref _bitLineTests, 0);
        Interlocked.Exchange(ref _bitLineTicks, 0);
        Interlocked.Exchange(ref _bitLineDisagreements, 0);
        Interlocked.Exchange(ref _fineSamples, 0);
        Interlocked.Exchange(ref _fineTicks, 0);
        Interlocked.Exchange(ref _fineMaxTicks, 0);
        Interlocked.Exchange(ref _fineExpanded, 0);
        Interlocked.Exchange(ref _fineDisagreements, 0);
        Interlocked.Exchange(ref _coarseTicks, 0);
        Interlocked.Exchange(ref _landmarkTicks, 0);
        Interlocked.Exchange(ref _coarseUnsoundAccepts, 0);
        Interlocked.Exchange(ref _coarseUnsoundRejects, 0);
        Interlocked.Exchange(ref _coarseChecked, 0);
        Interlocked.Exchange(ref _landmarkUnsoundAccepts, 0);
        Interlocked.Exchange(ref _landmarkUnsoundRejects, 0);
        Interlocked.Exchange(ref _landmarkChecked, 0);
        Interlocked.Exchange(ref _outsideComponentSamples, 0);
        Interlocked.Exchange(ref _chainTicks, 0);
        Array.Clear(ChainVerdicts);
        Array.Clear(FineSamplesBy);
        Array.Clear(FineTicksBy);
        Array.Clear(FineExpandedBy);
        Array.Clear(FineReachableBy);
        Array.Clear(FineNodesReachable);
        Array.Clear(FineNodesUnreachable);
        Array.Clear(CoarseVerdicts);
        Array.Clear(LandmarkVerdicts);
    }

    /// <summary>The exact line test over the bitboard - what tier one would really cost.</summary>
    public static void RecordBitboardLine(bool agreesWithDelegate, long ticks)
    {
        Interlocked.Increment(ref _bitLineTests);
        Interlocked.Add(ref _bitLineTicks, ticks);
        if (!agreesWithDelegate)
        {
            Interlocked.Increment(ref _bitLineDisagreements);
        }
    }

    /// <summary>
    /// The fine search over the bitboard: ground truth for the bounds, its own cost, and - the part
    /// that matters for sizing the tier - the same broken down by what the cheap pipeline had
    /// already concluded about that segment.
    /// </summary>
    public static void RecordFineSearch(BoundVerdict chainVerdict, GeodesicResult result, int expanded, long ticks, bool agreesWithDelegate)
    {
        Interlocked.Increment(ref _fineSamples);
        Interlocked.Add(ref _fineTicks, ticks);
        Interlocked.Add(ref _fineExpanded, expanded);
        if (!agreesWithDelegate)
        {
            Interlocked.Increment(ref _fineDisagreements);
        }

        Max(ref _fineMaxTicks, ticks);

        var slot = (int)chainVerdict;
        Interlocked.Increment(ref FineSamplesBy[slot]);
        Interlocked.Add(ref FineTicksBy[slot], ticks);
        Interlocked.Add(ref FineExpandedBy[slot], expanded);
        var reachable = result == GeodesicResult.Reachable;
        if (reachable)
        {
            Interlocked.Increment(ref FineReachableBy[slot]);
        }

        if (chainVerdict != BoundVerdict.Inconclusive)
        {
            return;
        }

        var bucket = 0;
        while (bucket < NodeBuckets.Length - 1 && expanded > NodeBuckets[bucket])
        {
            bucket++;
        }

        var histogram = reachable ? FineNodesReachable : FineNodesUnreachable;
        Interlocked.Increment(ref histogram[bucket]);
    }

    /// <summary>
    /// A sampled segment with an endpoint outside the reachable component - the current rule placed
    /// somewhere the explosives could never get to at all.
    /// </summary>
    public static void RecordOutsideComponentSample()
    {
        Interlocked.Increment(ref _outsideComponentSamples);
    }

    /// <summary>
    /// The pipeline as it would actually run: the landmark bound first because it is nearly free,
    /// the coarse bound only on what the landmarks could not settle. This is the number that says
    /// whether a geodesic rule fits in the budget, since it counts each segment once.
    /// </summary>
    public static void RecordChain(BoundVerdict verdict, long ticks)
    {
        Interlocked.Increment(ref ChainVerdicts[(int)verdict]);
        Interlocked.Add(ref _chainTicks, ticks);
    }

    public static void RecordBound(bool coarse, BoundVerdict verdict, long ticks)
    {
        var verdicts = coarse ? CoarseVerdicts : LandmarkVerdicts;
        Interlocked.Increment(ref verdicts[(int)verdict]);
        if (coarse)
        {
            Interlocked.Add(ref _coarseTicks, ticks);
        }
        else
        {
            Interlocked.Add(ref _landmarkTicks, ticks);
        }
    }

    /// <summary>
    /// Checks a bound's verdict against what the fine search actually found. An unsound answer here
    /// is not a tuning problem, it is a bound that cannot be shipped.
    /// </summary>
    public static void RecordBoundCheck(bool coarse, BoundVerdict verdict, GeodesicResult truth)
    {
        if (truth == GeodesicResult.Aborted)
        {
            return;
        }

        if (coarse)
        {
            Interlocked.Increment(ref _coarseChecked);
        }
        else
        {
            Interlocked.Increment(ref _landmarkChecked);
        }

        if (verdict == BoundVerdict.Accept && truth != GeodesicResult.Reachable)
        {
            if (coarse)
            {
                Interlocked.Increment(ref _coarseUnsoundAccepts);
            }
            else
            {
                Interlocked.Increment(ref _landmarkUnsoundAccepts);
            }
        }
        else if (verdict == BoundVerdict.Reject && truth == GeodesicResult.Reachable)
        {
            if (coarse)
            {
                Interlocked.Increment(ref _coarseUnsoundRejects);
            }
            else
            {
                Interlocked.Increment(ref _landmarkUnsoundRejects);
            }
        }
    }

    /// <summary>Which side of the current rule the sampled segment came from.</summary>
    public static void RecordGeodesicSplit(bool acceptedBySampling, GeodesicResult result)
    {
        if (acceptedBySampling)
        {
            Interlocked.Increment(ref _geodesicOnAccepted);
            if (result == GeodesicResult.Reachable)
            {
                Interlocked.Increment(ref _geodesicOnAcceptedReachable);
            }
        }
        else
        {
            Interlocked.Increment(ref _geodesicOnRejected);
            if (result == GeodesicResult.Reachable)
            {
                Interlocked.Increment(ref _geodesicOnRejectedReachable);
            }
        }
    }

    private static void Max(ref long target, long value)
    {
        long seen;
        while (value > (seen = Interlocked.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Records that a search started, and whether collection was on for it. Without this, a report
    /// full of zeroes cannot be told apart from a report taken before any search ran.
    /// </summary>
    public static void RecordSearchStart(bool collecting)
    {
        Interlocked.Increment(ref _searchRuns);
        if (collecting)
        {
            Interlocked.Increment(ref _searchRunsWithCollection);
        }
    }

    /// <summary>A candidate past the euclidean range, rejected before any line work.</summary>
    public static void RecordTrivialReject()
    {
        Interlocked.Increment(ref _trivialRejects);
    }

    public static void RecordSegment(bool acceptedBySampling, bool rayClear, int cells, long ticks)
    {
        Interlocked.Increment(ref _segmentTests);
        Interlocked.Add(ref _rayCells, cells);
        Interlocked.Add(ref _rayTicks, ticks);
        if (acceptedBySampling)
        {
            Interlocked.Increment(ref _sampledAccepts);
        }

        if (rayClear)
        {
            Interlocked.Increment(ref _rayClear);
        }

        if (acceptedBySampling && !rayClear)
        {
            Interlocked.Increment(ref _acceptedButRayBlocked);
        }
        else if (!acceptedBySampling && rayClear)
        {
            Interlocked.Increment(ref _rejectedButRayClear);
        }
    }

    public static void RecordGeodesic(GeodesicResult result, int expanded, long ticks)
    {
        Interlocked.Increment(ref _geodesicSamples);
        Interlocked.Add(ref _geodesicExpanded, expanded);
        Interlocked.Add(ref _geodesicTicks, ticks);
        switch (result)
        {
            case GeodesicResult.Reachable:
                Interlocked.Increment(ref _geodesicReachable);
                break;
            case GeodesicResult.Unreachable:
                Interlocked.Increment(ref _geodesicUnreachable);
                break;
            default:
                Interlocked.Increment(ref _geodesicAborted);
                break;
        }

        //Worst case matters as much as the mean here: one 5 ms query per mutation would be felt.
        Max(ref _geodesicMaxTicks, ticks);
    }

    public static string DescribeCounters()
    {
        var trivial = Interlocked.Read(ref _trivialRejects);
        var tests = Interlocked.Read(ref _segmentTests);
        var accepts = Interlocked.Read(ref _sampledAccepts);
        var rayClear = Interlocked.Read(ref _rayClear);
        var falseAccepts = Interlocked.Read(ref _acceptedButRayBlocked);
        var falseRejects = Interlocked.Read(ref _rejectedButRayClear);
        var rayCells = Interlocked.Read(ref _rayCells);
        var rayTicks = Interlocked.Read(ref _rayTicks);
        var geoSamples = Interlocked.Read(ref _geodesicSamples);
        var geoReachable = Interlocked.Read(ref _geodesicReachable);
        var geoUnreachable = Interlocked.Read(ref _geodesicUnreachable);
        var geoAborted = Interlocked.Read(ref _geodesicAborted);
        var geoExpanded = Interlocked.Read(ref _geodesicExpanded);
        var geoTicks = Interlocked.Read(ref _geodesicTicks);
        var geoMaxTicks = Interlocked.Read(ref _geodesicMaxTicks);

        var runs = Interlocked.Read(ref _searchRuns);
        var runsCollecting = Interlocked.Read(ref _searchRunsWithCollection);

        var sb = new StringBuilder();
        sb.AppendLine("[collection state]");
        sb.AppendLine($"  collecting right now:              {Enabled}");
        sb.AppendLine($"  searches started since last reset: {runs:N0}");
        sb.AppendLine($"  of those, with collection on:      {runsCollecting:N0}");
        if (runs > 0 && tests == 0)
        {
            sb.AppendLine("  WARNING: a search ran but nothing was recorded - collection was off for it.");
        }
        else if (runs == 0)
        {
            sb.AppendLine("  WARNING: no search has run since the last reset, so the counters below are empty.");
        }

        sb.AppendLine();
        sb.AppendLine("[segment tests]");
        sb.AppendLine($"  out-of-range rejects (no line work): {trivial:N0}");
        sb.AppendLine($"  segment tests (in range):           {tests:N0}");
        sb.AppendLine($"  accepted by current sampled rule:   {accepts:N0} ({Percent(accepts, tests)})");
        sb.AppendLine($"  exact line clear:                   {rayClear:N0} ({Percent(rayClear, tests)})");
        sb.AppendLine($"  ACCEPTED TODAY BUT LINE BLOCKED:    {falseAccepts:N0} ({Percent(falseAccepts, tests)} of tests, {Percent(falseAccepts, accepts)} of accepts)");
        sb.AppendLine($"  rejected today though line clear:   {falseRejects:N0} ({Percent(falseRejects, tests)})");
        sb.AppendLine($"  mean cells per line test:           {Mean(rayCells, tests)}");
        sb.AppendLine($"  mean line test cost:                {Micros(rayTicks, tests)} (delegate per cell; a bitboard would be several times cheaper)");
        sb.AppendLine();
        sb.AppendLine("[sampled bounded A* over the same walkability - cost of the expensive tier]");
        sb.AppendLine($"  samples:     {geoSamples:N0}");
        sb.AppendLine($"  reachable within range (a legal wrap-around the current rule refuses): {geoReachable:N0} ({Percent(geoReachable, geoSamples)})");
        sb.AppendLine($"  unreachable: {geoUnreachable:N0} ({Percent(geoUnreachable, geoSamples)})");
        sb.AppendLine($"  aborted at node limit: {geoAborted:N0}");
        sb.AppendLine($"  mean nodes expanded:   {Mean(geoExpanded, geoSamples)}");
        sb.AppendLine($"  mean cost: {Micros(geoTicks, geoSamples)}   worst: {Micros(geoMaxTicks, 1)}");
        var onAccepted = Interlocked.Read(ref _geodesicOnAccepted);
        var onAcceptedReachable = Interlocked.Read(ref _geodesicOnAcceptedReachable);
        var onRejected = Interlocked.Read(ref _geodesicOnRejected);
        var onRejectedReachable = Interlocked.Read(ref _geodesicOnRejectedReachable);
        sb.AppendLine($"  of samples the current rule ACCEPTED: {onAccepted:N0}, of which reachable {onAcceptedReachable:N0} ({Percent(onAcceptedReachable, onAccepted)})");
        sb.AppendLine($"    -> the rest are placements that cannot be built: {Percent(onAccepted - onAcceptedReachable, onAccepted)} of accepted-and-blocked");
        sb.AppendLine($"  of samples the current rule rejected:  {onRejected:N0}, of which reachable {onRejectedReachable:N0} ({Percent(onRejectedReachable, onRejected)})");
        sb.AppendLine();
        sb.Append(DescribeBounds());
        return sb.ToString();
    }

    private static string DescribeBounds()
    {
        var model = BoundModel;
        var bitTests = Interlocked.Read(ref _bitLineTests);
        var bitTicks = Interlocked.Read(ref _bitLineTicks);
        var bitDisagreements = Interlocked.Read(ref _bitLineDisagreements);
        var fineSamples = Interlocked.Read(ref _fineSamples);
        var fineTicks = Interlocked.Read(ref _fineTicks);
        var fineMaxTicks = Interlocked.Read(ref _fineMaxTicks);
        var fineExpanded = Interlocked.Read(ref _fineExpanded);
        var fineDisagreements = Interlocked.Read(ref _fineDisagreements);

        var sb = new StringBuilder();
        sb.AppendLine("[bound model]");
        sb.AppendLine($"  status: {BoundModelStatus}");
        if (model != null)
        {
            sb.AppendLine($"  component: {model.ComponentCells:N0} cells in {model.Width} x {model.Height}, origin ({model.OriginX}, {model.OriginY})");
            sb.AppendLine($"  coarse: block {model.BlockSize} -> {model.CoarseWidth} x {model.CoarseHeight} blocks");
            sb.AppendLine($"  landmarks: {model.LandmarkCount}   memory: {model.ApproximateBytes / 1024.0 / 1024.0:F1} MB");
            sb.AppendLine($"  build: {model.BuildMillis:F0} ms grids + {model.LandmarkMillis:F0} ms landmarks");
        }

        sb.AppendLine();
        sb.AppendLine("[tier one on the bitboard - the exact line test as it would actually ship]");
        sb.AppendLine($"  tests: {bitTests:N0}   mean cost: {Micros(bitTicks, bitTests)}");
        sb.AppendLine($"  disagreements with the delegate version: {bitDisagreements:N0} (must be 0)");
        sb.AppendLine();
        sb.AppendLine("[fine A* on the bitboard - the expensive tier written properly]");
        sb.AppendLine($"  samples: {fineSamples:N0}   mean nodes: {Mean(fineExpanded, fineSamples)}");
        sb.AppendLine($"  mean cost: {Micros(fineTicks, fineSamples)}   worst: {Micros(fineMaxTicks, 1)}");
        sb.AppendLine($"  disagreements with the delegate A*: {fineDisagreements:N0} (must be 0)");
        sb.AppendLine($"  sampled segments with an endpoint outside the reachable component: {Interlocked.Read(ref _outsideComponentSamples):N0}");
        sb.AppendLine();
        sb.Append(DescribeFineByVerdict());
        sb.AppendLine();
        sb.Append(DescribeBound("coarse two-sided blocks", CoarseVerdicts, Interlocked.Read(ref _coarseTicks),
            Interlocked.Read(ref _coarseChecked), Interlocked.Read(ref _coarseUnsoundAccepts), Interlocked.Read(ref _coarseUnsoundRejects)));
        sb.AppendLine();
        sb.Append(DescribeBound("ALT landmarks", LandmarkVerdicts, Interlocked.Read(ref _landmarkTicks),
            Interlocked.Read(ref _landmarkChecked), Interlocked.Read(ref _landmarkUnsoundAccepts), Interlocked.Read(ref _landmarkUnsoundRejects)));
        sb.AppendLine();

        var chainAccept = Interlocked.Read(ref ChainVerdicts[(int)BoundVerdict.Accept]);
        var chainReject = Interlocked.Read(ref ChainVerdicts[(int)BoundVerdict.Reject]);
        var chainInconclusive = Interlocked.Read(ref ChainVerdicts[(int)BoundVerdict.Inconclusive]);
        var chainOutside = Interlocked.Read(ref ChainVerdicts[(int)BoundVerdict.Outside]);
        var chainTotal = chainAccept + chainReject + chainInconclusive + chainOutside;
        var chainTicks = Interlocked.Read(ref _chainTicks);
        sb.AppendLine("[pipeline: landmarks first, coarse only on what they leave - the deployable shape]");
        sb.AppendLine($"  blocked segments: {chainTotal:N0}   mean cost: {Micros(chainTicks, chainTotal)}");
        sb.AppendLine($"  settled: {Percent(chainAccept + chainReject + chainOutside, chainTotal)} (accept {Percent(chainAccept, chainTotal)}, reject {Percent(chainReject, chainTotal)}, outside {Percent(chainOutside, chainTotal)})");
        sb.AppendLine($"  STILL NEEDS A FINE SEARCH: {chainInconclusive:N0} ({Percent(chainInconclusive, chainTotal)} of blocked)");
        return sb.ToString();
    }

    /// <summary>
    /// What the fine tier would really cost, by pipeline verdict, plus what a node cap would keep.
    /// </summary>
    private static string DescribeFineByVerdict()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[fine search split by what the pipeline already decided]");
        sb.AppendLine("  verdict        samples    reachable      mean nodes   mean cost");
        foreach (var verdict in new[] { BoundVerdict.Accept, BoundVerdict.Reject, BoundVerdict.Inconclusive, BoundVerdict.Outside })
        {
            var slot = (int)verdict;
            var samples = Interlocked.Read(ref FineSamplesBy[slot]);
            var reachable = Interlocked.Read(ref FineReachableBy[slot]);
            var expanded = Interlocked.Read(ref FineExpandedBy[slot]);
            var ticks = Interlocked.Read(ref FineTicksBy[slot]);
            sb.AppendLine($"  {verdict,-13} {samples,8:N0}   {Percent(reachable, samples),8}   {Mean(expanded, samples),10}   {Micros(ticks, samples),10}");
        }

        sb.AppendLine("  (only the Inconclusive row is traffic a shipped fine tier would see - the others are");
        sb.AppendLine("   already settled by the bounds, and Reject is where the expensive exhaustive searches went)");
        sb.AppendLine();

        var reachableTotal = 0L;
        var unreachableTotal = 0L;
        foreach (var count in FineNodesReachable)
        {
            reachableTotal += count;
        }

        foreach (var count in FineNodesUnreachable)
        {
            unreachableTotal += count;
        }

        sb.AppendLine("[node cap on the inconclusive bucket - what a capped fine search would still catch]");
        sb.AppendLine($"  reachable samples: {reachableTotal:N0}   unreachable samples: {unreachableTotal:N0}");
        sb.AppendLine("  cap      reachable found      unreachable proved");
        var reachableRunning = 0L;
        var unreachableRunning = 0L;
        for (var i = 0; i < NodeBuckets.Length; i++)
        {
            reachableRunning += Interlocked.Read(ref FineNodesReachable[i]);
            unreachableRunning += Interlocked.Read(ref FineNodesUnreachable[i]);
            var label = NodeBuckets[i] == int.MaxValue ? "no cap" : NodeBuckets[i].ToString();
            sb.AppendLine($"  {label,-8} {Percent(reachableRunning, reachableTotal),16}   {Percent(unreachableRunning, unreachableTotal),18}");
        }

        sb.AppendLine("  (a cap turns anything it does not resolve into a conservative reject, so the reachable");
        sb.AppendLine("   column is the share of legal wrap-arounds that cap would keep)");
        return sb.ToString();
    }

    private static string DescribeBound(string name, long[] verdicts, long ticks, long checkedCount, long unsoundAccepts, long unsoundRejects)
    {
        var accept = Interlocked.Read(ref verdicts[(int)BoundVerdict.Accept]);
        var reject = Interlocked.Read(ref verdicts[(int)BoundVerdict.Reject]);
        var inconclusive = Interlocked.Read(ref verdicts[(int)BoundVerdict.Inconclusive]);
        var outside = Interlocked.Read(ref verdicts[(int)BoundVerdict.Outside]);
        var total = accept + reject + inconclusive + outside;

        var sb = new StringBuilder();
        sb.AppendLine($"[{name} - run on every line-blocked segment]");
        sb.AppendLine($"  queries:      {total:N0}   mean cost: {Micros(ticks, total)}");
        sb.AppendLine($"  accept:       {accept:N0} ({Percent(accept, total)})");
        sb.AppendLine($"  reject:       {reject:N0} ({Percent(reject, total)})");
        sb.AppendLine($"  INCONCLUSIVE: {inconclusive:N0} ({Percent(inconclusive, total)}) <- the share that would still need a fine search");
        sb.AppendLine($"  outside model: {outside:N0} ({Percent(outside, total)})");
        sb.AppendLine($"  soundness, checked against the fine search on {checkedCount:N0} samples:");
        sb.AppendLine($"    accepted but unreachable: {unsoundAccepts:N0}   rejected but reachable: {unsoundRejects:N0}   (both must be 0)");
        return sb.ToString();
    }

    private static string Percent(long part, long total)
    {
        return total <= 0 ? "n/a" : (100.0 * part / total).ToString("F2", CultureInfo.InvariantCulture) + "%";
    }

    private static string Mean(long total, long count)
    {
        return count <= 0 ? "n/a" : ((double)total / count).ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string Micros(long ticks, long count)
    {
        if (count <= 0)
        {
            return "n/a";
        }

        var micros = ticks * 1_000_000.0 / Stopwatch.Frequency / count;
        return micros.ToString("F2", CultureInfo.InvariantCulture) + " us";
    }

    /// <summary>
    /// Exact grid line test: every cell the segment touches, corners included, with no sample gaps.
    /// A diagonal step through a corner requires both orthogonal cells, matching a body that cannot
    /// squeeze between two blocked cells.
    /// </summary>
    public static bool LineIsClear(Vector2 from, Vector2 to, Func<Vector2, bool> isWalkable, out int cellsVisited)
    {
        cellsVisited = 1;
        var x = (int)MathF.Floor(from.X);
        var y = (int)MathF.Floor(from.Y);
        var endX = (int)MathF.Floor(to.X);
        var endY = (int)MathF.Floor(to.Y);
        if (!isWalkable(new Vector2(x, y)))
        {
            return false;
        }

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0;
        var stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        var tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / dx);
        var tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / dy);
        var tMaxX = stepX == 0
            ? float.PositiveInfinity
            : (stepX > 0 ? x + 1 - from.X : from.X - x) / MathF.Abs(dx);
        var tMaxY = stepY == 0
            ? float.PositiveInfinity
            : (stepY > 0 ? y + 1 - from.Y : from.Y - y) / MathF.Abs(dy);

        //Floating point could otherwise leave the walk one cell short of the end forever.
        var guard = 4 * (Math.Abs(endX - x) + Math.Abs(endY - y)) + 8;
        while ((x != endX || y != endY) && guard-- > 0)
        {
            if (stepX != 0 && stepY != 0 && MathF.Abs(tMaxX - tMaxY) < 1e-6f)
            {
                cellsVisited += 2;
                if (!isWalkable(new Vector2(x + stepX, y)) || !isWalkable(new Vector2(x, y + stepY)))
                {
                    return false;
                }

                x += stepX;
                y += stepY;
                tMaxX += tDeltaX;
                tMaxY += tDeltaY;
            }
            else if (tMaxX < tMaxY)
            {
                x += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                y += stepY;
                tMaxY += tDeltaY;
            }

            cellsVisited++;
            if (!isWalkable(new Vector2(x, y)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Is there a walkable path from one point to the other no longer than <paramref name="budget"/>?
    /// Bounded A* with an octile heuristic: any node whose best possible total already exceeds the
    /// budget is dropped, so a dead end terminates almost at once. Returns the answer only - the
    /// planner never needs the path itself.
    /// </summary>
    public static GeodesicResult GeodesicWithin(
        Vector2 from,
        Vector2 to,
        float budget,
        Func<Vector2, bool> isWalkable,
        int nodeLimit,
        out int expanded)
    {
        expanded = 0;
        var startX = (int)MathF.Floor(from.X);
        var startY = (int)MathF.Floor(from.Y);
        var goalX = (int)MathF.Floor(to.X);
        var goalY = (int)MathF.Floor(to.Y);
        if (!isWalkable(new Vector2(startX, startY)) || !isWalkable(new Vector2(goalX, goalY)))
        {
            return GeodesicResult.Unreachable;
        }

        if (startX == goalX && startY == goalY)
        {
            return GeodesicResult.Reachable;
        }

        var best = new Dictionary<long, float> { [Key(startX, startY)] = 0 };
        var closed = new HashSet<long>();
        var open = new PriorityQueue<long, float>();
        open.Enqueue(Key(startX, startY), Octile(startX, startY, goalX, goalY));

        while (open.TryDequeue(out var key, out var f))
        {
            //The queue is ordered by best possible total, so once the cheapest one is over budget
            //nothing left can come in under it.
            if (f > budget)
            {
                return GeodesicResult.Unreachable;
            }

            if (!closed.Add(key))
            {
                continue;
            }

            if (++expanded > nodeLimit)
            {
                return GeodesicResult.Aborted;
            }

            var cx = (int)(key >> 32);
            var cy = (int)(uint)key;
            var g = best[key];
            for (var i = 0; i < NeighborX.Length; i++)
            {
                var ox = NeighborX[i];
                var oy = NeighborY[i];
                var nx = cx + ox;
                var ny = cy + oy;
                if (!isWalkable(new Vector2(nx, ny)))
                {
                    continue;
                }

                //No squeezing through a corner between two blocked cells.
                if (ox != 0 && oy != 0 &&
                    (!isWalkable(new Vector2(cx + ox, cy)) || !isWalkable(new Vector2(cx, cy + oy))))
                {
                    continue;
                }

                var tentative = g + (ox != 0 && oy != 0 ? DiagonalCost : 1f);
                if (tentative > budget)
                {
                    continue;
                }

                if (nx == goalX && ny == goalY)
                {
                    return GeodesicResult.Reachable;
                }

                var neighborKey = Key(nx, ny);
                if (best.TryGetValue(neighborKey, out var known) && known <= tentative)
                {
                    continue;
                }

                var estimate = tentative + Octile(nx, ny, goalX, goalY);
                if (estimate > budget)
                {
                    continue;
                }

                best[neighborKey] = tentative;
                open.Enqueue(neighborKey, estimate);
            }
        }

        return GeodesicResult.Unreachable;
    }

    private static readonly int[] NeighborX = [1, 1, 0, -1, -1, -1, 0, 1];
    private static readonly int[] NeighborY = [0, 1, 1, 1, 0, -1, -1, -1];

    private static long Key(int x, int y)
    {
        return ((long)x << 32) | (uint)y;
    }

    private static float Octile(int x, int y, int goalX, int goalY)
    {
        var dx = Math.Abs(x - goalX);
        var dy = Math.Abs(y - goalY);
        return dx > dy ? dx - dy + DiagonalCost * dy : dy - dx + DiagonalCost * dx;
    }

}

public enum GeodesicResult
{
    Reachable,
    Unreachable,
    Aborted,
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.Shared.Enums;
using GameOffsets2.Native;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace ExpeditionIcons;

/// <summary>
/// The area-side half of the pathfinding instrumentation: how big the walkable space the planner can
/// actually reach is, what it would cost to hold, and a single report file combining that with the
/// counters <see cref="PathfindingDiagnostics"/> gathers during a search.
/// </summary>
public partial class ExpeditionIcons
{
    private const string DiagnosticsFileName = "pathfinding-diagnostics.txt";

    private volatile string _pathfindingReport;
    private volatile bool _pathfindingMeasuring;
    private string _pathfindingReportPath;
    //Set when a report is written, because writing one resets the counters - without this the panel
    //reads as "nothing was ever recorded" one second after it recorded everything.
    private volatile string _lastReportSummary;

    /// <summary>
    /// Everything built for measuring rather than playing, behind one header: the rule comparison,
    /// the pathfinding instrumentation, and the map stat readout.
    /// </summary>
    private void DrawDiagnostics()
    {
        if (!ImGui.CollapsingHeader("Diagnostics and measurement"))
        {
            return;
        }

        //Status at the top level, where it is visible whether or not the sections are expanded.
        if (_comparisonRunning)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), _comparisonReport ?? "Comparison running...");
        }
        else if (_comparisonReportUnseen)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), "New comparison result below, and appended to " + DiagnosticsFileName);
            ImGui.SetNextItemOpen(true);
            _comparisonReportUnseen = false;
        }

        ImGui.Indent();
        if (ImGui.TreeNodeEx("Placement rules"))
        {
            DrawPlacementRuleSection();
            ImGui.TreePop();
        }

        if (ImGui.TreeNodeEx("Pathfinding measurements"))
        {
            DrawPathfindingSection();
            ImGui.TreePop();
        }

        if (ImGui.TreeNodeEx("Map stats"))
        {
            DrawStatSection();
            ImGui.TreePop();
        }

        ImGui.Unindent();
    }

    private void DrawPathfindingSection()
    {
        var collect = Settings.PlannerSettings.CollectPathfindingDiagnostics.Value;
        if (ImGui.Checkbox("Collect pathfinding diagnostics", ref collect))
        {
            Settings.PlannerSettings.CollectPathfindingDiagnostics.Value = collect;
        }

        var tests = PathfindingDiagnostics.SegmentTests;
        ImGui.Text($"Bound model: {PathfindingDiagnostics.BoundModelStatus}");
        if (BoundModelMismatch() is { } mismatch)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), mismatch);
        }
        ImGui.Text($"Segment tests recorded: {tests:N0}   searches since reset: {PathfindingDiagnostics.SearchRuns:N0} ({PathfindingDiagnostics.SearchRunsWithCollection:N0} with collection on)");
        if (_lastReportSummary is { } summary)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), summary);
        }

        if (tests == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                _lastReportSummary != null
                    ? "Counters are empty because the last report reset them. Run another search to collect more."
                    : PathfindingDiagnostics.Enabled
                        ? "Nothing recorded yet - run a search."
                        : "Collection is OFF - tick the box above, then run a search.");
        }

        ImGui.Separator();

        if (_pathfindingMeasuring)
        {
            ImGui.Text("Measuring area...");
        }
        else if (ImGui.Button("Measure area and write report"))
        {
            StartPathfindingMeasurement();
        }

        ImGui.SameLine();
        if (ImGui.Button("Build bound model now"))
        {
            StartBoundModelBuild();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset counters"))
        {
            PathfindingDiagnostics.Reset();
        }

        if (_pathfindingReport is not { } report)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy report to clipboard"))
        {
            ImGui.SetClipboardText(report);
        }

        if (_pathfindingReportPath != null)
        {
            ImGui.Text($"Appended to {_pathfindingReportPath}");
        }

        ImGui.BeginChild("pathfinding_report", new Vector2(0, 400), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.TextUnformatted(report);
        ImGui.EndChild();
    }

    /// <summary>
    /// Reads everything that needs the game on this thread, then does the flood fill off it - the
    /// fill touches every cell in the area and would otherwise be a visible frame hitch.
    /// </summary>
    private void StartPathfindingMeasurement()
    {
        var grid = _pathfindingData;
        var dimensions = _areaDimensions;
        if (grid == null || dimensions.X <= 0 || dimensions.Y <= 0)
        {
            _pathfindingReport = "No pathfinding data for this area.";
            return;
        }

        var snapshot = new DiagnosticsSnapshot(
            AreaId: GameController.Area.CurrentArea?.Area?.Id ?? "<null>",
            AreaName: GameController.Area.CurrentArea?.Area?.Name ?? "<null>",
            IsLogbook: IsLogbookArea,
            Dimensions: dimensions,
            DetonatorPos: DetonatorPos?.Pos,
            ExplosiveRangeGrid: _explosiveRange / GridToWorldMultiplier,
            ExplosiveRadiusGrid: _explosiveRadius / GridToWorldMultiplier,
            TotalExplosives: SafeStat(() => ExpeditionInfo.TotalExplosiveCount),
            PlacementDistancePct: GetMapStat(GameStat.MapExpeditionMaximumPlacementDistancePct),
            ExplosionRadiusPct: GetMapStat(GameStat.MapExpeditionExplosionRadiusPct),
            BlacklistedCircles: _blacklistedCircles.Count);

        _pathfindingMeasuring = true;
        _ = Task.Run(() =>
        {
            try
            {
                var report = BuildPathfindingReport(snapshot);
                _pathfindingReport = report;
                try
                {
                    var path = Path.Combine(DirectoryFullName, DiagnosticsFileName);
                    File.AppendAllText(path, report + Environment.NewLine);
                    _pathfindingReportPath = path;
                }
                catch (Exception ex)
                {
                    DebugWindow.LogError($"ExpeditionIcons could not write the diagnostics file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _pathfindingReport = $"Measurement failed: {ex}";
            }
            finally
            {
                _pathfindingMeasuring = false;
            }
        });
    }

    /// <summary>
    /// Builds the bound structures for this area on a background thread and publishes them when
    /// ready. The search does not wait: segments tested before it lands simply skip the bound
    /// measurements, which costs a little sample size and no correctness.
    /// </summary>
    private void StartBoundModelBuild()
    {
        //One model for the feature, the validation and the diagnostics - a second build path would
        //let the measurements describe a model the search never used.
        _ = EnsurePlacementModel(forceRebuild: true);
    }

    /// <summary>
    /// Block size and landmark count are baked into the model when it is built, so changing either
    /// setting does nothing until it is rebuilt - and the report reads almost identically either
    /// way. Says so rather than letting a run be silently spent on the old configuration.
    /// </summary>
    private string BoundModelMismatch()
    {
        if (PathfindingDiagnostics.BoundModel is not { } model)
        {
            return null;
        }

        var planner = Settings.PlannerSettings;
        if (model.BlockSize == planner.GeodesicCoarseBlockSize.Value &&
            model.LandmarkCount == planner.GeodesicLandmarkCount.Value)
        {
            return null;
        }

        return $"MISMATCH: the bound model in use was built at block {model.BlockSize} with {model.LandmarkCount} landmarks, " +
               $"not block {planner.GeodesicCoarseBlockSize.Value} with {planner.GeodesicLandmarkCount.Value}. " +
               "Press 'Build bound model now', then run a search - these bound numbers are for the OLD settings.";
    }

    private static int SafeStat(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return -1;
        }
    }

    private string BuildPathfindingReport(DiagnosticsSnapshot snapshot)
    {
        var sw = Stopwatch.StartNew();
        var width = snapshot.Dimensions.X;
        var height = snapshot.Dimensions.Y;
        var cells = (long)width * height;
        var walkable = 0L;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (IsValidPlacement(new Vector2(x, y)))
                {
                    walkable++;
                }
            }
        }

        var walkableScanMs = sw.Elapsed.TotalMilliseconds;
        var range = snapshot.ExplosiveRangeGrid;
        var maxReach = range * Math.Max(snapshot.TotalExplosives, 1) + snapshot.ExplosiveRadiusGrid;

        var sb = new StringBuilder();
        sb.AppendLine("================ ExpeditionIcons pathfinding diagnostics ================");
        sb.AppendLine($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"area: {snapshot.AreaId} / {snapshot.AreaName}   logbook: {snapshot.IsLogbook}");
        sb.AppendLine($"map stats: placement distance +{snapshot.PlacementDistancePct}%, explosion radius +{snapshot.ExplosionRadiusPct}%");
        sb.AppendLine();
        sb.AppendLine("[area size]");
        sb.AppendLine($"  dimensions:            {width} x {height} = {cells:N0} cells");
        sb.AppendLine($"  walkable+unblacklisted: {walkable:N0} ({Ratio(walkable, cells)}), scanned in {walkableScanMs:F0} ms");
        sb.AppendLine($"  existing int[][] grid:  ~{cells * 4 / 1024.0 / 1024.0:F1} MB (already held)");
        sb.AppendLine($"  1-bit walk board:       {cells / 8 / 1024.0:F0} KB");
        sb.AppendLine($"  1-byte walk board:      {cells / 1024.0 / 1024.0:F1} MB");
        sb.AppendLine();
        sb.AppendLine("[explosive reach]");
        sb.AppendLine($"  explosion range R:  {range:F1} grid");
        sb.AppendLine($"  explosion radius:   {snapshot.ExplosiveRadiusGrid:F1} grid");
        sb.AppendLine($"  total explosives:   {snapshot.TotalExplosives}");
        sb.AppendLine($"  maxReach (R * count): {maxReach:F0} grid");
        sb.AppendLine($"  bounded Dijkstra disc at R: ~{Math.PI * range * range:N0} cells");
        sb.AppendLine($"  per-anchor field (2R+1)^2 at 1 byte/cell: {Math.Pow(2 * range + 1, 2) / 1024.0:F0} KB");
        sb.AppendLine($"  blacklisted circles marked: {snapshot.BlacklistedCircles}");
        sb.AppendLine();

        if (snapshot.DetonatorPos is { } detonator)
        {
            sb.Append(DescribeReachableComponent(detonator, maxReach, snapshot.Dimensions));
        }
        else
        {
            sb.AppendLine("[reachable component] detonator position unknown - stand in the expedition and measure again.");
        }

        sb.AppendLine();
        sb.AppendLine("[planner settings]");
        var planner = Settings.PlannerSettings;
        sb.AppendLine($"  ValidatedIntermediatePoints: {planner.ValidatedIntermediatePoints.Value}");
        sb.AppendLine($"  SearchThreads: {planner.SearchThreads.Value}   PathGenerationSize: {planner.PathGenerationSize.Value}   MaxGenerationTime: {planner.GenerationTimeSeconds(IsLogbookArea)}s");
        sb.AppendLine($"  PathMutateChance: {planner.PathMutateChance.Value}   NewRandomPathInjectionRate: {planner.NewRandomPathInjectionRate.Value}");
        sb.AppendLine($"  ground-truth sample rate: 1 in {planner.DiagnosticsGeodesicSampleRate.Value}");
        sb.AppendLine($"  configured coarse block size: {planner.GeodesicCoarseBlockSize.Value}   configured landmarks: {planner.GeodesicLandmarkCount.Value}");
        if (BoundModelMismatch() is { } mismatch)
        {
            sb.AppendLine($"  !! {mismatch}");
        }
        sb.AppendLine();
        var recordedTests = PathfindingDiagnostics.SegmentTests;
        sb.Append(PathfindingDiagnostics.DescribeCounters());
        sb.AppendLine();
        sb.AppendLine("(counters were reset after this report, so the next one covers only the next search)");
        sb.AppendLine("========================================================================");
        PathfindingDiagnostics.Reset();
        _lastReportSummary = $"Last report written {DateTime.Now:HH:mm:ss} covering {recordedTests:N0} segment tests - counters reset.";
        return sb.ToString();
    }

    /// <summary>
    /// The walkable cells actually connected to the detonator within the explosive chain's total
    /// reach. This is the honest bound on the space the planner searches - the maxReach box on its
    /// own is far larger than any real expedition.
    /// </summary>
    private string DescribeReachableComponent(Vector2 detonator, float maxReach, Vector2i dimensions)
    {
        var width = dimensions.X;
        var height = dimensions.Y;
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();

        var start = FindNearestWalkable(detonator, width, height);
        if (start is not { } startCell)
        {
            sb.AppendLine("[reachable component] no walkable cell found near the detonator.");
            return sb.ToString();
        }

        var visited = new bool[(long)width * height];
        var queue = new Queue<int>();
        var startIndex = startCell.Y * width + startCell.X;
        visited[startIndex] = true;
        queue.Enqueue(startIndex);
        var count = 0L;
        int minX = startCell.X, maxX = startCell.X, minY = startCell.Y, maxY = startCell.Y;
        var reachSquared = maxReach * maxReach;

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;
            count++;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;

            for (var oy = -1; oy <= 1; oy++)
            {
                for (var ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0)
                    {
                        continue;
                    }

                    var nx = x + ox;
                    var ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    var neighborIndex = ny * width + nx;
                    if (visited[neighborIndex])
                    {
                        continue;
                    }

                    var offset = new Vector2(nx, ny) - detonator;
                    if (offset.LengthSquared() > reachSquared || !IsValidPlacement(new Vector2(nx, ny)))
                    {
                        visited[neighborIndex] = true;
                        continue;
                    }

                    visited[neighborIndex] = true;
                    queue.Enqueue(neighborIndex);
                }
            }
        }

        var boxWidth = maxX - minX + 1;
        var boxHeight = maxY - minY + 1;
        var boxCells = (long)boxWidth * boxHeight;
        sb.AppendLine("[reachable component - walkable and connected to the detonator, within maxReach]");
        sb.AppendLine($"  detonator grid pos:  {detonator.X:F0}, {detonator.Y:F0}  (fill started at {startCell.X}, {startCell.Y})");
        sb.AppendLine($"  cells:               {count:N0}");
        sb.AppendLine($"  bounding box:        ({minX}, {minY}) - ({maxX}, {maxY}) = {boxWidth} x {boxHeight} = {boxCells:N0} cells");
        sb.AppendLine($"  box fill:            {Ratio(count, boxCells)}");
        sb.AppendLine($"  box as 1-bit board:  {boxCells / 8 / 1024.0:F0} KB");
        sb.AppendLine($"  fill took:           {sw.Elapsed.TotalMilliseconds:F0} ms");
        return sb.ToString();
    }

    private Vector2i? FindNearestWalkable(Vector2 point, int width, int height)
    {
        var cx = (int)MathF.Floor(point.X);
        var cy = (int)MathF.Floor(point.Y);
        for (var radius = 0; radius < 64; radius++)
        {
            for (var oy = -radius; oy <= radius; oy++)
            {
                for (var ox = -radius; ox <= radius; ox++)
                {
                    if (Math.Abs(ox) != radius && Math.Abs(oy) != radius)
                    {
                        continue;
                    }

                    var x = cx + ox;
                    var y = cy + oy;
                    if (x < 0 || y < 0 || x >= width || y >= height)
                    {
                        continue;
                    }

                    if (IsValidPlacement(new Vector2(x, y)))
                    {
                        return new Vector2i(x, y);
                    }
                }
            }
        }

        return null;
    }

    private static string Ratio(long part, long total)
    {
        return total <= 0 ? "n/a" : (100.0 * part / total).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    private record DiagnosticsSnapshot(
        string AreaId,
        string AreaName,
        bool IsLogbook,
        Vector2i Dimensions,
        Vector2? DetonatorPos,
        float ExplosiveRangeGrid,
        float ExplosiveRadiusGrid,
        int TotalExplosives,
        int PlacementDistancePct,
        int ExplosionRadiusPct,
        int BlacklistedCircles);
}

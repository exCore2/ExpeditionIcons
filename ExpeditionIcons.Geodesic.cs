using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ExileCore2;
using ExpeditionIcons.PathPlannerData;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace ExpeditionIcons;

/// <summary>
/// Wiring for the geodesic placement rule: the per-area model it needs, the exact revalidation of a
/// finished path, and the side-by-side comparison of the two rules.
/// </summary>
public partial class ExpeditionIcons
{
    /// <summary>Which segments of a finished path survive an exact check, and where it first fails.</summary>
    public sealed record PathValidation(bool[] SegmentValid, int InvalidCount, int FirstInvalidIndex);

    private Task<PathBoundModel> _placementModelTask;
    private (int Block, int Landmarks) _placementModelConfig = (-1, -1);

    //Bumped whenever the model is invalidated. A build that was already running when the area
    //changed would otherwise finish and publish a model of the OLD area's terrain, which is not
    //just wasted memory - a search in the new area would validate placements against the wrong map.
    private int _placementModelGeneration;
    private PathBoundModel.Workspace _validationWorkspace;
    private PathBoundModel _validationWorkspaceModel;

    //The exact check runs from the UI thread for the displayed path and from a background task
    //during a comparison, and they share one 5 MB scratch buffer rather than allocating per call.
    private readonly object _validationLock = new();

    //Keyed by reference, like the loot cache: a candidate is never mutated once scored, and the
    //exact check is far too slow to repeat every frame.
    private readonly ConditionalWeakTable<PathCandidate, PathValidation> _validationCache = [];

    private volatile string _comparisonReport;
    private volatile bool _comparisonRunning;
    //Cleared once the section has been opened for it, so a finished comparison cannot sit unseen
    //behind a collapsed node while the user waits for a result that already arrived.
    private volatile bool _comparisonReportUnseen;

    /// <summary>
    /// The area's bound model, built once and shared by the search, the validation and the
    /// diagnostics. Rebuilt when the settings it bakes in change, or when the blacklist does.
    /// </summary>
    private Task<PathBoundModel> EnsurePlacementModel(bool forceRebuild = false)
    {
        var config = (Settings.PlannerSettings.GeodesicCoarseBlockSize.Value, Settings.PlannerSettings.GeodesicLandmarkCount.Value);
        if (!forceRebuild && _placementModelConfig == config && _placementModelTask is { IsFaulted: false } cached)
        {
            return cached;
        }

        if (_pathfindingData == null || DetonatorPos is not { Pos: var detonator })
        {
            PathfindingDiagnostics.BoundModelStatus = "not built (no detonator or no pathfinding data)";
            return Task.FromResult<PathBoundModel>(null);
        }

        var dimensions = _areaDimensions;
        var maxReach = _explosiveRange / GridToWorldMultiplier * Math.Max(SafeStat(() => ExpeditionInfo.TotalExplosiveCount), 1) +
                       _explosiveRadius / GridToWorldMultiplier;
        var (blockSize, landmarks) = config;
        _placementModelConfig = config;
        var generation = ++_placementModelGeneration;
        PathfindingDiagnostics.BoundModel = null;
        PathfindingDiagnostics.BoundModelStatus = "building...";
        return _placementModelTask = Task.Run(() =>
        {
            try
            {
                var model = PathBoundModel.Build(
                    IsValidPlacement, detonator, maxReach, dimensions.X, dimensions.Y, blockSize, landmarks);
                if (generation != _placementModelGeneration)
                {
                    //Zoned, or the settings changed, while this was building. Drop it on the floor.
                    return null;
                }

                PathfindingDiagnostics.BoundModel = model;
                PathfindingDiagnostics.BoundModelStatus = model == null
                    ? "build failed (no walkable cell near the detonator)"
                    : $"built (block {model.BlockSize}, {model.LandmarkCount} landmarks)";
                return model;
            }
            catch (Exception ex)
            {
                if (generation == _placementModelGeneration)
                {
                    PathfindingDiagnostics.BoundModelStatus = $"build failed: {ex.Message}";
                }

                DebugWindow.LogError($"ExpeditionIcons bound model build failed: {ex}");
                return null;
            }
        });
    }

    /// <summary>
    /// Drops the cached model. The blacklist is baked into it, so a newly marked circle would
    /// otherwise be ignored by every later search in this area.
    /// </summary>
    private void InvalidatePlacementModel()
    {
        //Called every tick once a zone is cleared, so it does nothing when there is nothing to drop.
        if (_placementModelTask == null && _validationWorkspace == null && PathfindingDiagnostics.BoundModel == null)
        {
            return;
        }

        _placementModelGeneration++;
        _placementModelTask = null;
        _placementModelConfig = (-1, -1);
        _validationWorkspace = null;
        _validationWorkspaceModel = null;
        PathfindingDiagnostics.BoundModel = null;
        PathfindingDiagnostics.BoundModelStatus = "not built";
    }

    /// <summary>
    /// The exact verdict on a finished path, cached per candidate. Null when the search that
    /// produced it was not using the geodesic rule, so nothing is drawn differently in that case.
    /// </summary>
    private PathValidation GetValidation(PathPlanner.DetailedLootScore score)
    {
        if (score?.Candidate is not { } candidate ||
            score.Environment is not { PlacementValidator: { } validator } environment)
        {
            return null;
        }

        return _validationCache.GetValue(candidate, c => ValidateExactly(c, environment, validator));
    }

    private PathValidation ValidateExactly(PathCandidate candidate, ExpeditionEnvironment environment, GeodesicPlacementValidator validator)
    {
        lock (_validationLock)
        {
            return ValidateExactlyCore(candidate, environment, validator);
        }
    }

    private PathValidation ValidateExactlyCore(PathCandidate candidate, ExpeditionEnvironment environment, GeodesicPlacementValidator validator)
    {
        if (!ReferenceEquals(_validationWorkspaceModel, validator.Model))
        {
            _validationWorkspaceModel = validator.Model;
            _validationWorkspace = validator.CreateWorkspace();
        }

        var points = candidate.Points;
        var valid = new bool[points.Count];
        var previous = environment.StartingPoint;
        var invalidCount = 0;
        var firstInvalid = -1;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            valid[i] = previous.DistanceLessThanOrEqual(point, environment.ExplosionRange) &&
                       validator.IsExactlyValid(previous, point, environment.ExplosionRange, _validationWorkspace);
            if (!valid[i])
            {
                invalidCount++;
                if (firstInvalid < 0)
                {
                    firstInvalid = i;
                }
            }

            previous = point;
        }

        return new PathValidation(valid, invalidCount, firstInvalid);
    }

    /// <summary>
    /// What the path is really worth: explosives are placed in order, so nothing past the first
    /// impossible link can be placed at all, and the score of that prefix is what the run would
    /// actually collect.
    /// </summary>
    private (double Raw, double Buildable, int Invalid, int Placed, int Total) EvaluateBuildable(
        PathPlanner.DetailedLootScore score, GeodesicPlacementValidator validator)
    {
        if (score?.Candidate is not { } candidate || score.Environment is not { } environment)
        {
            return (0, 0, 0, 0, 0);
        }

        var validation = ValidateExactly(candidate, environment, validator);
        var total = candidate.Points.Count;
        var placed = validation.FirstInvalidIndex < 0 ? total : validation.FirstInvalidIndex;
        var prefix = new PathCandidate(candidate.Points.Take(placed).ToList(), (int[])candidate.Choices.Clone());
        var planner = new PathPlanner(Settings.PlannerSettings);
        planner.Init(environment);
        return (score.TotalScore, planner.GetScore(prefix, environment), validation.InvalidCount, placed, total);
    }

    private sealed record ComparisonRun(double Raw, double Buildable, int Invalid, int Placed, int Total, int Iterations, int FirstInvalid);

    /// <summary>
    /// Runs every rule several times for the configured time and reports what each winner is worth
    /// after exact revalidation. Per-test costs cannot answer this on their own: a rule that accepts
    /// more placements also explores fewer of them per second, and only the finished paths say which
    /// way that trade lands.
    /// </summary>
    private void RunPlacementComparison()
    {
        if (_comparisonRunning)
        {
            return;
        }

        ExpeditionEnvironment environment;
        try
        {
            environment = PlannerEnvironment;
        }
        catch (Exception ex)
        {
            _comparisonReport = $"Cannot compare: {ex.Message}";
            return;
        }

        var modelTask = EnsurePlacementModel();
        var settings = Settings.PlannerSettings;
        var soundController = GameController.SoundController;
        _comparisonRunning = true;
        _comparisonReport = "Preparing the area model...";
        _ = Task.Run(async () =>
        {
            try
            {
                if (await modelTask is not { } model)
                {
                    _comparisonReport = "Cannot compare: the area model could not be built.";
                    return;
                }

                var truth = new GeodesicPlacementValidator(model);
                var runs = Math.Max(1, settings.PlacementComparisonRuns.Value);
                var rules = new (string Name, GeodesicPlacementValidator Validator)[]
                {
                    ($"original (samples={settings.ValidatedIntermediatePoints.Value})", null),
                    ("straight run only", new GeodesicPlacementValidator(model, allowWrapArounds: false)),
                    ("walkable path", new GeodesicPlacementValidator(model)),
                };

                //A list, not a dictionary: the report reads as a progression from cheapest rule to
                //most permissive, and that order should not depend on hashing.
                var results = new List<(string Name, List<ComparisonRun> Runs)>();
                foreach (var (name, validator) in rules)
                {
                    var list = new List<ComparisonRun>();
                    for (var i = 0; i < runs; i++)
                    {
                        _comparisonReport = $"Running {name}, {i + 1} of {runs}...";
                        list.Add(await RunOneSearch(settings, environment, soundController, validator, truth));
                    }

                    results.Add((name, list));
                }

                _comparisonReport = BuildComparisonReport(results, truth, settings, runs);
                _comparisonReportUnseen = true;
                AppendToDiagnosticsFile(_comparisonReport);
            }
            catch (Exception ex)
            {
                _comparisonReport = $"Comparison failed: {ex}";
                DebugWindow.LogError($"ExpeditionIcons placement comparison failed: {ex}");
            }
            finally
            {
                _comparisonRunning = false;
            }
        });
    }

    private async Task<ComparisonRun> RunOneSearch(
        PlannerSettings settings,
        ExpeditionEnvironment environment,
        SoundController soundController,
        GeodesicPlacementValidator validator,
        GeodesicPlacementValidator truth)
    {
        using var runner = new PathPlannerRunner();
        runner.Start(settings, environment, soundController, null, validator);
        await runner.Completion;
        var score = runner.CurrentBestPath;
        var evaluated = EvaluateBuildable(score, truth);
        var firstInvalid = score?.Candidate is { } candidate && score.Environment is { } env
            ? ValidateExactly(candidate, env, truth).FirstInvalidIndex
            : -1;
        return new ComparisonRun(
            evaluated.Raw, evaluated.Buildable, evaluated.Invalid, evaluated.Placed, evaluated.Total,
            runner.TotalIterations, firstInvalid);
    }

    private string BuildComparisonReport(
        List<(string Name, List<ComparisonRun> Runs)> results,
        GeodesicPlacementValidator truth,
        PlannerSettings settings,
        int runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================ ExpeditionIcons placement rule comparison ================");
        sb.AppendLine($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"area: {GameController.Area.CurrentArea?.Area?.Id ?? "<null>"}   logbook: {IsLogbookArea}");
        sb.AppendLine($"{runs} run(s) per rule, {settings.GenerationTimeSeconds(IsLogbookArea)}s each on {settings.SearchThreads.Value} threads");
        sb.AppendLine($"model: block {truth.Model.BlockSize}, {truth.Model.LandmarkCount} landmarks");
        sb.AppendLine();
        sb.AppendLine("  rule                      median   median      worst    runs with   median      median");
        sb.AppendLine("                          reported  buildable  buildable   invalid   placeable  generations");
        foreach (var (name, list) in results)
        {
            var invalidRuns = list.Count(x => x.Invalid > 0);
            sb.AppendLine(
                $"  {name,-22} {Format(Median(list.Select(x => x.Raw))),8} {Format(Median(list.Select(x => x.Buildable))),10} " +
                $"{Format(list.Min(x => x.Buildable)),10} {invalidRuns,10}   {Format(Median(list.Select(x => (double)x.Placed))),8}/{list[0].Total,-3} " +
                $"{Format(Median(list.Select(x => (double)x.Iterations))),10}");
        }

        sb.AppendLine();
        sb.AppendLine("  per run:");
        foreach (var (name, list) in results)
        {
            foreach (var run in list)
            {
                var failure = run.FirstInvalid < 0 ? "-" : $"#{run.FirstInvalid}";
                sb.AppendLine(
                    $"    {name,-22} reported {Format(run.Raw),8}   buildable {Format(run.Buildable),8}   " +
                    $"{run.Placed,2}/{run.Total,-2} placeable   {run.Invalid} invalid   first failure {failure,-4}   {run.Iterations} generations");
            }
        }

        sb.AppendLine();
        sb.AppendLine("  'buildable' scores only the prefix up to the first placement that cannot be made, because");
        sb.AppendLine("  the explosives are placed in order. The two exact rules cannot produce an invalid");
        sb.AppendLine("  placement by construction, so any invalid run under those is a bug worth reporting.");
        sb.AppendLine("==========================================================================");
        return sb.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static string Format(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private void AppendToDiagnosticsFile(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(DirectoryFullName, DiagnosticsFileName), text + Environment.NewLine);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"ExpeditionIcons could not write the comparison: {ex.Message}");
        }
    }

    /// <summary>Which rule is live, whether the current best path survives an exact check, and the comparison.</summary>
    private void DrawPlacementRuleSection()
    {
        ImGui.Text($"Rule in use: {(Settings.PlannerSettings.UseGeodesicPlacement ? "walkable path" : "original line sampling")}");
        ImGui.Text($"Area model: {PathfindingDiagnostics.BoundModelStatus}");
        if (GetValidation(EditedOrNativeScore) is { } validation)
        {
            var text = validation.InvalidCount == 0
                ? "Best path: every placement verified reachable."
                : $"Best path: {validation.InvalidCount} placement(s) cannot be built, first at #{validation.FirstInvalidIndex}.";
            ImGui.TextColored(validation.InvalidCount == 0 ? new Vector4(0.5f, 0.9f, 0.5f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f), text);
        }

        if (_comparisonRunning)
        {
            ImGui.Text("Comparison running - two searches back to back...");
        }
        else if (ImGui.Button("Compare both placement rules"))
        {
            RunPlacementComparison();
        }

        if (_comparisonReport is { } report)
        {
            if (ImGui.Button("Copy comparison to clipboard"))
            {
                ImGui.SetClipboardText(report);
            }

            ImGui.BeginChild("geodesic_comparison", new Vector2(0, 220), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
            ImGui.TextUnformatted(report);
            ImGui.EndChild();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ExileCore2;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public class PathPlannerRunner : IDisposable
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    public bool IsRunning => _task is { IsCompleted: false };
    private PathPlanner _pathPlanner;
    private ExpeditionEnvironment _environment;
    private BestValue[] BestValues;
    //The UI asks for the best path's detailed score every frame, so it is computed once per
    //candidate and cached here. Keys compare by reference, which means a candidate must not be
    //mutated after it has been scored - MutatePath clones rather than editing in place.
    private readonly ConditionalWeakTable<PathCandidate, PathPlanner.DetailedLootScore> _lootCache = [];

    public PathPlanner.DetailedLootScore CurrentBestPath
    {
        get
        {
            if (BestValues?.Where(x => x != null).MaxBy(x => x.Score)?.Path is not { } bestPath)
            {
                return null;
            }

            if (_lootCache.TryGetValue(bestPath, out var existingScore))
            {
                return existingScore;
            }

            return _pathPlanner is { } pathPlanner &&
                   _environment is { } environment
                ? _lootCache.GetValue(bestPath, p => pathPlanner.GetDetailedScore(p, environment))
                : null;
        }
    }

    public double CurrentBestScore => BestValues?.Max(x => x?.Score ?? 0) ?? 0;

    private Task _task;

    /// <summary>Completes when the search does, so a caller can run two of these back to back.</summary>
    public Task Completion => _task ?? Task.CompletedTask;

    /// <summary>Generations completed across every thread - the throughput half of any comparison.</summary>
    public int TotalIterations => BestValues?.Sum(x => x?.Iteration ?? 0) ?? 0;

    public void Start(PlannerSettings settings, ExpeditionEnvironment environment, SoundController soundController,
        Task<PathBoundModel> placementModel = null, GeodesicPlacementValidator validator = null)
    {
        _task = Run(settings, environment, soundController, placementModel, validator);
    }

    private async Task Run(PlannerSettings settings, ExpeditionEnvironment environment, SoundController soundController,
        Task<PathBoundModel> placementModel, GeodesicPlacementValidator validator)
    {
        try
        {
            //Awaited rather than polled: the rule has to be in place before the first candidate is
            //built, or the search spends its opening generations under the old one.
            if (validator != null)
            {
                environment = environment with { PlacementValidator = validator };
            }
            else if (placementModel != null && await placementModel is { } model)
            {
                environment = environment with
                {
                    PlacementValidator = new GeodesicPlacementValidator(
                        model, allowWrapArounds: settings.GeodesicAllowWrapArounds),
                };
            }
            else if (placementModel != null)
            {
                DebugWindow.LogError("ExpeditionIcons: geodesic placement is on but the area model could not be built - falling back to the original rule.");
            }

            _environment = environment;
            _pathPlanner = new PathPlanner(settings);
            _pathPlanner.Init(environment);
            var threadCount = Math.Max(settings.SearchThreads.Value, 1);
            //Read once, before the threads start: the settings object is live, and a search that
            //picked up an edit halfway through would have its threads stopping at different times.
            var generationTime = settings.GenerationTimeSeconds(environment.IsLogbook);
            BestValues = new BestValue[threadCount];
            var tasks = new List<Task>();
            for (int i = 0; i < threadCount; i++)
            {
                var ii = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var p = new PathPlanner(settings);
                        var sw = Stopwatch.StartNew();
                        var iterationSw = Stopwatch.StartNew();
                        p.Init(environment);
                        foreach (var bestPath in p.GetBestPathSeries(environment))
                        {
                            BestValues[ii] = new BestValue(bestPath.Candidate, bestPath.Score, (BestValues[ii]?.Iteration ?? 0) + 1, iterationSw.Elapsed.TotalMilliseconds);
                            iterationSw.Restart();
                            if (sw.Elapsed.TotalSeconds >= generationTime ||
                                _cts.IsCancellationRequested)
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.LogError($"Expedition search thread failed: {ex}");
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"ExpeditionIcons PathPlanner failed before completion: {ex}");
        }
        finally
        {
            DebugWindow.LogMsg("ExpeditionIcons PathPlanner finished.");
            if (settings.PlaySoundOnFinish)
            {
                soundController.PlaySound("expedition_attention");
            }

            _ = CurrentBestPath;
            _environment = null;
            _pathPlanner = null;
        }
    }

    public void Stop() => _cts.Cancel();

    public void Dispose() => _cts.Dispose();
}

public record BestValue(PathCandidate Path, double Score, int Iteration, double LastGenerationTime);
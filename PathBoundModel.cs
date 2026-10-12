using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace ExpeditionIcons;

public enum BoundVerdict
{
    /// <summary>The bound proves a walkable path within budget exists.</summary>
    Accept,

    /// <summary>The bound proves no walkable path within budget exists.</summary>
    Reject,

    /// <summary>Neither, so a real search would have to run.</summary>
    Inconclusive,

    /// <summary>A point outside the model - measured separately so it cannot be read as a verdict.</summary>
    Outside,
}

/// <summary>
/// The per-area structures the cheap tiers of a geodesic placement rule would use, built once and
/// read by every search thread. Nothing here decides anything for the planner yet - it exists so the
/// two candidate schemes can be measured against the fine search's own answers before either is
/// committed to:
/// <list type="bullet">
/// <item>a bitboard of the walkable cells actually connected to the detonator, which also gives the
/// real cost of the exact line test rather than the delegate-per-cell cost,</item>
/// <item>coarse block grids in two flavours - conservative (every cell in the block open) for sound
/// accepts, optimistic (any cell open) for sound rejects,</item>
/// <item>ALT landmarks: exact distances from a handful of well-spread cells, giving a triangle
/// inequality lower bound (sound rejects) and a via-landmark upper bound (sound accepts) for a
/// couple of array reads.</item>
/// </list>
/// </summary>
public sealed class PathBoundModel
{
    private const float Sqrt2 = 1.41421356f;
    private const ushort Unreachable = ushort.MaxValue;

    //Landmark distances are stored in quarter-cell units, which keeps a whole area inside a ushort
    //and halves what would otherwise be the largest allocation here.
    private const float LandmarkScale = 4f;

    //Landmark distances are rounded into that quarter-cell grid, so a difference of two of them can
    //overstate the real one by half a cell. The lower bound gives that back before it is trusted -
    //without it the bound rejects a segment that is within half a cell of fitting, which is exactly
    //what the first run caught it doing.
    private const float LandmarkRounding = 0.5f;

    //The coarse route is measured as euclidean polyline length, but the search it is checked
    //against counts octile grid steps, and a grid path can be up to 1/cos(22.5 degrees) longer than
    //the straight line it follows. The upper bound is inflated by that worst case so an accept
    //stays sound under either metric.
    private const float OctileSafety = 1.0824f;

    private readonly ulong[] _fine;
    private readonly ulong[] _coarseAll;
    private readonly ulong[] _coarseAny;
    private readonly ushort[][] _landmarks;

    public int OriginX { get; }
    public int OriginY { get; }
    public int Width { get; }
    public int Height { get; }
    public int BlockSize { get; }
    public int CoarseWidth { get; }
    public int CoarseHeight { get; }
    public int ComponentCells { get; }
    public double BuildMillis { get; private set; }
    public double LandmarkMillis { get; private set; }
    public int LandmarkCount => _landmarks.Length;

    public long ApproximateBytes =>
        (long)_fine.Length * 8 + (long)(_coarseAll.Length + _coarseAny.Length) * 8 +
        (long)_landmarks.Length * Width * Height * 2;

    private PathBoundModel(int originX, int originY, int width, int height, int blockSize, int componentCells)
    {
        OriginX = originX;
        OriginY = originY;
        Width = width;
        Height = height;
        BlockSize = blockSize;
        ComponentCells = componentCells;
        CoarseWidth = (width + blockSize - 1) / blockSize;
        CoarseHeight = (height + blockSize - 1) / blockSize;
        _fine = new ulong[((long)width * height + 63) / 64];
        _coarseAll = new ulong[((long)CoarseWidth * CoarseHeight + 63) / 64];
        _coarseAny = new ulong[((long)CoarseWidth * CoarseHeight + 63) / 64];
        _landmarks = [];
    }

    private PathBoundModel(PathBoundModel source, ushort[][] landmarks)
    {
        OriginX = source.OriginX;
        OriginY = source.OriginY;
        Width = source.Width;
        Height = source.Height;
        BlockSize = source.BlockSize;
        ComponentCells = source.ComponentCells;
        CoarseWidth = source.CoarseWidth;
        CoarseHeight = source.CoarseHeight;
        BuildMillis = source.BuildMillis;
        LandmarkMillis = source.LandmarkMillis;
        _fine = source._fine;
        _coarseAll = source._coarseAll;
        _coarseAny = source._coarseAny;
        _landmarks = landmarks;
    }

    /// <summary>
    /// Floods from the detonator to find the cells the explosives can actually reach, then builds
    /// every structure over that component only. Anything walkable but disconnected is left out, so
    /// the model is both smaller and incapable of claiming a path across a gap.
    /// </summary>
    public static PathBoundModel Build(
        Func<Vector2, bool> isWalkable,
        Vector2 detonator,
        float maxReach,
        int areaWidth,
        int areaHeight,
        int blockSize,
        int landmarkCount)
    {
        var sw = Stopwatch.StartNew();
        blockSize = Math.Max(2, blockSize);
        if (FindNearestWalkable(isWalkable, detonator, areaWidth, areaHeight) is not { } start)
        {
            return null;
        }

        var (startX, startY) = start;

        var visited = new ulong[((long)areaWidth * areaHeight + 63) / 64];
        var component = new List<int>(1 << 16);
        var queue = new Queue<int>();
        var reachSquared = maxReach * maxReach;
        var startIndex = startY * areaWidth + startX;
        SetBit(visited, startIndex);
        queue.Enqueue(startIndex);
        int minX = startX, maxX = startX, minY = startY, maxY = startY;
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % areaWidth;
            var y = index / areaWidth;
            component.Add(index);
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
                    if (nx < 0 || ny < 0 || nx >= areaWidth || ny >= areaHeight)
                    {
                        continue;
                    }

                    var neighbor = ny * areaWidth + nx;
                    if (GetBit(visited, neighbor))
                    {
                        continue;
                    }

                    SetBit(visited, neighbor);
                    var dx = nx - detonator.X;
                    var dy = ny - detonator.Y;
                    if (dx * dx + dy * dy <= reachSquared && isWalkable(new Vector2(nx, ny)))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        var model = new PathBoundModel(minX, minY, maxX - minX + 1, maxY - minY + 1, blockSize, component.Count);
        foreach (var index in component)
        {
            var x = index % areaWidth - minX;
            var y = index / areaWidth - minY;
            SetBit(model._fine, y * model.Width + x);
        }

        model.BuildCoarseGrids();
        model.BuildMillis = sw.Elapsed.TotalMilliseconds;
        if (landmarkCount <= 0)
        {
            return model;
        }

        var landmarkStart = Stopwatch.GetTimestamp();
        var landmarks = model.BuildLandmarks(landmarkCount, (startX - minX, startY - minY));

        var result = new PathBoundModel(model, landmarks)
        {
            LandmarkMillis = (Stopwatch.GetTimestamp() - landmarkStart) * 1000.0 / Stopwatch.Frequency,
        };
        return result;
    }

    private static (int X, int Y)? FindNearestWalkable(Func<Vector2, bool> isWalkable, Vector2 point, int width, int height)
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
                    if (x >= 0 && y >= 0 && x < width && y < height && isWalkable(new Vector2(x, y)))
                    {
                        return (x, y);
                    }
                }
            }
        }

        return null;
    }

    private void BuildCoarseGrids()
    {
        for (var by = 0; by < CoarseHeight; by++)
        {
            for (var bx = 0; bx < CoarseWidth; bx++)
            {
                var all = true;
                var any = false;
                for (var y = by * BlockSize; y < (by + 1) * BlockSize; y++)
                {
                    for (var x = bx * BlockSize; x < (bx + 1) * BlockSize; x++)
                    {
                        //A block running off the edge of the model cannot be conservative-open: the
                        //cells past the edge are not known to be walkable.
                        var open = x < Width && y < Height && GetBit(_fine, y * Width + x);
                        all &= open;
                        any |= open;
                    }
                }

                var index = by * CoarseWidth + bx;
                if (all)
                {
                    SetBit(_coarseAll, index);
                }

                if (any)
                {
                    SetBit(_coarseAny, index);
                }
            }
        }
    }

    /// <summary>
    /// Farthest-point selection, then one full Dijkstra per landmark.
    /// <para>
    /// The selection runs on the coarse blocks rather than the cells - it only has to spread the
    /// landmarks around the component, and a block-resolution answer does that just as well for a
    /// fraction of the work. That leaves the K fine Dijkstras independent of each other, so they run
    /// in parallel: the first version did them one after another because each one chose the next
    /// landmark, and took two seconds, which is most of a search.
    /// </para>
    /// </summary>
    private ushort[][] BuildLandmarks(int count, (int X, int Y) seed)
    {
        var sources = ChooseLandmarkCells(count, seed);
        var landmarks = new ushort[sources.Count][];

        //Each Dijkstra needs a distance array the size of the component - a few megabytes, straight
        //to the large object heap. Pooled and capped rather than one per landmark, so the peak is a
        //handful of buffers instead of sixteen, and repeated area loads do not fragment the heap.
        var scratch = new ConcurrentBag<float[]>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
        Parallel.For(0, sources.Count, options, i =>
        {
            if (!scratch.TryTake(out var distances))
            {
                distances = new float[Width * Height];
            }

            Dijkstra(sources[i], distances);
            var stored = new ushort[Width * Height];
            for (var j = 0; j < stored.Length; j++)
            {
                var d = distances[j];
                stored[j] = float.IsPositiveInfinity(d)
                    ? Unreachable
                    : (ushort)Math.Min(Unreachable - 1, (int)MathF.Round(d * LandmarkScale));
            }

            landmarks[i] = stored;
            scratch.Add(distances);
        });

        return landmarks;
    }

    private List<(int X, int Y)> ChooseLandmarkCells(int count, (int X, int Y) seed)
    {
        var chosen = new List<(int X, int Y)>(count);
        var best = new float[CoarseWidth * CoarseHeight];
        Array.Fill(best, float.PositiveInfinity);
        var distances = new float[CoarseWidth * CoarseHeight];
        var current = (X: seed.X / BlockSize, Y: seed.Y / BlockSize);
        for (var i = 0; i < count; i++)
        {
            if (FindCellInBlock(current) is { } cell)
            {
                chosen.Add(cell);
            }

            CoarseDijkstra(current, distances);
            var farthest = -1;
            var farthestDistance = -1f;
            for (var j = 0; j < best.Length; j++)
            {
                if (distances[j] < best[j])
                {
                    best[j] = distances[j];
                }

                var d = best[j];
                if (!float.IsPositiveInfinity(d) && d > farthestDistance)
                {
                    farthestDistance = d;
                    farthest = j;
                }
            }

            if (farthest < 0)
            {
                break;
            }

            current = (farthest % CoarseWidth, farthest / CoarseWidth);
        }

        if (chosen.Count == 0)
        {
            chosen.Add(seed);
        }

        return chosen;
    }

    private (int X, int Y)? FindCellInBlock((int X, int Y) block)
    {
        for (var y = block.Y * BlockSize; y < (block.Y + 1) * BlockSize; y++)
        {
            for (var x = block.X * BlockSize; x < (block.X + 1) * BlockSize; x++)
            {
                if (Walkable(x, y))
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    private void CoarseDijkstra((int X, int Y) source, float[] distances)
    {
        Array.Fill(distances, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        var sourceIndex = source.Y * CoarseWidth + source.X;
        distances[sourceIndex] = 0;
        queue.Enqueue(sourceIndex, 0);
        while (queue.TryDequeue(out var index, out var distance))
        {
            if (distance > distances[index])
            {
                continue;
            }

            var x = index % CoarseWidth;
            var y = index / CoarseWidth;
            for (var i = 0; i < NeighborX.Length; i++)
            {
                var nx = x + NeighborX[i];
                var ny = y + NeighborY[i];
                if ((uint)nx >= (uint)CoarseWidth || (uint)ny >= (uint)CoarseHeight ||
                    !GetBit(_coarseAny, (long)ny * CoarseWidth + nx))
                {
                    continue;
                }

                var next = distance + (NeighborX[i] != 0 && NeighborY[i] != 0 ? Sqrt2 : 1f);
                var neighbor = ny * CoarseWidth + nx;
                if (next >= distances[neighbor])
                {
                    continue;
                }

                distances[neighbor] = next;
                queue.Enqueue(neighbor, next);
            }
        }
    }

    private void Dijkstra((int X, int Y) source, float[] distances)
    {
        Array.Fill(distances, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        var sourceIndex = source.Y * Width + source.X;
        distances[sourceIndex] = 0;
        queue.Enqueue(sourceIndex, 0);
        while (queue.TryDequeue(out var index, out var distance))
        {
            if (distance > distances[index])
            {
                continue;
            }

            var x = index % Width;
            var y = index / Width;
            for (var i = 0; i < NeighborX.Length; i++)
            {
                var nx = x + NeighborX[i];
                var ny = y + NeighborY[i];
                if (!Walkable(nx, ny) || !DiagonalAllowed(x, y, NeighborX[i], NeighborY[i]))
                {
                    continue;
                }

                var next = distance + (NeighborX[i] != 0 && NeighborY[i] != 0 ? Sqrt2 : 1f);
                var neighbor = ny * Width + nx;
                if (next >= distances[neighbor])
                {
                    continue;
                }

                distances[neighbor] = next;
                queue.Enqueue(neighbor, next);
            }
        }
    }

    private static readonly int[] NeighborX = [1, 1, 0, -1, -1, -1, 0, 1];
    private static readonly int[] NeighborY = [0, 1, 1, 1, 0, -1, -1, -1];

    private static void SetBit(ulong[] bits, long index) => bits[index >> 6] |= 1UL << (int)(index & 63);

    private static bool GetBit(ulong[] bits, long index) => (bits[index >> 6] & (1UL << (int)(index & 63))) != 0;

    /// <summary>Model-local coordinates.</summary>
    private bool Walkable(int x, int y)
    {
        return (uint)x < (uint)Width && (uint)y < (uint)Height && GetBit(_fine, (long)y * Width + x);
    }

    private bool DiagonalAllowed(int x, int y, int ox, int oy)
    {
        return ox == 0 || oy == 0 || (Walkable(x + ox, y) && Walkable(x, y + oy));
    }

    /// <summary>
    /// In the reachable component, not merely inside its bounding box. The soundness cross-checks
    /// need this: a walkable cell in a disconnected pocket is unreachable by definition, and the
    /// full-area reference search does not know that.
    /// </summary>
    public bool IsInComponent(Vector2 point)
    {
        var (x, y) = ToLocal(point);
        return Walkable(x, y);
    }

    public bool Contains(Vector2 point)
    {
        var x = (int)MathF.Floor(point.X) - OriginX;
        var y = (int)MathF.Floor(point.Y) - OriginY;
        return (uint)x < (uint)Width && (uint)y < (uint)Height;
    }

    private (int X, int Y) ToLocal(Vector2 point)
    {
        return ((int)MathF.Floor(point.X) - OriginX, (int)MathF.Floor(point.Y) - OriginY);
    }

    /// <summary>
    /// The exact line test again, over the bitboard instead of the placement delegate. Same walk,
    /// same corner rule - the point is to measure what tier one really costs.
    /// </summary>
    public bool LineIsClear(Vector2 from, Vector2 to)
    {
        var (x, y) = ToLocal(from);
        var (endX, endY) = ToLocal(to);
        if (!Walkable(x, y))
        {
            return false;
        }

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0;
        var stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        var tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / dx);
        var tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / dy);
        var localFromX = from.X - OriginX;
        var localFromY = from.Y - OriginY;
        var tMaxX = stepX == 0
            ? float.PositiveInfinity
            : (stepX > 0 ? x + 1 - localFromX : localFromX - x) / MathF.Abs(dx);
        var tMaxY = stepY == 0
            ? float.PositiveInfinity
            : (stepY > 0 ? y + 1 - localFromY : localFromY - y) / MathF.Abs(dy);
        var guard = 4 * (Math.Abs(endX - x) + Math.Abs(endY - y)) + 8;
        while ((x != endX || y != endY) && guard-- > 0)
        {
            if (stepX != 0 && stepY != 0 && MathF.Abs(tMaxX - tMaxY) < 1e-6f)
            {
                if (!Walkable(x + stepX, y) || !Walkable(x, y + stepY))
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

            if (!Walkable(x, y))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The fine search, over the bitboard with flat arrays and a generation stamp instead of
    /// dictionaries. Ground truth for the bounds, and the honest cost of the expensive tier.
    /// </summary>
    public GeodesicResult AStar(Vector2 from, Vector2 to, float budget, Workspace workspace, out int expanded)
    {
        return AStar(from, to, budget, workspace, workspace.NodeLimit, out expanded);
    }

    /// <summary>
    /// As above, but giving up after <paramref name="nodeCap"/> expansions. An aborted search is not
    /// an answer: the caller decides what to do with it, and the only safe choice is to treat it as
    /// a reject.
    /// </summary>
    public GeodesicResult AStar(Vector2 from, Vector2 to, float budget, Workspace workspace, int nodeCap, out int expanded)
    {
        expanded = 0;
        var (startX, startY) = ToLocal(from);
        var (goalX, goalY) = ToLocal(to);
        if (!Walkable(startX, startY) || !Walkable(goalX, goalY))
        {
            return GeodesicResult.Unreachable;
        }

        if (startX == goalX && startY == goalY)
        {
            return GeodesicResult.Reachable;
        }

        var g = workspace.Fine;
        var stamp = workspace.FineStamp;
        var generation = ++workspace.FineGeneration;
        var queue = workspace.FineQueue;
        queue.Clear();
        var startIndex = startY * Width + startX;
        g[startIndex] = 0;
        stamp[startIndex] = generation;
        queue.Enqueue(startIndex, Octile(startX, startY, goalX, goalY));
        while (queue.TryDequeue(out var index, out var f))
        {
            if (f > budget)
            {
                return GeodesicResult.Unreachable;
            }

            if (++expanded > nodeCap)
            {
                return GeodesicResult.Aborted;
            }

            var x = index % Width;
            var y = index / Width;
            var cost = g[index];
            for (var i = 0; i < NeighborX.Length; i++)
            {
                var ox = NeighborX[i];
                var oy = NeighborY[i];
                var nx = x + ox;
                var ny = y + oy;
                if (!Walkable(nx, ny) || !DiagonalAllowed(x, y, ox, oy))
                {
                    continue;
                }

                var tentative = cost + (ox != 0 && oy != 0 ? Sqrt2 : 1f);
                if (tentative > budget)
                {
                    continue;
                }

                if (nx == goalX && ny == goalY)
                {
                    return GeodesicResult.Reachable;
                }

                var neighbor = ny * Width + nx;
                if (stamp[neighbor] == generation && g[neighbor] <= tentative)
                {
                    continue;
                }

                var estimate = tentative + Octile(nx, ny, goalX, goalY);
                if (estimate > budget)
                {
                    continue;
                }

                stamp[neighbor] = generation;
                g[neighbor] = tentative;
                queue.Enqueue(neighbor, estimate);
            }
        }

        return GeodesicResult.Unreachable;
    }

    /// <summary>
    /// Two-sided coarse bound. A route across conservative blocks is a real walkable route, so its
    /// length is an upper bound and proves an accept; a route across optimistic blocks is the most
    /// generous the terrain could possibly allow, so its length (less one block at each end, for
    /// where inside the end blocks the points actually sit) is a lower bound and proves a reject.
    /// </summary>
    public BoundVerdict Coarse(Vector2 from, Vector2 to, float budget, Workspace workspace)
    {
        if (!Contains(from) || !Contains(to))
        {
            return BoundVerdict.Outside;
        }

        var (fromX, fromY) = ToLocal(from);
        var (toX, toY) = ToLocal(to);
        var startBlock = (X: fromX / BlockSize, Y: fromY / BlockSize);
        var goalBlock = (X: toX / BlockSize, Y: toY / BlockSize);

        if (GetBit(_coarseAll, (long)startBlock.Y * CoarseWidth + startBlock.X) &&
            GetBit(_coarseAll, (long)goalBlock.Y * CoarseWidth + goalBlock.X))
        {
            var entry = CentreDistance(fromX, fromY, startBlock);
            var exit = CentreDistance(toX, toY, goalBlock);
            var remaining = budget - entry - exit;
            if (remaining >= 0)
            {
                var length = CoarseRoute(startBlock, goalBlock, _coarseAll, remaining / OctileSafety, workspace);
                if ((length + entry + exit) * OctileSafety <= budget)
                {
                    return BoundVerdict.Accept;
                }
            }
        }

        //The slack is what the endpoints can be worth inside their own blocks: at most one block of
        //travel at each end that the coarse route already counted.
        var slack = 2f * BlockSize;
        var optimistic = CoarseRoute(startBlock, goalBlock, _coarseAny, budget + slack, workspace);
        if (float.IsPositiveInfinity(optimistic) || optimistic - slack > budget)
        {
            return BoundVerdict.Reject;
        }

        return BoundVerdict.Inconclusive;
    }

    private float CentreDistance(int x, int y, (int X, int Y) block)
    {
        var cx = block.X * BlockSize + (BlockSize - 1) / 2f;
        var cy = block.Y * BlockSize + (BlockSize - 1) / 2f;
        var dx = x - cx;
        var dy = y - cy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Centre-to-centre route length in cell units, or infinity if none fits the budget.</summary>
    private float CoarseRoute((int X, int Y) start, (int X, int Y) goal, ulong[] blocks, float budget, Workspace workspace)
    {
        if (start == goal)
        {
            return 0;
        }

        var g = workspace.Coarse;
        var stamp = workspace.CoarseStamp;
        var generation = ++workspace.CoarseGeneration;
        var queue = workspace.CoarseQueue;
        queue.Clear();
        var startIndex = start.Y * CoarseWidth + start.X;
        g[startIndex] = 0;
        stamp[startIndex] = generation;
        queue.Enqueue(startIndex, CoarseOctile(start.X, start.Y, goal.X, goal.Y));
        while (queue.TryDequeue(out var index, out var f))
        {
            if (f > budget)
            {
                return float.PositiveInfinity;
            }

            var x = index % CoarseWidth;
            var y = index / CoarseWidth;
            var cost = g[index];
            for (var i = 0; i < NeighborX.Length; i++)
            {
                var ox = NeighborX[i];
                var oy = NeighborY[i];
                var nx = x + ox;
                var ny = y + oy;
                if ((uint)nx >= (uint)CoarseWidth || (uint)ny >= (uint)CoarseHeight)
                {
                    continue;
                }

                if (!GetBit(blocks, (long)ny * CoarseWidth + nx))
                {
                    continue;
                }

                //Same corner rule as the fine grid, one level up.
                if (ox != 0 && oy != 0 &&
                    (!GetBit(blocks, (long)y * CoarseWidth + (x + ox)) || !GetBit(blocks, (long)(y + oy) * CoarseWidth + x)))
                {
                    continue;
                }

                var tentative = cost + (ox != 0 && oy != 0 ? Sqrt2 * BlockSize : BlockSize);
                if (tentative > budget)
                {
                    continue;
                }

                if (nx == goal.X && ny == goal.Y)
                {
                    return tentative;
                }

                var neighbor = ny * CoarseWidth + nx;
                if (stamp[neighbor] == generation && g[neighbor] <= tentative)
                {
                    continue;
                }

                var estimate = tentative + CoarseOctile(nx, ny, goal.X, goal.Y);
                if (estimate > budget)
                {
                    continue;
                }

                stamp[neighbor] = generation;
                g[neighbor] = tentative;
                queue.Enqueue(neighbor, estimate);
            }
        }

        return float.PositiveInfinity;
    }

    /// <summary>
    /// Landmark bound. Two array reads per landmark: the difference of the two distances is a lower
    /// bound on the real one (triangle inequality) and their sum is the length of a real route
    /// through that landmark.
    /// </summary>
    public BoundVerdict Landmark(Vector2 from, Vector2 to, float budget)
    {
        if (_landmarks.Length == 0)
        {
            return BoundVerdict.Inconclusive;
        }

        if (!Contains(from) || !Contains(to))
        {
            return BoundVerdict.Outside;
        }

        var (fromX, fromY) = ToLocal(from);
        var (toX, toY) = ToLocal(to);
        var fromIndex = fromY * Width + fromX;
        var toIndex = toY * Width + toX;
        var lower = 0f;
        var upper = float.PositiveInfinity;
        foreach (var landmark in _landmarks)
        {
            var rawFrom = landmark[fromIndex];
            var rawTo = landmark[toIndex];
            if (rawFrom == Unreachable || rawTo == Unreachable)
            {
                //One end reachable from this landmark and the other not means separate components.
                if (rawFrom != rawTo)
                {
                    return BoundVerdict.Reject;
                }

                continue;
            }

            var distanceFrom = rawFrom / LandmarkScale;
            var distanceTo = rawTo / LandmarkScale;
            lower = MathF.Max(lower, MathF.Abs(distanceFrom - distanceTo) - LandmarkRounding);
            upper = MathF.Min(upper, distanceFrom + distanceTo);
        }

        if (lower > budget)
        {
            return BoundVerdict.Reject;
        }

        return upper <= budget ? BoundVerdict.Accept : BoundVerdict.Inconclusive;
    }

    private static float Octile(int x, int y, int goalX, int goalY)
    {
        var dx = Math.Abs(x - goalX);
        var dy = Math.Abs(y - goalY);
        return dx > dy ? dx - dy + Sqrt2 * dy : dy - dx + Sqrt2 * dx;
    }

    private float CoarseOctile(int x, int y, int goalX, int goalY)
    {
        return Octile(x, y, goalX, goalY) * BlockSize;
    }

    /// <summary>Per-thread scratch. Generation stamps mean nothing has to be cleared between queries.</summary>
    public sealed class Workspace
    {
        private readonly int _fineCells;
        private float[] _fine;
        private int[] _fineStamp;

        public Workspace(PathBoundModel model, int nodeLimit)
        {
            _fineCells = model.Width * model.Height;
            Coarse = new float[model.CoarseWidth * model.CoarseHeight];
            CoarseStamp = new int[model.CoarseWidth * model.CoarseHeight];
            NodeLimit = nodeLimit;
        }

        //Two arrays the size of the whole component - about 5 MB together, per thread. With the
        //undecided budget at zero the search never runs a fine query at all, so they are only
        //allocated by the code that actually needs them: validating a finished path, and the
        //diagnostics.
        public float[] Fine => _fine ??= new float[_fineCells];
        public int[] FineStamp => _fineStamp ??= new int[_fineCells];
        public int FineGeneration;
        public PriorityQueue<int, float> FineQueue { get; } = new();
        public float[] Coarse { get; }
        public int[] CoarseStamp { get; }
        public int CoarseGeneration;
        public PriorityQueue<int, float> CoarseQueue { get; } = new();
        public int NodeLimit { get; }
    }
}

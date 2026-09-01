using System;
using System.Collections.Generic;

namespace TruckHub.Services;

/// <summary>
/// Clean-room A* over RoutingGraph - informed by (not copied from) the publicly-documented design
/// of Rares-Muntean/TruckNav-Sim's own routing engine (GPL-3, not reused here): search state is
/// keyed by (node, cameFromNode) rather than just node, which is what makes a hard U-turn check
/// possible without extra bookkeeping - you always know which node you'd be reversing back onto.
///
/// Deliberately simpler than TruckNav-Sim in scope: no maneuver-type/exit-number/DLC modeling (this
/// only ever routes over roads the local install can already drive, and only needs a visual path,
/// not turn-by-turn narration) - see Assets\RouteGraph\GENERATION.md for the full reasoning.
/// </summary>
public static class RoutingService
{
    private readonly record struct SearchState(int Node, int PrevNode);

    /// <summary>Finds the nearest graph node to a raw world (x, z) position - used both to resolve
    /// the truck's live position into a start node, and as a fallback if a destination can't be
    /// resolved via the exact company lookup.</summary>
    public static int FindNearestNode(RoutingGraph graph, float x, float z)
    {
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < graph.NodeX.Length; i++)
        {
            var dx = graph.NodeX[i] - x;
            var dz = graph.NodeZ[i] - z;
            var distSq = dx * dx + dz * dz;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>Resolves a job's destination to an exact graph node via the packed company table,
    /// matching ATS telemetry's own lowercase city/company tokens directly - no fuzzy search.</summary>
    public static int? ResolveCompanyNode(RoutingGraph graph, string cityToken, string companyToken)
    {
        foreach (var company in graph.Companies)
        {
            if (string.Equals(company.CityToken, cityToken, StringComparison.OrdinalIgnoreCase)
                && string.Equals(company.CompanyToken, companyToken, StringComparison.OrdinalIgnoreCase))
            {
                return company.NodeIndex;
            }
        }

        return null;
    }

    /// <summary>Finds a route from startNode to endNode, returning the sequence of node indices to
    /// visit (inclusive of both ends), or null if no path exists. Pure CPU work over an in-memory
    /// graph - callers should run this on a background thread.</summary>
    public static List<int>? FindRoute(RoutingGraph graph, int startNode, int endNode)
    {
        if (startNode == endNode)
        {
            return new List<int> { startNode };
        }

        var destX = graph.NodeX[endNode];
        var destZ = graph.NodeZ[endNode];

        // Edge weights are travel TIME in seconds now, not distance (see GENERATION.md - routes
        // were preferring winding back roads over the interstate because a shorter route isn't
        // necessarily a faster one). The heuristic has to estimate time too, in the same units, or
        // its magnitude is meaningless against the actual edge weights - straight-line distance
        // divided by an optimistic top speed (rather than the real speed limit for wherever `node`
        // happens to be, which would mean per-node country/road-class lookups this only needs to
        // approximate) keeps it a legitimate, if generous, lower-bound time estimate.
        //
        // Weighted A* (Snyder's technique, not truly admissible) on top of that - straight-line
        // distance underestimates real road distance enough on its own that a plain A* search on
        // ATS-scale cross-country jobs (some genuinely exceed 2,500km) explores far more of the
        // graph than it needs to before converging. A modest overestimate trades a small amount of
        // route optimality for dramatically fewer iterations - an easy call given this only ever
        // needs a reasonable-looking visual route, not a provably shortest one.
        const float heuristicWeight = 1.4f;
        const float optimisticTopSpeedMetersPerSecond = 80f * 0.44704f; // 80 mph - fastest freeway limit seen in-map.
        float Heuristic(int node)
        {
            var dx = graph.NodeX[node] - destX;
            var dz = graph.NodeZ[node] - destZ;
            return MathF.Sqrt(dx * dx + dz * dz) / optimisticTopSpeedMetersPerSecond * heuristicWeight;
        }

        var gScore = new Dictionary<SearchState, float>();
        var cameFrom = new Dictionary<SearchState, SearchState>();
        var visited = new HashSet<SearchState>();

        var startState = new SearchState(startNode, -1);
        gScore[startState] = 0;

        var open = new PriorityQueue<SearchState, float>();
        open.Enqueue(startState, Heuristic(startNode));

        // Generous but bounded - a search that can't reach the destination at all (e.g. genuinely
        // disconnected regions) should give up rather than spin. Confirmed too low at 2,000,000: a
        // real 2,684km ATS job (nearly the length of the whole map) exhausted that budget and
        // returned no route at all - raised well above anything a legitimate in-map job should need.
        const int maxIterations = 20_000_000;
        var iterations = 0;

        while (open.Count > 0)
        {
            if (++iterations > maxIterations)
            {
                return null;
            }

            var current = open.Dequeue();
            if (visited.Contains(current))
            {
                continue;
            }
            visited.Add(current);

            if (current.Node == endNode)
            {
                return ReconstructPath(cameFrom, current);
            }

            var currentG = gScore.GetValueOrDefault(current, float.MaxValue);

            foreach (var edge in graph.Adjacency[current.Node])
            {
                // Hard U-turn prevention: never immediately backtrack onto the node we just came
                // from - the single biggest source of nonsense routes without it.
                if (edge.To == current.PrevNode)
                {
                    continue;
                }

                var next = new SearchState(edge.To, current.Node);
                var tentativeG = currentG + edge.Weight;
                if (tentativeG < gScore.GetValueOrDefault(next, float.MaxValue))
                {
                    gScore[next] = tentativeG;
                    cameFrom[next] = current;
                    open.Enqueue(next, tentativeG + Heuristic(edge.To));
                }
            }
        }

        return null;
    }

    private static List<int> ReconstructPath(Dictionary<SearchState, SearchState> cameFrom, SearchState end)
    {
        var path = new List<int> { end.Node };
        var current = end;
        while (cameFrom.TryGetValue(current, out var prev))
        {
            path.Add(prev.Node);
            current = prev;
        }

        path.Reverse();
        return path;
    }
}

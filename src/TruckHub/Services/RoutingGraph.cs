using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>A company's exact resolved destination node.</summary>
public sealed record CompanyDestination(string CityToken, string CompanyToken, int NodeIndex);

/// <summary>
/// The routable road-network graph (see Assets\RouteGraph\GENERATION.md for how it was built), read
/// straight out of the embedded zip - no disk extraction, matching the earlier lesson from the map
/// tiles that WebView2's fetch() had trouble with embedded/zipped data over a virtual host. That
/// problem doesn't apply here at all: this data is only ever consumed by C# (RoutingService), never
/// fetched by the WebView2 page, so reading directly from the zip archive is simple and safe.
///
/// Loaded when the GPS map window opens or LAN Mode starts (GpsMapViewModel.PrewarmRoutingGraph) and
/// kept until that view model is disposed - never at app startup, so a session that never touches
/// the GPS never pays for it. Roughly 2.3 million nodes / 3.0 million edges for ATS (2.8 million /
/// 3.7 million for ETS2).
///
/// Edges are stored as compressed-sparse-row arrays - EdgeOffsets[n] .. EdgeOffsets[n + 1] is node
/// n's slice of EdgeTargets / EdgeWeights - rather than one List per node. At this size the
/// per-node list objects cost roughly four times the edge data itself (the old List&lt;RouteEdge&gt;[]
/// layout held about 226 MB of live heap for ATS and 283 MB for ETS2; this layout holds about
/// 51 MB and 65 MB), and the router only ever walks a node's edges in order, which a contiguous slice
/// does just as well. Within a node the edges keep the order they have in edges.bin, so the router's
/// tie-breaking - and therefore every route it produces - is unchanged.
/// </summary>
public sealed class RoutingGraph
{
    private const string AtsResourceName = "TruckHub.RouteGraph.route-graph.zip";

    // Not yet used by any live code path - see GameMapProfile. Embedded now so the data exists,
    // read here so loading it is a one-line change once GameMapProfile.Ets2Enabled flips to true.
    private const string Ets2ResourceName = "TruckHub.RouteGraph.ets2-route-graph.zip";

    private const int NodeRecordBytes = 8;   // x, z as float32
    private const int EdgeRecordBytes = 16;  // from uint32, to uint32, weight float32, heading float32 (unused)

    // Records are read through a small fixed buffer (a multiple of both record sizes, and under the
    // 85,000-byte large-object threshold) instead of inflating each whole entry into one byte[] - the
    // edge table alone is ~48 MB, which would otherwise sit around as large-object garbage after load.
    private const int ReadBufferBytes = 65536;

    public float[] NodeX { get; }
    public float[] NodeZ { get; }

    /// <summary>Node n's edges are EdgeTargets / EdgeWeights indices EdgeOffsets[n] (inclusive) to
    /// EdgeOffsets[n + 1] (exclusive). Length is node count + 1.</summary>
    public int[] EdgeOffsets { get; }
    public int[] EdgeTargets { get; }
    public float[] EdgeWeights { get; }
    public int EdgeCount => EdgeTargets.Length;

    public IReadOnlyList<CompanyDestination> Companies { get; }

    private RoutingGraph(float[] nodeX, float[] nodeZ, int[] edgeOffsets, int[] edgeTargets, float[] edgeWeights,
        List<CompanyDestination> companies)
    {
        NodeX = nodeX;
        NodeZ = nodeZ;
        EdgeOffsets = edgeOffsets;
        EdgeTargets = edgeTargets;
        EdgeWeights = edgeWeights;
        Companies = companies;
    }

    /// <summary>Loads the graph for the given game - defaults to ATS, the only profile any live code
    /// path actually requests today (see GameMapProfile). Ets2 is loadable here (for standalone
    /// sanity-checking) even while GameMapProfile.Ets2Enabled is false.</summary>
    public static RoutingGraph Load(SimGame game = SimGame.Ats)
    {
        var resourceName = game == SimGame.Ets2 ? Ets2ResourceName : AtsResourceName;
        var assembly = Assembly.GetExecutingAssembly();
        using var zipStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var nodesEntry = GetEntry(archive, "nodes.bin");
        var edgesEntry = GetEntry(archive, "edges.bin");

        var nodeCount = checked((int)(nodesEntry.Length / NodeRecordBytes));
        var nodeX = new float[nodeCount];
        var nodeZ = new float[nodeCount];
        var buffer = new byte[ReadBufferBytes];
        var nodeIndex = 0;
        using (var nodes = nodesEntry.Open())
        {
            int got;
            while (nodeIndex < nodeCount && (got = ReadRecords(nodes, buffer, NodeRecordBytes)) > 0)
            {
                for (var offset = 0; offset + NodeRecordBytes <= got && nodeIndex < nodeCount; offset += NodeRecordBytes, nodeIndex++)
                {
                    nodeX[nodeIndex] = BitConverter.ToSingle(buffer, offset);
                    nodeZ[nodeIndex] = BitConverter.ToSingle(buffer, offset + 4);
                }
            }
        }

        var edgeCount = checked((int)(edgesEntry.Length / EdgeRecordBytes));

        // Pass 1: count each node's outgoing edges (stored shifted by one so the prefix sum below
        // leaves offsets[n] = where node n's slice starts).
        var edgeOffsets = new int[nodeCount + 1];
        ForEachEdge(edgesEntry, buffer, edgeCount, nodeCount, (from, _, _) => edgeOffsets[from + 1]++);
        for (var i = 0; i < nodeCount; i++)
        {
            edgeOffsets[i + 1] += edgeOffsets[i];
        }

        // Pass 2: place every edge into its node's slice in file order (a stable counting sort), using
        // edgeOffsets[from] as that node's write cursor - which leaves each entry holding its own
        // slice END (= the next node's start), fixed up by the shift below.
        var edgeTargets = new int[edgeCount];
        var edgeWeights = new float[edgeCount];
        ForEachEdge(edgesEntry, buffer, edgeCount, nodeCount, (from, to, weight) =>
        {
            var slot = edgeOffsets[from]++;
            edgeTargets[slot] = to;
            edgeWeights[slot] = weight;
        });
        for (var i = nodeCount; i > 0; i--)
        {
            edgeOffsets[i] = edgeOffsets[i - 1];
        }
        edgeOffsets[0] = 0;

        using var companiesStream = GetEntry(archive, "companies.json").Open();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var companies = JsonSerializer.Deserialize<List<CompanyDestinationDto>>(companiesStream, jsonOptions) ?? new();
        var companyList = new List<CompanyDestination>(companies.Count);
        foreach (var c in companies)
        {
            companyList.Add(new CompanyDestination(c.CityToken, c.CompanyToken, c.NodeIndex));
        }

        return new RoutingGraph(nodeX, nodeZ, edgeOffsets, edgeTargets, edgeWeights, companyList);
    }

    /// <summary>Streams the edge table once, handing each record's from / to / weight to `visit`.
    /// The heading field (offset 12) isn't used by the current router - see GENERATION.md.</summary>
    private static void ForEachEdge(ZipArchiveEntry edgesEntry, byte[] buffer, int edgeCount, int nodeCount,
        Action<int, int, float> visit)
    {
        using var edges = edgesEntry.Open();
        var seen = 0;
        int got;
        while (seen < edgeCount && (got = ReadRecords(edges, buffer, EdgeRecordBytes)) > 0)
        {
            for (var offset = 0; offset + EdgeRecordBytes <= got && seen < edgeCount; offset += EdgeRecordBytes, seen++)
            {
                var from = BitConverter.ToUInt32(buffer, offset);
                var to = BitConverter.ToUInt32(buffer, offset + 4);
                if (from >= (uint)nodeCount || to >= (uint)nodeCount)
                {
                    throw new InvalidDataException($"edges.bin record {seen} refers to a node outside the {nodeCount} in nodes.bin.");
                }

                visit((int)from, (int)to, BitConverter.ToSingle(buffer, offset + 8));
            }
        }

        if (seen != edgeCount)
        {
            throw new InvalidDataException($"edges.bin ended after {seen} of {edgeCount} records.");
        }
    }

    /// <summary>Fills `buffer` with as many whole records as the stream can supply right now (a
    /// DeflateStream may return short reads) and returns how many bytes that is - a multiple of
    /// `recordBytes`, or 0 at the end of the stream.</summary>
    private static int ReadRecords(Stream stream, byte[] buffer, int recordBytes)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                break;
            }
            filled += read;
        }

        return filled - filled % recordBytes;
    }

    private static ZipArchiveEntry GetEntry(ZipArchive archive, string entryName) =>
        archive.GetEntry(entryName)
        ?? throw new InvalidOperationException($"'{entryName}' not found in route-graph.zip.");

    private sealed record CompanyDestinationDto(string CityToken, string CompanyToken, int NodeIndex);
}

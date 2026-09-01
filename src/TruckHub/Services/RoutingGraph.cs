using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace TruckHub.Services;

/// <summary>One directed edge in the routable road network.</summary>
public readonly struct RouteEdge
{
    public readonly int To;
    public readonly float Weight;

    public RouteEdge(int to, float weight)
    {
        To = to;
        Weight = weight;
    }
}

/// <summary>A company's exact resolved destination node.</summary>
public sealed record CompanyDestination(string CityToken, string CompanyToken, int NodeIndex);

/// <summary>
/// The routable road-network graph (see Assets\RouteGraph\GENERATION.md for how it was built), read
/// straight out of the embedded zip - no disk extraction, matching the earlier lesson from the map
/// tiles that WebView2's fetch() had trouble with embedded/zipped data over a virtual host. That
/// problem doesn't apply here at all: this data is only ever consumed by C# (RoutingService), never
/// fetched by the WebView2 page, so reading directly from the zip archive is simple and safe.
///
/// Only ever loaded when a route is actually needed (an active job with a known destination) - not
/// at app startup, and not merely because the GPS map window is open, matching the whole feature's
/// "costs nothing until used" discipline. ~360,000 nodes / ~860,000 edges, a few MB in memory -
/// loaded once and kept for the GpsMapWindow's lifetime.
/// </summary>
public sealed class RoutingGraph
{
    private const string ResourceName = "TruckHub.RouteGraph.route-graph.zip";

    public float[] NodeX { get; }
    public float[] NodeZ { get; }
    public List<RouteEdge>[] Adjacency { get; }
    public IReadOnlyList<CompanyDestination> Companies { get; }

    private RoutingGraph(float[] nodeX, float[] nodeZ, List<RouteEdge>[] adjacency, List<CompanyDestination> companies)
    {
        NodeX = nodeX;
        NodeZ = nodeZ;
        Adjacency = adjacency;
        Companies = companies;
    }

    public static RoutingGraph Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var zipStream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var nodesBytes = ReadEntry(archive, "nodes.bin");
        var edgesBytes = ReadEntry(archive, "edges.bin");
        var companiesJson = System.Text.Encoding.UTF8.GetString(ReadEntry(archive, "companies.json"));

        var nodeCount = nodesBytes.Length / 8;
        var nodeX = new float[nodeCount];
        var nodeZ = new float[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            nodeX[i] = BitConverter.ToSingle(nodesBytes, i * 8);
            nodeZ[i] = BitConverter.ToSingle(nodesBytes, i * 8 + 4);
        }

        var adjacency = new List<RouteEdge>[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            adjacency[i] = new List<RouteEdge>();
        }

        var edgeCount = edgesBytes.Length / 16;
        for (var i = 0; i < edgeCount; i++)
        {
            var offset = i * 16;
            var from = BitConverter.ToUInt32(edgesBytes, offset);
            var to = BitConverter.ToUInt32(edgesBytes, offset + 4);
            var weight = BitConverter.ToSingle(edgesBytes, offset + 8);
            // heading (offset+12) isn't used by the current router - see GENERATION.md.
            adjacency[from].Add(new RouteEdge((int)to, weight));
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var companies = JsonSerializer.Deserialize<List<CompanyDestinationDto>>(companiesJson, jsonOptions) ?? new();
        var companyList = new List<CompanyDestination>(companies.Count);
        foreach (var c in companies)
        {
            companyList.Add(new CompanyDestination(c.CityToken, c.CompanyToken, c.NodeIndex));
        }

        return new RoutingGraph(nodeX, nodeZ, adjacency, companyList);
    }

    private static byte[] ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"'{entryName}' not found in route-graph.zip.");
        using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record CompanyDestinationDto(string CityToken, string CompanyToken, int NodeIndex);
}

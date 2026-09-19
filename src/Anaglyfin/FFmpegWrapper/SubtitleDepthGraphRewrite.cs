namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The outcome of putting a subtitle depth into a graph the server wrote: the merged graph, or the
/// one reason this graph is not the shape a depth stage can be placed in.
/// </summary>
/// <remarks>
/// There is no half-way outcome, and that is the point. A depth the wrapper cannot place is a depth
/// the viewer does not get, and the server's own graph - which it wrote, and which FFmpeg runs -
/// plays the film with flat subtitles exactly as it would have without the plugin. A half-rewritten
/// graph would be a playback that fails to start, or worse a playback that renders one subtitle
/// twice and calls it 3D.
/// </remarks>
public sealed record SubtitleDepthGraphRewrite
{
    /// <summary>Gets whether the depth was placed.</summary>
    public required bool IsApplied { get; init; }

    /// <summary>Gets the merged graph; empty for a refusal.</summary>
    public string Graph { get; init; } = string.Empty;

    /// <summary>Gets why the graph was left alone; empty when it was not.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The graph the rewrite wrote.</summary>
    /// <param name="graph">The merged graph.</param>
    /// <returns>The applied outcome.</returns>
    public static SubtitleDepthGraphRewrite Applied(string graph)
        => new() { IsApplied = true, Graph = graph };

    /// <summary>The graph the rewrite left alone.</summary>
    /// <param name="reason">Why the graph is not the shape a depth stage can be placed in.</param>
    /// <returns>The refusal outcome, carrying no graph.</returns>
    public static SubtitleDepthGraphRewrite NotSupported(string reason)
        => new() { IsApplied = false, Reason = reason };
}

using System;

namespace RayTree.Location;

internal static class UriBuilder
{
	private const string Schene = "ray";

	public static Uri BuildSystem(string queueType, string host, string systemId)
	{
		return new Uri($"{Schene}://{queueType}/{host}/systems/{systemId}", UriKind.Absolute);
	}

	public static Uri BuildNode(string queueType, string host, string systemId, string nodeId)
	{
		return new Uri($"{Schene}://{queueType}/{host}/systems/{systemId}/nodes/{nodeId}", UriKind.Absolute);
	}
}
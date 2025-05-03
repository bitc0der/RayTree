using System;

namespace RayTree.Location;

public sealed class NodeLocation : ILocation
{
	public SystemLocation SystemLocation { get; }

	public string NodeId { get; }

	public Uri Uri { get; }

	public NodeLocation(SystemLocation systemLocation, string nodeId)
	{
		SystemLocation = systemLocation ?? throw new ArgumentNullException(nameof(systemLocation));
		NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));

		Uri = UriBuilder.BuildNode(host: systemLocation.Host, systemId: systemLocation.SystemId, nodeId: nodeId);
	}

	public override string ToString() => Uri.ToString();
}
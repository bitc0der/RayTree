using System;

namespace RayTree.Location;

public sealed class SystemLocation : ILocation
{
	public string Host { get; }

	public string SystemId { get; }

	public Uri Uri { get; }

	public SystemLocation(string queueType, string host, string systemId)
	{
		Host = host ?? throw new ArgumentNullException(nameof(host));
		SystemId = systemId ?? throw new ArgumentNullException(nameof(systemId));

		Uri = UriBuilder.BuildSystem(queueType: queueType, host: host, systemId: systemId);
	}

	public static SystemLocation Create(string queueType, string systemId)
	{
		var hostName = Environment.MachineName;

		return new SystemLocation(queueType: queueType, host: hostName, systemId: systemId);
	}

	public override string ToString() => Uri.ToString();
}
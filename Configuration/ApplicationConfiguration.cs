using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SlumberProxy.Configuration;

public class ApplicationConfiguration
{
    [JsonProperty(Required = Required.DisallowNull)]
    public string DockerSocketUri { get; private set; } = "unix:///var/run/docker.sock";

    [JsonProperty(Required = Required.Always)]
    public string DockerContainerName { get; private set; } = "";

    /// <summary>
    /// Should the configuration be applied to all containers in the docker-compose group
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull)]
    public bool ApplyToDockerComposeGroup { get; private set; } = true;

    /// <summary>
    /// How long should we wait after reactivating a container before we try and access it?
    /// We delay <see cref="DockerContainerHealthCheck"/> until after this
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull)]
    public TimeSpan DockerContainerStartupTime { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Should the container be checked for health after reactivating
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull)]
    public bool DockerContainerHealthCheck { get; private set; } = false;

    /// <summary>
    /// How often (on reactivating) to check the health of the container
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull)]
    public TimeSpan DockerContainerHealthCheckInterval { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Should support <see cref="ApplicationType.Tcp"/> or <see cref="ApplicationType.Udp"/>
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull, ItemConverterType = typeof(StringEnumConverter))]
    public ApplicationType ApplicationType { get; private set; } = ApplicationType.Tcp;

    [JsonProperty(Required = Required.Always)]
    public ushort ListenPort { get; private set; } = 0;

    [JsonProperty(Required = Required.Always)]
    public ushort TargetPort { get; private set; } = 0;

    [JsonProperty(Required = Required.DisallowNull)]
    public string TargetAddress { get; private set; } = "127.0.0.1";

    /// <summary>
    /// Inactivity timeout for a container
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull)]
    public TimeSpan InactiveContainerTime { get; private set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often to check for inactivity
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull)]
    public TimeSpan InactivityCheckInterval { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What to do when the container is inactive
    /// </summary>
    [JsonProperty(Required = Required.DisallowNull, ItemConverterType = typeof(StringEnumConverter))]
    public InactiveContainerAction InactiveContainerAction { get; private set; } = InactiveContainerAction.Pause;
}

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ContainerSuspender.Configuration;

public class SuspenderConfiguration
{
    [JsonProperty(Required = Required.Always)]
    public List<ApplicationConfiguration> Applications { get; private set; } = new();
}

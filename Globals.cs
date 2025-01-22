using System;
using System.Collections.Generic;
using System.IO;
using SlumberProxy.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SlumberProxy
{
    public static class Globals
    {
        private static string configPath = "config.json";
        
        public static SuspenderConfiguration Configuration { get; private set; }
        public static ILoggerFactory LoggerFactory { get; private set; }
        public static Container.DockerManager DockerManager { get; private set; }

        public static void Setup(List<string> args)
        {
            if (args.Contains("-h") || args.Contains("--help"))
            {
                Console.WriteLine("Usage: " + System.AppDomain.CurrentDomain.FriendlyName + " [config-file] [-h | --help] [-v | --verbose]");
                return;
            }

            var logLevel = LogLevel.Information;

            if (args.Contains("-v") || args.Contains("--verbose"))
            {
                logLevel = LogLevel.Trace;
                args.Remove("-v");
                args.Remove("--verbose");
            }

            if (args.Count > 0)
                configPath = string.Join(" ", args);

            if (!File.Exists(configPath))
                throw new FileNotFoundException($"Config file '{configPath}' not found.");

            ParseConfigFile();

            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            {
                builder
                    .ClearProviders()
                    .AddConsole()
                    .SetMinimumLevel(logLevel);
            });

            DockerManager = new Container.DockerManager();
        }

        private static void ParseConfigFile()
        {
            var configuration = JsonConvert.DeserializeObject<SuspenderConfiguration>(File.ReadAllText(configPath), new JsonSerializerSettings());
            Configuration = configuration ?? throw new Exception($"Failed to parse config file '{configPath}'.");
        }

        public static void Dispose()
        {
            DockerManager?.Dispose();
            LoggerFactory?.Dispose();
        }
    }
}

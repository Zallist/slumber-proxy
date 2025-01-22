using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerSuspender.Container
{
    public class DockerManager : IDisposable
    {
        private class EventMonitor(DockerClient dockerClient) : IProgress<Message>, IDisposable
        {
            private readonly CancellationTokenSource cancellationTokenSource = new();
            private readonly DockerClient dockerClient = dockerClient;

            public event Action<Message> OnAnyEvent;

            public void Setup() => _ = Task.Factory.StartNew(
                async () => await dockerClient.System.MonitorEventsAsync(new(), this, cancellationTokenSource.Token),
                cancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            void IProgress<Message>.Report(Message value) => OnAnyEvent?.Invoke(value);

            public void Dispose()
            {
                cancellationTokenSource?.Cancel();
                cancellationTokenSource?.Dispose();
            }
        }

        private readonly Dictionary<string, DockerClient> uriToClient = [];
        private readonly Dictionary<DockerClient, EventMonitor> clientToMonitor = [];

        public DockerClient GetClient(string uri)
        {
            lock (uriToClient)
            {
                if (!uriToClient.TryGetValue(uri, out var client))
                {
                    client = new DockerClientConfiguration(new Uri(uri)).CreateClient();
                    uriToClient.TryAdd(uri, client);
                }

                return client;
            }
        }

        private EventMonitor GetEventMonitor(DockerClient client)
        {
            var isNew = false;
            EventMonitor monitor;

            lock (clientToMonitor)
            {
                if (!clientToMonitor.TryGetValue(client, out monitor))
                {
                    isNew = true;
                    monitor = new EventMonitor(client);
                    clientToMonitor.TryAdd(client, monitor);
                }
            }

            if (isNew)
                monitor.Setup();

            return monitor;
        }

        public void MonitorForAnyEvents(DockerClient client, Action<Message> onAnyEvent)
        {
            var monitor = GetEventMonitor(client);
            monitor.OnAnyEvent += onAnyEvent;
        }

        public void Dispose()
        {
            foreach (var monitor in clientToMonitor.Values)
                monitor.Dispose();

            foreach (var client in uriToClient.Values)
                client.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}

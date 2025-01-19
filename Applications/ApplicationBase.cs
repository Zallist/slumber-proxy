using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContainerSuspender.Configuration;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace ContainerSuspender.Applications;

public abstract class ApplicationBase : IDisposable
{
    protected DateTime lastActivity = DateTime.UtcNow;
    protected readonly DockerClient dockerClient;
    protected readonly ApplicationConfiguration configuration;
    protected readonly ILogger<ApplicationBase> logger;

    protected bool containerIsInactiveState = false;

    public ApplicationBase(ApplicationConfiguration configuration)
    {
        logger = Program.LoggerFactory.CreateLogger<ApplicationBase>();

        dockerClient = new DockerClientConfiguration(new Uri(configuration.DockerSocketUri)).CreateClient();
        this.configuration = configuration;

        CheckIfContainerIsInactiveState();
    }

    public abstract void Start();

    public void ActivityDetected() => lastActivity = DateTime.UtcNow;

    public async ValueTask<bool> DoActivityCheck(CancellationToken cancellationToken = default)
    {
        if (containerIsInactiveState)
            return containerIsInactiveState;

        if (lastActivity + configuration.InactiveContainerTime < DateTime.UtcNow)
        {
            logger.LogInformation("Container '{configuration.DockerContainerName}' is inactive; suspending...", configuration.DockerContainerName);

            switch (configuration.InactiveContainerAction)
            {
                case InactiveContainerAction.Pause:
                    await dockerClient.Containers.PauseContainerAsync(await GetContainerId(cancellationToken), cancellationToken);
                    break;
                case InactiveContainerAction.Stop:
                    await dockerClient.Containers.StopContainerAsync(await GetContainerId(cancellationToken), new Docker.DotNet.Models.ContainerStopParameters(), cancellationToken);
                    break;
            }

            containerIsInactiveState = true;
        }

        return containerIsInactiveState;
    }

    private bool CheckIfContainerIsInactiveState(CancellationToken cancellationToken = default)
    {
        var containerId = GetContainerId(cancellationToken).GetAwaiter().GetResult();
        var response = dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken).GetAwaiter().GetResult()
            ?? throw new Exception($"Container '{configuration.DockerContainerName}' not found.");

        return containerIsInactiveState = !response.State.Running || response.State.Paused;
    }

    private TaskCompletionSource EnsureContainerIsRunningTaskCompletionSource = null!;

    public async ValueTask EnsureContainerIsRunning(CancellationToken cancellationToken = default)
    {
        if (containerIsInactiveState)
        {
            bool isMaster = false;

            try
            {
                lock (this)
                {
                    if (EnsureContainerIsRunningTaskCompletionSource == null)
                    {
                        EnsureContainerIsRunningTaskCompletionSource = new();
                        isMaster = true;
                    }
                    else
                    {
                        isMaster = false;
                    }
                }

                if (EnsureContainerIsRunningTaskCompletionSource != null && !isMaster)
                {
                    await EnsureContainerIsRunningTaskCompletionSource.Task.ConfigureAwait(false);
                    return;
                }

                logger.LogInformation("Container '{configuration.DockerContainerName}' is inactive; starting...", configuration.DockerContainerName);

                var containerId = await GetContainerId(cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;
                if (string.IsNullOrEmpty(containerId))
                {
                    // TODO : Create the container
                    logger.LogError("Container '{configuration.DockerContainerName}' not found. TODO : Create the container; Skipping for now.", configuration.DockerContainerName);
                    return;
                }

                var inspectResult = await dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                if (inspectResult == null)
                {
                    // TODO : Create the container
                    logger.LogError("Container '{configuration.DockerContainerName}' not found. TODO : Create the container; Skipping for now.", configuration.DockerContainerName);
                    return;
                }

                if (inspectResult.State.Paused)
                {
                    await dockerClient.Containers.UnpauseContainerAsync(containerId, cancellationToken);
                }
                else if (!inspectResult.State.Running)
                {
                    if (!await dockerClient.Containers.StartContainerAsync(containerId, new Docker.DotNet.Models.ContainerStartParameters(), cancellationToken))
                    {
                        logger.LogError("Failed to start container '{configuration.DockerContainerName}'.", configuration.DockerContainerName);
                        return;
                    }
                }

                if (cancellationToken.IsCancellationRequested) return;

                await Task.Delay(configuration.TargetContainerStartupTime, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                bool healthCheckPassed;

                do
                {
                    if (string.IsNullOrWhiteSpace(configuration.TargetContainerHealthCheck))
                    {
                        healthCheckPassed = true;
                        break;
                    }

                    logger.LogInformation("Checking if container '{configuration.DockerContainerName}' is healthy...", configuration.DockerContainerName);
                    logger.LogWarning("NOT IMPLEMENTED YET");
                    break;

                    if (!healthCheckPassed)
                    {
                        await Task.Delay(100, cancellationToken);
                        if (cancellationToken.IsCancellationRequested) return;
                    }
                }
                while (!healthCheckPassed);

                containerIsInactiveState = false;
                EnsureContainerIsRunningTaskCompletionSource?.TrySetResult();
            }
            finally
            {
                if (isMaster)
                {
                    EnsureContainerIsRunningTaskCompletionSource?.TrySetResult();
                    EnsureContainerIsRunningTaskCompletionSource = null;
                }
            }
        }
    }

    protected async ValueTask<string> GetContainerId(CancellationToken cancellationToken = default)
    {
        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters() { All = true }, cancellationToken);

        var containerId = containers.FirstOrDefault(c => c.Names.Contains($"/{configuration.DockerContainerName}"))?.ID;

        if (containerId == null)
            logger.LogError("Container '{configuration.DockerContainerName}' not found.", configuration.DockerContainerName);

        return containerId ?? string.Empty;
    }

    public virtual void Dispose()
    {
        dockerClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}

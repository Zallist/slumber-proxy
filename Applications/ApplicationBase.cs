using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
        if (!containerIsInactiveState && lastActivity + configuration.InactiveContainerTime < DateTime.UtcNow)
        {
            logger.LogInformation("Container '{configuration.DockerContainerName}' is inactive; suspending...", configuration.DockerContainerName);

            await foreach (var id in GetContainerIds(configuration.DockerContainerName, cancellationToken))
                await ApplyToContainer(id);

            containerIsInactiveState = true;
        }

        return containerIsInactiveState;

        async ValueTask ApplyToContainer(string containerId)
        {
            switch (configuration.InactiveContainerAction)
            {
                case InactiveContainerAction.Pause:
                    await dockerClient.Containers.PauseContainerAsync(containerId, cancellationToken);
                    break;
                case InactiveContainerAction.Stop:
                    await dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters(), cancellationToken);
                    break;
                default:
                    throw new NotImplementedException($"{configuration.InactiveContainerAction} is not implemented.");
            }
        }
    }

    private bool CheckIfContainerIsInactiveState(CancellationToken cancellationToken = default)
    {
        var containerId = GetContainerId(cancellationToken).GetAwaiter().GetResult();

        var response = dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken).GetAwaiter().GetResult()
            ?? throw new Exception($"Container '{configuration.DockerContainerName}' not found.");

        return containerIsInactiveState = !response.State.Running || response.State.Paused;
    }

    private TaskCompletionSource<bool> EnsureContainerIsRunningTaskCompletionSource = null!;

    public async ValueTask<bool> EnsureContainerIsRunning(CancellationToken cancellationToken = default)
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
                    return await EnsureContainerIsRunningTaskCompletionSource.Task.ConfigureAwait(false);

                logger.LogInformation("Container '{configuration.DockerContainerName}' is inactive; starting...", configuration.DockerContainerName);

                await foreach (var id in GetContainerIds(configuration.DockerContainerName, cancellationToken))
                {
                    var inspect = await dockerClient.Containers.InspectContainerAsync(id, cancellationToken);

                    if (inspect == null)
                    {
                        // TODO : Create the container
                        logger.LogError("Container '{configuration.DockerContainerName}', id: {id} not found. TODO : Create the container; Skipping for now.", configuration.DockerContainerName, id);
                        return false;
                    }

                    if (inspect.State.Paused)
                    {
                        await dockerClient.Containers.UnpauseContainerAsync(id, cancellationToken);
                    }
                    else if (!inspect.State.Running)
                    {
                        var started = await dockerClient.Containers.StartContainerAsync(id, new ContainerStartParameters(), cancellationToken);

                        if (!started)
                        {
                            // TODO : Create the container
                            logger.LogError("Failed to start container '{configuration.DockerContainerName}', id: {id}.", configuration.DockerContainerName, id);
                            return false;
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested) return false;

                await Task.Delay(configuration.TargetContainerStartupTime, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return false;

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
                        if (cancellationToken.IsCancellationRequested) return false;
                    }
                }
                while (!healthCheckPassed);

                containerIsInactiveState = false;
                return containerIsInactiveState;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start container '{configuration.DockerContainerName}'.", configuration.DockerContainerName);
                return false;
            }
            finally
            {
                if (isMaster)
                {
                    EnsureContainerIsRunningTaskCompletionSource?.TrySetResult(!containerIsInactiveState);
                    EnsureContainerIsRunningTaskCompletionSource = null;
                }
            }
        }
        else
        {
            return !containerIsInactiveState;
        }
    }

    protected async Task<string> GetContainerId(CancellationToken cancellationToken = default)
    {
        await foreach (var id in GetContainerIds(configuration.DockerContainerName, cancellationToken))
            return id;

        return null;
    }

    protected IAsyncEnumerable<string> GetContainerIds(CancellationToken cancellationToken = default) => 
        GetContainerIds(configuration.DockerContainerName, cancellationToken);

    protected async IAsyncEnumerable<string> GetContainerIds(string dockerContainerIdOrName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var allContainers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters() { All = true }, cancellationToken);
        var allNetworks = await dockerClient.Networks.ListNetworksAsync(new NetworksListParameters() {  }, cancellationToken);
        var allVolumes = await dockerClient.Volumes.ListAsync(new VolumesListParameters() {  }, cancellationToken);

        var baseContainer = allContainers.FirstOrDefault(c => c.Names.Contains($"/{dockerContainerIdOrName}"));

        if (string.IsNullOrEmpty(baseContainer?.ID)) // Double check in case somehow ID is empty
        {
            logger.LogError("Container '{dockerContainerIdOrName}' not found.", dockerContainerIdOrName);
            yield break;
        }

        // We always want to yield the base container
        yield return baseContainer.ID;
        logger.LogDebug("Container '{dockerContainerIdOrName}' found with ID '{baseContainer.ID}'.", dockerContainerIdOrName, baseContainer.ID);

        if (configuration.ApplyToDockerComposeGroup)
        {
            // If we care about the docker compose group, then we want to yield all containers in the group
            var composeProject = baseContainer.Labels["com.docker.compose.project"];

            if (string.IsNullOrEmpty(composeProject))
            {
                logger.LogInformation("Container '{dockerContainerIdOrName}' is not part of a docker compose group.", dockerContainerIdOrName);
            }
            else
            {
                foreach (var container in allContainers)
                {
                    if (container.ID == baseContainer.ID) continue; // Skip the base container

                    if (container.Labels["com.docker.compose.project"] == composeProject)
                    {
                        yield return container.ID;
                        logger.LogDebug("Compose-Related container for '{dockerContainerIdOrName}' found with ID '{container.ID}'.", dockerContainerIdOrName, container.ID);
                    }
                }
            }
        }
    }

    public virtual void Dispose()
    {
        dockerClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}

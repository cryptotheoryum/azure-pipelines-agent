using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Agent.Plugins.PipelineArtifact.Telemetry;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.BlobStore.Common.Telemetry;
using Microsoft.VisualStudio.Services.Content.Common.Telemetry;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Newtonsoft.Json;

namespace Agent.Plugins.PipelineArtifact
{
    // A wrapper of DedupManifestArtifactClient, providing basic functionalities such as uploading and downloading pipeline artifacts.
    public class PipelineArtifactServer
    {
        // Upload from target path to Azure DevOps BlobStore service through DedupManifestArtifactClient, then associate it with the build
        internal async Task UploadAsync(
            AgentTaskPluginExecutionContext context,
            Guid projectId,
            int pipelineId,
            string name,
            string source,
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            BlobStoreClientTelemetry clientTelemetry;
            DedupManifestArtifactClient dedupManifestClient = DedupManifestArtifactClientFactory.CreateDedupManifestClient(context, connection, out clientTelemetry);

            using (clientTelemetry)
            {
                //Upload the pipeline artifact.
                PipelineArtifactActionRecord uploadRecord = clientTelemetry.CreateRecord<PipelineArtifactActionRecord>((level, uri, type) =>
                    new PipelineArtifactActionRecord(level, uri, type, nameof(UploadAsync), context));
                PublishResult result = await clientTelemetry.MeasureActionAsync(
                    record: uploadRecord,
                    actionAsync: async () =>
                    {
                        return await dedupManifestClient.PublishAsync(source, cancellationToken);
                    }
                );
                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineArtifact, record: uploadRecord);

                // 2) associate the pipeline artifact with an build artifact
                BuildServer buildHelper = new BuildServer(connection);
                Dictionary<string, string> propertiesDictionary = new Dictionary<string, string>();
                propertiesDictionary.Add(PipelineArtifactConstants.RootId, result.RootId.ValueString);
                propertiesDictionary.Add(PipelineArtifactConstants.ProofNodes, StringUtil.ConvertToJson(result.ProofNodes.ToArray()));
                var artifact = await buildHelper.AssociateArtifact(projectId, pipelineId, name, ArtifactResourceTypes.PipelineArtifact, result.ManifestId.ValueString, propertiesDictionary, cancellationToken);
                context.Output(StringUtil.Loc("AssociateArtifactWithBuild", artifact.Id, pipelineId));
            }
        }

        // Download pipeline artifact from Azure DevOps BlobStore service through DedupManifestArtifactClient to a target path
        // Old V0 function
        internal Task DownloadAsync(
            AgentTaskPluginExecutionContext context,
            Guid projectId,
            int pipelineId,
            string artifactName,
            string targetDir,
            CancellationToken cancellationToken)
        {
            var downloadParameters = new PipelineArtifactDownloadParameters
            {
                ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectId,
                ProjectId = projectId,
                PipelineId = pipelineId,
                ArtifactName = artifactName,
                TargetDirectory = targetDir
            };

            return this.DownloadAsync(context, downloadParameters, DownloadOptions.SingleDownload, cancellationToken);
        }

        // Download with minimatch patterns, V1.
        internal async Task DownloadAsync(
            AgentTaskPluginExecutionContext context,
            PipelineArtifactDownloadParameters downloadParameters,
            DownloadOptions downloadOptions, 
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            BlobStoreClientTelemetry clientTelemetry;
            DedupManifestArtifactClient dedupManifestClient = DedupManifestArtifactClientFactory.CreateDedupManifestClient(context, connection, out clientTelemetry);
            BuildServer buildHelper = new BuildServer(connection);

            using (clientTelemetry)
            {
                // download all pipeline artifacts if artifact name is missing
                if (downloadOptions == DownloadOptions.MultiDownload)
                {
                    List<BuildArtifact> artifacts;
                    if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                    {
                        artifacts = await buildHelper.GetArtifactsAsync(downloadParameters.ProjectId, downloadParameters.PipelineId, cancellationToken);
                    }
                    else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                    {
                        if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                        {
                            throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                        }
                        else
                        {
                            artifacts = await buildHelper.GetArtifactsWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, cancellationToken);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                    }

                    IEnumerable<BuildArtifact> pipelineArtifacts = artifacts.Where(a => a.Resource.Type == PipelineArtifactConstants.PipelineArtifact);
                    if (pipelineArtifacts.Count() == 0)
                    {
                        throw new ArgumentException("Could not find any pipeline artifacts in the build.");
                    }
                    else
                    {
                        context.Output(StringUtil.Loc("DownloadingMultiplePipelineArtifacts", pipelineArtifacts.Count()));

                        var artifactNameAndManifestIds = pipelineArtifacts.ToDictionary(
                            keySelector: (a) => a.Name, // keys should be unique, if not something is really wrong
                            elementSelector: (a) => DedupIdentifier.Create(a.Resource.Data));
                        // 2) download to the target path
                        var options = DownloadPipelineArtifactOptions.CreateWithMultiManifestIds(
                            artifactNameAndManifestIds,
                            downloadParameters.TargetDirectory,
                            proxyUri: null,
                            minimatchPatterns: downloadParameters.MinimatchFilters);

                        PipelineArtifactActionRecord downloadRecord = clientTelemetry.CreateRecord<PipelineArtifactActionRecord>((level, uri, type) =>
                            new PipelineArtifactActionRecord(level, uri, type, nameof(DownloadAsync), context));
                        await clientTelemetry.MeasureActionAsync(
                            record: downloadRecord,
                            actionAsync: async () =>
                            {
                                await dedupManifestClient.DownloadAsync(options, cancellationToken);
                            });
                        // Send results to CustomerIntelligence
                        context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineArtifact, record: downloadRecord);
                        }
                }
                else if (downloadOptions == DownloadOptions.SingleDownload)
                {
                    // 1) get manifest id from artifact data
                    BuildArtifact buildArtifact;
                    if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                    {
                        buildArtifact = await buildHelper.GetArtifact(downloadParameters.ProjectId, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                    }
                    else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                    {
                        if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                        {
                            throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                        }
                        else
                        {
                            buildArtifact = await buildHelper.GetArtifactWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                    }

                    var manifestId = DedupIdentifier.Create(buildArtifact.Resource.Data);
                    var options = DownloadPipelineArtifactOptions.CreateWithManifestId(
                        manifestId,
                        downloadParameters.TargetDirectory,
                        proxyUri: null,
                        minimatchPatterns: downloadParameters.MinimatchFilters);

                    PipelineArtifactActionRecord downloadRecord = clientTelemetry.CreateRecord<PipelineArtifactActionRecord>((level, uri, type) =>
                            new PipelineArtifactActionRecord(level, uri, type, nameof(DownloadAsync), context));
                    await clientTelemetry.MeasureActionAsync(
                        record: downloadRecord,
                        actionAsync: async () =>
                        {
                            await dedupManifestClient.DownloadAsync(options, cancellationToken);
                        });
                    // Send results to CustomerIntelligence
                    context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineArtifact, record: downloadRecord);
                }
                else
                {
                    throw new InvalidOperationException($"Invalid {nameof(downloadOptions)}!");
                }
            }
        }

        // Download for version 2. This decision was made because version 1 is sealed and we didn't want to break any existing customers.
        internal async Task DownloadAsyncV2(
            AgentTaskPluginExecutionContext context,
            PipelineArtifactDownloadParameters downloadParameters,
            DownloadOptions downloadOptions,
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            BuildServer buildHelper = new BuildServer(connection);

            // download all pipeline artifacts if artifact name is missing
            if (downloadOptions == DownloadOptions.MultiDownload)
            {
                List<BuildArtifact> artifacts;
                if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                {
                    artifacts = await buildHelper.GetArtifactsAsync(downloadParameters.ProjectId, downloadParameters.PipelineId, cancellationToken);
                }
                else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                {
                    if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                    {
                        throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                    }
                    else
                    {
                        artifacts = await buildHelper.GetArtifactsWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, cancellationToken);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                }

                IEnumerable<BuildArtifact> buildArtifacts = artifacts.Where(a => a.Resource.Type == PipelineArtifactConstants.Container);
                IEnumerable<BuildArtifact> pipelineArtifacts = artifacts.Where(a => a.Resource.Type == PipelineArtifactConstants.PipelineArtifact);
                if (buildArtifacts.Any())
                {
                    FileContainerProvider provider = new FileContainerProvider(connection, this.CreateTracer(context));
                    await provider.DownloadMultipleArtifactsAsync(downloadParameters, buildArtifacts, cancellationToken);
                }

                if (pipelineArtifacts.Any())
                {
                    PipelineArtifactProvider provider = new PipelineArtifactProvider(context, connection, this.CreateTracer(context));
                    await provider.DownloadMultipleArtifactsAsync(downloadParameters, pipelineArtifacts, cancellationToken);
                }
            }
            else if (downloadOptions == DownloadOptions.SingleDownload)
            {
                // 1) get manifest id from artifact data
                BuildArtifact buildArtifact;
                if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                {
                    buildArtifact = await buildHelper.GetArtifact(downloadParameters.ProjectId, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                }
                else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                {
                    if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                    {
                        throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                    }
                    else
                    {
                        buildArtifact = await buildHelper.GetArtifactWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                }

                ArtifactProviderFactory factory = new ArtifactProviderFactory(context, connection, this.CreateTracer(context));
                IArtifactProvider provider = factory.GetProvider(buildArtifact);
                await provider.DownloadSingleArtifactAsync(downloadParameters, buildArtifact, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"Invalid {nameof(downloadOptions)}!");
            }
        }

        private BuildDropManager CreateBulidDropManager(AgentTaskPluginExecutionContext context, VssConnection connection)
        {
            var dedupStoreHttpClient = connection.GetClient<DedupStoreHttpClient>();
            var tracer = this.CreateTracer(context);
            dedupStoreHttpClient.SetTracer(tracer);
            var client = new DedupStoreClientWithDataport(dedupStoreHttpClient, 16 * Environment.ProcessorCount);
            var buildDropManager = new BuildDropManager(client, tracer);
            return buildDropManager;
        }

        private CallbackAppTraceSource CreateTracer(AgentTaskPluginExecutionContext context)
        {
            var tracer = new CallbackAppTraceSource(str => context.Output(str), System.Diagnostics.SourceLevels.Information);
            return tracer;
        }
    }

    internal class PipelineArtifactDownloadParameters
    {
        /// <remarks>
        /// Options on how to retrieve the build using the following parameters.
        /// </remarks>
        public BuildArtifactRetrievalOptions ProjectRetrievalOptions { get; set; }
        /// <remarks>
        /// Either project ID or project name need to be supplied.
        /// </remarks>
        public Guid ProjectId { get; set; }
        /// <remarks>
        /// Either project ID or project name need to be supplied.
        /// </remarks>
        public string ProjectName { get; set; }
        public int PipelineId { get; set; }
        public string ArtifactName { get; set; }
        public string TargetDirectory { get; set; }
        public string[] MinimatchFilters { get; set; }
    }

    internal enum BuildArtifactRetrievalOptions
    {
        RetrieveByProjectId,
        RetrieveByProjectName
    }

    internal enum DownloadOptions
    {
        SingleDownload,
        MultiDownload
    }
}
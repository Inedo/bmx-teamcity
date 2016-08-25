﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Inedo.BuildMaster.Extensibility;
using Inedo.BuildMaster.Extensibility.Operations;
using Inedo.Diagnostics;
using Inedo.ExecutionEngine.Executer;
using Inedo.BuildMasterExtensions.TeamCity.Operations;
using Inedo.BuildMaster.Data;

namespace Inedo.BuildMasterExtensions.TeamCity
{
    /// <summary>
    /// Implements the work logic to queue a build in TeamCity.
    /// Connection details for the API are passed by the calling class.
    /// </summary>
    internal sealed class QueueBuildOperationManager
    {
        const int WAIT_DELAY_SECS = 2;

        private QueueBuildOperation Operation { get; }
        private IGenericBuildMasterContext Context { get; }

        private int progressPercent;
        private string progressMessage;

        public QueueBuildOperationManager(QueueBuildOperation operation, IGenericBuildMasterContext context)
        {
            if (context.ApplicationId == null)
                throw new InvalidOperationException("context requires a valid application ID");

            this.Operation = operation;
            this.Context = context;
        }

        public OperationProgress GetProgress()
        {
            return new OperationProgress(this.progressPercent, this.progressMessage);
        }

        public async Task QueueBuildAsync(CancellationToken cancellationToken, bool logProgressToExecutionLog = true)
        {
            TeamCityBuildType buildType = null;

            buildType = this.Operation.api.GetBuildType(this.Operation.BuildConfigurationId); // will raise an error if not found

            this.Operation.LogInformation($"Retrieving Build Information for {buildType.id}...");

            TeamCityBuild build = new TeamCityBuild();
            string xml;

            using (var client = new WebClient(this.Operation))
            {
                this.Operation.LogDebug("Triggering TeamCity build configuration id {0}", buildType.id);

                client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/xml";
                xml = client.UploadString("app/rest/buildQueue", $"<build  branchName=\"{this.Operation.BranchName}\"><buildType id=\"{buildType.id}\" /></build>"); // We WANT synchronous call for this one !
                build.Update(xml);

                // record build details

                this.Operation.LogInformation($"Building of build configuration {buildType.id} was triggered successfully. For reference Build ID is {build.id}.");
                this.Operation.LogInformation("TeamCity is currently building...");

                if (!this.Operation.WaitForCompletion)
                {
                    this.Operation.LogInformation("WaitForCompletion is set to false in the plan, so we will not wait for TeamCity to finish build and will continue our business...");
                    return;
                }

                // Loop until the build is complete
                string prevStatusText = "";
                do
                {
                    await Task.Delay(WAIT_DELAY_SECS * 1000, cancellationToken).ConfigureAwait(false); 

                    xml = await client.DownloadStringTaskAsync($"app/rest/buildQueue/id:{build.id}").ConfigureAwait(false);
                    build.Update(xml);

                    this.progressPercent = build.percentageComplete;
                    this.progressMessage = $"Building {build.projectName} Build #{build.number} ({build.percentageComplete}% Complete)";

                    if (logProgressToExecutionLog)
                    {
                        if (prevStatusText != build.statusText)
                            this.Operation.LogDebug($"{this.progressMessage} : {build.statusText}");

                        prevStatusText = build.statusText;

                        //if (build.running)
                        //    this.Operation.LogInformation(build.statusText);

                        if (build.waitReason.Length != 0)
                            this.Operation.LogDebug($"Waiting for Teamcity... Reason: {build.waitReason}");
                    }

                } while (build.running || build.status == TeamCityBuild.BuildStatuses.Unknown);

                this.Operation.LogInformation($"{build.projectName} build #{build.number} : {build.statusText}");

                this.Operation.LogInformation($"Creating $TeamCityBuildNumber variable with value {build.number}");

                // Creates TeamCityBuildNumber package variable
                await new DB.Context(false).Variables_CreateOrUpdateVariableDefinitionAsync(
                    "TeamCityBuildNumber",
                    Application_Id: this.Context.ApplicationId,
                    Release_Number: this.Context.ReleaseNumber,
                    Build_Number: this.Context.BuildNumber,
                    Value_Text: build.number,
                    Sensitive_Indicator: false,
                    Environment_Id: null,
                    ServerRole_Id: null,
                    Server_Id: null,
                    ApplicationGroup_Id: null,
                    Execution_Id: null,
                    Promotion_Id: null,
                    Deployable_Id: null
                ).ConfigureAwait(false);
            }
        }
        
    }
}

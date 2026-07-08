# AzPipelineLog

Small PowerShell tool for fetching and caching logs from Azure Pipelines. The module provides a single command, `Get-AzPipelineLog`, which receives a pipeline ID or a run ID as a parameter, fetches all logs for the specified runs (if a pipeline is specified, it retrieves all its runs) and stores them in a local cache. It returns a structured run object that lets you easily query various information about the run, steps,...

## Example usage

```pwsh
Get-AzPipelineLog 12345, 12346, 123457 -Offline ...
| ? Result -ne succeeed
| % Steps
| ? Name -like "*Unit tests*"
| ? Result -notin succeeded, skipped
| select -First 100
| ? {$_.Log | grep "test failed: "}
```

Use auto-complete to discover the available properties. The cmdlet returns a list of matching pipeline runs, you can access the list of stages/jobs/steps for each run with the `.Stages`/`.Jobs`/`.Steps` properties, there are also corresponding properties to go back up from each one (e.g., `.Run`).

## Custom wrapper

To make the cmdlet usage easier, you may want to write a custom wrapper that supplies a fixed `-CacheDir` path, a `-ProjectUrl` to your ADO instance and supports pipelines by name:

```pwsh
enum PipelineName {
    PR = 12345
    PostMergeCI = 12346
    Perf = 12347
    # ...
}

function Get-AzPipelineLog {
	[CmdletBinding(DefaultParameterSetName="Pipeline", PositionalBinding=$false)]
    param(
        [Parameter(Mandatory, Position=0, ParameterSetName="Pipeline")][PipelineName[]]$Pipeline,
        [Parameter(Mandatory, ParameterSetName="BuildId")][int[]]$BuildId,
        [Parameter(Position = 1)][ScriptBlock]$Filter,
        [securestring]$AccessToken = $null,
        [switch]$Offline
    )

    $Arg = @{}
    if ($PSCmdlet.ParameterSetName -eq "Pipeline") {
        $Arg["Pipeline"] = [int[]]$Pipeline
    } else {
        $Arg["BuildId"] = $BuildId
    }
    $Arg["Filter"] = $Filter
    $Arg["AccessToken"] = $AccessToken
    $Arg["Offline"] = $Offline

    AzPipelineLog\Get-AzPipelineLog @Arg `
        -CacheDir "path\to\cache\dir" `
        -ProjectUrl "https://dev.azure.com/myorg/myproject"
}
```
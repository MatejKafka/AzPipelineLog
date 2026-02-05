using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AzPipelineLog;

/// Gets Azure DevOps pipeline logs for a specified pipeline or specific runs.
[Cmdlet(VerbsCommon.Get, "AzPipelineLog", DefaultParameterSetName = PipelinePS)]
[OutputType(typeof(PipelineRunResult))]
public class GetAzPipelineLogCmdlet : PSCmdlet, IDisposable {
    private const string PipelinePS = "Pipeline";
    private const string BuildIdPS = "BuildId";

    /// The ID of the pipeline to get logs for.
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = PipelinePS)]
    public int[] Pipeline = null!;

    /// The ID of a specific run to get logs for.
    [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = BuildIdPS)]
    public int[] BuildId = null!;

    /// Path to a cache directory where downloaded logs will be cached.
    [Parameter(Mandatory = true)]
    public string CacheDir = null!;

    /// The URL of the Azure DevOps project, e.g., https://dev.azure/org/project.
    [Parameter(Mandatory = true)]
    public string ProjectUrl = null!;

    /// A script block to filter pipeline runs.
    [Parameter(Position = 1)]
    public ScriptBlock? Filter;

    /// The access token for Azure DevOps. Defaults to SYSTEM_ACCESSTOKEN environment variable.
    [Parameter]
    public SecureString? AccessToken;

    /// Only return cached results without querying the API.
    [Parameter]
    public SwitchParameter Offline;

    private readonly CancellationTokenSource _cts = new();
    private LogCache _cache = null!;
    private AdoClient? _client;

    protected override void BeginProcessing() {
        _cache = new LogCache(SessionState.Path.GetUnresolvedProviderPathFromPSPath(CacheDir), "br");

        if (!Offline) {
            _client = new AdoClient(GetAccessToken(), ProjectUrl);
        }
    }

    private string GetAccessToken() {
        if (AccessToken != null) {
            return ConvertSecureStringToString(AccessToken);
        }
        if (Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN") is { } token) {
            return token;
        }

        WriteInformation("Retrieving access token using azureauth...", null);

        // use azureauth to retrieve a fresh token
        var process = new System.Diagnostics.Process {
            StartInfo = new System.Diagnostics.ProcessStartInfo {
                FileName = "azureauth",
                Arguments = "ado token --mode broker",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new PSArgumentException("Failed to retrieve access token using azureauth.");
        }
        return output.Trim();
    }

    protected override void ProcessRecord() {
        if (ParameterSetName == PipelinePS) {
            foreach (var pipeline in Pipeline) {
                ProcessByPipeline(pipeline);
            }
        } else {
            foreach (var buildId in BuildId) {
                ProcessByBuildId(buildId);
            }
        }
    }

    private bool ShouldProcessRun(PipelineRun run) {
        if (Filter == null) {
            return true;
        }
        var result = Filter.InvokeWithContext(null, [new("_", run)], []);
        return LanguagePrimitives.ConvertTo<bool>(result);
    }

    private void ProcessByPipeline(int pipelineId) {
        PipelineRun[] apiRunsArray = [];

        if (!Offline) {
            var apiRuns = _client!.GetPipelineRuns(pipelineId, _cts.Token).GetAwaiter().GetResult();
            // Filter out unfinished runs - we don't want to cache incomplete data
            apiRunsArray = apiRuns.Where(r => r.State == "completed").ToArray();
            WriteVerbose($"Fetched {apiRunsArray.Length} completed runs from the API.");
        }

        // Combine API runs with cached runs, deduplicate by ID, order chronologically (by ID descending = newest first)
        var cachedRuns = _cache.LoadAllCachedRuns(pipelineId);
        var cachedBuildIds = cachedRuns.Select(r => r.Id).ToHashSet();
        var allRuns = apiRunsArray
            .Concat(cachedRuns)
            .DistinctBy(r => r.Id)
            .OrderByDescending(r => r.Id)
            .Where(ShouldProcessRun)
            .ToList();

        // always fetch 8 runs ahead to improve concurrency
        using var limit = new SemaphoreSlim(8);

        var tasks = allRuns.Select(async run => {
            await limit.WaitAsync(_cts.Token);
            try {
                return (run, cachedBuildIds.Contains(run.Id) ? ((int, int)?)null : await CacheRun(run));
            } finally {
                limit.Release();
            }
        });

        var i = 0;
        foreach (var t in tasks) {
            i++;
            try {
                var (run, stats) = t.GetAwaiter().GetResult();
                if (stats is var (jobLogs, stepLogs)) {
                    WriteVerbose($"[{i}/{allRuns.Count}] Cached run #{run.Id} ({jobLogs} job logs, {stepLogs} step logs).");
                }
                WriteObject(new PipelineRunResult(run, _cache, ProjectUrl));
            } catch (EmptyTimelineException) {
                // ignore runs without timelines
                continue;
            } catch (HttpRequestException e) {
                WriteError(new ErrorRecord(
                    e,
                    "ApiRequestFailed",
                    ErrorCategory.ConnectionError,
                    null));
            }
        }
    }

    private void ProcessByBuildId(int buildId) {
        // Try to load from cache first
        var run = _cache.LoadRunMetadata(buildId);
        if (run != null) {
            WriteVerbose($"Run {buildId} found in cache");
            WriteObject(new PipelineRunResult(run, _cache, ProjectUrl));
            return;
        }

        if (Offline) {
            WriteError(new ErrorRecord(
                new InvalidOperationException($"Run {buildId} not found in cache."),
                "RunNotFound",
                ErrorCategory.ObjectNotFound,
                buildId));
            return;
        }

        // Not in cache, fetch from server
        run = _client!.GetPipelineRun(buildId, _cts.Token).GetAwaiter().GetResult();
        if (run.State != "completed") {
            WriteError(new ErrorRecord(
                new InvalidOperationException($"Run {buildId} is not completed (state: {run.State})."),
                "RunNotCompleted",
                ErrorCategory.InvalidOperation,
                buildId));
            return;
        }

        var (jobLogs, stepLogs) = CacheRun(run).GetAwaiter().GetResult();
        WriteVerbose($"Cached run #{run.Id} ({jobLogs} job logs, {stepLogs} step logs).");
        WriteObject(new PipelineRunResult(run, _cache, ProjectUrl));
    }

    private async Task<(int, int)> CacheRun(PipelineRun run) {
        _cts.Token.ThrowIfCancellationRequested();

        var logStats = (0, 0);
        if (!_cache.HasTimeline(run.Id)) {
            logStats = await CacheRunTimeline(run.Id);
        }

        // cache metadata last, so that we know that any stored run is complete
        _cache.SaveRunMetadata(run);
        return logStats;
    }

    private class EmptyTimelineException(string message, Exception innerException) : Exception(message, innerException);

    private async Task<(int, int)> CacheRunTimeline(int buildId) {
        // Fetch timeline (needed to know which logs to download)
        Timeline timeline;
        try {
            timeline = await _client!.GetTimeline(buildId, _cts.Token);
        } catch (HttpRequestException e) when (e.InnerException is JsonException eJson && eJson.BytePositionInLine == 0) {
            throw new EmptyTimelineException($"Run {buildId} does not have a timeline.", e);
        }

        // 1. First cache job logs (step logs are extracted from job logs on demand)
        var jobsWithLogs = timeline.Records.Where(r => r.Log != null && r.Type == "Job").ToList();
        var downloadTasks = jobsWithLogs.Select(async r => {
            var log = await _client!.GetAzString(r.Log!.Url, _cts.Token);
            var path = _cache.GetLogPath(buildId, r.Log!.Id);
            LogCache.SaveLog(path, log);
        }).ToList();

        // 2. For jobs without job-level logs (e.g. cancelled), download step logs individually
        var jobsWithoutLogs = timeline.Records.Where(r => r.Type == "Job" && r.Log == null).Select(r => r.Id).ToHashSet();
        var stepsNeedingLogs = timeline.Records.Where(r => r.Type == "Task" && r.Log != null && jobsWithoutLogs.Contains(r.ParentId!.Value)).ToList();

        downloadTasks.AddRange(stepsNeedingLogs.Select(async r => {
            var log = await _client!.GetAzString(r.Log!.Url, _cts.Token);
            var path = _cache.GetLogPath(buildId, r.Log!.Id);
            LogCache.SaveLog(path, log);
        }));
        await Task.WhenAll(downloadTasks);

        // 3. Then cache timeline (after all logs are downloaded)
        _cache.SaveTimeline(buildId, timeline);
        return (jobsWithLogs.Count, stepsNeedingLogs.Count);
    }

    private static string ConvertSecureStringToString(SecureString secureString) {
        IntPtr ptr = Marshal.SecureStringToGlobalAllocUnicode(secureString);
        try {
            return Marshal.PtrToStringUni(ptr) ?? "";
        } finally {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    protected override void StopProcessing() {
        _cts.Cancel();
        base.StopProcessing();
    }

    public void Dispose() {
        _client?.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// Result object containing pipeline run information with lazy-loaded timeline data.
public class PipelineRunResult : IComparable {
    private readonly PipelineRun _run;
    private readonly LogCache _cache;
    private readonly string _projectUrl;

    internal PipelineRunResult(PipelineRun run, LogCache cache, string projectUrl) {
        _run = run;
        _cache = cache;
        _projectUrl = projectUrl;
    }

    public int Id => _run.Id;
    public string Name => _run.Name;
    public string Result => _run.Result!;
    public object? Parameters => _run.TemplateParameters;
    public DateTime StartTime => _run.StartTime;
    public DateTime FinishTime => _run.FinishTime!.Value;
    public int PipelineId => _run.Pipeline.Id;
    public string PipelineName => _run.Pipeline.Name;

    public string Url => $"{_projectUrl}/_build/results?buildId={_run.Id}";

    public override string ToString() => $"#{Name.Split(' ')[0]}";

    private Timeline Timeline => field ??= _cache.LoadTimeline(_run.Id) ?? throw new InvalidOperationException($"Timeline for run {_run.Id} not found in cache.");
    private T[] GetByType<T>(string type, Func<TimelineRecord, T> selectFn) => [.. Timeline.Records.Where(r => r.Type == type).Select(selectFn)];

    // skipped records do not have actual logs, only a single line documenting the skip reason
    // parsing the logs from parent job log is hard, because the skip logs often have the same timestamp as nearby
    //  step begin/end logs, so it's hard to guess the right order for steps to match the records in the job log
    private string? GetCachePath(TimelineRecord record)
        => record.Log == null || record.Result == "skipped" ? null : _cache.GetLogPath(_run.Id, record.Log.Id);

    // phases are a "hidden" layer between stages and jobs to implement
    //  parallel/matrix jobs, we skip them in the public output
    private TimelineRecord[] Phases => field ??= GetByType("Phase", r => r);

    public TimelineStage[] Stages => field ??= GetByType("Stage", stage => {
        return new TimelineStage(stage, this);
    });

    public TimelineJob[] Jobs => field ??= GetByType("Job", job => {
        if (job.ParentId == null) {
            return new TimelineJob(job, this, GetCachePath(job), null);
        }
        var phase = Phases.First(p => p.Id == job.ParentId);
        var stage = Stages.First(s => s.Id == phase.ParentId);
        return new TimelineJob(job, this, GetCachePath(job), stage);
    });

    public TimelineStep[] Steps => field ??= GetStepsWithIndex();

    private TimelineStep[] GetStepsWithIndex() {
        // Group steps by job and assign indices based on StartTime order
        var stepsByJob = Timeline.Records
            .Where(r => r.Type == "Task")
            .GroupBy(r => r.ParentId);

        var allSteps = new List<TimelineStep>();
        foreach (var jobGroup in stepsByJob) {
            var job = Jobs.First(j => j.Id == jobGroup.Key);
            var i = 0;
            foreach (var step in jobGroup.OrderBy(s => s.StartTime)) {
                // steps don't have their own log files, the logs are extracted from the parent job
                var jobLogPath = GetCachePath(job.Record);
                allSteps.Add(new TimelineStep(step, this, jobLogPath, job, stepIndex: i));
                // skipped steps don't have logs, so they won't appear in the job log, don't increment index
                if (step.Result != "skipped") i++;
            }
        }
        return [.. allSteps];
    }

    public override bool Equals(object? obj) => obj is PipelineRunResult other && Id == other.Id;
    public override int GetHashCode() => Id.GetHashCode();

    // Select-Object -Unique uses IComparable to determine uniqueness :(
    public int CompareTo(object? obj) => obj is PipelineRunResult other
        ? Id.CompareTo(other.Id)
        : throw new ArgumentException($"Not a {nameof(PipelineRunResult)}");
}

/// Wraps a timeline record with lazy log loading.
public class TimelineRecordResult : IComparable {
    public readonly PipelineRunResult Run;
    internal readonly TimelineRecord Record;

    internal TimelineRecordResult(TimelineRecord record, PipelineRunResult run) {
        Record = record;
        Run = run;
    }

    public Guid Id => Record.Id;
    public string Name => Record.Name;
    public string Result => Record.Result;
    public int Attempt => Record.Attempt;
    public DateTime? StartTime => Record.StartTime;
    public DateTime? FinishTime => Record.FinishTime;

    public string[]? Errors => Record.Issues?.Where(i => i.Type == "error").Select(i => i.Message).ToArray();
    public string[]? Warnings => Record.Issues?.Where(i => i.Type == "warning").Select(i => i.Message).ToArray();

    public override string ToString() => Name;

    public override bool Equals(object? obj) => obj is TimelineRecordResult other && Id == other.Id && Run.Id == other.Run.Id;
    public override int GetHashCode() => HashCode.Combine(Id, Run.Id);

    // Select-Object -Unique uses IComparable to determine uniqueness :(
    public int CompareTo(object? obj) => obj is TimelineRecordResult other
        ? Run.Id == other.Run.Id ? Id.CompareTo(other.Id) : Run.Id.CompareTo(other.Run.Id)
        : throw new ArgumentException($"Not a {nameof(TimelineRecordResult)}");
}

public class TimelineStage : TimelineRecordResult {
    internal TimelineStage(TimelineRecord record, PipelineRunResult run) : base(record, run) { }

    public TimelineJob[] Jobs => Run.Jobs.Where(j => j.Stage == this).ToArray();
    public TimelineStep[] Steps => Run.Steps.Where(s => s.Stage == this).ToArray();
}

public partial class TimelineJob : TimelineRecordResult {
    public readonly TimelineStage? Stage;
    public string? Agent => Record.WorkerName;
    public readonly string? LogPath;
    public string Url => $"{Run.Url}&view=logs&j={Id}";

    private (string Name, ReadOnlyMemory<string> Lines)[]? StepLogRanges => field ??= LogReader.ParseJobLog(LogTimestamps);

    internal TimelineJob(TimelineRecord record, PipelineRunResult run, string? logPath, TimelineStage? stage) : base(record, run) {
        Stage = stage;
        LogPath = logPath;
    }

    public TimelineStep[] Steps => Run.Steps.Where(s => s.Job == this).ToArray();

    public bool HasLog => LogPath != null;

    /// Gets the log content for this job. Returns null if no log exists.
    public string[]? Log => LogReader.StripTimestamps(LogTimestamps);

    /// Gets the log content for this job, including timestamps. Returns null if no log exists.
    public string[]? LogTimestamps => field ??= LogReader.ReadLog(LogPath);

    /// Gets the log content for this job, with ANSI escape sequences stripped. Returns null if no log exists.
    public string[]? LogAscii => field ??= LogReader.StripAnsiEscapes(Log);

    internal string[]? GetStepLog(int stepIndex, string expectedName) {
        if (StepLogRanges == null) return null;
        var (name, lines) = StepLogRanges[stepIndex];
        if (!MatchesLogName(name, expectedName)) {
            throw new InvalidOperationException(
                $"Step name mismatch at index {stepIndex} in job '{Name}'. Expected '{expectedName}', found '{name}'.");
        }
        return lines.ToArray();
    }

    private static bool MatchesLogName(string logName, string expectedName) {
        var withoutPrefix = expectedName.StartsWith("Pre-job: ", StringComparison.Ordinal) ? expectedName.AsSpan("Pre-job: ".Length)
            : expectedName.StartsWith("Post-job: ", StringComparison.Ordinal) ? expectedName.AsSpan("Post-job: ".Length)
            : expectedName.AsSpan();
        return logName.Equals(withoutPrefix, StringComparison.Ordinal);
    }
}

public class TimelineStep : TimelineRecordResult {
    public readonly TimelineJob Job;
    public readonly string? LogPath;
    public string Url => $"{Job.Url}&t={Id}";

    /// Index of this step within its parent job.
    private readonly int _index;

    internal TimelineStep(TimelineRecord record, PipelineRunResult run, string? logPath, TimelineJob job, int stepIndex) : base(record, run) {
        Job = job;
        LogPath = logPath;
        _index = stepIndex;
    }

    public TimelineStage? Stage => Job.Stage;
    public bool HasLog => LogPath != null;

    /// Gets the log content for this step. Returns null if no log exists.
    public string[]? Log => LogReader.StripTimestamps(LogTimestamps);

    /// Gets the log content for this step, including timestamps. Returns null if no log exists.
    public string[]? LogTimestamps => field ??= LogPath == null ? null : Job.GetStepLog(_index, Name) ?? LogReader.ReadLog(LogPath);

    /// Gets the log content for this step, with ANSI escape sequences stripped. Returns null if no log exists.
    public string[]? LogAscii => field ??= LogReader.StripAnsiEscapes(Log);
}

internal static partial class LogReader {
    public static string[]? StripAnsiEscapes(IEnumerable<string>? log) => log?.Select(line => PSHostUserInterface.GetOutputString(line, false)).ToArray();
    public static string[]? StripTimestamps(IEnumerable<string>? log) => log?.Select(line => line[TimestampLength..]).ToArray();
    public static string[]? ReadLog(string? path) => path == null ? null : ReconstructMultilineEntries(LogCache.ReadLogLines(path)).ToArray();

    private const int TimestampLength = 29;

    // Timestamp format: "2024-01-15T10:30:45.1234567Z " (29 chars)
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z ")]
    private static partial Regex TimestampRegex();

    /// Reconstructs multiline log entries by grouping continuation lines with their timestamped parent.
    /// This only seems to occur with internal ADO logs, normally each log line starts with a timestamp.
    private static IEnumerable<string> ReconstructMultilineEntries(IEnumerable<string> lines) {
        var currentEntry = new List<string>();
        foreach (var line in lines) {
            if (TimestampRegex().IsMatch(line)) {
                // Start of a new timestamped entry - flush previous if exists
                if (currentEntry.Count > 0) {
                    yield return string.Join("\n", currentEntry);
                    currentEntry.Clear();
                }
                currentEntry.Add(line);
            } else {
                // Continuation line - append to current entry
                currentEntry.Add(line);
            }
        }

        // Flush final entry
        if (currentEntry.Count > 0) {
            yield return string.Join("\n", currentEntry);
        }
    }

    // for completed jobs, the job log contains logs for all steps in order, parse it to avoid downloading step logs separately
    public static (string Name, ReadOnlyMemory<string> Lines)[]? ParseJobLog(string[]? log)
        => log == null ? null : ParseJobLogIter(log).ToArray();

    private static IEnumerable<(string Name, ReadOnlyMemory<string> Lines)> ParseJobLogIter(string[] log) {
        ReadOnlySpan<char> Line(int i) => log[i].AsSpan(TimestampLength);

        const string startMarker = "##[section]Starting: ";
        const string finishMarker = "##[section]Finishing: ";

        int i = 0;
        i++; // skip job start section

        while (i < log.Length) {
            var line = Line(i++);
            if (!line.StartsWith(startMarker, StringComparison.Ordinal)) {
                continue;
            }

            int startLine = i - 1;
            var stepName = line[startMarker.Length..];

            while (i < log.Length) {
                var endLine = Line(i++);
                if (!endLine.StartsWith(finishMarker, StringComparison.Ordinal)) {
                    continue;
                }

                var endStepName = endLine[finishMarker.Length..];
                if (MemoryExtensions.Equals(stepName, endStepName, StringComparison.Ordinal)) {
                    break;
                }
            }

            yield return (stepName.ToString(), log.AsMemory(startLine..i));
        }
    }
}
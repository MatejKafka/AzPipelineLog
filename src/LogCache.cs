using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace AzPipelineLog;

/// <summary>
/// Caches pipeline run metadata and log files to disk.
/// Cache structure:
/// - Run metadata: &lt;cache-dir&gt;/runs.jsonl (line-delimited JSON, contains pipeline ID per run)
/// - Timeline: &lt;cache-dir&gt;/&lt;run-id&gt;/_timeline.json
/// - Log files: &lt;cache-dir&gt;/&lt;run-id&gt;/&lt;log-id&gt;.log
/// </summary>
internal class LogCache(string cacheDir, string? compression = null) {
    private readonly string LogExtension = compression != null ? $".log.{compression}" : ".log";

    /// Gets the path to the runs metadata file.
    public string GetRunsFilePath() => Path.Combine(cacheDir, "runs.jsonl");

    /// Gets the path to the timeline cache file.
    public string GetTimelinePath(int runId) => Path.Combine(cacheDir, runId.ToString(), "_timeline.json");

    /// Gets the path to the log cache file.
    public string GetLogPath(int runId, int logId) {
        return Path.Combine(cacheDir, runId.ToString(), $"{logId}{LogExtension}");
    }

    /// Appends run metadata to the runs.jsonl file.
    public void SaveRunMetadata(PipelineRun run) {
        Directory.CreateDirectory(cacheDir);
        var json = JsonSerializer.Serialize(run, RunSerializationOptions);
        lock (this) {
            File.AppendAllLines(GetRunsFilePath(), [json]);
        }
    }

    /// Loads a specific run's metadata from the cache.
    public PipelineRun? LoadRunMetadata(int runId) {
        var runsFile = GetRunsFilePath();
        if (!File.Exists(runsFile))
            return null;

        lock (this) {
            foreach (var line in File.ReadLines(runsFile)) {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var run = JsonSerializer.Deserialize<PipelineRun>(line, RunSerializationOptions);
                if (run?.Id == runId)
                    return run;
            }
            return null;
        }
    }

    /// Gets all cached run metadata for a specific pipeline.
    public List<PipelineRun> LoadAllCachedRuns(int pipelineId) {
        lock (this) {
            return LoadAllCachedRunsInner(pipelineId).ToList();
        }
    }

    private IEnumerable<PipelineRun> LoadAllCachedRunsInner(int pipelineId) {
        var runsFile = GetRunsFilePath();
        if (!File.Exists(runsFile))
            yield break;

        foreach (var line in File.ReadLines(runsFile)) {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var run = JsonSerializer.Deserialize<PipelineRun>(line, RunSerializationOptions);
            if (run?.Pipeline?.Id == pipelineId)
                yield return run;
        }
    }

    public bool HasTimeline(int runId) {
        return File.Exists(GetTimelinePath(runId));
    }

    /// Caches timeline to disk.
    public void SaveTimeline(int runId, Timeline timeline) {
        var path = GetTimelinePath(runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, timeline, TimelineSerializationOptions);
    }

    /// Loads cached timeline from disk.
    public Timeline? LoadTimeline(int runId) {
        try {
            using var stream = File.OpenRead(GetTimelinePath(runId));
            return JsonSerializer.Deserialize<Timeline>(stream, TimelineSerializationOptions);
        } catch (FileNotFoundException) {
            return null;
        } catch (DirectoryNotFoundException) {
            return null;
        }
    }

    /// Caches log content to disk.
    public static void SaveLog(string path, string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using Stream compression = Path.GetExtension(path) switch {
            ".gz" => new GZipStream(file, CompressionLevel.Optimal),
            ".br" => new BrotliStream(file, CompressionLevel.Optimal),
            _ => file
        };
        using var writer = new StreamWriter(compression);
        writer.Write(content);
    }

    /// Reads cached log content from disk.
    public static IEnumerable<string> ReadLogLines(string path) {
        using var file = File.OpenRead(path);
        using Stream compression = Path.GetExtension(path) switch {
            ".gz" => new GZipStream(file, CompressionMode.Decompress),
            ".br" => new BrotliStream(file, CompressionMode.Decompress),
            _ => file
        };
        using var reader = new StreamReader(compression);
        string? line;
        while ((line = reader.ReadLine()) != null) {
            yield return line;
        }
    }

    private static readonly JsonSerializerOptions RunSerializationOptions = new(AdoClient.DeserializationOptions) {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions TimelineSerializationOptions = new(AdoClient.DeserializationOptions) {
        WriteIndented = true,
    };
}

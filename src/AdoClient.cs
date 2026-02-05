using System;
using System.Collections;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AzPipelineLog;

internal sealed class AdoClient : IDisposable {
    // concurrent requests are throttled, otherwise ADO gives us HTTP 429
    private readonly HttpClient _client = new(new ThrottlingDelegatingHandler(128) {
        InnerHandler = new HttpClientHandler()
    });
    private readonly string _baseUrl;

    /// <param name="accessToken"></param>
    /// <param name="projectUrl">Base URL of the Azure DevOps project, e.g., https://dev.azure/org/project.</param>
    public AdoClient(string accessToken, string projectUrl) {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        _baseUrl = projectUrl + "/_apis";
    }

    public void Dispose() {
        _client.Dispose();
    }

    public async Task<HttpResponseMessage> InvokeAzApi(string url, CancellationToken cancellationToken = default) {
        if (!url.StartsWith("https://")) {
            url = $"{_baseUrl}/{url}";
        }
        var response = await _client.GetAsync(url, cancellationToken);

        if ((int)response.StatusCode is 203) {
            response.Dispose();
            throw new Exception($"Access token is not valid.");
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    internal static readonly JsonSerializerOptions DeserializationOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = {
            new PowerShellJsonConverter()
        }
    };

    public async Task<T> InvokeAzApi<T>(string url, CancellationToken cancellationToken = default) {
        if (!url.StartsWith("https://")) {
            url = $"{_baseUrl}/{url}";
        }
        try {
            using var response = await InvokeAzApi(url, cancellationToken);
            return (await response.Content.ReadFromJsonAsync<T>(DeserializationOptions, cancellationToken))!;
        } catch (Exception e) {
            throw new HttpRequestException($"Azure API invocation '{url}' failed: {e.Message}", e);
        }
    }

    public async Task<string> GetAzString(string url, CancellationToken cancellationToken = default) {
        using var response = await InvokeAzApi(url, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<PipelineRun[]> GetPipelineRuns(int pipelineId, CancellationToken cancellationToken = default) {
        return (await InvokeAzApi<AdoList<PipelineRun>>($"/pipelines/{pipelineId}/runs?api-version=7.1", cancellationToken)).Value;
    }

    public async Task<PipelineRun> GetPipelineRun(int runId, CancellationToken cancellationToken = default) {
        // the /build/builds/{id} endpoint does not require pipeline ID, unlike /pipelines/{id}/runs/{id}
        return (await InvokeAzApi<BuildInfo>($"/build/builds/{runId}?api-version=7.1", cancellationToken)).ToPipelineRun();
    }

    public async Task<Timeline> GetTimeline(int runId, CancellationToken cancellationToken = default) {
        return await InvokeAzApi<Timeline>($"/build/builds/{runId}/timeline", cancellationToken);
    }

    private record AdoList<T>(int Count, T[] Value);

    private record BuildInfo(int Id, string BuildNumber, string Status, string Result, Hashtable? TemplateParameters, DateTime QueueTime, DateTime FinishTime, Pipeline Definition) {
        public PipelineRun ToPipelineRun() => new(Id, BuildNumber, Status, TemplateParameters, Definition, QueueTime, FinishTime, Result);
    }

    private class ThrottlingDelegatingHandler(int maxConcurrency) : DelegatingHandler {
        private readonly SemaphoreSlim _limitSemaphore = new(maxConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            await _limitSemaphore.WaitAsync(cancellationToken);
            try {
                return await base.SendAsync(request, cancellationToken);
            } finally {
                _limitSemaphore.Release();
            }
        }
    }
}

internal record Pipeline(int Id, string Name, int Revision);
internal record PipelineRun(
    int Id,
    string Name,
    string State,
    Hashtable? TemplateParameters,
    Pipeline Pipeline,
    [property: JsonPropertyName("createdDate")]
    DateTime StartTime,
    [property: JsonPropertyName("finishedDate")]
    DateTime? FinishTime = null,
    string? Result = null
);
internal record Timeline(Guid Id, TimelineRecord[] Records);
internal record LogInfo(int Id, string Type, string Url);
internal record TaskInfo(Guid Id, string Name, string Version);
internal record Issue(
    string Type,
    string? Category,
    string Message,
    Hashtable? Data = null
);
internal record TimelineRecord(
    Guid Id,
    Guid? ParentId,
    string Type,
    string Name,
    string? RefName,
    DateTime? StartTime,
    DateTime? FinishTime,
    string State,
    string Result,
    string? WorkerName,
    LogInfo? Log,
    TaskInfo? Task,
    int Attempt,
    string? Identifier,

    int ErrorCount,
    int WarningCount,
    Issue[]? Issues = null
);

internal class PowerShellJsonConverter : JsonConverter<object> {
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.StartObject) {
            var converter = options.GetConverter(typeof(Hashtable)) as JsonConverter<Hashtable>;
            return converter?.Read(ref reader, typeof(Hashtable), options);
        }

        if (reader.TokenType == JsonTokenType.StartArray) {
            var converter = options.GetConverter(typeof(object[])) as JsonConverter<object[]>;
            return converter?.Read(ref reader, typeof(object[]), options);
        }

        return reader.TokenType switch {
            JsonTokenType.Null => null,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number when reader.TryGetInt64(out long l) => l,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String when reader.TryGetDateTime(out DateTime datetime) => datetime,
            JsonTokenType.String => reader.GetString(),
            _ => throw new NotSupportedException($"Unsupported JSON token: {reader.TokenType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, object objectToWrite, JsonSerializerOptions options) {
        JsonSerializer.Serialize(writer, objectToWrite, objectToWrite.GetType(), options);
    }
}
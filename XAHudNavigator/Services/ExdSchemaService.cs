using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace XAHudNavigator.Services;

public sealed class ExdSchemaService
{
    private const string LatestSchemaRefUrl = "https://api.github.com/repos/xivdev/EXDSchema/contents/schemas/latest?ref=main";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly Dictionary<string, ExdSchemaSheetInfo?> schemaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> statusCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly IDeserializer yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private string? latestSchemaRef;
    private Dictionary<string, List<ExdSchemaColumnInfo>>? columnsCache;

    public ExdSchemaSheetInfo? TryGetSheetInfo(string sheetName, out string status)
    {
        lock (gate)
        {
            if (schemaCache.TryGetValue(sheetName, out var cached))
            {
                status = statusCache.TryGetValue(sheetName, out var cachedStatus)
                    ? cachedStatus
                    : cached != null ? $"EXDSchema loaded ({cached.SourceRef[..Math.Min(8, cached.SourceRef.Length)]})" : "EXDSchema unavailable";
                return cached;
            }
        }

        try
        {
            var schemaRef = GetLatestSchemaRef();
            var schemaYaml = GetGitHubText($"{sheetName}.yml", schemaRef);
            var schema = yamlDeserializer.Deserialize<ExdSchemaSheetInfo>(schemaYaml) ?? new ExdSchemaSheetInfo();
            schema.SourceRef = schemaRef;
            schema.FlattenedFields = BuildFlattenedFields(schema, GetColumnsForSheet(sheetName, schemaRef));

            var okStatus = $"EXDSchema loaded ({schemaRef[..Math.Min(8, schemaRef.Length)]}) • {schema.FlattenedFields.Count} formatted fields";
            lock (gate)
            {
                schemaCache[sheetName] = schema;
                statusCache[sheetName] = okStatus;
            }

            status = okStatus;
            return schema;
        }
        catch (Exception ex)
        {
            var failStatus = $"EXDSchema unavailable: {ex.Message}";
            lock (gate)
            {
                schemaCache[sheetName] = null;
                statusCache[sheetName] = failStatus;
            }

            status = failStatus;
            return null;
        }
    }

    public void Invalidate(string? sheetName = null)
    {
        lock (gate)
        {
            if (string.IsNullOrEmpty(sheetName))
            {
                schemaCache.Clear();
                statusCache.Clear();
                columnsCache = null;
                latestSchemaRef = null;
                return;
            }

            schemaCache.Remove(sheetName);
            statusCache.Remove(sheetName);
        }
    }

    private string GetLatestSchemaRef()
    {
        lock (gate)
        {
            if (!string.IsNullOrEmpty(latestSchemaRef))
                return latestSchemaRef;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestSchemaRefUrl);
        using var response = HttpClient.Send(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var schemaRef = doc.RootElement.GetProperty("sha").GetString();
        if (string.IsNullOrWhiteSpace(schemaRef))
            throw new InvalidOperationException("EXDSchema latest ref was empty.");

        lock (gate)
        {
            latestSchemaRef ??= schemaRef;
            return latestSchemaRef;
        }
    }

    private string GetGitHubText(string path, string schemaRef)
    {
        var url = $"https://api.github.com/repos/xivdev/EXDSchema/contents/{path}?ref={schemaRef}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = HttpClient.Send(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var content = doc.RootElement.GetProperty("content").GetString();
        var encoding = doc.RootElement.GetProperty("encoding").GetString();
        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException($"Unexpected GitHub encoding for {path}.");

        var bytes = Convert.FromBase64String(content.Replace("\n", string.Empty).Replace("\r", string.Empty));
        return Encoding.UTF8.GetString(bytes);
    }

    private Dictionary<string, List<ExdSchemaColumnInfo>> GetColumnsCache(string schemaRef)
    {
        lock (gate)
        {
            if (columnsCache != null)
                return columnsCache;
        }

        var columnsYaml = GetGitHubText(".github/columns.yml", schemaRef);
        var parsed = yamlDeserializer.Deserialize<Dictionary<string, List<ExdSchemaColumnInfo>>>(columnsYaml)
                     ?? new Dictionary<string, List<ExdSchemaColumnInfo>>(StringComparer.OrdinalIgnoreCase);

        var normalized = new Dictionary<string, List<ExdSchemaColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in parsed)
            normalized[kvp.Key] = kvp.Value ?? new List<ExdSchemaColumnInfo>();

        lock (gate)
        {
            columnsCache ??= normalized;
            return columnsCache;
        }
    }

    private List<ExdSchemaColumnInfo> GetColumnsForSheet(string sheetName, string schemaRef)
    {
        var cache = GetColumnsCache(schemaRef);
        return cache.TryGetValue(sheetName, out var columns)
            ? columns
            : new List<ExdSchemaColumnInfo>();
    }

    private static List<ExdSchemaFlatField> BuildFlattenedFields(ExdSchemaSheetInfo sheet, List<ExdSchemaColumnInfo> columns)
    {
        var result = new List<ExdSchemaFlatField>();
        var columnIndex = 0;
        FlattenFields(sheet.Fields ?? new List<ExdSchemaField>(), string.Empty, result, columns, ref columnIndex);
        return result;
    }

    private static void FlattenFields(IReadOnlyList<ExdSchemaField> fields, string prefix, List<ExdSchemaFlatField> result, List<ExdSchemaColumnInfo> columns, ref int columnIndex)
    {
        var unnamedCounter = 1;
        foreach (var field in fields)
        {
            var fieldName = string.IsNullOrWhiteSpace(field.Name) ? $"Unknown{unnamedCounter++}" : field.Name!;
            FlattenField(field, fieldName, prefix, result, columns, ref columnIndex);
        }
    }

    private static void FlattenField(ExdSchemaField field, string fieldName, string prefix, List<ExdSchemaFlatField> result, List<ExdSchemaColumnInfo> columns, ref int columnIndex)
    {
        var typeName = string.IsNullOrWhiteSpace(field.Type) ? "scalar" : field.Type!;
        if (string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase))
        {
            var count = Math.Max(1, field.Count ?? 1);
            if (field.Fields == null || field.Fields.Count == 0)
            {
                for (var i = 0; i < count; i++)
                    AddLeaf(result, columns, ref columnIndex, BuildPath(prefix, $"{fieldName}[{i}]"), "scalar", field.Comment, null, null);
                return;
            }

            if (field.Fields.Count == 1 && string.IsNullOrWhiteSpace(field.Fields[0].Name) && !string.Equals(field.Fields[0].Type, "array", StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < count; i++)
                {
                    var inner = field.Fields[0];
                    var innerType = string.IsNullOrWhiteSpace(inner.Type) ? "scalar" : inner.Type!;
                    AddLeaf(result, columns, ref columnIndex, BuildPath(prefix, $"{fieldName}[{i}]"), innerType, inner.Comment ?? field.Comment, inner.Targets, inner.Condition);
                }
                return;
            }

            for (var i = 0; i < count; i++)
                FlattenFields(field.Fields, BuildPath(prefix, $"{fieldName}[{i}]"), result, columns, ref columnIndex);

            return;
        }

        AddLeaf(result, columns, ref columnIndex, BuildPath(prefix, fieldName), typeName, field.Comment, field.Targets, field.Condition);
    }

    private static void AddLeaf(List<ExdSchemaFlatField> result, List<ExdSchemaColumnInfo> columns, ref int columnIndex, string path, string typeName, string? comment, List<string>? targets, ExdSchemaCondition? condition)
    {
        var column = columnIndex < columns.Count ? columns[columnIndex] : null;
        result.Add(new ExdSchemaFlatField
        {
            ColumnIndex = columnIndex,
            Path = path,
            Type = typeName,
            Comment = comment ?? string.Empty,
            Targets = targets ?? new List<string>(),
            Condition = condition,
            ColumnType = column?.Type ?? string.Empty,
            Offset = column?.Offset,
        });
        columnIndex++;
    }

    private static string BuildPath(string prefix, string segment)
    {
        if (string.IsNullOrEmpty(prefix))
            return segment;
        if (segment.StartsWith("[", StringComparison.Ordinal))
            return prefix + segment;
        return prefix + "." + segment;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"XAHudNavigator/{XAHudNavigator.BuildInfo.Version}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

public sealed class ExdSchemaSheetInfo
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "displayField")]
    public string? DisplayField { get; set; }

    [YamlMember(Alias = "fields")]
    public List<ExdSchemaField> Fields { get; set; } = new();

    public string SourceRef { get; set; } = string.Empty;
    public List<ExdSchemaFlatField> FlattenedFields { get; set; } = new();
}

public sealed class ExdSchemaField
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "count")]
    public int? Count { get; set; }

    [YamlMember(Alias = "comment")]
    public string? Comment { get; set; }

    [YamlMember(Alias = "fields")]
    public List<ExdSchemaField>? Fields { get; set; }

    [YamlMember(Alias = "targets")]
    public List<string>? Targets { get; set; }

    [YamlMember(Alias = "condition")]
    public ExdSchemaCondition? Condition { get; set; }
}

public sealed class ExdSchemaCondition
{
    [YamlMember(Alias = "switch")]
    public string? Switch { get; set; }

    [YamlMember(Alias = "cases")]
    public Dictionary<string, List<string>>? Cases { get; set; }
}

public sealed class ExdSchemaColumnInfo
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [YamlMember(Alias = "offset")]
    public int Offset { get; set; }
}

public sealed class ExdSchemaFlatField
{
    public int ColumnIndex { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public List<string> Targets { get; set; } = new();
    public ExdSchemaCondition? Condition { get; set; }
    public string ColumnType { get; set; } = string.Empty;
    public int? Offset { get; set; }
}

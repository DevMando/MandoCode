using System.Globalization;
using System.Text.Json;

namespace MandoCode.Acp.JsonRpc;

/// <summary>
/// A JSON-RPC request id: a number or a string. Incoming ids are kept exactly as received so the
/// response echoes them unchanged (a peer may use string ids even though this client sends numbers).
/// </summary>
public readonly struct JsonRpcId : IEquatable<JsonRpcId>
{
    private readonly string? _raw;

    private JsonRpcId(string raw, bool isString)
    {
        _raw = raw;
        IsString = isString;
    }

    public bool IsString { get; }

    /// <summary>The id as text: the string value, or the number's literal text.</summary>
    public string Text => _raw ?? "";

    public static JsonRpcId FromNumber(long value) => new(value.ToString(CultureInfo.InvariantCulture), false);

    public static JsonRpcId FromString(string value) => new(value, true);

    /// <summary>Reads an id from a message; returns false for a missing, null or non-scalar id.</summary>
    public static bool TryRead(JsonElement element, out JsonRpcId id)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                id = new JsonRpcId(element.GetRawText(), false);
                return true;
            case JsonValueKind.String:
                id = new JsonRpcId(element.GetString() ?? "", true);
                return true;
            default:
                id = default;
                return false;
        }
    }

    /// <summary>The id as a long, if it is an integral number (the only kind this client sends).</summary>
    public bool TryGetInt64(out long value)
    {
        if (!IsString && long.TryParse(_raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            return true;

        value = 0;
        return false;
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        if (IsString)
            writer.WriteStringValue(_raw);
        else
            writer.WriteRawValue(_raw ?? "0");
    }

    public bool Equals(JsonRpcId other) => IsString == other.IsString && string.Equals(_raw, other._raw, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is JsonRpcId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(IsString, _raw);
    public override string ToString() => IsString ? $"\"{_raw}\"" : _raw ?? "";
}

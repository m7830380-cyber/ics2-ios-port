using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// The game's save (FileBasedPrefs JSON: StringData / IntData / FloatData / BoolData lists of Key/Value).
sealed class SaveJson
{
    readonly JsonObject m_Root;

    SaveJson(JsonObject root) { m_Root = root; }

    public static SaveJson Parse(string json) => new SaveJson(JsonNode.Parse(json).AsObject());

    public string ToJson() => m_Root.ToJsonString();

    public int Count => new[] { "StringData", "IntData", "FloatData", "BoolData" }.Sum(k => (m_Root[k] as JsonArray)?.Count ?? 0);

    JsonObject Find(string list, string key) =>
        (m_Root[list] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => (string)o["Key"] == key);

    public string Str(string key) => (string)Find("StringData", key)?["Value"];
    public int Int(string key) => (int?)Find("IntData", key)?["Value"] ?? 0;
    public float Float(string key) => (float?)Find("FloatData", key)?["Value"] ?? 0f;

    public void SetStr(string key, string v) => Set("StringData", key, JsonValue.Create(v));
    public void SetInt(string key, int v) => Set("IntData", key, JsonValue.Create(v));
    public void SetFloat(string key, float v) => Set("FloatData", key, JsonValue.Create(v));

    void Set(string list, string key, JsonNode value)
    {
        var item = Find(list, key);
        if (item != null) { item["Value"] = value; return; }
        if (!(m_Root[list] is JsonArray arr)) m_Root[list] = arr = new JsonArray();
        arr.Add(new JsonObject { ["Key"] = key, ["Value"] = value });
    }

    /// The game's on-disk form: each char XOR "my-secret" (FBPPConfig.EncryptionSecret in SaveManager).
    public static string Scramble(string data)
    {
        const string secret = "my-secret";
        var sb = new StringBuilder(data.Length);
        for (int i = 0; i < data.Length; i++) sb.Append((char)(data[i] ^ secret[i % secret.Length]));
        return sb.ToString();
    }
}

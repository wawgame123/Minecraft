using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerLauncher.Models;

public sealed class NewsItem
{
    public const string TextKind = "text";
    public const string ImageKind = "image";
    public const string HtmlKind = "html";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = TextKind;

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Title) ? Text : Title;
    }
}

public sealed class NewsItemListJsonConverter : JsonConverter<List<NewsItem>>
{
    public override List<NewsItem> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new List<NewsItem>();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("news must be an array.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return result;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString() ?? "";
                result.Add(new NewsItem
                {
                    Title = text,
                    Text = text,
                    Kind = NewsItem.TextKind
                });
                continue;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var item = JsonSerializer.Deserialize<NewsItem>(ref reader, options);
                if (item is not null)
                {
                    result.Add(item);
                }

                continue;
            }

            reader.Skip();
        }

        throw new JsonException("Unexpected end of news array.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<NewsItem> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndArray();
    }
}

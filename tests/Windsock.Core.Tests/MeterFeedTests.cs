using Windsock.Core.Meter;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Reading one value from a JSON document, and which addresses may be read.</summary>
public sealed class MeterFeedTests
{
    private const string Document = """
        {
          "status": "ok",
          "data": {
            "price": 64123.4567891,
            "items": [ { "name": "first", "value": 1 }, { "name": "last", "value": 2.5 } ],
            "a.b": "dotted",
            "flag": true,
            "empty": null,
            "quote": "a long piece of text that will not fit on a taskbar at all"
          }
        }
        """;

    [Theory]
    [InlineData("status", "ok")]
    [InlineData("data.price", "64123.5")]
    [InlineData("$.data.price", "64123.5")]
    [InlineData("$data.price", "64123.5")]
    [InlineData("data.items[0].name", "first")]
    [InlineData("data.items[-1].value", "2.5")]
    [InlineData("data[\"a.b\"]", "dotted")]
    [InlineData("data['a.b']", "dotted")]
    [InlineData("data.flag", "true")]
    [InlineData("data.empty", "—")]
    public void AField_IsFoundByItsPath(string path, string value)
    {
        FieldReading read = JsonField.Read(Document, path);

        Assert.Null(read.Problem);
        Assert.Equal(value, read.Value);
    }

    [Fact]
    public void ANumber_IsKeptAsANumber_ForColourLevels()
    {
        Assert.Equal(64123.4567891, JsonField.Read(Document, "data.price").Number!.Value, 6);
        Assert.Equal(2.5, JsonField.Read("{ \"v\": \"2.5\" }", "v").Number);
    }

    [Fact]
    public void AStringTooLongForATaskbar_IsCutShort()
    {
        string value = JsonField.Read(Document, "data.quote").Value!;

        Assert.Equal(JsonField.Longest, value.Length);
        Assert.EndsWith("…", value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data.missing", "no field")]
    [InlineData("data.items[5]", "no item 5")]
    [InlineData("status.deeper", "no field")]
    [InlineData("data.items", "list or an object")]
    [InlineData("data", "list or an object")]
    public void AFieldThatIsNotAValue_SaysWhy(string path, string why)
    {
        FieldReading read = JsonField.Read(Document, path);

        Assert.Null(read.Value);
        Assert.Contains(why, read.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData("a.")]
    [InlineData("a[")]
    [InlineData("a[x]")]
    [InlineData(null)]
    public void APathNotWrittenAsOne_IsRefused(string? path)
    {
        Assert.False(JsonField.IsPath(path));
        Assert.NotNull(JsonField.Read(Document, path).Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"a\": ")]
    public void AnAnswerThatIsNotJson_SaysSo(string json)
    {
        Assert.NotNull(JsonField.Read(json, "a").Problem);
    }

    [Fact]
    public void ADocumentNestedBeyondReason_IsNotRead()
    {
        string deep = new string('[', 200) + new string(']', 200);

        Assert.Contains("not JSON", JsonField.Read(deep, "a").Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.example.com/v1/price", FeedKind.Web)]
    [InlineData("https://192.168.1.1/status", FeedKind.Local)]
    [InlineData("http://localhost:8080/metrics.json", FeedKind.Local)]
    [InlineData("http://127.0.0.1/x", FeedKind.Local)]
    [InlineData("http://192.168.0.10/api", FeedKind.Local)]
    [InlineData("http://10.1.2.3/api", FeedKind.Local)]
    [InlineData("http://172.20.0.5/api", FeedKind.Local)]
    [InlineData("http://[::1]:9000/x", FeedKind.Local)]
    [InlineData("http://[fd12:3456::1]/x", FeedKind.Local)]
    [InlineData(@"C:\Data\reading.json", FeedKind.File)]
    [InlineData("file:///C:/Data/reading.json", FeedKind.File)]
    public void AnAddressThatMayBeRead_SaysWhatItIs(string address, FeedKind kind)
    {
        (FeedKind? found, string? problem) = FeedAddress.Check(address);

        Assert.Null(problem);
        Assert.Equal(kind, found);
    }

    /// <summary>
    /// Plain http only stays on this PC or the local network; anything beyond
    /// would cross the internet in the clear.
    /// </summary>
    [Theory]
    [InlineData("http://example.com/x.json")]
    [InlineData("http://8.8.8.8/x")]
    [InlineData("http://172.32.0.1/x")]
    [InlineData("ftp://example.com/x")]
    [InlineData("data.json")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAddressThatMayNotBeRead_SaysWhy(string address)
    {
        (FeedKind? kind, string? problem) = FeedAddress.Check(address);

        Assert.Null(kind);
        Assert.NotNull(problem);
    }
}

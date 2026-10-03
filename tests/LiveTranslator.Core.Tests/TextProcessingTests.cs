using System.Text;
using System.Text.Json.Nodes;

using LiveTranslator.Core.Http;
using LiveTranslator.Core.Pipeline;

namespace LiveTranslator.Core.Tests;

public class SegmenterTests
{
    [Fact]
    public void Splits_complete_sentences_and_keeps_partial()
    {
        var s = Segmenter.Split("Hello there. How are you? I'm fine! And then");
        Assert.Equal(["Hello there.", "How are you?", "I'm fine!"], s.Complete);
        Assert.Equal("And then", s.Partial);
    }

    [Fact]
    public void Does_not_split_decimals_abbreviations_or_inside_cjk_runs()
    {
        var s = Segmenter.Split("It costs 3.5 dollars, said Mr. Smith. 你好。今天天气");
        Assert.Equal(["It costs 3.5 dollars, said Mr. Smith.", "你好。"], s.Complete);
        Assert.Equal("今天天气", s.Partial);
    }

    [Fact]
    public void Groups_repeated_punctuation_and_closing_quotes()
    {
        var s = Segmenter.Split("Really?! \"Yes.\" Next");
        Assert.Equal(["Really?!", "\"Yes.\""], s.Complete);
        Assert.Equal("Next", s.Partial);
    }
}

public class TextNormalizerTests
{
    [Theory]
    [InlineData("the U.S. economy", "the US economy")]
    [InlineData("hello ,world .", "hello, world.")]
    [InlineData("你好 。 世界", "你好。世界")]
    [InlineData("pi is 3.14", "pi is 3.14")]
    public void Normalizes_spacing_and_acronyms(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Long_unpunctuated_line_becomes_a_sentence_and_short_line_is_joined()
    {
        var text = TextNormalizer.Normalize("this is a fairly long line without any punctuation\nshort one\nnext");
        Assert.Equal("this is a fairly long line without any punctuation. short one, next", text);
    }

    [Fact]
    public void Empty_lines_do_not_crash() => Assert.Equal("a, b", TextNormalizer.Normalize("a\n\n\nb"));
}

public class TextKeyTests
{
    [Fact]
    public void Same_utterance_detection()
    {
        Assert.True(TextKey.IsSameUtterance(TextKey.Of("Hello."), TextKey.Of("Hello, world.")));
        Assert.True(TextKey.IsSameUtterance(TextKey.Of("I want to go there today"), TextKey.Of("I want to go their today")));
        Assert.False(TextKey.IsSameUtterance(TextKey.Of("Good morning everyone"), TextKey.Of("The weather is bad")));
        Assert.Equal(4, TextKey.WeightedLength("你好"));
    }
}

public class ThinkTagFilterTests
{
    [Fact]
    public void Removes_think_block_split_across_chunks()
    {
        var f = new ThinkTagFilter();
        var output = new StringBuilder();
        foreach (var chunk in new[] { "<thi", "nk>reason", "ing</th", "ink>\n\n你", "好<", "b>" })
            output.Append(f.Push(chunk));
        output.Append(f.Flush());
        Assert.Equal("\n\n你好<b>", output.ToString());
    }

    [Fact]
    public void Plain_text_passes_through_immediately()
    {
        var f = new ThinkTagFilter();
        Assert.Equal("hello", f.Push("hello"));
        Assert.Equal("", f.Flush());
    }
}

public class JsonMergeTests
{
    [Fact]
    public void Deep_merges_objects_replaces_values_and_null_removes()
    {
        var target = JsonNode.Parse("""{"a":1,"cfg":{"x":1,"y":2},"drop":true}""")!.AsObject();
        JsonMerge.DeepMerge(target, JsonMerge.ParseObject("""{"a":2,"cfg":{"y":3,"z":4},"drop":null, /* comment */ }"""));
        Assert.Equal("""{"a":2,"cfg":{"x":1,"y":3,"z":4}}""", target.ToJsonString());
    }

    [Fact]
    public void Blank_is_empty_and_non_object_is_rejected()
    {
        Assert.Empty(JsonMerge.ParseObject("  "));
        Assert.Throws<FormatException>(() => JsonMerge.ParseObject("42"));
    }
}

public class StreamReaderTests
{
    [Fact]
    public async Task Sse_handles_crlf_comments_multiline_and_missing_terminator()
    {
        var raw = ": keep-alive\r\nevent: delta\r\ndata: line1\r\ndata: line2\r\n\r\ndata: {\"a\":1}\ndata: {\"b\":2}\n\ndata: tail";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var events = await TestData.Collect(StreamReaders.ReadSseDataAsync(stream));
        Assert.Equal(["line1\nline2", "{\"a\":1}", "{\"b\":2}", "tail"], events);
    }

    [Fact]
    public async Task Ndjson_skips_blank_lines()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}\n\n{\"b\":2}\n"));
        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], await TestData.Collect(StreamReaders.ReadLinesAsync(stream)));
    }
}

public class TranslationCacheTests
{
    [Fact]
    public void Evicts_least_recently_used()
    {
        var cache = new TranslationCache(2);
        cache.Set("a", "1");
        cache.Set("b", "2");
        Assert.True(cache.TryGet("a", out _)); // a is now most recent
        cache.Set("c", "3");
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal("1", a);
    }
}

using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// TextDecoder's UTF-8 path. Since upstream 004bcdd it is a native op: malformed input
/// becomes U+FFFD as WHATWG specifies (the pure-JS decoder before it produced garbage), and
/// a large body still decodes in one copy (M4: the original decoder built a V8 rope of one
/// cons cell per character, several GB for a 100 MB body).
/// </summary>
public sealed class Utf8DecodeTests
{
    // Port of upstream 004bcdd's text_decoder_replaces_malformed_utf8_and_preserves_bom_options.
    [Fact]
    public void TextDecoderReplacesMalformedUtf8AndPreservesBomOptions()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        string result = fixture.Runtime.Evaluate("""
            JSON.stringify((() => {
              const cases = [
                [[0x80], '�'],
                [[0xc0, 0xaf], '��'],
                [[0xe0, 0x80, 0xaf], '���'],
                [[0xed, 0xa0, 0x80], '���'],
                [[0xf4, 0x90, 0x80, 0x80], '����'],
                [[0xf5, 0x80, 0x80, 0x80], '����'],
                [[0xe2, 0x82], '�'],
                [[0xf0, 0x9f, 0x92], '�'],
                [[0xc2, 0x41], '�A'],
                [[0xe2, 0x28, 0xa1], '�(�'],
              ];
              for (const [bytes, expected] of cases) {
                const buffer = new Uint8Array([99, ...bytes, 99]);
                const view = new DataView(buffer.buffer, 1, bytes.length);
                const actual = new TextDecoder().decode(view);
                if (actual !== expected) return {bytes, actual, expected};
                try { new TextDecoder('utf-8', {fatal:true}).decode(view); return 'fatal accepted'; }
                catch (error) { if (!(error instanceof TypeError)) return error.name; }
              }
              const bytes = new Uint8Array([0xef,0xbb,0xbf,65,0,34,92,10,0xc3,0xa9,0xf0,0x9f,0x98,0x80]);
              const expected = 'A\0"\\\né😀';
              const decoder = new TextDecoder('utf8', {fatal:true});
              return [new TextDecoder().decode(bytes) === expected,
                new TextDecoder('utf8', {ignoreBOM:true}).decode(bytes) === '﻿' + expected,
                decoder.decode(bytes) === expected, decoder.decode(new Uint8Array()) === ''];
            })())
            """)?.ToString() ?? "null";
        Assert.Equal("[true,true,true,true]", result);
    }

    [Fact]
    public void LargeAndWellFormedBodiesDecodeUnchanged()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        string result = fixture.Runtime.Evaluate("""
            (() => {
              const text = 'a\u{1F600}"\\é中'.repeat(5000);
              if (new TextDecoder().decode(new TextEncoder().encode(text)) !== text) return 'mismatch';
              return String(new TextDecoder().decode(new Uint8Array(2000000).fill(0x61)).length);
            })()
            """)?.ToString() ?? "null";
        Assert.Equal("2000000", result);
    }
}

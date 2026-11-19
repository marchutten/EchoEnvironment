using System.Text;
using EchoEnvironment.Hardware;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Characterization tests for <see cref="LineFramer"/> (Phase 3a).
///
/// 3a's rule was "do not change framing semantics while writing this", and the plan flagged the two
/// unpinned behaviours specifically: what a "\r\n" line ending does, and what happens on overflow.
/// Both are pinned here as CURRENT behaviour, not as approved behaviour. The central test is
/// <see cref="MatchesTheOldMainWindowFramer_LineForLine"/>, which replays the same chunk sequence
/// through a literal copy of the algorithm MainWindow used before the seam existed and requires the
/// two to agree line-for-line. That is what makes 3a provably behaviour-neutral rather than
/// "looks the same".
/// </summary>
public class LineFramerTests
{
    private static string FrameLine(int count) =>
        string.Join(",", Enumerable.Range(100, count));

    // -----------------------------------------------------------------------------------------
    // The old algorithm, copied verbatim from MainWindow before Phase 3, so the new one can be
    // compared against it. This is a test fixture, not production code.
    // -----------------------------------------------------------------------------------------

    private static int OldIndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] is '\n' or '\r')
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> OldFramer(IEnumerable<string> chunks, out long droppedBytes)
    {
        var buffer = new StringBuilder();
        var lines = new List<string>();
        droppedBytes = 0;

        foreach (string chunk in chunks)
        {
            buffer.Append(chunk);
            int newline;
            while ((newline = OldIndexOfNewline(buffer)) >= 0)
            {
                lines.Add(buffer.ToString(0, newline));
                buffer.Remove(0, newline + 1);
            }

            if (buffer.Length > 10000)
            {
                droppedBytes += buffer.Length;
                buffer.Clear();
            }
        }

        return lines;
    }

    private static List<string> NewFramer(IEnumerable<string> chunks, out long droppedBytes)
    {
        var framer = new LineFramer();
        var lines = new List<string>();

        foreach (string chunk in chunks)
        {
            framer.Append(chunk);
            while (framer.TryReadLine(out string line))
            {
                lines.Add(line);
            }

            framer.TrimOverflow();
        }

        droppedBytes = framer.DroppedBytes;
        return lines;
    }

    // -----------------------------------------------------------------------------------------
    // The equivalence claim, tested directly.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The same chunk sequence through the old MainWindow algorithm and through LineFramer must
    /// produce the same lines in the same order, and the same overflow drop count. If 3a changed any
    /// framing semantics, this is the test that says so.
    /// </summary>
    [Fact]
    public void MatchesTheOldMainWindowFramer_LineForLine()
    {
        List<string> chunks = BuildChunkSequence();

        List<string> expected = OldFramer(chunks, out long expectedDropped);
        List<string> actual = NewFramer(chunks, out long actualDropped);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal<string>(expected, actual);
        Assert.Equal(expectedDropped, actualDropped);

        // A guard that never fires is decoration (plan risk register, High/High). Prove the sequence
        // actually exercised both the multi-line-per-chunk path and the overflow path.
        Assert.True(expected.Count > 100, $"sequence produced only {expected.Count} lines");
        Assert.True(expectedDropped > 0, "sequence never overflowed the buffer");
    }

    [Fact]
    public void MatchesTheOldMainWindowFramer_OnCarriageReturnOnlyLineEndings()
    {
        // A device that terminates with bare '\r' is legal for the old code, and LineFramer must
        // treat it identically rather than assuming '\n'.
        string[] chunks = ["100,101\r102,103\r", "104,105\r"];

        Assert.Equal<string>(OldFramer(chunks, out _), NewFramer(chunks, out _));
    }

    // -----------------------------------------------------------------------------------------
    // The two behaviours the plan called unpinned.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CarriageReturnNewline_YieldsTheLine_AndThenAnEmptyLine()
    {
        // CHARACTERIZATION, NOT APPROVAL. A terminator is a single '\n' OR a single '\r', so "\r\n"
        // consumes the '\r' as the terminator and leaves the '\n' to terminate an empty line. The old
        // code did exactly this, and the app never notices because blank lines are filtered by the
        // caller before they reach the parser. "Fixing" it here would be a framing change smuggled
        // into a structural move.
        var framer = new LineFramer();
        framer.Append("100,101\r\n");

        Assert.True(framer.TryReadLine(out string first));
        Assert.Equal("100,101", first);

        Assert.True(framer.TryReadLine(out string second));
        Assert.Equal(string.Empty, second);

        Assert.False(framer.TryReadLine(out _));
    }

    [Fact]
    public void CarriageReturnNewline_BeforeAnotherFrame_StillKeepsTheNextFrame()
    {
        var framer = new LineFramer();
        framer.Append("100,101\r\n102,103\n");

        Assert.True(framer.TryReadLine(out string a));
        Assert.True(framer.TryReadLine(out string b));
        Assert.Equal("100,101", a);
        Assert.Equal(string.Empty, b);
        Assert.Equal("102,103", Assert.Single(ReadAll(framer)));
    }

    [Fact]
    public void Overflow_DiscardsTheUnreadRemainder_AndCountsIt()
    {
        // The old code cleared the buffer past 10 000 characters and told nobody. LineFramer must do
        // the same thing, but observably.
        var framer = new LineFramer();
        framer.Append(new string('7', 10_001));

        Assert.False(framer.TryReadLine(out _));
        Assert.Equal(10_001, framer.BufferedCharacters);

        framer.TrimOverflow();

        Assert.Equal(10_001, framer.DroppedBytes);
        Assert.Equal(0, framer.BufferedCharacters);
    }

    [Fact]
    public void Overflow_DoesNotDiscardCompleteLinesAlreadyDrained()
    {
        // WHY TrimOverflow IS A SEPARATE CALL rather than a check inside Append (deviation from 3a's
        // sketch): the old code checked AFTER draining every complete line in the chunk. A chunk
        // holding three complete frames plus a wall of garbage delivered those three frames. Dropping
        // inside Append would have thrown them away, which is a behaviour change.
        var framer = new LineFramer();
        framer.Append("100,101\n102,103\n104,105\n" + new string('9', 10_001));

        Assert.Equal<string>(new[] { "100,101", "102,103", "104,105" }, ReadAll(framer));

        framer.TrimOverflow();
        Assert.Equal(10_001, framer.DroppedBytes);
    }

    [Fact]
    public void OverflowGuard_IsOffByDefaultOnlyPastTheLimit_AndCanBeDisabled()
    {
        Assert.Equal(LineFramer.DefaultMaxBuffer, new LineFramer().MaxBuffer);

        var disabled = new LineFramer { MaxBuffer = 0 };
        disabled.Append(new string('7', 50_000));
        disabled.TrimOverflow();

        Assert.Equal(0, disabled.DroppedBytes);
        Assert.Equal(50_000, disabled.BufferedCharacters);
    }

    [Fact]
    public void DroppedBytes_AccumulatesAcrossSeparateOverflows()
    {
        var framer = new LineFramer();
        for (int i = 0; i < 3; i++)
        {
            framer.Append(new string('7', 10_001));
            framer.TrimOverflow();
        }

        Assert.Equal(30_003, framer.DroppedBytes);
    }

    [Fact]
    public void Clear_IsNotCountedAsDropped()
    {
        // A deliberate reset (a new connection) must not be reported as data loss.
        var framer = new LineFramer();
        framer.Append("100,101");
        framer.Clear();

        Assert.Equal(0, framer.DroppedBytes);
        Assert.Equal(0, framer.BufferedCharacters);
    }

    // -----------------------------------------------------------------------------------------
    // Chunk-boundary behaviour — the reason a framer is needed at all.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void LineSplitAcrossChunks_IsReassembledExactlyOnce()
    {
        var framer = new LineFramer();
        framer.Append("100,101,10");
        Assert.False(framer.TryReadLine(out _));

        framer.Append("2,103\n");
        Assert.True(framer.TryReadLine(out string line));
        Assert.Equal("100,101,102,103", line);

        Assert.False(framer.TryReadLine(out _));
    }

    [Fact]
    public void MultipleLinesInOneChunk_AreReadInOrder()
    {
        var framer = new LineFramer();
        framer.Append("100\n101\n102\n");

        Assert.Equal<string>(new[] { "100", "101", "102" }, ReadAll(framer));
    }

    [Fact]
    public void UnterminatedTail_StaysInBufferForTheNextChunk()
    {
        var framer = new LineFramer();
        framer.Append("100\n200,201");

        Assert.Equal<string>(new[] { "100" }, ReadAll(framer));
        Assert.Equal(7, framer.BufferedCharacters);

        framer.Append("\n");
        Assert.Equal<string>(new[] { "200,201" }, ReadAll(framer));
    }

    [Fact]
    public void TryReadPendingLine_ReturnsTheUnterminatedRemainder_OnceOnly()
    {
        // Replay-only drain: a log file has an end, a serial port does not.
        var framer = new LineFramer();
        framer.Append("100,101\n102,103");

        Assert.Equal<string>(new[] { "100,101" }, ReadAll(framer));

        Assert.True(framer.TryReadPendingLine(out string tail));
        Assert.Equal("102,103", tail);
        Assert.False(framer.TryReadPendingLine(out _));
    }

    [Fact]
    public void TryReadPendingLine_ReturnsFalseWhenNothingIsPending()
    {
        var framer = new LineFramer();
        framer.Append("100,101\n");

        Assert.True(framer.TryReadLine(out _));
        Assert.False(framer.TryReadPendingLine(out string line));
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public void AppendOfNullOrEmpty_IsANoOp()
    {
        var framer = new LineFramer();
        framer.Append("");
        framer.Append(null!);

        Assert.Equal(0, framer.BufferedCharacters);
        Assert.False(framer.TryReadLine(out _));
    }

    [Fact]
    public void ManyLinesInOneLargeChunk_AreAllReturned_AndNothingIsLost()
    {
        // The cursor-based read must not skip or duplicate a line when a single chunk carries
        // hundreds of them, which is the case the old O(n²) Remove() handled worst.
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            sb.Append(FrameLine(16)).Append('\n');
        }

        var framer = new LineFramer();
        framer.Append(sb.ToString());

        List<string> lines = ReadAll(framer);
        Assert.Equal(500, lines.Count);
        Assert.Equal<string>(Enumerable.Range(0, 500).Select(_ => FrameLine(16)), lines);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static List<string> ReadAll(LineFramer framer)
    {
        var lines = new List<string>();
        while (framer.TryReadLine(out string line))
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// A deterministic mix of the shapes the serial path actually sees: complete 16- and 64-zone
    /// frames, "\n" / "\r\n" / bare-"\r" terminators, garbage lines, blank lines produced by CRLF,
    /// unterminated tails, and one wall of garbage large enough to force an overflow.
    /// </summary>
    private static List<string> BuildChunkSequence()
    {
        var rng = new Random(20261119);
        var chunks = new List<string>();

        for (int c = 0; c < 400; c++)
        {
            var sb = new StringBuilder();
            int pieces = rng.Next(1, 6);
            for (int p = 0; p < pieces; p++)
            {
                switch (rng.Next(6))
                {
                    case 0:
                        sb.Append(FrameLine(16)).Append('\n');
                        break;
                    case 1:
                        sb.Append(FrameLine(64)).Append("\r\n");
                        break;
                    case 2:
                        sb.Append(FrameLine(16)).Append('\r');
                        break;
                    case 3:
                        sb.Append("not a frame").Append("\r\n");
                        break;
                    case 4:
                        sb.Append("\r\n");
                        break;
                    default:
                        sb.Append("unterminated");
                        break;
                }
            }

            chunks.Add(sb.ToString());
        }

        chunks.Add(new string('7', 12_000));
        chunks.Add(FrameLine(16) + "\n");

        return chunks;
    }
}

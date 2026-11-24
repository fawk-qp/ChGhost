using ChGhost.Core.Engine;
using ChGhost.Core.Models;

namespace ChGhost.Tests;

public class HumanMoveSelectorTests
{
    [Fact]
    public void ParseInfoPvLine_ParsesCpAndMateAndMultiPvCorrectly()
    {
        Assert.True(StockfishEngine.TryParseInfoPvLine(
            "info depth 18 seldepth 22 multipv 1 score cp 35 nodes 123456 nps 100000 hashfull 10 pv e2e4 e7e5 g1f3",
            out CandidateMove cpMove));
        Assert.Equal("e2e4", cpMove.Uci);
        Assert.Equal(35, cpMove.ScoreCp);
        Assert.False(cpMove.IsMate);
        Assert.Equal(18, cpMove.Depth);
        Assert.Equal(1, cpMove.MultiPvIndex);

        Assert.True(StockfishEngine.TryParseInfoPvLine(
            "info depth 12 multipv 2 score cp -17 pv d2d4 d7d5 c2c4",
            out CandidateMove negativeCp));
        Assert.Equal(-17, negativeCp.ScoreCp);
        Assert.Equal(2, negativeCp.MultiPvIndex);

        Assert.True(StockfishEngine.TryParseInfoPvLine(
            "info depth 9 multipv 1 score mate 3 pv h5f7 g8h8 h7h8q",
            out CandidateMove mateIn));
        Assert.True(mateIn.IsMate);
        Assert.Equal(3, mateIn.MateIn);
        Assert.Equal("h5f7", mateIn.Uci);

        Assert.True(StockfishEngine.TryParseInfoPvLine(
            "info depth 7 multipv 1 score mate -2 pv a1a8 h8h1",
            out CandidateMove matedIn));
        Assert.True(matedIn.IsMate);
        Assert.Equal(-2, matedIn.MateIn);

        Assert.False(StockfishEngine.TryParseInfoPvLine(
            "info depth 15 multipv 1 score cp 28 lowerbound pv e2e4 e7e5",
            out _));
        Assert.False(StockfishEngine.TryParseInfoPvLine(
            "info depth 15 multipv 1 score cp 28 upperbound pv e2e4 e7e5",
            out _));
    }

    [Fact]
    public void Select_ForcedTacticGap_AlwaysPicksPv1WithMinimalDelay()
    {
        var candidates = new[]
        {
            Cp("e2e4", 250, 1),
            Cp("d2d4", 40, 2)
        };
        var options = new HumanizerOptions(MinDelayMs: 120, MaxDelayMs: 150);

        for (int i = 0; i < 50; i++)
        {
            MoveRecommendation rec = HumanMoveSelector.Select(candidates, EmptyGrid(), options, new Random(i));
            Assert.Equal("e2e4", rec.Primary.Uci);
            Assert.Empty(rec.Alternatives);
            Assert.True(rec.RecommendedDelayMs <= 150, $"delay={rec.RecommendedDelayMs}");
        }
    }

    [Fact]
    public void Select_MateInTwo_AlwaysPicksMate()
    {
        var candidates = new[]
        {
            new CandidateMove("h5f7", 0, true, 2, 20, 1),
            Cp("e2e4", 80, 2)
        };

        for (int i = 0; i < 20; i++)
        {
            MoveRecommendation rec = HumanMoveSelector.Select(candidates, EmptyGrid(), new HumanizerOptions(), new Random(i));
            Assert.Equal("h5f7", rec.Primary.Uci);
            Assert.True(rec.Primary.IsMate);
        }
    }

    [Fact]
    public void Select_EqualQuietMoves_RespectsMaxCpLossAndProducesAlternatives()
    {
        var candidates = new[]
        {
            Cp("e2e4", 20, 1),
            Cp("d2d4", 12, 2),
            Cp("c2c4", -5, 3),
            Cp("a2a3", -120, 4)
        };
        var options = new HumanizerOptions(MaxCpLoss: 45, AdvisorMaxGapCp: 65, AdvisorMaxArrows: 2);

        for (int i = 0; i < 30; i++)
        {
            MoveRecommendation rec = HumanMoveSelector.Select(candidates, EmptyGrid(), options, new FirstRollRandom());
            Assert.NotEqual("a2a3", rec.Primary.Uci);
            Assert.Contains(rec.Alternatives, c => c.Uci == "d2d4");
            Assert.Contains(rec.Alternatives, c => c.Uci == "c2c4");
        }
    }

    [Fact]
    public void Select_ComplexPosition_ProducesBoundedPacingDelay()
    {
        var candidates = new[]
        {
            Cp("e2e4", 30, 1),
            Cp("d2d4", 28, 2),
            Cp("c2c4", 25, 3),
            Cp("g1f3", 20, 4)
        };
        var options = new HumanizerOptions(MinDelayMs: 200, MaxDelayMs: 800);

        for (int i = 0; i < 30; i++)
        {
            MoveRecommendation rec = HumanMoveSelector.Select(candidates, EmptyGrid(), options, new Random(i));
            Assert.InRange(rec.RecommendedDelayMs, 200, 800);
        }
    }

    [Fact]
    public void Select_HumanBonusPrefersCastlingOverCornerRookShuffle()
    {
        char[,] grid = EmptyGrid();
        grid[7, 4] = 'K';
        grid[7, 0] = 'R';
        grid[7, 1] = '1';
        grid[7, 6] = '1';

        double castling = HumanMoveSelector.EstimateHumanIntuitionBonus("e1g1", grid);
        double rookShuffle = HumanMoveSelector.EstimateHumanIntuitionBonus("a1b1", grid);

        Assert.True(castling > rookShuffle, $"castling={castling}, rookShuffle={rookShuffle}");
    }

    private static CandidateMove Cp(string uci, int scoreCp, int multiPv)
        => new(uci, scoreCp, false, 0, 20, multiPv);

    private static char[,] EmptyGrid()
    {
        var grid = new char[8, 8];
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
            grid[r, c] = '1';
        return grid;
    }

    private sealed class FirstRollRandom : Random
    {
        public override double NextDouble() => 0.0;
    }
}

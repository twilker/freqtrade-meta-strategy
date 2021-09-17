using System;

namespace FreqtradeMetaStrategy
{
    public class ParameterScore
    {
        public ParameterScore(ParameterType type, int value, double score, double winner)
        {
            Type = type;
            Value = value;
            Score = score;
            Winner = winner;
        }

        public ParameterType Type { get; }
        public int Value { get; }
        public double Score { get; }
        public double Winner { get; }
    }
}
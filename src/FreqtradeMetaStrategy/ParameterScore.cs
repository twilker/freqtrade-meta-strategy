using System;

namespace FreqtradeMetaStrategy
{
    public class ParameterScore
    {
        public ParameterScore(ParameterType type, int value, int score)
        {
            Type = type;
            Value = value;
            Score = score;
        }

        public ParameterType Type { get; }
        public int Value { get; }
        public int Score { get; }
    }
}
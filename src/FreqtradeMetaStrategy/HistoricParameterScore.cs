namespace FreqtradeMetaStrategy
{
    public class HistoricParameterScore : ParameterScore
    {
        public HistoricParameterScore(ParameterType type, int value, double score, string date) : base(type, value, score, 0)
        {
            Date = date;
        }

        public string Date { get; }
    }
}
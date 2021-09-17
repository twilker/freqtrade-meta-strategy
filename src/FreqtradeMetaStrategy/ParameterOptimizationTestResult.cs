using System;

namespace FreqtradeMetaStrategy
{
    public class ParameterOptimizationTestResult
    {
        public string Strategy { get; set; }
        public bool DataDownloaded { get; set; }
        public string[] AllPairs { get; set; }
        public DateTime EndDate { get; set; }
        public ParameterOptimization ParameterOptimization { get; set; }
        public ParameterScore[] Scores { get; set; }
        public ParameterScore[] AccumulatedScores { get; set; }
        public HistoricParameterScore[] HistoricScores { get; set; }
    }
}
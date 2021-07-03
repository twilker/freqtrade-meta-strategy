using Newtonsoft.Json.Linq;

namespace FreqtradeMetaStrategy
{
    public class OptimizedStrategy
    {
        public string StrategyName { get; }
        public double PredictedDailyProfit { get; }
        public double PredictedShortTermDailyProfit { get; }
        public BackTestingResult BackTestingResult { get; }
        public int PredictedDaysToDoubleWallet => (int) (1 / PredictedDailyProfit);

        public OptimizedStrategy(string strategyName, double predictedDailyProfit, double predictedShortTermDailyProfit,
                                 BackTestingResult backTestingResult)
        {
            StrategyName = strategyName;
            PredictedDailyProfit = predictedDailyProfit;
            PredictedShortTermDailyProfit = predictedShortTermDailyProfit;
            BackTestingResult = backTestingResult;
        }

        public override string ToString()
        {
            return $"{nameof(StrategyName)}: {StrategyName}, {nameof(PredictedDailyProfit)}: {PredictedDailyProfit*100}%, {nameof(PredictedShortTermDailyProfit)}: {PredictedShortTermDailyProfit*100}%, {nameof(PredictedDaysToDoubleWallet)}: {PredictedDaysToDoubleWallet}";
        }
    }
}
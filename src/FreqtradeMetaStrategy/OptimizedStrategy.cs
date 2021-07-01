using Newtonsoft.Json.Linq;

namespace FreqtradeMetaStrategy
{
    public class OptimizedStrategy
    {
        public string StrategyName { get; }
        public double PredictedDailyProfit { get; }
        public double PredictedShortTermDailyProfit { get; }
        public int PredictedDaysToDoubleWallet => (int) (1 / PredictedDailyProfit);

        public OptimizedStrategy(string strategyName, double predictedDailyProfit, double predictedShortTermDailyProfit)
        {
            StrategyName = strategyName;
            PredictedDailyProfit = predictedDailyProfit;
            PredictedShortTermDailyProfit = predictedShortTermDailyProfit;
        }

        public override string ToString()
        {
            return $"{nameof(StrategyName)}: {StrategyName}, {nameof(PredictedDailyProfit)}: {PredictedDailyProfit*100}%, {nameof(PredictedShortTermDailyProfit)}: {PredictedShortTermDailyProfit*100}%, {nameof(PredictedDaysToDoubleWallet)}: {PredictedDaysToDoubleWallet}";
        }
    }
}
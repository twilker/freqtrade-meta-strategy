namespace FreqtradeMetaStrategy
{
    public class ProgramConfiguration
    {
        public string StrategyRepositoryUrl { get; set; } = "https://github.com/freqtrade/freqtrade-strategies.git";
        public string CoingeckoApiBaseUrl { get; set; } = "https://api.coingecko.com/api/v3";
        public string CoingeckoGetTickersMethod { get; set; } = "exchanges/$(exchange)/tickers?page=$(page)";
        public string CoingeckoGetCoinDataMethod { get; set; } = "coins/$(coin)?tickers=false&market_data=false&community_data=false&developer_data=false";
        public string UnstableStakeCoin { get; set; } = "bitcoin";
        public double UnstableCoinWallet { get; set; } = 0.015;
        public string StableStakeCoin { get; set; } = "tether";
        public double StableCoinWallet { get; set; } = 500;
        public string Exchange { get; set; } = "binance";
        public string HyperoptFunction { get; set; } = "SortinoHyperOptLossDaily";
        public int VolumeWeight { get; set; } = 1;
        public int TrustWeight { get; set; } = 1;
        public int RateLimitTimeout { get; set; } = 70000;
        public int BackTestingPairsBatchSize { get; set; } = 10;
        public double MinimumTrustScore { get; set; } = 50;
        public double MinimumDailyProfit { get; set; } = 0.005;
        public double MinimumTradesPerDay { get; set; } = 1;
        public int MaxStrategySuggestions { get; set; } = 5;
        public int StrategyRunningDays { get; set; } = 3;

        public string[] StrategyBlacklist { get; set; } =
        {
            "MultiMa",
            "InformativeSample",
            "GodStra",
            "GodStraNew",
            "Supertrend",
            "Zeus",
            "DoesNothingStrategy"
        };
    }
}
namespace FreqtradeMetaStrategy
{
    public class ProgramConfiguration
    {
        public string StrategyRepositoryUrl { get; set; } = "https://github.com/freqtrade/freqtrade-strategies.git";
        public string CoingeckoApiBaseUrl { get; set; } = "https://api.coingecko.com/api/v3";
        public string CoingeckoGetTickersMethod { get; set; } = "exchanges/$(exchange)/tickers?page=$(page)";
        public string UnstableBaseCoin { get; set; } = "bitcoin";
        public string StableBaseCoin { get; set; } = "tether";
        public string Exchange { get; set; } = "binance";
        public int PairsCutoff { get; set; } = 10;
    }
}
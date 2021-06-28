using System;
using Newtonsoft.Json;

namespace FreqtradeMetaStrategy
{
    public class CoingeckoTickers
    {
        public string Name { get; set; }
        public Ticker[] Tickers { get; set; }

        public override string ToString()
        {
            return $"{nameof(Tickers)}: {string.Join<Ticker>(Environment.NewLine, Tickers)}";
        }
    }

    public class Ticker
    {
        public string Base { get; set; }
        [JsonProperty(PropertyName = "coin_id")]
        public string BaseId { get; set; }
        public string Target { get; set; }
        public double Volume { get; set; }
        [JsonProperty(PropertyName = "trust_score")]
        public string TrustScore { get; set; }
        [JsonProperty(PropertyName = "is_anomaly")]
        public bool IsAnomaly { get; set; }
        [JsonProperty(PropertyName = "is_stale")]
        public bool IsStale { get; set; }

        public override string ToString()
        {
            return $"{nameof(Base)}: {Base}, {nameof(Target)}: {Target}, {nameof(Volume)}: {Volume}, {nameof(TrustScore)}: {TrustScore}, {nameof(IsAnomaly)}: {IsAnomaly}, {nameof(IsStale)}: {IsStale}";
        }
    }
}
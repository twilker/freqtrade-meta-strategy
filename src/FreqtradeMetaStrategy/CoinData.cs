using Newtonsoft.Json;

namespace FreqtradeMetaStrategy
{
    public class CoinData
    {
        [JsonProperty(PropertyName = "coingecko_score")]
        public double? CoingeckoScore { get; set; }
        [JsonProperty(PropertyName = "sentiment_votes_up_percentage")]
        public double? VotesUpScore { get; set; }
        [JsonProperty(PropertyName = "community_score")]
        public double? CommunityScore { get; set; }

        public override string ToString()
        {
            return $"{nameof(CoingeckoScore)}: {CoingeckoScore}, {nameof(VotesUpScore)}: {VotesUpScore}, {nameof(CommunityScore)}: {CommunityScore}";
        }
    }
}
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

    public class TickersCache
    {
        public TickersCache(DateTime validUntil, Ticker[] tickers)
        {
            ValidUntil = validUntil;
            Tickers = tickers;
        }

        public DateTime ValidUntil { get; set; }
        public Ticker[] Tickers { get; set; }
    }

    public class Ticker : IEquatable<Ticker>
    {
        public string Base { get; set; }
        [JsonProperty(PropertyName = "coin_id")]
        public string BaseId { get; set; }
        [JsonProperty(PropertyName = "target_coin_id")]
        public string TargetId { get; set; }
        public string Target { get; set; }
        public double Volume { get; set; }
        [JsonProperty(PropertyName = "trust_score")]
        public string TrustScore { get; set; }
        [JsonProperty(PropertyName = "is_anomaly")]
        public bool IsAnomaly { get; set; }
        [JsonProperty(PropertyName = "is_stale")]
        public bool IsStale { get; set; }
        //[JsonIgnore]
        public double TrustRating { get; set; }
        [JsonIgnore]
        public int TrustRank { get; set; }
        [JsonIgnore]
        public int VolumeRank { get; set; }
        [JsonIgnore]
        public int WeightedValue { get; set; }

        public string ToTradingPairString()
        {
            return $"{Base}/{Target}";
        }

        public override string ToString()
        {
            return $"{nameof(Base)}: {Base}, {nameof(BaseId)}: {BaseId}, {nameof(TargetId)}: {TargetId}, {nameof(Target)}: {Target}, {nameof(Volume)}: {Volume}, {nameof(TrustScore)}: {TrustScore}, {nameof(IsAnomaly)}: {IsAnomaly}, {nameof(IsStale)}: {IsStale}, {nameof(TrustRating)}: {TrustRating}, {nameof(TrustRank)}: {TrustRank}, {nameof(VolumeRank)}: {VolumeRank}, {nameof(WeightedValue)}: {WeightedValue}";
        }

        public bool Equals(Ticker other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Base == other.Base && Target == other.Target;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return Equals((Ticker) obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Base, Target);
        }

        public static bool operator ==(Ticker left, Ticker right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(Ticker left, Ticker right)
        {
            return !Equals(left, right);
        }
    }
}
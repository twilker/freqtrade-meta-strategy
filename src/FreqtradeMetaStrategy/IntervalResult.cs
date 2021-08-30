#region Copyright

//  Copyright (c) Tobias Wilker and contributors
//  This file is licensed under MIT

#endregion

using System;

namespace FreqtradeMetaStrategy
{
    public class IntervalResult
    {
        public string StartDate { get; set; }
        public string EndDate { get; set; }
        public double Profit { get; set; }
        public double DrawDown { get; set; }
        public double MarketChange { get; set; }
        public PairProfit[] Pairs { get; set; } = Array.Empty<PairProfit>();
    }
}
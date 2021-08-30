#region Copyright
//  Copyright (c) Tobias Wilker and contributors
//  This file is licensed under MIT
#endregion

using System;

namespace FreqtradeMetaStrategy
{
    public class BlacklistOptimizationPairsPartitionResult
    {
        public string[] PairList { get; set; }
        public bool Completed { get; set; }
        public IntervalResult[] Results { get; set; } = Array.Empty<IntervalResult>();
    }
}
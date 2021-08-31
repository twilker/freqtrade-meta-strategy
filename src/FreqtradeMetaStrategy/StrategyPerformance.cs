#region Copyright
//  Copyright (c) Tobias Wilker and contributors
//  This file is licensed under MIT
#endregion

namespace FreqtradeMetaStrategy
{
    public class StrategyPerformance
    {
        public double Unfiltered { get; set; }
        public double Filtered { get; set; }
        public double Overall { get; set; }
    }
}
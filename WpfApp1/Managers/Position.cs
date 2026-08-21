using Binance.Net.Enums;
using System;

namespace OrderWatchLite.Managers
{
    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public OrderSide Side { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal Leverage { get; set; }
        public decimal StopLossPrice { get; set; }
        public long StopLossOrderId { get; set; }
        public bool IsBreakEvenTriggered { get; set; } = false;
        public decimal BreakEvenThreshold { get; set; } = 3m;
        public decimal StopLossPercent { get; set; } = 5m;
        public DateTime OpenTime { get; set; } = DateTime.Now;
    }
}
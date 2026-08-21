using Binance.Net.Enums;
using System;

namespace OrderWatchLite.Managers
{
    /// <summary>
    /// 当前交易轮次中的一个逻辑保护层。
    ///
    /// 注意：
    /// 1. 这不是 Binance 的真实总仓位。
    /// 2. Binance 同品种同方向的仓位会合并。
    /// 3. 本类只是记录程序内部的分层保护信息。
    /// </summary>
    public class Position
    {
        /// <summary>
        /// 程序内部唯一编号。
        /// 从 1 开始，本轮仓位归零后重新从 1 开始。
        /// </summary>
        public long LayerId { get; set; }

        /// <summary>
        /// 交易对，例如 BTCUSDT。
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// 方向。
        /// Buy = 多仓
        /// Sell = 空仓
        /// </summary>
        public OrderSide Side { get; set; }

        /// <summary>
        /// 这一层当前实际还剩多少数量。
        ///
        /// 注意：
        /// 这是这一层的逻辑数量，不代表 Binance 当前总仓位。
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// 这一层实际成交的加权平均开仓价格。
        /// </summary>
        public decimal EntryPrice { get; set; }

        /// <summary>
        /// 开仓时使用的杠杆。
        /// </summary>
        public decimal Leverage { get; set; }

        /// <summary>
        /// 当前这一层保护止损价格。
        /// </summary>
        public decimal StopLossPrice { get; set; }

        /// <summary>
        /// Binance 主订单 ID。
        /// </summary>
        public long EntryOrderId { get; set; }

        /// <summary>
        /// Binance 保护 STOP-MARKET 订单 ID。
        /// 0 表示当前还没有成功建立保护单。
        /// </summary>
        public long StopLossOrderId { get; set; }

        /// <summary>
        /// 程序生成的保护订单 ClientOrderId。
        ///
        /// 重启程序后可以通过这个 ID 找回自己的保护订单。
        /// </summary>
        public string StopLossClientOrderId { get; set; } = string.Empty;

        /// <summary>
        /// 程序生成的主订单 ClientOrderId。
        /// </summary>
        public string EntryClientOrderId { get; set; } = string.Empty;

        /// <summary>
        /// 是否已经执行过保本。
        /// </summary>
        public bool IsBreakEvenTriggered { get; set; }

        /// <summary>
        /// 保本触发所需的盈利百分比。
        /// </summary>
        public decimal BreakEvenThreshold { get; set; } = 3m;

        /// <summary>
        /// 初始止损百分比。
        /// </summary>
        public decimal StopLossPercent { get; set; } = 5m;

        /// <summary>
        /// 本层建立时间。
        /// </summary>
        public DateTime OpenTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 最近一次同步时间。
        /// </summary>
        public DateTime LastSyncTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 当前保护层是否仍然有效。
        /// false = 已经结束/被完全减掉/保护单已触发。
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 是否属于本程序创建的保护订单。
        /// </summary>
        public bool HasProtectionOrder =>
            StopLossOrderId > 0 &&
            !string.IsNullOrWhiteSpace(StopLossClientOrderId);

        /// <summary>
        /// 生成本层保护订单的 ClientOrderId。
        ///
        /// Binance ClientOrderId 长度有限，因此保持简短。
        /// </summary>
        public string BuildStopLossClientOrderId()
        {
            string symbol = Symbol.Length > 10
                ? Symbol.Substring(0, 10)
                : Symbol;

            string side = Side == OrderSide.Buy ? "L" : "S";

            return $"OWLT_SL_{symbol}_{side}_{LayerId}_{Guid.NewGuid():N}"
                .Substring(0, Math.Min(32, $"OWLT_SL_{symbol}_{side}_{LayerId}_{Guid.NewGuid():N}".Length));
        }

        /// <summary>
        /// 创建一层保护层。
        /// </summary>
        public static Position Create(
            long layerId,
            string symbol,
            OrderSide side,
            decimal quantity,
            decimal entryPrice,
            decimal leverage,
            decimal stopLossPrice,
            decimal stopLossPercent,
            decimal breakEvenThreshold)
        {
            return new Position
            {
                LayerId = layerId,
                Symbol = symbol,
                Side = side,
                Quantity = quantity,
                EntryPrice = entryPrice,
                Leverage = leverage,
                StopLossPrice = stopLossPrice,

                StopLossPercent = stopLossPercent,
                BreakEvenThreshold = breakEvenThreshold,

                IsBreakEvenTriggered = false,
                IsActive = true,

                OpenTime = DateTime.Now,
                LastSyncTime = DateTime.Now
            };
        }
    }
}
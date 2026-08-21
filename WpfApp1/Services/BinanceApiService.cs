using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;

namespace OrderWatchLite.Services
{
    public class BinanceApiService
    {
        private readonly BinanceRestClient _restClient;

        public BinanceApiService(string apiKey, string apiSecret, bool useTestNet = true)
        {
            // 官方文档写法：使用 BinanceRestClientOptions
            var options = new BinanceRestClientOptions
            {
                ApiCredentials = new ApiCredentials(apiKey, apiSecret),
                SpotOptions = { BaseAddress = useTestNet ? "https://testnet.binance.vision" : "https://api.binance.com" },
                UsdFuturesOptions = { BaseAddress = useTestNet ? "https://testnet.binancefuture.com" : "https://fapi.binance.com" }
            };
            _restClient = new BinanceRestClient(options);
        }

        /// <summary>
        /// 获取所有 USDT 合约交易对
        /// 官方文档：BinanceFuturesUsdtSymbol 使用 Name 属性[reference:8]
        /// </summary>
        public async Task<List<string>> GetAllSymbolsAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
                if (result.Success && result.Data != null)
                {
                    return result.Data.Symbols
                        .Where(s => s.Status == SymbolStatus.Trading && s.Name.EndsWith("USDT"))
                        .Select(s => s.Name)
                        .OrderBy(s => s)
                        .ToList();
                }
                return new List<string>();
            }
            catch { return new List<string>(); }
        }

        /// <summary>
        /// 获取当前价格
        /// </summary>
        public async Task<decimal?> GetCurrentPriceAsync(string symbol)
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol);
                if (result.Success && result.Data != null)
                    return result.Data.LastPrice;
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 获取账户余额（USDT）
        /// </summary>
        public async Task<decimal?> GetAccountBalanceAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Account.GetAccountInfoV3Async();
                if (result.Success && result.Data != null)
                {
                    var asset = result.Data.Assets.FirstOrDefault(a => a.Asset == "USDT");
                    return asset?.WalletBalance ?? 0;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 获取步长信息
        /// </summary>
        public async Task<LotSizeInfo?> GetLotSizeInfoAsync(string symbol)
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(symbol);
                if (result.Success && result.Data != null)
                {
                    var symbolData = result.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                    if (symbolData != null)
                    {
                        var filter = symbolData.LotSizeFilter;
                        return new LotSizeInfo
                        {
                            StepSize = filter.StepSize,
                            MinQty = filter.MinQuantity,
                            MaxQty = filter.MaxQuantity
                        };
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 下单并挂止损单
        /// 官方文档：使用 UsdFuturesApi.Trading.PlaceOrderAsync[reference:9]
        /// 止损单使用 FuturesOrderType.Stop[reference:11]
        /// </summary>
        public async Task<(bool success, string orderId, string stopOrderId, string error)>
            PlaceOrderWithStopLossAsync(
                string symbol,
                OrderSide side,
                decimal quantity,
                decimal stopPrice)
        {
            try
            {
                // 1. 下市价主单（官方写法）
                var mainResult = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    newClientOrderId: Guid.NewGuid().ToString()
                );

                if (!mainResult.Success)
                {
                    return (false, string.Empty, string.Empty, mainResult.Error?.Message ?? "主单下单失败");
                }

                string mainOrderId = mainResult.Data.Id.ToString();

                // 2. 挂止损市价单（reduce only）
                OrderSide closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                var stopResult = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Stop,
                    quantity: quantity,
                    stopPrice: stopPrice,
                    price: stopPrice,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    newClientOrderId: $"SL_{Guid.NewGuid():N}".Substring(0, 32),
                    reduceOnly: true
                );

                if (!stopResult.Success)
                {
                    return (true, mainOrderId, string.Empty, $"主单成功但止损单失败: {stopResult.Error?.Message}");
                }

                return (true, mainOrderId, stopResult.Data.Id.ToString(), string.Empty);
            }
            catch (Exception ex)
            {
                return (false, string.Empty, string.Empty, ex.Message);
            }
        }

        /// <summary>
        /// 平仓（市价，只减仓）
        /// </summary>
        public async Task<(bool success, string orderId, string error)> ClosePositionAsync(
            string symbol,
            decimal quantity,
            OrderSide closeSide)
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    newClientOrderId: $"CLOSE_{Guid.NewGuid():N}".Substring(0, 32),
                    reduceOnly: true
                );

                if (!result.Success)
                {
                    return (false, string.Empty, result.Error?.Message ?? "平仓失败");
                }

                return (true, result.Data.Id.ToString(), string.Empty);
            }
            catch (Exception ex)
            {
                return (false, string.Empty, ex.Message);
            }
        }

        /// <summary>
        /// 获取所有持仓（非零）
        /// 使用 Trading.GetPositionsAsync 获取持仓信息[reference:12]
        /// </summary>
        public async Task<List<PositionInfo>> GetPositionsAsync()
        {
            try
            {
                // 使用 Trading.GetPositionsAsync 获取持仓[reference:13]
                var result = await _restClient.UsdFuturesApi.Trading.GetPositionsAsync();
                if (!result.Success || result.Data == null)
                    return new List<PositionInfo>();

                return result.Data
                    .Where(p => p.Quantity != 0)
                    .Select(p => new PositionInfo
                    {
                        Symbol = p.Symbol,
                        Quantity = Math.Abs(p.Quantity),
                        Side = p.Quantity > 0 ? OrderSide.Buy : OrderSide.Sell,
                        EntryPrice = p.EntryPrice,
                        MarkPrice = p.MarkPrice,
                        UnrealizedPnl = p.UnrealizedPnl,
                        Leverage = p.Leverage,
                        PnlPercent = p.EntryPrice != 0
                            ? (p.Quantity > 0
                                ? (p.MarkPrice - p.EntryPrice) / p.EntryPrice * 100
                                : (p.EntryPrice - p.MarkPrice) / p.EntryPrice * 100)
                            : 0
                    })
                    .ToList();
            }
            catch { return new List<PositionInfo>(); }
        }
    }

    public class LotSizeInfo
    {
        public decimal StepSize { get; set; }
        public decimal MinQty { get; set; }
        public decimal MaxQty { get; set; }
    }

    public class PositionInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public OrderSide Side { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public int Leverage { get; set; }
        public decimal PnlPercent { get; set; }
        public string DisplayText { get; set; } = string.Empty;
    }
}
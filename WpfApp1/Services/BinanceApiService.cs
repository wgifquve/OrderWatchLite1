using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrderWatchLite.Services
{
    /// <summary>
    /// Binance USD-M Futures API 服务层。
    /// 采用 Binance.Net 13.4.0 官方推荐的初始化方式，并使用强类型属性。
    /// </summary>
    public class BinanceApiService : IDisposable
    {
        private readonly BinanceRestClient _restClient;
        private readonly bool _useTestNet;

        // 缓存交易规则
        private ExchangeInfoCache? _exchangeInfoCache;
        private readonly SemaphoreSlim _exchangeInfoLock = new(1, 1);
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

        public BinanceApiService(string apiKey, string apiSecret, bool useTestNet = true)
        {
            _useTestNet = useTestNet;

            // 使用官方推荐的初始化方式
            _restClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new BinanceCredentials(apiKey, apiSecret);
                options.Environment = useTestNet
                    ? BinanceEnvironment.Testnet
                    : BinanceEnvironment.Live;
            });
        }

        // ============================================================
        // 交易对列表
        // ============================================================

        public async Task<List<string>> GetAllSymbolsAsync()
        {
            try
            {
                var exchangeInfo = await GetExchangeInfoAsync();
                return exchangeInfo?.Symbols
                    .Where(x => x.Status == SymbolStatus.Trading && x.Name.EndsWith("USDT"))
                    .Select(x => x.Name)
                    .OrderBy(x => x)
                    .ToList() ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        // ============================================================
        // 价格、余额
        // ============================================================

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

        public async Task<decimal?> GetAccountBalanceAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Account.GetAccountInfoV3Async();
                if (!result.Success || result.Data == null)
                    return null;

                var asset = result.Data.Assets
                    .FirstOrDefault(a => a.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                    return 0m;

                return asset.WalletBalance;
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // 步长信息（从缓存获取）
        // ============================================================

        public async Task<LotSizeInfo?> GetLotSizeInfoAsync(string symbol)
        {
            var exchangeInfo = await GetExchangeInfoAsync();
            if (exchangeInfo == null)
                return null;

            var symbolData = exchangeInfo.Symbols
                .FirstOrDefault(s => s.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase));

            if (symbolData == null)
                return null;

            var lotFilter = symbolData.LotSizeFilter;
            if (lotFilter == null)
                return null;

            return new LotSizeInfo
            {
                StepSize = lotFilter.StepSize,
                MinQty = lotFilter.MinQuantity,
                MaxQty = lotFilter.MaxQuantity
            };
        }

        // ============================================================
        // 下单（主单 + 止损）
        // ============================================================

        public async Task<(bool success, string orderId, string stopOrderId, string error)>
            PlaceOrderWithStopLossAsync(
                string symbol,
                OrderSide side,
                decimal quantity,
                decimal stopPrice)
        {
            try
            {
                if (quantity <= 0)
                    return (false, string.Empty, string.Empty, "下单数量必须大于 0");

                if (stopPrice <= 0)
                    return (false, string.Empty, string.Empty, "止损价格必须大于 0");

                // 1. 市价主单
                var mainResult = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    newClientOrderId: Guid.NewGuid().ToString("N")
                );

                if (!mainResult.Success || mainResult.Data == null)
                {
                    return (false, string.Empty, string.Empty, mainResult.Error?.Message ?? "主单下单失败");
                }

                string mainOrderId = mainResult.Data.Id.ToString();

                // 2. 止损市价单（reduce only）
                OrderSide closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                var stopResult = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.StopMarket,
                    quantity: quantity,
                    stopPrice: stopPrice,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    newClientOrderId: $"SL_{Guid.NewGuid():N}".Substring(0, 32),
                    reduceOnly: true
                );

                if (!stopResult.Success || stopResult.Data == null)
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

        // ============================================================
        // 平仓（市价，reduce only）
        // ============================================================

        public async Task<(bool success, string orderId, string error)> ClosePositionAsync(
            string symbol,
            decimal quantity,
            OrderSide closeSide)
        {
            try
            {
                if (quantity <= 0)
                    return (false, string.Empty, "平仓数量必须大于 0");

                var result = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    newClientOrderId: $"CLOSE_{Guid.NewGuid():N}".Substring(0, 32),
                    reduceOnly: true
                );

                if (!result.Success || result.Data == null)
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

        // ============================================================
        // 获取持仓（转换为内部 PositionInfo）
        // ============================================================

        public async Task<List<PositionInfo>> GetPositionsAsync()
        {
            try
            {
                var result = await _restClient.UsdFuturesApi.Trading.GetPositionsAsync();
                if (!result.Success || result.Data == null)
                    return new List<PositionInfo>();

                var positions = new List<PositionInfo>();

                foreach (var p in result.Data)
                {
                    // BinancePositionV3 属性：
                    // PositionAmt, EntryPrice, MarkPrice, Leverage (decimal?)
                    decimal positionAmt = p.PositionAmt;
                    if (positionAmt == 0)
                        continue;

                    decimal entryPrice = p.EntryPrice;
                    decimal markPrice = p.MarkPrice;
                    decimal quantity = Math.Abs(positionAmt);
                    OrderSide side = positionAmt > 0 ? OrderSide.Buy : OrderSide.Sell;

                    // 计算浮盈亏（自己计算，不依赖不存在的 UnrealizedPnl）
                    decimal unrealizedPnl = 0m;
                    if (entryPrice > 0 && markPrice > 0)
                    {
                        unrealizedPnl = side == OrderSide.Buy
                            ? (markPrice - entryPrice) * quantity
                            : (entryPrice - markPrice) * quantity;
                    }

                    decimal pnlPercent = 0m;
                    if (entryPrice > 0)
                    {
                        pnlPercent = side == OrderSide.Buy
                            ? (markPrice - entryPrice) / entryPrice * 100m
                            : (entryPrice - markPrice) / entryPrice * 100m;
                    }

                    int leverage = 0;
                    if (p.Leverage.HasValue)
                    {
                        leverage = (int)Math.Round(p.Leverage.Value, MidpointRounding.AwayFromZero);
                    }

                    positions.Add(new PositionInfo
                    {
                        Symbol = p.Symbol,
                        Quantity = quantity,
                        Side = side,
                        EntryPrice = entryPrice,
                        MarkPrice = markPrice,
                        UnrealizedPnl = unrealizedPnl,
                        Leverage = leverage,
                        PnlPercent = pnlPercent
                    });
                }

                return positions;
            }
            catch
            {
                return new List<PositionInfo>();
            }
        }

        // ============================================================
        // 私有：缓存 ExchangeInfo
        // ============================================================

        private async Task<ExchangeInfoCache?> GetExchangeInfoAsync()
        {
            await _exchangeInfoLock.WaitAsync();
            try
            {
                if (_exchangeInfoCache != null &&
                    DateTime.UtcNow - _exchangeInfoCache.FetchedAt < _cacheExpiry)
                {
                    return _exchangeInfoCache;
                }

                var result = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
                if (!result.Success || result.Data == null)
                    return _exchangeInfoCache;

                _exchangeInfoCache = new ExchangeInfoCache
                {
                    FetchedAt = DateTime.UtcNow,
                    Symbols = result.Data.Symbols.ToList()
                };
                return _exchangeInfoCache;
            }
            finally
            {
                _exchangeInfoLock.Release();
            }
        }

        // ============================================================
        // IDisposable
        // ============================================================

        public void Dispose()
        {
            _restClient?.Dispose();
            _exchangeInfoLock?.Dispose();
        }
    }

    // ============================================================
    // 内部缓存模型
    // ============================================================

    internal class ExchangeInfoCache
    {
        public DateTime FetchedAt { get; set; }
        public List<Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtSymbol> Symbols { get; set; } = new();
    }

    // ============================================================
    // 对外暴露的数据模型（UI 层使用）
    // ============================================================

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